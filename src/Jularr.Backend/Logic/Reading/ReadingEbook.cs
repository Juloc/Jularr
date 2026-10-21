using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Xhtml;
using Jularr.Backend.Data.Logic.Reading;

namespace Jularr.Backend.Logic.Reading
{
    // A prepared book as an EPUB 3 from the reader's cache: its sections, pictures and contents; with a translation its blocks carry the translated text.
    internal static class ReadingEbook
    {
        private static readonly HtmlParser Parser = new();

        public static byte[] Build(ReadingEbookContent book)
        {
            var images = new Dictionary<string, (string Href, string MediaType)>(StringComparer.Ordinal);
            var files = new List<(string Href, byte[] Bytes)>();
            foreach (var image in book.Images)
            {
                var href = "images/" + images.Count.ToString("D5", CultureInfo.InvariantCulture) + Extension(image.MediaType);
                images[ReadingHtml.ResourceUrl(book.DocumentId, image.Path)] = (href, image.MediaType);
                files.Add((href, image.Bytes));
            }

            var language = Escape(book.Language);
            var sections = new List<string>();
            for (var index = 0; index < book.Sections.Count; index++)
            {
                var href = SectionHref(index);
                sections.Add(href);
                files.Add((href, Encoding.UTF8.GetBytes(Page(book.Title, language, Section(book.Sections[index], book.Translations?[index], images)))));
            }

            var toc = book.Toc.Count > 0
                ? book.Toc.Where(entry => entry.Section < sections.Count).Select(entry => (entry.Title, SectionHref(entry.Section) + (entry.Anchor is null ? "" : "#" + entry.Anchor)))
                : sections.Select((href, index) => (book.SectionTitles[index] ?? (index + 1).ToString(CultureInfo.InvariantCulture), href));
            var nav = "<nav xmlns:epub=\"http://www.idpf.org/2007/ops\" epub:type=\"toc\" id=\"toc\"><ol>"
                + string.Concat(toc.Select(entry => "<li><a href=\"" + Escape(entry.Item2) + "\">" + Escape(entry.Item1) + "</a></li>")) + "</ol></nav>";

            var manifest = new StringBuilder("<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/><item id=\"style\" href=\"style.css\" media-type=\"text/css\"/>");
            foreach (var (href, index) in sections.Select((href, index) => (href, index)))
            {
                manifest.Append("<item id=\"s").Append(index.ToString("D5", CultureInfo.InvariantCulture)).Append("\" href=\"").Append(href).Append("\" media-type=\"application/xhtml+xml\"/>");
            }

            foreach (var (href, mediaType) in images.Values)
            {
                manifest.Append("<item id=\"i").Append(href["images/".Length..href.LastIndexOf('.')]).Append("\" href=\"").Append(href).Append("\" media-type=\"").Append(Escape(mediaType)).Append("\"/>");
            }

            var package = "<?xml version=\"1.0\" encoding=\"utf-8\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"id\" xml:lang=\"" + language + "\">"
                + "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"id\">" + Escape(book.Identifier) + "</dc:identifier><dc:title>" + Escape(book.Title)
                + "</dc:title><dc:language>" + language + "</dc:language><meta property=\"dcterms:modified\">" + book.Modified.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                + "</meta>" + (book.TranslatedFrom is { } source ? "<dc:description>Machine-translated from " + Escape(source) + " by Jularr.</dc:description>" : "") + "</metadata>"
                + "<manifest>" + manifest + "</manifest><spine" + (book.IsRightToLeft ? " page-progression-direction=\"rtl\"" : "") + ">"
                + string.Concat(sections.Select((_, index) => "<itemref idref=\"s" + index.ToString("D5", CultureInfo.InvariantCulture) + "\"/>")) + "</spine></package>";

            // The mimetype comes first and uncompressed, as readers expect.
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                Add(archive, "mimetype", Encoding.ASCII.GetBytes("application/epub+zip"), CompressionLevel.NoCompression);
                Add(archive, "META-INF/container.xml", Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">"
                    + "<rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>"), CompressionLevel.Optimal);
                Add(archive, "OEBPS/content.opf", Encoding.UTF8.GetBytes(package), CompressionLevel.Optimal);
                Add(archive, "OEBPS/nav.xhtml", Encoding.UTF8.GetBytes(Page(book.Title, language, nav)), CompressionLevel.Optimal);
                Add(archive, "OEBPS/style.css", Encoding.UTF8.GetBytes(Style), CompressionLevel.Optimal);
                foreach (var (href, bytes) in files)
                {
                    Add(archive, "OEBPS/" + href, bytes, href.StartsWith("images/", StringComparison.Ordinal) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                }
            }

            return output.ToArray();
        }

        // Figures keep their room: a page of its own, or the width of the text; the picture always fits the screen.
        private const string Style = "figure{margin:1em 0;text-align:center}figure[data-figure=page]{margin:0;page-break-before:always;page-break-after:always;break-before:page;break-after:page}"
            + "figure img,img[data-figure]{display:block;max-width:100%;max-height:100vh;height:auto;margin:0 auto}figcaption{font-size:.9em}";

        private static string SectionHref(int index) => "s" + index.ToString("D5", CultureInfo.InvariantCulture) + ".xhtml";

        // Reader links and pictures point into the EPUB; a translated block keeps its element and takes the translated text, like in the reader.
        private static string Section(string html, IReadOnlyList<string>? translation, IReadOnlyDictionary<string, (string Href, string MediaType)> images)
        {
            var document = Parser.ParseDocument("<!doctype html><html><body>" + html + "</body></html>");
            var body = document.Body!;
            if (translation is not null)
            {
                var blocks = body.QuerySelectorAll("[data-b]").ToArray();
                for (var index = 0; index < blocks.Length && index < translation.Count; index++)
                {
                    if (translation[index].Length > 0 && blocks[index].QuerySelector("[data-b]") is null)
                    {
                        blocks[index].TextContent = translation[index];
                    }
                }
            }

            foreach (var image in body.QuerySelectorAll("img").ToArray())
            {
                if (image.GetAttribute("src") is { } src && images.TryGetValue(src, out var target))
                {
                    image.SetAttribute("src", target.Href);
                    image.RemoveAttribute("loading");
                    image.SetAttribute("alt", image.GetAttribute("alt") ?? "");
                }
                else
                {
                    image.Remove();
                }
            }

            // A page only the original shows has no place in a book file.
            foreach (var stand in body.QuerySelectorAll("figure[data-figure=original]").ToArray())
            {
                stand.Remove();
            }

            foreach (var figure in body.QuerySelectorAll("figure[data-figure]"))
            {
                figure.SetAttribute("epub:type", "figure");
            }

            foreach (var link in body.QuerySelectorAll("a[href^='?s=']").ToArray())
            {
                var href = link.GetAttribute("href")!;
                var anchor = href.IndexOf('#', StringComparison.Ordinal);
                link.SetAttribute("href", int.TryParse(anchor < 0 ? href[3..] : href[3..anchor], NumberStyles.None, CultureInfo.InvariantCulture, out var section)
                    ? SectionHref(section) + (anchor < 0 ? "" : href[anchor..])
                    : "#");
            }

            return string.Concat(body.ChildNodes.Select(node => node.ToHtml(XhtmlMarkupFormatter.Instance)));
        }

        private static string Page(string title, string language, string body) =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><!DOCTYPE html><html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" xml:lang=\"" + language + "\" lang=\"" + language
            + "\"><head><meta charset=\"utf-8\"/><title>" + Escape(title) + "</title><link rel=\"stylesheet\" type=\"text/css\" href=\"style.css\"/></head><body>" + body + "</body></html>";

        private static string Extension(string mediaType) => mediaType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            _ => ".bin"
        };

        private static string Escape(string text) => SecurityElement.Escape(text);

        private static void Add(ZipArchive archive, string name, byte[] bytes, CompressionLevel level)
        {
            using var stream = archive.CreateEntry(name, level).Open();
            stream.Write(bytes);
        }
    }

    // Sections are the prepared reader HTML in order; translations, when given, hold one text per block for every section.
    internal sealed record ReadingEbookContent(long DocumentId, string Identifier, string Title, string Language, string? TranslatedFrom, DateTime Modified, bool IsRightToLeft,
        IReadOnlyList<string> Sections, IReadOnlyList<string?> SectionTitles, IReadOnlyList<IReadOnlyList<string>>? Translations, IReadOnlyList<ReadingStructureEntry> Toc,
        IReadOnlyList<ReadingEbookResourceData> Images);
}
