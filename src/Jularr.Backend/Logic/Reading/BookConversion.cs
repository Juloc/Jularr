using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Jularr.Backend.Logic.Library;

namespace Jularr.Backend.Logic.Reading
{
    // A book whose own format the reader does not read (FB2, TXT, RTF, DOCX, MOBI, AZW, AZW3) opens through a reading copy: an EPUB made in memory
    // and prepared like any EPUB. The file in the library is never changed. Malformed or oversized files raise InvalidDataException.
    internal static partial class BookConversion
    {
        public const long MaximumSourceBytes = 256L * 1024 * 1024;
        public const long MaximumTextBytes = 64L * 1024 * 1024;
        public const int MaximumImages = 5000;
        public const long MaximumImageBytes = 256L * 1024 * 1024;

        // A section of the copy holds about this much HTML; a longer chapter continues in the next one.
        private const int SectionCharacters = 150_000;

        [GeneratedRegex("href=\"#([^\"]{1,256})\"")]
        private static partial Regex LocalLink();

        public static bool IsConverted(string relativePath) => LibraryFileTypes.Extension(relativePath) is ".fb2" or ".fb2.zip" or ".txt" or ".rtf" or ".docx" or ".mobi" or ".azw" or ".azw3";

        // The reading copy of a book file as EPUB bytes; a protected Kindle book raises BookProtectedException.
        public static byte[] ToEpub(string relativePath, Stream file)
        {
            if (file.CanSeek && file.Length > MaximumSourceBytes)
            {
                throw new InvalidDataException("The book exceeds the size limit.");
            }

            var extension = LibraryFileTypes.Extension(relativePath);
            var book = extension switch
            {
                ".fb2" => Fb2Book.Read(ReadBounded(file, MaximumTextBytes)),
                ".fb2.zip" => Fb2Book.ReadZip(file),
                ".txt" => TextBook.Read(ReadBounded(file, MaximumTextBytes)),
                ".rtf" => RtfBook.Read(ReadBounded(file, MaximumSourceBytes)),
                ".docx" => DocxBook.Read(file),
                ".mobi" or ".azw" or ".azw3" => MobiBook.Read(ReadBounded(file, MaximumSourceBytes)),
                _ => throw new InvalidDataException("The format has no reading copy.")
            };

            if (book.Chapters.All(chapter => chapter.Blocks.Count == 0))
            {
                throw new InvalidDataException("The book holds no text.");
            }

            return Write(book);
        }

        // The whole stream, at most limit bytes.
        public static byte[] ReadBounded(Stream stream, long limit)
        {
            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (memory.Length + read > limit)
                {
                    throw new InvalidDataException("The book exceeds the size limit.");
                }

                memory.Write(buffer, 0, read);
            }

            return memory.ToArray();
        }

        // An entry of a ZIP-based book, at most limit bytes whatever its directory claims.
        public static byte[] ReadEntry(ZipArchiveEntry entry, long limit)
        {
            if (entry.Length > limit)
            {
                throw new InvalidDataException("An entry exceeds the size limit.");
            }

            using var stream = entry.Open();
            return ReadBounded(stream, limit);
        }

        public static ZipArchive OpenZip(Stream file)
        {
            try
            {
                var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
                if (archive.Entries.Count > EpubReader.MaximumEntries)
                {
                    archive.Dispose();
                    throw new InvalidDataException("The archive has too many entries.");
                }

                return archive;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                throw new InvalidDataException("The archive cannot be read.", exception);
            }
        }

        // Text for HTML and XML: markup characters escaped, every other character kept as it is; characters XML forbids leave.
        public static string Escape(string text)
        {
            if (text.AsSpan().IndexOfAny("&<>\"") < 0 && !text.Any(character => character < ' ' && character is not ('\t' or '\n' or '\r')))
            {
                return text;
            }

            var html = new StringBuilder(text.Length + 16);
            foreach (var character in text)
            {
                switch (character)
                {
                    case '&': html.Append("&amp;"); break;
                    case '<': html.Append("&lt;"); break;
                    case '>': html.Append("&gt;"); break;
                    case '"': html.Append("&quot;"); break;
                    case < ' ' and not ('\t' or '\n' or '\r'): break;
                    default: html.Append(character); break;
                }
            }

            return html.ToString();
        }

        // The EPUB: one XHTML file per section, the pictures beside them, navigation from the chapter titles.
        private static byte[] Write(ConvertedBook book)
        {
            // Sections: a chapter starts one; a long chapter continues in the next at a block boundary.
            var sections = new List<(int Chapter, StringBuilder Html)>();
            var blockSection = new Dictionary<(int Chapter, int Block), int>();
            for (var chapter = 0; chapter < book.Chapters.Count; chapter++)
            {
                StringBuilder? current = null;
                var blocks = book.Chapters[chapter].Blocks;
                for (var block = 0; block < blocks.Count; block++)
                {
                    if (current is null || current.Length > 0 && current.Length + blocks[block].Length > SectionCharacters)
                    {
                        current = new StringBuilder();
                        sections.Add((chapter, current));
                    }

                    blockSection[(chapter, block)] = sections.Count - 1;
                    current.Append(blocks[block]).Append('\n');
                }
            }

            // Links to an anchor of another section name its file.
            var anchorFiles = book.Anchors.Where(anchor => blockSection.ContainsKey(anchor.Value)).ToDictionary(anchor => anchor.Key, anchor => blockSection[anchor.Value], StringComparer.Ordinal);
            var chapterFiles = new Dictionary<int, int>();
            for (var index = sections.Count - 1; index >= 0; index--)
            {
                chapterFiles[sections[index].Chapter] = index;
            }

            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                void Entry(string name, string text, CompressionLevel level = CompressionLevel.Fastest)
                {
                    using var writer = new StreamWriter(archive.CreateEntry(name, level).Open(), new UTF8Encoding(false));
                    writer.Write(text);
                }

                Entry("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
                Entry("META-INF/container.xml",
                    """<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>""");

                var manifest = new StringBuilder("""<item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>""");
                var spine = new StringBuilder();
                for (var index = 0; index < sections.Count; index++)
                {
                    var name = SectionFile(index);
                    var html = LocalLink().Replace(sections[index].Html.ToString(), match => anchorFiles.TryGetValue(match.Groups[1].Value, out var target) && target != index
                        ? "href=\"" + SectionFile(target) + "#" + match.Groups[1].Value + "\""
                        : match.Value);
                    Entry(name, "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title></title></head><body>\n" + html + "</body></html>");
                    manifest.Append("<item id=\"s").Append(index.ToString(CultureInfo.InvariantCulture)).Append("\" href=\"").Append(name).Append("\" media-type=\"application/xhtml+xml\"/>");
                    spine.Append("<itemref idref=\"s").Append(index.ToString(CultureInfo.InvariantCulture)).Append("\"/>");
                }

                var number = 0;
                foreach (var (path, image) in book.Images)
                {
                    using (var stream = archive.CreateEntry(path, CompressionLevel.NoCompression).Open())
                    {
                        stream.Write(image.Bytes);
                    }

                    manifest.Append("<item id=\"i").Append((number++).ToString(CultureInfo.InvariantCulture)).Append("\" href=\"").Append(Escape(path)).Append("\" media-type=\"")
                        .Append(image.MediaType).Append("\"/>");
                }

                var metadata = "<dc:title>" + Escape(book.Title ?? "") + "</dc:title>" + (book.Language is { } language ? "<dc:language>" + Escape(language) + "</dc:language>" : "");
                Entry("content.opf", "<?xml version=\"1.0\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">"
                    + metadata + "</metadata><manifest>" + manifest + "</manifest><spine>" + spine + "</spine></package>");
                Entry("nav.xhtml", Navigation(book, chapterFiles));
            }

            return output.ToArray();
        }

        private static string SectionFile(int index) => "s" + index.ToString("D5", CultureInfo.InvariantCulture) + ".xhtml";

        // The chapters with a title as a nested list by their level.
        private static string Navigation(ConvertedBook book, IReadOnlyDictionary<int, int> chapterFiles)
        {
            var html = new StringBuilder("<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><body><nav epub:type=\"toc\"><ol>");
            var depth = 0;
            var first = true;
            for (var index = 0; index < book.Chapters.Count; index++)
            {
                var chapter = book.Chapters[index];
                if (chapter.Title is not { Length: > 0 } title || !chapterFiles.TryGetValue(index, out var file))
                {
                    continue;
                }

                var level = first ? 0 : Math.Clamp(chapter.Level, 0, depth + 1);
                if (!first && level > depth)
                {
                    html.Append("<ol>");
                    depth++;
                }
                else if (!first)
                {
                    html.Append("</li>");
                    for (; depth > level; depth--)
                    {
                        html.Append("</ol></li>");
                    }
                }

                html.Append("<li><a href=\"").Append(SectionFile(file)).Append("\">").Append(Escape(title)).Append("</a>");
                first = false;
            }

            if (!first)
            {
                html.Append("</li>");
                for (; depth > 0; depth--)
                {
                    html.Append("</ol></li>");
                }
            }

            return html.Append("</ol></nav></body></html>").ToString();
        }
    }

    // A Kindle book whose text is encrypted.
    internal sealed class BookProtectedException() : Exception("The book is protected by DRM.");

    // A Kindle book whose text uses a compression the reader does not read.
    internal sealed class BookCompressionException() : Exception("The book's compression is not supported.");

    // The book a converter read: chapters of HTML blocks (paragraphs, headings, pictures), the pictures by their path in the copy, and anchors for links.
    internal sealed class ConvertedBook
    {
        private readonly Dictionary<string, string?> _imageKeys = new(StringComparer.Ordinal);
        private long _characters;
        private long _imageBytes;
        private string? _pendingAnchor;

        public string? Title { get; set; }

        public string? Language { get; set; }

        public List<ConvertedChapter> Chapters { get; } = [];

        public Dictionary<string, ConvertedImage> Images { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, (int Chapter, int Block)> Anchors { get; } = new(StringComparer.Ordinal);

        public ConvertedChapter Chapter(string? title, int level)
        {
            var chapter = new ConvertedChapter(title is { Length: > 500 } ? title[..500] : title, level, []);
            Chapters.Add(chapter);
            return chapter;
        }

        // A block of HTML in the last chapter; a book without a chapter gets one.
        public void Add(string html)
        {
            if (html.Length == 0)
            {
                return;
            }

            _characters += html.Length;
            if (_characters > BookConversion.MaximumTextBytes)
            {
                throw new InvalidDataException("The book exceeds the size limit.");
            }

            // The block carries the id a link may name.
            var close = html.IndexOf('>');
            var end = html.IndexOfAny([' ', '>', '/']);
            if (_pendingAnchor is { } id && html.StartsWith('<') && close > 0 && end > 1 && !html[..close].Contains(" id=", StringComparison.Ordinal))
            {
                html = html[..end] + " id=\"" + BookConversion.Escape(id) + "\"" + html[end..];
            }

            _pendingAnchor = null;
            (Chapters.Count == 0 ? Chapter(null, 0) : Chapters[^1]).Blocks.Add(html);
        }

        // An id the next block of the last chapter carries.
        public void Anchor(string id)
        {
            if (Chapters.Count == 0)
            {
                Chapter(null, 0);
            }

            if (id.Length <= 256 && Anchors.TryAdd(id, (Chapters.Count - 1, Chapters[^1].Blocks.Count)))
            {
                _pendingAnchor ??= id;
            }
        }

        // A picture under the converter's own key (its id or record number); null when its bytes are no picture or the limits are reached. The path is where sections find it.
        public string? Image(string key, byte[] bytes)
        {
            if (_imageKeys.TryGetValue(key, out var known))
            {
                return known;
            }

            string? path = null;
            if (ComicPages.ContentType(bytes) is { } mediaType && Images.Count < BookConversion.MaximumImages && _imageBytes + bytes.Length <= BookConversion.MaximumImageBytes)
            {
                path = "images/" + Images.Count.ToString("D5", CultureInfo.InvariantCulture) + "." + mediaType["image/".Length..];
                _imageBytes += bytes.Length;
                Images[path] = new ConvertedImage(mediaType, bytes);
            }

            _imageKeys[key] = path;
            return path;
        }

        // A picture on its own line.
        public static string Picture(string path, string? alternative = null) => "<p><img src=\"" + path + "\" alt=\"" + BookConversion.Escape(alternative ?? "") + "\"/></p>";
    }

    internal sealed record ConvertedChapter(string? Title, int Level, List<string> Blocks);

    internal sealed record ConvertedImage(string MediaType, byte[] Bytes);
}
