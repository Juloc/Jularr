using System.Globalization;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;
using Jularr.Backend.Data.Logic.Reading;

namespace Jularr.Backend.Logic.Reading
{
    // Book XHTML as safe reader HTML: no scripts, styles or remote content; numbered blocks locate progress.
    internal static class ReadingHtml
    {
        // Raised when the prepared output changes; old documents are prepared again.
        public const int AnalyzerVersion = 2;

        // A figure is a picture worth its own room: a page of its own, or the full width of the text.
        public const string PageFigure = "page";
        public const string WideFigure = "wide";

        // A page that only the original shows stands in the text as an empty figure naming its number.
        public const string OriginalFigure = "original";

        private static readonly string[] Blocks = ["p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "blockquote", "pre", "figure", "table", "dt", "dd", "hr"];
        private static readonly HtmlParser Parser = new();

        public static string Text(string html) => string.Join(' ', (Parser.ParseDocument(html).Body?.TextContent ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        public static string ResourceUrl(long documentId, string path) =>
            "/api/v1/reading/documents/" + documentId.ToString(CultureInfo.InvariantCulture) + "/resource?path=" + Uri.EscapeDataString(path);

        // The kind of figure a picture of this size is; none for decoration and small pictures that stay in the line.
        public static string? FigureKind(int width, int height, bool alone)
        {
            if (width < 300 || height < 200)
            {
                return null;
            }

            return alone && width >= 400 && height >= 400 && (long)width * height >= 250_000 ? PageFigure : WideFigure;
        }

        public static PreparedSection Prepare(string xhtml, string sectionPath, long documentId, IReadOnlyDictionary<string, int> sections, IReadOnlyDictionary<string, string> manifest,
            Func<string, (int Width, int Height)?>? imageSize = null)
        {
            // SVG covers become plain images before sanitizing.
            var source = Parser.ParseDocument(xhtml);
            foreach (var image in source.QuerySelectorAll("svg image").ToArray())
            {
                var href = image.GetAttribute("xlink:href") ?? image.GetAttribute("href");
                var replacement = source.CreateElement("img");
                replacement.SetAttribute("src", href ?? "");
                image.Closest("svg")?.ReplaceWith(replacement);
            }

            var directory = EpubReader.DirectoryOf(sectionPath);
            var resources = new HashSet<string>(StringComparer.Ordinal);
            var sources = new Dictionary<string, string>(StringComparer.Ordinal);
            var sanitizer = CreateSanitizer();
            sanitizer.PostProcessNode += (_, args) =>
            {
                if (args.Node is not IElement element)
                {
                    return;
                }

                // Images only from the package.
                if (element.LocalName == "img")
                {
                    var path = EpubReader.Normalize(directory, element.GetAttribute("src"));
                    if (path is not null && manifest.TryGetValue(path, out var mediaType) && mediaType.StartsWith("image/", StringComparison.Ordinal))
                    {
                        element.SetAttribute("src", ResourceUrl(documentId, path));
                        element.SetAttribute("loading", "lazy");
                        resources.Add(path);
                        sources[ResourceUrl(documentId, path)] = path;
                    }
                    else
                    {
                        element.Remove();
                    }
                }

                // Links stay inside the reader or open the web.
                if (element.LocalName == "a" && element.GetAttribute("href") is { } href)
                {
                    if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        element.SetAttribute("rel", "noopener noreferrer");
                    }
                    else if (href.StartsWith('#'))
                    {
                        element.SetAttribute("href", href);
                    }
                    else if (EpubReader.Normalize(directory, href) is { } path && sections.TryGetValue(path, out var index))
                    {
                        var anchor = EpubReader.Anchor(href);
                        element.SetAttribute("href", "?s=" + index.ToString(CultureInfo.InvariantCulture) + (anchor is null ? "" : "#" + anchor));
                    }
                    else
                    {
                        element.RemoveAttribute("href");
                    }
                }
            };

            var body = source.Body?.InnerHtml ?? "";
            var clean = Parser.ParseDocument("<!doctype html><html><body>" + sanitizer.Sanitize(body) + "</body></html>");

            Figures(clean, sources, imageSize);

            // Numbered blocks are the semantic locator.
            var count = 0;
            foreach (var block in clean.Body!.QuerySelectorAll(string.Join(',', Blocks)))
            {
                block.SetAttribute("data-b", count.ToString(CultureInfo.InvariantCulture));
                count++;
            }

            var characters = clean.Body.TextContent.Count(character => !char.IsWhiteSpace(character));
            return new PreparedSection(clean.Body.InnerHtml, characters, count, resources);
        }

        // Large pictures become figures: the declared kind where the book says so, else by the picture's size; a picture alone in its block takes the block's place.
        private static void Figures(IDocument clean, Dictionary<string, string> sources, Func<string, (int Width, int Height)?>? imageSize)
        {
            // Only a picture or its figure can carry the kind.
            foreach (var marked in clean.Body!.QuerySelectorAll("[data-figure]").ToArray())
            {
                var stand = marked.LocalName == "figure" && marked.GetAttribute("data-figure") == OriginalFigure && int.TryParse(marked.GetAttribute("data-page"), NumberStyles.None, CultureInfo.InvariantCulture, out _);
                if (marked.LocalName is not ("figure" or "img") || marked.LocalName == "figure" && marked.QuerySelector("img") is null && !stand)
                {
                    marked.RemoveAttribute("data-figure");
                }

                if (!stand)
                {
                    marked.RemoveAttribute("data-page");
                }
            }

            foreach (var image in clean.Body.QuerySelectorAll("img").ToArray())
            {
                var figure = image.ParentElement is { LocalName: "figure" } parent && parent.QuerySelectorAll("img").Length == 1 ? parent : null;
                var declared = (figure ?? image).GetAttribute("data-figure");
                var size = image.GetAttribute("src") is { } source && sources.TryGetValue(source, out var path) ? imageSize?.Invoke(path) : null;

                // The topmost wrapper holding only this picture.
                var block = figure ?? image;
                if (figure is null)
                {
                    while (block.ParentElement is { } wrapper && wrapper != clean.Body && wrapper.LocalName is "p" or "div" or "a" or "span" or "center" or "section"
                        && wrapper.QuerySelectorAll("img").Length == 1 && string.IsNullOrWhiteSpace(wrapper.TextContent))
                    {
                        block = wrapper;
                    }
                }

                var alone = figure is not null || block != image;
                var kind = declared is PageFigure or WideFigure ? declared : size is { } known ? FigureKind(known.Width, known.Height, alone) : null;
                image.RemoveAttribute("data-figure");
                if (kind is null)
                {
                    figure?.RemoveAttribute("data-figure");
                    continue;
                }

                if (figure is null && alone)
                {
                    figure = clean.CreateElement("figure");
                    block.ReplaceWith(figure);
                    figure.AppendChild(image);
                }

                (figure ?? image).SetAttribute("data-figure", kind);
            }
        }

        private static HtmlSanitizer CreateSanitizer()
        {
            // Reader typography replaces book styles.
            var sanitizer = new HtmlSanitizer();
            sanitizer.AllowedTags.ExceptWith(["form", "input", "button", "select", "option", "textarea", "label", "fieldset", "legend", "style", "font", "center"]);
            sanitizer.AllowedAttributes.ExceptWith(["style", "class", "align", "bgcolor", "color", "background", "width", "height"]);
            sanitizer.AllowedAttributes.Add("data-b");
            sanitizer.AllowedAttributes.Add("data-figure");
            sanitizer.AllowedAttributes.Add("data-page");
            sanitizer.AllowedCssProperties.Clear();
            sanitizer.AllowedSchemes.Clear();
            sanitizer.AllowedSchemes.UnionWith(["http", "https", "mailto"]);
            sanitizer.AllowedAtRules.Clear();
            sanitizer.KeepChildNodes = true;
            return sanitizer;
        }
    }
}
