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

        // The page of the PDF a block of a converted PDF starts on: the anchor between its pages and the reading text.
        public const string PdfPageAttribute = "data-pdf-page";
        public const string PdfEndAttribute = "data-pdf-end";

        private static readonly string[] Blocks = ["p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "blockquote", "pre", "figure", "table", "dt", "dd", "hr"];
        private static readonly HtmlParser Parser = new();

        public static string Text(string html) => string.Join(' ', (Parser.ParseDocument(html).Body?.TextContent ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        // Ruby annotations (<rt>/<rp>) belong to the display, not to the text sent to machine translation.
        // Keep the prepared HTML intact for reading and only extract the base text here.
        public static string TranslationText(IElement block) => TranslationNode(block).Trim();

        private static string TranslationNode(INode node)
        {
            if (node is IElement element && element.LocalName is "rt" or "rp") return "";
            if (node.NodeType == NodeType.Text) return node.TextContent;
            return string.Concat(node.ChildNodes.Select(TranslationNode));
        }

        // A small amount of preceding source text helps the model keep names, speaker and point of view.
        // Context must never be included in the translated output or alter block positions.
        public static string TranslationContext(IReadOnlyList<IElement> blocks, int index)
        {
            var previous = new List<string>();
            for (var i = Math.Max(0, index - 3); i < index; i++)
            {
                if (blocks[i].QuerySelector("[data-b]") is not null) continue;
                var value = TranslationText(blocks[i]);
                if (value.Length > 0) previous.Add(value);
            }
            var context = string.Join("\n", previous);
            return context.Length > 800 ? context[^800..] : context;
        }

        // The page of a converted PDF the first block holding the words starts on.
        public static int? PageOfText(string html, string query)
        {
            foreach (var block in Parser.ParseDocument(html).QuerySelectorAll("[" + PdfPageAttribute + "]"))
            {
                if (block.TextContent.Contains(query, StringComparison.OrdinalIgnoreCase) && PageOf(block) is { } page)
                {
                    return page;
                }
            }

            return null;
        }

        // The page a numbered block starts on, from its own anchor or the nearest before it; a stand-in for an original page names its page too.
        public static int? PageOfBlock(string html, int block)
        {
            int? page = null;
            foreach (var element in Parser.ParseDocument(html).QuerySelectorAll("[data-b]"))
            {
                page = PageOf(element) ?? page;
                if (element.GetAttribute("data-b") == block.ToString(CultureInfo.InvariantCulture))
                {
                    return page;
                }
            }

            return null;
        }

        // The first numbered block that ends on this page or later: the one the page starts in, or the first to start after it.
        public static int? BlockOfPage(string html, int page)
        {
            foreach (var element in Parser.ParseDocument(html).QuerySelectorAll("[data-b]"))
            {
                var end = int.TryParse(element.GetAttribute(PdfEndAttribute), NumberStyles.None, CultureInfo.InvariantCulture, out var ended) ? ended : PageOf(element);
                if (end is { } found && found >= page && int.TryParse(element.GetAttribute("data-b"), NumberStyles.None, CultureInfo.InvariantCulture, out var block))
                {
                    return block;
                }
            }

            return null;
        }

        private static int? PageOf(IElement element) => int.TryParse(element.GetAttribute(PdfPageAttribute) ?? element.GetAttribute("data-page"), NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page >= 1 ? page : null;

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

            // The page of a PDF a block starts on is a number or nothing.
            foreach (var marked in clean.Body!.QuerySelectorAll("[" + PdfPageAttribute + "],[" + PdfEndAttribute + "]").ToArray())
            {
                foreach (var attribute in new[] { PdfPageAttribute, PdfEndAttribute })
                {
                    if (marked.HasAttribute(attribute) && (!int.TryParse(marked.GetAttribute(attribute), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1))
                    {
                        marked.RemoveAttribute(attribute);
                    }
                }
            }

            // Numbered blocks are the semantic locator. A book whose lines are divs (a converted PDF, a plain export) has its text in leaf divs: they are blocks too,
            // else that text is never translated and its chapter heading never found.
            var count = 0;
            foreach (var block in clean.Body!.QuerySelectorAll("*").Where(element => Blocks.Contains(element.LocalName) || IsTextDiv(element)).ToArray())
            {
                block.SetAttribute("data-b", count.ToString(CultureInfo.InvariantCulture));
                count++;
            }

            var characters = clean.Body.TextContent.Count(character => !char.IsWhiteSpace(character));
            return new PreparedSection(clean.Body.InnerHtml, characters, count, resources);
        }

        // A div that holds text and no other block or div: the smallest unit of text a book without paragraphs has.
        private static bool IsTextDiv(AngleSharp.Dom.IElement element) =>
            element.LocalName == "div" && !string.IsNullOrWhiteSpace(element.TextContent)
            && !element.QuerySelectorAll("*").Any(child => child.LocalName == "div" || Blocks.Contains(child.LocalName));

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
                    if (block.GetAttribute(PdfPageAttribute) is { } anchored)
                    {
                        figure.SetAttribute(PdfPageAttribute, anchored);
                    }

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
            sanitizer.AllowedAttributes.Add(PdfPageAttribute);
            sanitizer.AllowedAttributes.Add(PdfEndAttribute);
            sanitizer.AllowedCssProperties.Clear();
            sanitizer.AllowedSchemes.Clear();
            sanitizer.AllowedSchemes.UnionWith(["http", "https", "mailto"]);
            sanitizer.AllowedAtRules.Clear();
            sanitizer.KeepChildNodes = true;
            return sanitizer;
        }
    }
}
