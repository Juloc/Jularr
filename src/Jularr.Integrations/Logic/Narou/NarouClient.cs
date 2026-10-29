using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Integrations.Data.Narou;

namespace Jularr.Integrations.Logic.Narou
{
    // Shōsetsuka ni Narō: the official API finds and describes web novels, the novel pages give the chapters.
    // Only these two hosts are asked, one request at a time per host and at most one a second.
    internal sealed partial class NarouClient(HttpClient http)
    {
        public const string Api = "https://api.syosetu.com/novelapi/api/";
        public const string Site = "https://ncode.syosetu.com/";
        public const int MaximumChapters = 9000;
        private const int MaximumResults = 20;
        private const int MaximumPageBytes = 2 * 1024 * 1024;
        private const long MaximumTextBytes = 256L * 1024 * 1024;
        private const int FailuresBeforeGivingUp = 5;
        private const string Fields = "t-n-w-s-gf-nt-e-ga";
        private static readonly TimeSpan RequestInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan PassLength = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(10);
        private static readonly Pace ApiPace = new();
        private static readonly Pace SitePace = new();

        public static bool IsNcode(string? value) => value is { Length: >= 6 and <= 12 } && NcodePattern().IsMatch(value);

        // The novel's page; a short story carries its text there.
        public static Uri NovelUri(string ncode) => new(Site + ncode.ToLowerInvariant() + "/");

        // A title search, or the one novel of an ncode.
        public async Task<IReadOnlyList<NarouNovel>> SearchAsync(string query, CancellationToken ct)
        {
            var text = query.Trim();
            if (text.Length is 0 or > 100) return [];
            return IsNcode(text)
                ? await ReadListAsync("ncode=" + text.ToLowerInvariant(), ct)
                : await ReadListAsync("title=1&order=hyoka&word=" + Uri.EscapeDataString(text), ct);
        }

        public async Task<NarouNovel?> ReadAsync(string ncode, CancellationToken ct) =>
            IsNcode(ncode) ? (await ReadListAsync("ncode=" + ncode.ToLowerInvariant(), ct)).FirstOrDefault() : throw new NarouException(NarouFailure.Missing, "Invalid ncode.");

        private async Task<IReadOnlyList<NarouNovel>> ReadListAsync(string filter, CancellationToken ct)
        {
            // The answer starts with the count row, then one record per novel.
            var page = await GetAsync(ApiPace, new Uri(Api + "?out=json&of=" + Fields + "&lim=" + MaximumResults.ToString(CultureInfo.InvariantCulture) + "&" + filter), ct);
            try
            {
                using var document = JsonDocument.Parse(page, new JsonDocumentOptions { MaxDepth = 8 });
                if (document.RootElement.ValueKind != JsonValueKind.Array) throw new NarouException(NarouFailure.InvalidResponse, "Narō's API returned no list.");
                var novels = new List<NarouNovel>();
                foreach (var item in document.RootElement.EnumerateArray().Skip(1).Take(MaximumResults))
                {
                    if (Novel(item) is { } novel) novels.Add(novel);
                }

                return novels;
            }
            catch (JsonException exception)
            {
                throw new NarouException(NarouFailure.InvalidResponse, "Narō's API returned invalid JSON.", exception);
            }
        }

        private static NarouNovel? Novel(JsonElement item)
        {
            var ncode = String(item, "ncode")?.ToLowerInvariant();
            var title = String(item, "title")?.Trim();
            if (!IsNcode(ncode) || string.IsNullOrEmpty(title)) return null;
            var firstUp = String(item, "general_firstup");
            int? year = firstUp is { Length: >= 4 } && int.TryParse(firstUp[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            var shortStory = Number(item, "novel_type") == 2;
            return new NarouNovel(ncode!, title, String(item, "writer")?.Trim() ?? "", String(item, "story")?.Trim(), year, shortStory, Number(item, "end") == 0,
                shortStory ? 1 : Number(item, "general_all_no") ?? 0);
        }

        // One chapter from its page: the title and the text without the foreword and afterword.
        public async Task<NarouChapter> ReadChapterAsync(NarouNovel novel, int number, CancellationToken ct)
        {
            var html = await GetAsync(SitePace, novel.IsShortStory ? NovelUri(novel.Ncode) : new Uri(NovelUri(novel.Ncode), number.ToString(CultureInfo.InvariantCulture) + "/"), ct);
            return ParseChapter(html, number, novel.IsShortStory ? novel.Title : null);
        }

        internal static NarouChapter ParseChapter(string html, int number, string? fallbackTitle)
        {
            var heading = ChapterTitle().Match(html);
            var title = heading.Success ? Plain(heading.Groups["value"].Value).Trim() : "";
            if (title.Length == 0) title = fallbackTitle ?? "#" + number.ToString(CultureInfo.InvariantCulture);

            var texts = ChapterText().Matches(html).Where(match => !match.Groups["class"].Value.Contains("--", StringComparison.Ordinal)).Select(match => match.Groups["value"].Value).ToList();
            if (texts.Count == 0 && LegacyText().Match(html) is { Success: true } legacy) texts.Add(legacy.Groups["value"].Value);
            if (texts.Count == 0) throw new NarouException(NarouFailure.InvalidResponse, "Narō's page holds no chapter text.");

            // Paragraphs keep their ruby; an empty line between them stays one empty line.
            var body = new StringBuilder();
            var blank = false;
            foreach (Match paragraph in texts.SelectMany(text => Paragraph().Matches(text)))
            {
                var line = Line(paragraph.Groups["value"].Value);
                if (line.Trim().Length == 0)
                {
                    blank = body.Length > 0;
                    continue;
                }

                if (blank) body.Append("<p><br/></p>");
                body.Append("<p>").Append(line).Append("</p>");
                blank = false;
            }

            return body.Length == 0 ? throw new NarouException(NarouFailure.InvalidResponse, "Narō's chapter is empty.") : new NarouChapter(number, title, body.ToString());
        }

        // A bounded pass of a download: chapters already kept in the folder are skipped, the next ones are read until the pass ends.
        // Once every chapter is kept the book is written to the destination and the folder goes.
        public async Task<NarouProgress> DownloadAsync(string ncode, string destination, long? maximumBytes, Func<long, IDisposable> reserveWrite, CancellationToken ct)
        {
            var folder = Path.Combine(Path.GetDirectoryName(destination)!, "narou");
            Directory.CreateDirectory(folder);
            var statePath = Path.Combine(folder, "novel.json");
            var state = ReadState(statePath, ncode);
            if (state is null)
            {
                var novel = await ReadAsync(ncode, ct) ?? throw new NarouException(NarouFailure.Missing, "Narō does not know this ncode.");
                if (novel.ChapterCount is < 1 or > MaximumChapters) throw new NarouException(NarouFailure.TooLarge, "The novel has no chapters or more than Jularr reads.");
                state = new NarouDownloadState(novel.Ncode, novel.Title, novel.Writer, novel.Story, novel.IsShortStory, novel.ChapterCount, 0);
                WriteState(statePath, state);
            }

            var novelState = new NarouNovel(state.Ncode, state.Title, state.Writer, state.Story, null, state.IsShortStory, false, state.ChapterCount);
            var started = Stopwatch.GetTimestamp();
            var done = 0;
            var progressed = false;
            try
            {
                for (var number = 1; number <= state.ChapterCount; number++)
                {
                    var path = ChapterPath(folder, number);
                    if (File.Exists(path))
                    {
                        done++;
                        continue;
                    }

                    if (Stopwatch.GetElapsedTime(started) > PassLength)
                    {
                        if (state.Failures > 0) WriteState(statePath, state with { Failures = 0 });
                        return new NarouProgress(done, state.ChapterCount, false);
                    }

                    var chapter = await ReadChapterAsync(novelState, number, ct);
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(chapter);
                    using (reserveWrite(bytes.Length))
                    {
                        await File.WriteAllBytesAsync(path + ".partial", bytes, ct);
                        File.Move(path + ".partial", path, true);
                    }

                    done++;
                    progressed = true;
                }
            }
            catch (NarouException exception) when (exception.Failure == NarouFailure.Unavailable)
            {
                // The kept chapters stay; a few failed attempts in a row without progress give up.
                var failures = progressed ? 1 : state.Failures + 1;
                if (failures >= FailuresBeforeGivingUp) throw;
                WriteState(statePath, state with { Failures = failures });
                return new NarouProgress(done, state.ChapterCount, false, RetryDelay);
            }

            await WriteEpubAsync(state, folder, destination, maximumBytes, reserveWrite, ct);
            Directory.Delete(folder, true);
            return new NarouProgress(done, state.ChapterCount, true);
        }

        private static async Task WriteEpubAsync(NarouDownloadState state, string folder, string destination, long? maximumBytes, Func<long, IDisposable> reserveWrite, CancellationToken ct)
        {
            var paths = Enumerable.Range(1, state.ChapterCount).Select(number => ChapterPath(folder, number)).ToArray();
            var textBytes = paths.Sum(path => new FileInfo(path).Length);
            if (textBytes > MaximumTextBytes || maximumBytes is > 0 && textBytes > maximumBytes * 4)
            {
                throw new NarouException(NarouFailure.TooLarge, "The novel exceeds the size limit.");
            }

            using (reserveWrite(textBytes + 65536))
            {
                await using var file = new FileStream(destination, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536, useAsync: true);
                using (var book = new NarouEpub(file, state.Ncode, state.Title, state.Writer, state.Story))
                {
                    foreach (var path in paths)
                    {
                        ct.ThrowIfCancellationRequested();
                        var chapter = JsonSerializer.Deserialize<NarouChapter>(await File.ReadAllBytesAsync(path, ct))
                            ?? throw new NarouException(NarouFailure.InvalidResponse, "A kept chapter is unreadable.");
                        book.AddChapter(chapter);
                    }

                    book.Complete();
                }

                await file.FlushAsync(ct);
                if (maximumBytes is > 0 && file.Length > maximumBytes)
                {
                    throw new NarouException(NarouFailure.TooLarge, "The book exceeds the size limit.");
                }
            }
        }

        private static string ChapterPath(string folder, int number) => Path.Combine(folder, number.ToString("D5", CultureInfo.InvariantCulture) + ".json");

        // What a download keeps between passes: the novel as it was when the download began and the failed attempts in a row.
        private sealed record NarouDownloadState(string Ncode, string Title, string Writer, string? Story, bool IsShortStory, int ChapterCount, int Failures);

        private static NarouDownloadState? ReadState(string path, string ncode)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var state = JsonSerializer.Deserialize<NarouDownloadState>(File.ReadAllBytes(path));
                return state is not null && state.Ncode == ncode.ToLowerInvariant() && state.ChapterCount is >= 1 and <= MaximumChapters ? state : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static void WriteState(string path, NarouDownloadState state)
        {
            File.WriteAllBytes(path + ".partial", JsonSerializer.SerializeToUtf8Bytes(state));
            File.Move(path + ".partial", path, true);
        }

        private async Task<string> GetAsync(Pace pace, Uri address, CancellationToken ct)
        {
            await pace.WaitAsync(RequestInterval, ct);
            try
            {
                using var response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone || (int)response.StatusCode is >= 300 and < 400)
                {
                    throw new NarouException(NarouFailure.Missing, "Narō does not have this page.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new NarouException(NarouFailure.Unavailable, "Narō answered " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
                }

                if (response.Content.Headers.ContentLength is > MaximumPageBytes) throw new NarouException(NarouFailure.TooLarge, "Narō's page is too large.");
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                using var buffer = new MemoryStream();
                var chunk = new byte[16384];
                int read;
                while ((read = await input.ReadAsync(chunk, ct)) > 0)
                {
                    if (buffer.Length + read > MaximumPageBytes) throw new NarouException(NarouFailure.TooLarge, "Narō's page is too large.");
                    buffer.Write(chunk, 0, read);
                }

                return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            }
            catch (HttpRequestException exception)
            {
                throw new NarouException(NarouFailure.Unavailable, "Narō could not be reached.", exception);
            }
            catch (TaskCanceledException exception) when (!ct.IsCancellationRequested)
            {
                throw new NarouException(NarouFailure.Unavailable, "Narō did not answer in time.", exception);
            }
        }

        // A paragraph's text with its ruby; every other markup goes.
        private static string Line(string html)
        {
            var line = new StringBuilder();
            var at = 0;
            foreach (Match ruby in Ruby().Matches(html))
            {
                line.Append(NarouEpub.Escape(Plain(html[at..ruby.Index])));
                var inner = RubyAside().Replace(ruby.Groups["value"].Value, "");
                var reading = string.Concat(RubyText().Matches(inner).Select(match => Plain(match.Groups["value"].Value)));
                var written = NarouEpub.Escape(Plain(RubyText().Replace(inner, "")));
                line.Append(reading.Length == 0 ? written : "<ruby>" + written + "<rt>" + NarouEpub.Escape(reading) + "</rt></ruby>");
                at = ruby.Index + ruby.Length;
            }

            return line.Append(NarouEpub.Escape(Plain(html[at..]))).ToString();
        }

        private static string Plain(string html) => WebUtility.HtmlDecode(Tag().Replace(html, "")).Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);

        private static string? String(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static int? Number(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

        // Requests of one host start at least the interval apart.
        private sealed class Pace
        {
            private readonly SemaphoreSlim _gate = new(1, 1);
            private long _last;

            public async Task WaitAsync(TimeSpan interval, CancellationToken ct)
            {
                await _gate.WaitAsync(ct);
                try
                {
                    var wait = interval - Stopwatch.GetElapsedTime(_last);
                    if (_last != 0 && wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                    _last = Stopwatch.GetTimestamp();
                }
                finally
                {
                    _gate.Release();
                }
            }
        }

        [GeneratedRegex("^n[0-9]{4}[a-z]{1,4}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex NcodePattern();

        [GeneratedRegex(@"<h1\b[^>]*class\s*=\s*""[^""]*\bp-novel__title\b[^""]*""[^>]*>(?<value>.*?)</h1>|<p\b[^>]*class\s*=\s*""novel_subtitle""[^>]*>(?<value>.*?)</p>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        private static partial Regex ChapterTitle();

        [GeneratedRegex(@"<div\b[^>]*class\s*=\s*""(?<class>[^""]*\bp-novel__text\b[^""]*)""[^>]*>(?<value>.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        private static partial Regex ChapterText();

        [GeneratedRegex(@"<div\b[^>]*id\s*=\s*""novel_honbun""[^>]*>(?<value>.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        private static partial Regex LegacyText();

        [GeneratedRegex(@"<p\b[^>]*>(?<value>.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        private static partial Regex Paragraph();

        [GeneratedRegex(@"<ruby\b[^>]*>(?<value>.*?)</ruby>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        private static partial Regex Ruby();

        [GeneratedRegex(@"<rt\b[^>]*>(?<value>.*?)</rt>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        private static partial Regex RubyText();

        [GeneratedRegex(@"<rp\b[^>]*>.*?</rp>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        private static partial Regex RubyAside();

        [GeneratedRegex("<[^>]*>")]
        private static partial Regex Tag();
    }
}
