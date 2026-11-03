using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Jularr.Backend.Logic.Reading
{
    // A Word document: paragraphs with bold and italic, headings by their style or outline level, tables as rows, embedded pictures, the title from its properties.
    internal static class DocxBook
    {
        private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
        private static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
        private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/package/2006/relationships";
        private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

        public static ConvertedBook Read(Stream file)
        {
            using var archive = BookConversion.OpenZip(file);
            if (archive.Entries.Sum(entry => entry.Length) > EpubReader.MaximumTotalBytes)
            {
                throw new InvalidDataException("The document exceeds the size limits.");
            }

            var document = Xml(archive, "word/document.xml") ?? throw new InvalidDataException("The file is no Word document.");
            var book = new ConvertedBook
            {
                Title = Xml(archive, "docProps/core.xml")?.Descendants(Dc + "title").FirstOrDefault()?.Value.Trim() is { Length: > 0 } title ? title : null
            };

            // Heading styles by their id: "heading 1" (any language's built-in name keeps the English one), a title, or an outline level.
            var headings = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var style in Xml(archive, "word/styles.xml")?.Descendants(W + "style") ?? [])
            {
                var id = (string?)style.Attribute(W + "styleId");
                var name = ((string?)style.Element(W + "name")?.Attribute(W + "val") ?? "").ToLowerInvariant();
                var outline = Number(style.Element(W + "pPr")?.Element(W + "outlineLvl"));
                var level = name.StartsWith("heading ", StringComparison.Ordinal) && int.TryParse(name[8..], out var number) ? number : name == "title" ? 1 : outline is { } value ? value + 1 : 0;
                if (id is not null && level is >= 1 and <= 6) headings[id] = level;
            }

            var links = Xml(archive, "word/_rels/document.xml.rels")?.Root?.Elements(Relationships + "Relationship")
                .Where(link => (string?)link.Attribute("Id") is not null && (string?)link.Attribute("TargetMode") != "External")
                .GroupBy(link => (string)link.Attribute("Id")!).ToDictionary(group => group.Key, group => (string?)group.First().Attribute("Target"), StringComparer.Ordinal) ?? [];

            string? Picture(string? id)
            {
                if (id is null || links.GetValueOrDefault(id) is not { } target || EpubReader.Normalize("word/", target) is not { } path || archive.GetEntry(path) is not { } entry)
                {
                    return null;
                }

                return book.Image(path, () => entry.Length > BookConversion.MaximumPictureBytes ? [] : BookConversion.ReadEntry(entry, BookConversion.MaximumPictureBytes));
            }

            // A paragraph's runs: text, tabs and breaks with their emphasis, pictures where they stand.
            string Runs(XElement paragraph)
            {
                var html = new StringBuilder();
                foreach (var run in paragraph.Descendants(W + "r"))
                {
                    var properties = run.Element(W + "rPr");
                    var bold = IsOn(properties?.Element(W + "b"));
                    var italic = IsOn(properties?.Element(W + "i"));
                    var text = new StringBuilder();
                    foreach (var part in run.Elements())
                    {
                        if (part.Name == W + "t") text.Append(BookConversion.Escape(part.Value));
                        else if (part.Name == W + "tab") text.Append(' ');
                        else if (part.Name == W + "br" && (string?)part.Attribute(W + "type") is null or "textWrapping") text.Append("<br/>");
                        else if (part.Name == W + "drawing" || part.Name == W + "pict")
                        {
                            var id = (string?)part.Descendants(A + "blip").FirstOrDefault()?.Attribute(R + "embed") ?? (string?)part.Descendants(V + "imagedata").FirstOrDefault()?.Attribute(R + "id");
                            if (Picture(id) is { } path) text.Append("<img src=\"").Append(path).Append("\" alt=\"\"/>");
                        }
                    }

                    if (text.Length == 0) continue;
                    html.Append(bold ? "<strong>" : "").Append(italic ? "<em>" : "").Append(text).Append(italic ? "</em>" : "").Append(bold ? "</strong>" : "");
                }

                return html.ToString();
            }

            var body = document.Root?.Element(W + "body") ?? throw new InvalidDataException("The Word document has no body.");
            // Content controls hold paragraphs too.
            foreach (var element in body.Elements().SelectMany(element => element.Name == W + "sdt" ? element.Element(W + "sdtContent")?.Elements() ?? [] : [element]))
            {
                if (element.Name == W + "tbl")
                {
                    var rows = element.Elements(W + "tr").Select(row => "<tr>" + string.Concat(row.Elements(W + "tc")
                        .Select(cell => "<td>" + string.Join("<br/>", cell.Elements(W + "p").Select(Runs).Where(text => text.Length > 0)) + "</td>")) + "</tr>");
                    book.Add("<table>" + string.Concat(rows) + "</table>");
                    continue;
                }

                if (element.Name != W + "p")
                {
                    continue;
                }

                var html = Runs(element);
                var plain = string.Concat(element.Descendants(W + "t").Select(text => text.Value)).Trim();
                if (html.Length == 0)
                {
                    continue;
                }

                var paragraphProperties = element.Element(W + "pPr");
                var styleId = (string?)paragraphProperties?.Element(W + "pStyle")?.Attribute(W + "val");
                var outline = Number(paragraphProperties?.Element(W + "outlineLvl"));
                var level = outline is { } value and < 6 ? value + 1 : styleId is not null && headings.TryGetValue(styleId, out var styled) ? styled : 0;
                if (level is >= 1 and <= 6 && plain.Length is > 0 and <= 300)
                {
                    if (level <= 3) book.Chapter(string.Join(' ', plain.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), level - 1);
                    book.Add("<h" + level + ">" + html + "</h" + level + ">");
                }
                else
                {
                    book.Add((paragraphProperties?.Element(W + "numPr") is not null ? "<p>• " : "<p>") + html + "</p>");
                }
            }

            return book;
        }

        private static int? Number(XElement? property) => int.TryParse((string?)property?.Attribute(W + "val"), out var number) ? number : null;

        // A property element is on unless its value says off.
        private static bool IsOn(XElement? property) => property is not null && (string?)property.Attribute(W + "val") is null or "1" or "true" or "on";

        private static XDocument? Xml(ZipArchive archive, string path)
        {
            if (archive.GetEntry(path) is not { } entry)
            {
                return null;
            }

            // No DTDs, no external entities, bounded in size and depth.
            return EpubReader.LoadXml(BookConversion.ReadEntry(entry, EpubReader.MaximumEntryBytes), EpubReader.MaximumEntryBytes);
        }
    }
}
