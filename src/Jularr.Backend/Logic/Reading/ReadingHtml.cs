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
        public const int AnalyzerVersion = 1;

        private static readonly string[] Blocks = ["p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "blockquote", "pre", "figure", "table", "dt", "dd", "hr"];
        private static readonly HtmlParser Parser = new();

        public static string Text(string html) => string.Join(' ', (Parser.ParseDocument(html).Body?.TextContent ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        public static string ResourceUrl(long documentId, string path) =>
            "/api/v1/reading/documents/" + documentId.ToString(CultureInfo.InvariantCulture) + "/resource?path=" + Uri.EscapeDataString(path);

        public static PreparedSection Prepare(string xhtml, string sectionPath, long documentId, IReadOnlyDictionary<string, int> sections, IReadOnlyDictionary<string, string> manifest)
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

        private static HtmlSanitizer CreateSanitizer()
        {
            // Reader typography replaces book styles.
            var sanitizer = new HtmlSanitizer();
            sanitizer.AllowedTags.ExceptWith(["form", "input", "button", "select", "option", "textarea", "label", "fieldset", "legend", "style", "font", "center"]);
            sanitizer.AllowedAttributes.ExceptWith(["style", "class", "align", "bgcolor", "color", "background", "width", "height"]);
            sanitizer.AllowedAttributes.Add("data-b");
            sanitizer.AllowedCssProperties.Clear();
            sanitizer.AllowedSchemes.Clear();
            sanitizer.AllowedSchemes.UnionWith(["http", "https", "mailto"]);
            sanitizer.AllowedAtRules.Clear();
            sanitizer.KeepChildNodes = true;
            return sanitizer;
        }
    }
}
