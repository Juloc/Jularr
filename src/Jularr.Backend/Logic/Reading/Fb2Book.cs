using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Jularr.Backend.Logic.Reading
{
    // A FictionBook 2 (also zipped): its sections as chapters, notes as their own chapter, pictures from its binaries.
    internal static class Fb2Book
    {
        // Deeper nesting is read as plain text.
        private const int MaximumDepth = 64;

        static Fb2Book()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public static ConvertedBook ReadZip(Stream file)
        {
            using var archive = BookConversion.OpenZip(file);
            var entry = archive.Entries.FirstOrDefault(entry => entry.FullName.EndsWith(".fb2", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("The archive holds no FictionBook.");
            return Read(BookConversion.ReadEntry(entry, BookConversion.MaximumTextBytes));
        }

        public static ConvertedBook Read(byte[] bytes)
        {
            // No DTDs, no external entities, bounded depth; the declared encoding (often windows-1251) is honoured.
            var document = EpubReader.LoadXml(bytes, BookConversion.MaximumTextBytes);

            if (document.Root is not { Name.LocalName: "FictionBook" } root)
            {
                throw new InvalidDataException("The file is no FictionBook.");
            }

            var book = new ConvertedBook();
            var binaries = new Dictionary<string, XElement>(StringComparer.Ordinal);
            foreach (var binary in Children(root, "binary"))
            {
                if ((string?)binary.Attribute("id") is { Length: > 0 } id) binaries.TryAdd(id, binary);
            }

            var reading = new Reading(book, binaries);
            var info = Children(root, "description").SelectMany(description => Children(description, "title-info")).FirstOrDefault();
            book.Title = Clean(info is null ? null : Children(info, "book-title").FirstOrDefault()?.Value);
            book.Language = Clean(info is null ? null : Children(info, "lang").FirstOrDefault()?.Value) is { Length: <= 35 } language ? language : null;

            // The cover and the main text's title open the book; then every body: the main text, then notes and comments.
            var first = true;
            var cover = info is null ? null : Children(info, "coverpage").SelectMany(page => Children(page, "image")).FirstOrDefault();
            foreach (var body in Children(root, "body"))
            {
                var notes = (string?)body.Attribute("name") is "notes" or "comments";
                var title = Children(body, "title").FirstOrDefault();
                if (notes)
                {
                    book.Chapter(TitleText(title) ?? "Notes", 0);
                }
                else if (first && (title is not null || cover is not null))
                {
                    book.Chapter(TitleText(title), 0);
                    if (cover is not null && reading.Picture(cover) is { } picture) book.Add(picture);
                }
                else if (title is not null)
                {
                    book.Chapter(TitleText(title), 0);
                }

                first = false;

                if (title is not null)
                {
                    book.Add("<h1>" + reading.Lines(title) + "</h1>");
                }

                reading.Container(body, 0, !notes);
            }

            return book;
        }

        private static IEnumerable<XElement> Children(XElement parent, string name) => parent.Elements().Where(element => element.Name.LocalName == name);

        private static string? TitleText(XElement? title) => title is null ? null : Clean(string.Join(" ", title.Elements().Select(line => line.Value)));

        private static string? Clean(string? value)
        {
            var text = string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return text.Length == 0 ? null : text;
        }

        private static string? Href(XElement element) => element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "href")?.Value;

        private sealed class Reading(ConvertedBook book, IReadOnlyDictionary<string, XElement> binaries)
        {
            // A body or section: its sections start chapters (in the main text) or stay headings (in notes); everything else is a block.
            public void Container(XElement container, int depth, bool chapters)
            {
                foreach (var child in container.Elements())
                {
                    if (child.Name.LocalName == "title" && container.Name.LocalName == "body")
                    {
                        continue;
                    }

                    if (child.Name.LocalName == "section")
                    {
                        Section(child, depth, chapters);
                    }
                    else
                    {
                        Block(child, depth);
                    }
                }
            }

            private void Section(XElement section, int depth, bool chapters)
            {
                var title = Children(section, "title").FirstOrDefault();
                if (chapters)
                {
                    book.Chapter(TitleText(title), depth);
                }

                if ((string?)section.Attribute("id") is { Length: > 0 } id)
                {
                    book.Anchor(id);
                }

                if (title is not null)
                {
                    var level = Math.Min(depth + 2, 6);
                    book.Add("<h" + level + ">" + Lines(title) + "</h" + level + ">");
                }

                foreach (var child in section.Elements().Where(child => child.Name.LocalName != "title"))
                {
                    if (child.Name.LocalName == "section" && depth < MaximumDepth)
                    {
                        Section(child, depth + 1, chapters);
                    }
                    else
                    {
                        Block(child, depth);
                    }
                }
            }

            private void Block(XElement element, int depth)
            {
                if ((string?)element.Attribute("id") is { Length: > 0 } id)
                {
                    book.Anchor(id);
                }

                switch (element.Name.LocalName)
                {
                    case "empty-line":
                        break;
                    case "image":
                        if (Picture(element) is { } picture) book.Add(picture);
                        break;
                    case "subtitle":
                        book.Add("<p><strong>" + Inline(element, 0) + "</strong></p>");
                        break;
                    case "text-author":
                        book.Add("<p><em>" + Inline(element, 0) + "</em></p>");
                        break;
                    case "poem" or "epigraph" or "cite" or "annotation" or "stanza":
                        book.Add("<blockquote>" + Quoted(element, depth) + "</blockquote>");
                        break;
                    case "table":
                        book.Add("<table>" + string.Concat(Children(element, "tr").Select(row => "<tr>" + string.Concat(row.Elements()
                            .Select(cell => (cell.Name.LocalName == "th" ? "<th>" : "<td>") + Inline(cell, 0) + (cell.Name.LocalName == "th" ? "</th>" : "</td>"))) + "</tr>")) + "</table>");
                        break;
                    case "section":
                        book.Add("<p>" + BookConversion.Escape(Clean(element.Value) ?? "") + "</p>");
                        break;
                    default:
                        if (Clean(element.Value) is not null || element.Descendants().Any(child => child.Name.LocalName == "image")) book.Add("<p>" + Inline(element, 0) + "</p>");
                        break;
                }
            }

            // A poem's or a quote's lines and paragraphs inside its quote block.
            private string Quoted(XElement element, int depth)
            {
                var html = new StringBuilder();
                foreach (var child in element.Elements())
                {
                    html.Append(child.Name.LocalName switch
                    {
                        "title" => "<p><strong>" + Lines(child) + "</strong></p>",
                        "stanza" when depth < MaximumDepth => Quoted(child, depth + 1),
                        "v" or "p" => "<p>" + Inline(child, 0) + "</p>",
                        "text-author" => "<p><em>" + Inline(child, 0) + "</em></p>",
                        "empty-line" => "",
                        _ => Clean(child.Value) is { } text ? "<p>" + BookConversion.Escape(text) + "</p>" : ""
                    });
                }

                return html.ToString();
            }

            // A title's paragraphs as lines of one heading.
            public string Lines(XElement title) => string.Join("<br/>", title.Elements().Where(line => line.Name.LocalName == "p").Select(line => Inline(line, 0)));

            public string? Picture(XElement image) => Image(image) is { } path ? ConvertedBook.Picture(path, (string?)image.Attribute("alt")) : null;

            private string? Image(XElement image)
            {
                if (Href(image) is not ['#', .. var id] || !binaries.TryGetValue(id, out var binary))
                {
                    return null;
                }

                return book.Image(id, () =>
                {
                    try
                    {
                        return Convert.FromBase64String(binary.Value);
                    }
                    catch (FormatException)
                    {
                        return [];
                    }
                });
            }

            private string Inline(XElement element, int depth)
            {
                if (depth > MaximumDepth)
                {
                    return BookConversion.Escape(element.Value);
                }

                var html = new StringBuilder();
                foreach (var node in element.Nodes())
                {
                    if (node is XText text)
                    {
                        html.Append(BookConversion.Escape(text.Value));
                        continue;
                    }

                    if (node is not XElement child)
                    {
                        continue;
                    }

                    var inner = Inline(child, depth + 1);
                    html.Append(child.Name.LocalName switch
                    {
                        "strong" => "<strong>" + inner + "</strong>",
                        "emphasis" => "<em>" + inner + "</em>",
                        "strikethrough" => "<s>" + inner + "</s>",
                        "sub" => "<sub>" + inner + "</sub>",
                        "sup" => "<sup>" + inner + "</sup>",
                        "code" => "<code>" + inner + "</code>",
                        "a" => Href(child) is { } href && (href.StartsWith('#') || href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                            ? "<a href=\"" + BookConversion.Escape(href) + "\">" + ((string?)child.Attribute("type") == "note" ? "<sup>" + inner + "</sup>" : inner) + "</a>"
                            : inner,
                        "image" => Image(child) is { } path ? "<img src=\"" + path + "\" alt=\"\"/>" : "",
                        _ => inner
                    });
                }

                return html.ToString();
            }
        }
    }
}
