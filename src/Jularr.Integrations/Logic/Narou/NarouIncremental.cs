using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Jularr.Integrations.Logic.Narou
{
    internal sealed partial class NarouClient
    {
        private const int MaximumExistingEpubBytes = 64 * 1024 * 1024;
        private static readonly Regex OwnChapter = new(@"^OEBPS/chapters/(?<number>\d{4})\.xhtml$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex OwnTocLink = new(
            @"<a href=""chapters/(?<number>\d{4})\.xhtml"">(?<title>.*?)</a>",
            RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

        // Append newly published chapters without touching existing chapter bytes/identifiers.
        // Stable EPUB spine, section ordering and asset identity are required to keep reader
        // positions, annotations, translations and bookmarks anchored to old chapters.
        // A renumbered or edited old TOC must be reviewed, never silently replaced.
        public async Task<byte[]?> AppendNewChaptersAsync(string ncode, Stream previous,
            long? maxOutputBytes, CancellationToken ct)
        {
            if (!IsNcode(ncode)) throw new NarouException("Invalid Narō ncode.");
            await using var previousBytes = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while ((read = await previous.ReadAsync(buffer, ct)) > 0)
            {
                if (previousBytes.Length + read > MaximumExistingEpubBytes)
                    throw new NarouException("The existing Narō EPUB exceeds the supported size limit.");
                previousBytes.Write(buffer, 0, read);
            }
            previousBytes.Position = 0;

            using var old = new ZipArchive(previousBytes, ZipArchiveMode.Read, leaveOpen: true);
            var opf = await ReadEntryAsync(old, "OEBPS/content.opf", 2 * 1024 * 1024, ct);
            var id = "urn:syosetu:" + ncode.ToLowerInvariant();
            if (!opf.Contains("<dc:identifier id=\"uid\">" + id + "</dc:identifier>", StringComparison.Ordinal))
                throw new NarouException("The existing EPUB is not this Narō work.");
            var originalToc = await ReadEntryAsync(old, "OEBPS/toc.xhtml", 2 * 1024 * 1024, ct);
            var tocLinks = OwnTocLink.Matches(originalToc).Cast<Match>().ToArray();
            var chapterEntries = old.Entries.Where(entry => OwnChapter.IsMatch(entry.FullName))
                .OrderBy(entry => entry.FullName, StringComparer.Ordinal).ToArray();
            var count = chapterEntries.Length;
            if (count == 0 || count != tocLinks.Length || count > MaximumChapters
                || chapterEntries.Where((entry, index) =>
                    entry.FullName != "OEBPS/chapters/" + (index + 1).ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + ".xhtml").Any())
                throw new NarouException("The existing EPUB has an unsupported chapter layout.");

            var allowed = old.Entries.All(entry => entry.FullName is "mimetype" or
                "META-INF/container.xml" or "OEBPS/toc.xhtml" or "OEBPS/content.opf"
                || OwnChapter.IsMatch(entry.FullName));
            if (!allowed)
                throw new NarouException("The EPUB contains files not produced by the Narō adapter.");

            var work = await ReadWorkAsync(ncode, ct);
            if (work.Chapters.Count < count)
                throw new NarouException("Narō removed chapters; manual review required.");
            for (var index = 0; index < count; index++)
            {
                var match = tocLinks[index];
                if (match.Groups["number"].Value != (index + 1).ToString("D4", System.Globalization.CultureInfo.InvariantCulture)
                    || match.Groups["title"].Value != Esc(work.Chapters[index].Title))
                    throw new NarouException("Narō changed old chapters; manual review required.");
            }
            if (work.Chapters.Count == count)
                return null;

            await using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                Write(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
                var container = await ReadEntryAsync(old, "META-INF/container.xml", 4096, ct);
                Write(zip, "META-INF/container.xml", container);
                foreach (var entry in chapterEntries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.Length > MaximumPageBytes * 3L)
                        throw new NarouException("Existing chapter too large.");
                    var copy = zip.CreateEntry(entry.FullName, CompressionLevel.Fastest);
                    await using var source = entry.Open();
                    await using var destination = copy.Open();
                    await source.CopyToAsync(destination, ct);
                }

                var totalNewTextBytes = 0;
                for (var index = count; index < work.Chapters.Count; index++)
                {
                    var chapter = work.Chapters[index];
                    var text = await ReadChapterTextAsync(work.Ncode, chapter.Number, ct);
                    totalNewTextBytes = checked(totalNewTextBytes + Encoding.UTF8.GetByteCount(text));
                    if (totalNewTextBytes > MaximumTextBytes)
                        throw new NarouException("New chapters exceed the supported text limit.");
                    var path = "OEBPS/chapters/" + (index + 1).ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + ".xhtml";
                    var paragraphs = string.Join("", text.Split('\n')
                        .Where(line => !string.IsNullOrWhiteSpace(line))
                        .Select(line => "<p>" + Esc(line) + "</p>"));
                    Write(zip, path, "<?xml version=\"1.0\" encoding=\"utf-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\" xml:lang=\"ja\"><head><title>"
                        + Esc(chapter.Title) + "</title></head><body><h1>" + Esc(chapter.Title)
                        + "</h1>" + paragraphs + "</body></html>");
                }

                var manifest = new StringBuilder("<item id=\"toc\" href=\"toc.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>");
                var spine = new StringBuilder();
                var toc = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" xml:lang=\"ja\"><head><title>Contents</title></head><body><nav epub:type=\"toc\"><ol>");
                for (var index = 0; index < work.Chapters.Count; index++)
                {
                    var number = (index + 1).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
                    manifest.Append("<item id=\"ch").Append(number).Append("\" href=\"chapters/").Append(number)
                        .Append(".xhtml\" media-type=\"application/xhtml+xml\"/>");
                    spine.Append("<itemref idref=\"ch").Append(number).Append("\"/>");
                    toc.Append("<li><a href=\"chapters/").Append(number).Append(".xhtml\">")
                        .Append(Esc(work.Chapters[index].Title)).Append("</a></li>");
                }
                toc.Append("</ol></nav></body></html>");
                Write(zip, "OEBPS/toc.xhtml", toc.ToString());
                Write(zip, "OEBPS/content.opf",
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"uid\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"uid\">"
                    + id + "</dc:identifier><dc:title>" + Esc(work.Title + " (Web Novel)")
                    + "</dc:title><dc:language>ja</dc:language><dc:creator>" + Esc(work.Author)
                    + "</dc:creator></metadata><manifest>" + manifest + "</manifest><spine>"
                    + spine + "</spine></package>");
            }
            if (output.Length > MaximumExistingEpubBytes
                || maxOutputBytes is > 0 && output.Length > maxOutputBytes)
                throw new NarouException("Updated EPUB exceeds the allowed size.");
            return output.ToArray();
        }

        private async Task<string> ReadChapterTextAsync(string ncode, int number, CancellationToken ct)
        {
            var html = await GetPageAsync(number == 0 ? WorkUri(ncode)
                : new Uri(WorkUri(ncode), number + "/"), ct);
            var body = Body.Match(html);
            if (!body.Success)
                throw new NarouException("Narō returned no chapter content.");
            var text = Plain(body.Groups["value"].Value);
            if (text.Length == 0)
                throw new NarouException("Narō returned an empty chapter.");
            return text;
        }

        private static async Task<string> ReadEntryAsync(ZipArchive zip, string name, int maximum, CancellationToken ct)
        {
            var entry = zip.GetEntry(name) ?? throw new NarouException("EPUB structure is incomplete.");
            if (entry.Length > maximum)
                throw new NarouException("EPUB entry is too large.");
            await using var input = entry.Open();
            using var output = new MemoryStream();
            var bytes = new byte[8192];
            int read;
            while ((read = await input.ReadAsync(bytes, ct)) > 0)
            {
                if (output.Length + read > maximum)
                    throw new NarouException("EPUB entry is too large.");
                output.Write(bytes, 0, read);
            }
            return Encoding.UTF8.GetString(output.ToArray());
        }
    }
}
