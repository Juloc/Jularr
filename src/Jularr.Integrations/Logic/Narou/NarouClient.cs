using System.IO.Compression;
using System.Net;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace Jularr.Integrations.Logic.Narou
{
    // Narō's public ncode works are WEB NOVELS. Never treat this source as an edition of
    // a published Light Novel without an explicit source identity on the requested Work.
    internal sealed class NarouClient(HttpClient http)
    {
        private const int MaximumIndexPages = 50;
        private const int MaximumChapters = 3000;
        private const int MaximumPageBytes = 2 * 1024 * 1024;
        private const int MaximumTextBytes = 64 * 1024 * 1024;
        private static readonly Regex Ncode = new(@"^n[0-9a-z]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex ChapterLink = new(@"<a\b[^>]*href\s*=\s*[""']/(?:novel/)?(?<key>n[0-9a-z]+)/(?<num>\d+)/?[""'][^>]*>(?<name>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex NextPage = new(@"href\s*=\s*[""'][^""']*[?&](?:amp;)?p=(?<page>\d+)[^""']*[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Body = new(@"<div\b(?=[^>]*(?:id\s*=\s*[""']novel_honbun[""']|class\s*=\s*[""'][^""']*p-novel__body[^""']*[""']))[^>]*>(?<value>.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex Title = new(@"<h1\b[^>]*class\s*=\s*[""'][^""']*p-novel__title[^""']*[""'][^>]*>(?<value>.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex Author = new(@"<[^>]*class\s*=\s*[""'][^""']*p-novel__author[^""']*[""'][^>]*>(?<value>.*?)</[^>]+>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex Rubies = new(@"<(?:rt|rp)\b[^>]*>.*?</(?:rt|rp)>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex Breaks = new(@"<br\s*/?>|</p\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsNcode(string? value) =>
            value is { Length: >= 2 and <= 32 } && Ncode.IsMatch(value);

        private static Uri WorkUri(string ncode) =>
            new("https://ncode.syosetu.com/" + ncode.ToLowerInvariant() + "/");

        public async Task ProbeAsync(CancellationToken ct)
        {
            await GetPageAsync(new Uri("https://ncode.syosetu.com/"), ct);
        }

        public async Task<NarouWork> ReadWorkAsync(string ncode, CancellationToken ct)
        {
            if (!IsNcode(ncode)) throw new NarouException("Invalid Narō ncode.");
            var root = WorkUri(ncode);
            var first = await GetPageAsync(root, ct);
            var title = Plain(Title.Match(first).Groups["value"].Value);
            if (title.Length == 0) throw new NarouException("Narō did not return a work title.");
            var author = Plain(Author.Match(first).Groups["value"].Value)
                .Replace("作者：", "", StringComparison.Ordinal).Trim();
            var chapterMap = new SortedDictionary<int, NarouChapter>();
            AddChapters(first, ncode, chapterMap);
            var page = first;
            for (var current = 1; current < MaximumIndexPages;)
            {
                var next = NextPage.Matches(page).Select(match =>
                    int.TryParse(match.Groups["page"].Value, out var n) ? n : 0)
                    .Where(n => n > current && n <= MaximumIndexPages).DefaultIfEmpty().Min();
                if (next <= current) break;
                page = await GetPageAsync(new Uri(root, "?p=" + next), ct);
                var oldCount = chapterMap.Count;
                AddChapters(page, ncode, chapterMap);
                if (chapterMap.Count == oldCount) break;
                current = next;
            }
            if (chapterMap.Count == 0 || chapterMap.Count > MaximumChapters)
                throw new NarouException("Narō has no chapters or exceeds the supported chapter limit.");
            return new NarouWork(ncode.ToLowerInvariant(), title, author, [.. chapterMap.Values]);
        }

        public async Task WriteEpubAsync(string ncode, string destination, long? maximumBytes,
            Func<long, IDisposable> reserveWrite, CancellationToken ct)
        {
            var work = await ReadWorkAsync(ncode, ct);
            var chapters = new List<(NarouChapter Chapter, string Text)>();
            var textBytes = 0;
            foreach (var chapter in work.Chapters)
            {
                ct.ThrowIfCancellationRequested();
                var html = await GetPageAsync(new Uri(WorkUri(work.Ncode), chapter.Number + "/"), ct);
                var body = Body.Match(html);
                if (!body.Success) throw new NarouException("Narō returned no chapter content.");
                var text = Plain(body.Groups["value"].Value);
                if (text.Length == 0) throw new NarouException("Narō returned an empty chapter.");
                textBytes = checked(textBytes + Encoding.UTF8.GetByteCount(text));
                if (textBytes > MaximumTextBytes) throw new NarouException("The web novel exceeds the supported text limit.");
                chapters.Add((chapter, text));
            }

            await using var memory = new MemoryStream();
            using (var epub = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                Write(epub, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
                Write(epub, "META-INF/container.xml",
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
                var title = Esc(work.Title + " (Web Novel)");
                var manifest = new StringBuilder("<item id=\"toc\" href=\"toc.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>");
                var spine = new StringBuilder();
                var toc = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" xml:lang=\"ja\"><head><title>Contents</title></head><body><nav epub:type=\"toc\"><ol>");
                for (var i = 0; i < chapters.Count; i++)
                {
                    var (chapter, text) = chapters[i];
                    var id = (i + 1).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
                    var file = "chapters/" + id + ".xhtml";
                    manifest.Append("<item id=\"ch").Append(id).Append("\" href=\"").Append(file).Append("\" media-type=\"application/xhtml+xml\"/>");
                    spine.Append("<itemref idref=\"ch").Append(id).Append("\"/>");
                    toc.Append("<li><a href=\"").Append(file).Append("\">").Append(Esc(chapter.Title)).Append("</a></li>");
                    var paragraphs = string.Join("", text.Split('\n')
                        .Where(line => !string.IsNullOrWhiteSpace(line))
                        .Select(line => "<p>" + Esc(line) + "</p>"));
                    Write(epub, "OEBPS/" + file,
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\" xml:lang=\"ja\"><head><title>" +
                        Esc(chapter.Title) + "</title></head><body><h1>" + Esc(chapter.Title) + "</h1>" + paragraphs + "</body></html>");
                }
                toc.Append("</ol></nav></body></html>");
                Write(epub, "OEBPS/toc.xhtml", toc.ToString());
                Write(epub, "OEBPS/content.opf",
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"uid\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"uid\">urn:syosetu:" +
                    Esc(work.Ncode) + "</dc:identifier><dc:title>" + title + "</dc:title><dc:language>ja</dc:language><dc:creator>" +
                    Esc(work.Author) + "</dc:creator></metadata><manifest>" + manifest + "</manifest><spine>" + spine + "</spine></package>");
            }

            if (maximumBytes is > 0 && memory.Length > maximumBytes)
                throw new NarouException("The resulting EPUB exceeds the download size limit.");
            using var reservation = reserveWrite(memory.Length);
            memory.Position = 0;
            await using var fileStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            await memory.CopyToAsync(fileStream, ct);
            await fileStream.FlushAsync(ct);
        }

        private async Task<string> GetPageAsync(Uri uri, CancellationToken ct)
        {
            // URI is constructed only from a validated ncode and local pagination numbers.
            try
            {
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > MaximumPageBytes)
                    throw new NarouException("Narō page exceeds the supported size limit.");
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                using var buffer = new MemoryStream();
                var chunk = new byte[16384];
                int read;
                while ((read = await input.ReadAsync(chunk, ct)) > 0)
                {
                    if (buffer.Length + read > MaximumPageBytes)
                        throw new NarouException("Narō page exceeds the supported size limit.");
                    buffer.Write(chunk, 0, read);
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
            catch (HttpRequestException exception)
            {
                throw new NarouException("Narō is unavailable or requires manual access.", exception);
            }
        }

        private static void AddChapters(string page, string ncode, IDictionary<int, NarouChapter> chapters)
        {
            foreach (Match link in ChapterLink.Matches(page))
            {
                if (!link.Groups["key"].Value.Equals(ncode, StringComparison.OrdinalIgnoreCase)
                    || !int.TryParse(link.Groups["num"].Value, out var number) || number <= 0)
                    continue;
                var title = Plain(link.Groups["name"].Value);
                chapters[number] = new NarouChapter(number, title.Length == 0 ? "Chapter " + number : title);
            }
        }

        private static string Plain(string html)
        {
            var value = Rubies.Replace(html, "");
            value = Breaks.Replace(value, "\n");
            value = Tags.Replace(value, "");
            return WebUtility.HtmlDecode(value).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        }

        private static string Esc(string? text) => SecurityElement.Escape(text ?? "") ?? "";

        private static void Write(ZipArchive zip, string path, string content, CompressionLevel level = CompressionLevel.Fastest)
        {
            var entry = zip.CreateEntry(path, level);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }

    internal sealed record NarouWork(string Ncode, string Title, string Author, IReadOnlyList<NarouChapter> Chapters);
    internal sealed record NarouChapter(int Number, string Title);

    internal sealed class NarouException(string message, Exception? inner = null) : Exception(message, inner);
}
