using System.Globalization;
using System.IO.Compression;
using System.Text;
using Jularr.Integrations.Data.Narou;

namespace Jularr.Integrations.Logic.Narou
{
    // A web novel as an EPUB 3 in Japanese: one section per chapter in Narō's order and a table of contents.
    // The book names its ncode, so it is the web novel and never a published edition.
    internal sealed class NarouEpub : IDisposable
    {
        private static readonly UTF8Encoding Utf8 = new(false);
        private readonly ZipArchive _zip;
        private readonly string _ncode;
        private readonly string _title;
        private readonly string _writer;
        private readonly string? _story;
        private readonly List<(string Id, string Title)> _chapters = [];

        public NarouEpub(Stream output, string ncode, string title, string writer, string? story)
        {
            _zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            _ncode = ncode;
            _title = title;
            _writer = writer;
            _story = story;
            Write("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Write("META-INF/container.xml", """
                <?xml version="1.0" encoding="utf-8"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
                """);
        }

        public void AddChapter(NarouChapter chapter)
        {
            var id = "c" + (_chapters.Count + 1).ToString("D5", CultureInfo.InvariantCulture);
            _chapters.Add((id, chapter.Title));
            Write("OEBPS/" + id + ".xhtml", Document(chapter.Title, "<section epub:type=\"chapter\"><h2>" + Escape(chapter.Title) + "</h2>" + chapter.Body + "</section>"));
        }

        public void Dispose() => _zip.Dispose();

        // The table of contents and the package close the book.
        public void Complete()
        {
            var toc = new StringBuilder("<nav epub:type=\"toc\"><ol>");
            var manifest = new StringBuilder("<item id=\"toc\" href=\"toc.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>");
            var spine = new StringBuilder();
            foreach (var (id, title) in _chapters)
            {
                toc.Append("<li><a href=\"").Append(id).Append(".xhtml\">").Append(Escape(title)).Append("</a></li>");
                manifest.Append("<item id=\"").Append(id).Append("\" href=\"").Append(id).Append(".xhtml\" media-type=\"application/xhtml+xml\"/>");
                spine.Append("<itemref idref=\"").Append(id).Append("\"/>");
            }

            Write("OEBPS/toc.xhtml", Document(_title, toc.Append("</ol></nav>").ToString()));
            var modified = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            Write("OEBPS/content.opf",
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"uid\" xml:lang=\"ja\">"
                + "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"uid\">urn:syosetu:" + Escape(_ncode) + "</dc:identifier>"
                + "<dc:title>" + Escape(_title) + "</dc:title>" + (_writer.Length > 0 ? "<dc:creator>" + Escape(_writer) + "</dc:creator>" : "") + "<dc:language>ja</dc:language>"
                + "<dc:source>" + Escape(NarouClient.NovelUri(_ncode).ToString()) + "</dc:source>" + (_story is { Length: > 0 } ? "<dc:description>" + Escape(_story) + "</dc:description>" : "")
                + "<meta property=\"dcterms:modified\">" + modified + "</meta></metadata><manifest>" + manifest + "</manifest><spine>" + spine + "</spine></package>");
        }

        // Text for XHTML: markup characters escaped, characters XML does not allow left out.
        public static string Escape(string text)
        {
            var escaped = new StringBuilder(text.Length);
            foreach (var character in text)
            {
                switch (character)
                {
                    case '&': escaped.Append("&amp;"); break;
                    case '<': escaped.Append("&lt;"); break;
                    case '>': escaped.Append("&gt;"); break;
                    case '"': escaped.Append("&quot;"); break;
                    case < ' ' and not ('\t' or '\n'): break;
                    case '￾' or '￿': break;
                    default: escaped.Append(character); break;
                }
            }

            return escaped.ToString();
        }

        private static string Document(string title, string body) =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><!DOCTYPE html><html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" xml:lang=\"ja\" lang=\"ja\">"
            + "<head><meta charset=\"utf-8\"/><title>" + Escape(title) + "</title></head><body>" + body + "</body></html>";

        private void Write(string path, string content, CompressionLevel level = CompressionLevel.Optimal)
        {
            using var writer = new StreamWriter(_zip.CreateEntry(path, level).Open(), Utf8);
            writer.Write(content);
        }
    }
}
