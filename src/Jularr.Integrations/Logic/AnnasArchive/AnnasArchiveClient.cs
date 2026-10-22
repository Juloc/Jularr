using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Jularr.Integrations.Data.AnnasArchive;
using Jularr.Integrations.Data.Browser;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Logic.Common;

namespace Jularr.Integrations.Logic.AnnasArchive
{
    // Search results and member fast downloads; the member key only goes to Anna's Archive itself.
    internal sealed partial class AnnasArchiveClient(HttpClient http, ISourceGateResolver gates, ISourceBrowser browser)
    {
        // The source answers on one of its mirrors; all of them are tried, the fast way first.
        private const int SlowServers = 3;
        private const long MaximumPageBytes = 4L * 1024 * 1024;
        private static readonly TimeSpan MaximumWait = TimeSpan.FromDays(1);

        // The official mirrors of the source (https://annas-archive.gl/faq#mirrors); all of them are tried, the one
        // of the connection first. Others carry the name without permission and steal donations: never use them.
        private static readonly string[] OfficialMirrors =
        [
            "https://annas-archive.gl",
            "https://annas-archive.pk",
            "https://annas-archive.gd"
        ];

        public static Uri RecordUri(Uri site, string md5) => new(site, "md5/" + md5);

        // The public waiting page of one download server.
        public static Uri SlowPageUri(Uri site, string md5, int server = 0) => new(site, "slow_download/" + md5 + "/0/" + server.ToString(CultureInfo.InvariantCulture));

        public static string? ReadMd5(Uri record) => Md5Path().Match(record.AbsolutePath) is { Success: true } match ? match.Groups[1].Value : null;

        public async Task<IReadOnlyList<AnnasArchiveRecord>> SearchAsync(Uri site, string query, CancellationToken ct)
        {
            // The source answers on one of its mirrors; all of them are tried, the fast way first.
            var mirrors = Mirrors(site);
            string? page = null;
            Uri? answered = null;
            foreach (var mirror in mirrors)
            {
                if (await ReadAsync(mirror, query, ct) is { } direct)
                {
                    page = direct;
                    answered = mirror;
                    break;
                }
            }

            if (page is null)
            {
                // A check in front of the site: the browser reads the mirrors at the same time.
                var read = await browser.ReadPagesAsync([.. mirrors.Select(mirror => new Uri(mirror, "search?q=" + Uri.EscapeDataString(query)))], ct);
                if (read is null)
                {
                    throw new AnnasArchiveException(AnnasArchiveFailure.Gate, "Anna's Archive could not be searched.");
                }

                page = read.Html;
                answered = new Uri(read.Address.GetLeftPart(UriPartial.Authority) + "/");
            }

            var records = new List<AnnasArchiveRecord>();
            foreach (var record in Records(page, answered!))
            {
                if (records.All(known => known.Md5 != record.Md5))
                {
                    records.Add(record);
                }
            }

            return records;
        }

        // Each record is a row: the file's facts, the title link and the author.
        private static IReadOnlyList<AnnasArchiveRecord> Records(string page, Uri site)
        {
            if (page.Length > MaximumPageBytes)
            {
                throw new AnnasArchiveException(AnnasArchiveFailure.InvalidResponse, "The search page is too large.");
            }

            var document = new HtmlParser().ParseDocument(page);
            var records = new List<AnnasArchiveRecord>();
            foreach (var link in document.QuerySelectorAll("a[href^='/md5/']"))
            {
                if (Md5Path().Match(link.GetAttribute("href") ?? "") is not { Success: true } match || records.Any(record => record.Md5 == match.Groups[1].Value))
                {
                    continue;
                }

                if (ReadRecord(link, match.Groups[1].Value, site) is { } record)
                {
                    records.Add(record);
                }

                if (records.Count >= 50)
                {
                    break;
                }
            }

            return records;
        }

        // The mirrors of the source; the one of the connection comes first. Only a connection that points at
        // the source itself gets the others.
        private static IReadOnlyList<Uri> Mirrors(Uri site)
        {
            var mirrors = new List<Uri> { site };
            if (!OfficialMirrors.Any(mirror => string.Equals(new Uri(mirror).Host, site.Host, StringComparison.OrdinalIgnoreCase)))
            {
                return mirrors;
            }

            foreach (var mirror in OfficialMirrors)
            {
                var address = new Uri(mirror.TrimEnd('/') + "/");
                if (!mirrors.Any(known => known.Host == address.Host))
                {
                    mirrors.Add(address);
                }
            }

            return mirrors;
        }

        // The page of one mirror, or nothing when it puts a check in front.
        private async Task<string?> ReadAsync(Uri mirror, string query, CancellationToken ct)
        {
            try
            {
                return await DirectDownload.ReadPageAsync(http, new Uri(mirror, "search?q=" + Uri.EscapeDataString(query)), gates, browser, ct);
            }
            catch (DirectDownloadException)
            {
                return null;
            }
        }

        public async Task DownloadAsync(Uri site, string memberKey, string md5, string destination, CancellationToken ct, long? maximumBytes = null, Func<long, IDisposable>? reserveWrite = null)
        {
            // The member API names a download address; the file is fetched without the key.
            Uri target;
            try
            {
                using var response = await DirectDownload.SendAsync(http, new Uri(site, "dyn/api/fast_download.json?md5=" + md5 + "&key=" + Uri.EscapeDataString(memberKey)), null, gates, ct);
                using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var address = json.RootElement.TryGetProperty("download_url", out var url) && url.ValueKind == JsonValueKind.String ? url.GetString() : null;
                if (address is null || !Uri.TryCreate(address, UriKind.Absolute, out target!) || target.Scheme != Uri.UriSchemeHttps)
                {
                    throw new AnnasArchiveException(AnnasArchiveFailure.Refused, "Anna's Archive refused the fast download.");
                }
            }
            catch (DirectDownloadException exception)
            {
                throw new AnnasArchiveException(exception.Failure switch
                {
                    DirectDownloadFailure.Unauthorized => AnnasArchiveFailure.Refused,
                    DirectDownloadFailure.Gate => AnnasArchiveFailure.Gate,
                    _ => AnnasArchiveFailure.Unavailable
                }, "Anna's Archive could not be asked for the download.", exception);
            }
            catch (JsonException exception)
            {
                throw new AnnasArchiveException(AnnasArchiveFailure.InvalidResponse, "Anna's Archive returned invalid JSON.", exception);
            }

            await DownloadFileAsync(target, destination, ct, maximumBytes, reserveWrite);
        }

        // The public slow download: the page's own countdown is handed to the caller, its check is never passed.
        public async Task<AnnasArchivePage> ReadSlowPageAsync(Uri site, string md5, CancellationToken ct)
        {
            AnnasArchiveException? last = null;
            for (var server = 0; server < SlowServers; server++)
            {
                string page;
                try
                {
                    page = await DirectDownload.ReadPageAsync(http, SlowPageUri(site, md5, server), gates, browser, ct);
                    if (page.Length > MaximumPageBytes)
                    {
                        throw new AnnasArchiveException(AnnasArchiveFailure.InvalidResponse, "The download page is too large.");
                    }
                }
                catch (DirectDownloadException exception) when (exception.Failure == DirectDownloadFailure.Gate)
                {
                    throw new AnnasArchiveException(AnnasArchiveFailure.Gate, "The slow download asks for a manual confirmation.", exception);
                }
                catch (DirectDownloadException exception)
                {
                    last = new AnnasArchiveException(AnnasArchiveFailure.Unavailable, "The slow download page could not be read.", exception);
                    continue;
                }

                // The page names how long to wait and where the file is.
                var document = new HtmlParser().ParseDocument(page);
                var wait = ReadWait(document.Body?.TextContent ?? "");
                if (wait > MaximumWait)
                {
                    last = new AnnasArchiveException(AnnasArchiveFailure.InvalidResponse, "The download page asks for more than a day.");
                    continue;
                }

                var link = document.QuerySelectorAll("a[href]")
                    .Select(anchor => Uri.TryCreate(site, anchor.GetAttribute("href"), out var uri) ? (Uri: uri, Text: anchor.TextContent) : (Uri: (Uri?)null, Text: ""))
                    .Where(candidate => candidate.Uri is { Scheme: "https" } uri && !IsSitePage(site, uri))
                    .FirstOrDefault(candidate => Extension().IsMatch(candidate.Uri!.AbsolutePath) || candidate.Text.Contains("download", StringComparison.OrdinalIgnoreCase)).Uri;

                // A page that only counts down names no file yet; the file comes with the next visit.
                if (link is null && wait > TimeSpan.Zero)
                {
                    return new AnnasArchivePage(null, wait);
                }

                if (link is null)
                {
                    last = new AnnasArchiveException(AnnasArchiveFailure.InvalidResponse, "The slow download page names no file.");
                    continue;
                }

                return new AnnasArchivePage(link, wait);
            }

            throw last ?? new AnnasArchiveException(AnnasArchiveFailure.Unavailable, "No slow download server answered.");
        }

        // A file link a person obtained after the site's own check; no key is sent.
        public async Task DownloadFileAsync(Uri file, string destination, CancellationToken ct, long? maximumBytes = null, Func<long, IDisposable>? reserveWrite = null)
        {
            var complete = false;
            var destinationExisted = File.Exists(destination);
            try
            {
                await DirectDownload.SaveFileAsync(http, file, destination, gates, browser, maximumBytes, reserveWrite, ct);
                // The site may answer with a page instead of the file; then the browser fetches it.
                if (await IsDocumentAsync(destination, ct))
                {
                    complete = true;
                    return;
                }

                File.Delete(destination);
                await browser.SaveFileAsync(file, destination, maximumBytes, reserveWrite, ct);
                if (!await IsDocumentAsync(destination, ct))
                {
                    throw new AnnasArchiveException(AnnasArchiveFailure.InvalidResponse, "The browser did not download a supported book file.");
                }

                complete = true;
            }
            catch (DirectDownloadException exception) when (exception.Failure != DirectDownloadFailure.TooLarge)
            {
                throw new AnnasArchiveException(Failure(exception), "The file could not be downloaded.", exception);
            }
            finally
            {
                if (!complete && !destinationExisted)
                {
                    try
                    {
                        File.Delete(destination);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // Preserve the download error; cleanup can be retried by the operation lifecycle.
                    }
                }
            }
        }

        private static AnnasArchiveFailure Failure(DirectDownloadException exception) => exception.Failure == DirectDownloadFailure.Gate ? AnnasArchiveFailure.Gate : AnnasArchiveFailure.Unavailable;

        // A book is a ZIP, a PDF, a MOBI or an AZW3; a page in place of it means the site answered with its site.
        private static async Task<bool> IsDocumentAsync(string path, CancellationToken ct)
        {
            try
            {
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16, useAsync: true);
                var head = new byte[4];
                var read = await file.ReadAsync(head, ct);
                return read >= 2 && (head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x03 && head[3] == 0x04
                    || head[0] == 0x25 && head[1] == 0x50 && head[2] == 0x44 && head[3] == 0x46
                    || head[0] == 0x42 && head[1] == 0x4F && head[2] == 0x4F && head[3] == 0x4B);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool IsSitePage(Uri site, Uri uri) => string.Equals(site.Host, uri.Host, StringComparison.OrdinalIgnoreCase)
            && (uri.AbsolutePath.StartsWith("/md5/", StringComparison.Ordinal) || uri.AbsolutePath.StartsWith("/slow_download/", StringComparison.Ordinal)
                || uri.AbsolutePath.StartsWith("/fast_download/", StringComparison.Ordinal) || uri.AbsolutePath.StartsWith("/search", StringComparison.Ordinal)
                || uri.AbsolutePath.StartsWith("/account", StringComparison.Ordinal) || uri.AbsolutePath.StartsWith("/donate", StringComparison.Ordinal) || uri.AbsolutePath == "/");

        private static long? ReadSize(string facts)
        {
            var match = Size().Match(facts);
            if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            var factor = match.Groups[2].Value.ToUpperInvariant() switch { "KB" => 1024d, "MB" => 1024d * 1024, "GB" => 1024d * 1024 * 1024, _ => 1d };
            return (long)(value * factor);
        }

        // The page states its wait in seconds, minutes or a clock; the longest statement wins.
        private static TimeSpan ReadWait(string text)
        {
            var wait = TimeSpan.Zero;
            void Longest(TimeSpan candidate)
            {
                if (candidate > wait) wait = candidate;
            }

            foreach (Match match in CountdownSeconds().Matches(text))
            {
                if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) Longest(TimeSpan.FromSeconds(seconds));
            }

            foreach (Match match in CountdownMinutes().Matches(text))
            {
                if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)) Longest(TimeSpan.FromMinutes(minutes));
            }

            foreach (Match match in CountdownClock().Matches(text))
            {
                // Two parts count minutes and seconds; three parts add hours.
                var first = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var second = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                if (match.Groups[3].Success)
                {
                    var third = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                    if (second < 60 && third < 60) Longest(new TimeSpan(first, second, third));
                }
                else if (second < 60)
                {
                    Longest(TimeSpan.FromMinutes(first) + TimeSpan.FromSeconds(second));
                }
            }

            return wait;
        }

        private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];

        // Each record is a row: the file's facts, the title link and the author.
        private static AnnasArchiveRecord? ReadRecord(IElement link, string md5, Uri site)
        {
            var title = (link.QuerySelector("h3")?.TextContent ?? link.TextContent ?? "").Trim();
            if (title.Length == 0)
            {
                return null;
            }

            var row = Row(link);
            var lines = Lines(row);
            var facts = lines.FirstOrDefault(line => Size().IsMatch(line)) ?? "";
            var format = Format().Match(facts) is { Success: true } found ? found.Groups[1].Value.ToLowerInvariant()
                : lines.Select(line => Format().Match(line)).FirstOrDefault(match => match.Success)?.Groups[1].Value.ToLowerInvariant();
            if (format is null)
            {
                return null;
            }

            var author = link.QuerySelector(".italic")?.TextContent.Trim() ?? Author(row);
            var language = Language().Match(facts) is { Success: true } code ? code.Groups[1].Value : null;
            return new AnnasArchiveRecord(md5, Limit(title, 500), author is null ? null : Limit(author, 200), format, language, ReadSize(facts), site, IsPart(lines));
        }

        // A file that names a part of a work is only that part.
        private static bool IsPart(IReadOnlyList<string> lines)
        {
            foreach (var line in lines)
            {
                if (Part().IsMatch(line))
                {
                    return true;
                }
            }

            return false;
        }

        // The facts sit around the title link.
        private static IElement Row(IElement link)
        {
            var element = link;
            for (var level = 0; level < 6 && element is not null; level++)
            {
                if (Lines(element).Any(line => Size().IsMatch(line)))
                {
                    return element;
                }

                element = element.ParentElement;
            }

            return link;
        }

        private static List<string> Lines(IElement element) => (element.TextContent ?? "")
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Length > 0)
            .ToList();

        // The author is the first link that searches for a name.
        private static string? Author(IElement row) => row.QuerySelectorAll("a[href^='/search?q=']")
            .FirstOrDefault(anchor => (anchor.GetAttribute("href") ?? "").Length > "/search?q=".Length)?.TextContent.Trim();

        [GeneratedRegex("^/md5/([0-9a-f]{32})$")]
        private static partial Regex Md5Path();

        [GeneratedRegex(@"\.(epub|pdf|mobi|azw3)\b", RegexOptions.IgnoreCase)]
        private static partial Regex Extension();

        [GeneratedRegex(@"\b(epub|pdf|mobi|azw3)\b", RegexOptions.IgnoreCase)]
        private static partial Regex Format();

        // A file that names a part of a work: "(1 of 3)", "Part 2", "Band 1", "Vol. 2", "Teil 2", "Tomo 2".
        [GeneratedRegex(@"\(\s*\d+\s+of\s+\d+\s*\)|\b(part|band|vol|volume|teil|tomo|partie)\.?\s*\d+", RegexOptions.IgnoreCase)]
        private static partial Regex Part();

        [GeneratedRegex(@"wait\D{0,40}?([0-9]{1,5})\s*(?:s\b|sec|second)", RegexOptions.IgnoreCase)]
        private static partial Regex CountdownSeconds();

        [GeneratedRegex(@"wait\D{0,40}?([0-9]{1,4})\s*(?:m\b|min|minute)", RegexOptions.IgnoreCase)]
        private static partial Regex CountdownMinutes();

        [GeneratedRegex(@"(?:wait|ready|in)\b\D{0,20}?([0-9]{1,2}):([0-9]{2})(?::([0-9]{2}))?", RegexOptions.IgnoreCase)]
        private static partial Regex CountdownClock();

        [GeneratedRegex(@"\[([a-z]{2,3})\]")]
        private static partial Regex Language();

        [GeneratedRegex(@"([0-9]+(?:\.[0-9]+)?)\s*(KB|MB|GB)", RegexOptions.IgnoreCase)]
        private static partial Regex Size();
    }
}
