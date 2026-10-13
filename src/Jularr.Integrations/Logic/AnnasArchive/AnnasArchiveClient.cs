using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Jularr.Integrations.Data.AnnasArchive;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Logic.Common;

namespace Jularr.Integrations.Logic.AnnasArchive
{
    // Search results and member fast downloads; the member key only goes to Anna's Archive itself.
    internal sealed partial class AnnasArchiveClient(HttpClient http, ISourceGateResolver gates)
    {
        public const long MaximumBytes = 500L * 1024 * 1024;
        private const long MaximumPageBytes = 4L * 1024 * 1024;

        public static Uri RecordUri(Uri site, string md5) => new(site, "md5/" + md5);

        public static string? ReadMd5(Uri record) => Md5Path().Match(record.AbsolutePath) is { Success: true } match ? match.Groups[1].Value : null;

        public async Task<IReadOnlyList<AnnasArchiveRecord>> SearchAsync(Uri site, string query, CancellationToken ct)
        {
            string page;
            try
            {
                using var response = await DirectDownload.SendAsync(http, new Uri(site, "search?q=" + Uri.EscapeDataString(query)), null, gates, ct);
                if (response.Content.Headers.ContentLength > MaximumPageBytes)
                {
                    throw new AnnasArchiveException(AnnasArchiveFailure.InvalidResponse, "The search page is too large.");
                }

                page = await response.Content.ReadAsStringAsync(ct);
            }
            catch (DirectDownloadException exception)
            {
                throw new AnnasArchiveException(exception.Failure == DirectDownloadFailure.Gate ? AnnasArchiveFailure.Gate : AnnasArchiveFailure.Unavailable, "Anna's Archive could not be searched.", exception);
            }

            // Each record links to /md5/<hash>; its text names title, author and file facts.
            var document = new HtmlParser().ParseDocument(page);
            var records = new List<AnnasArchiveRecord>();
            foreach (var link in document.QuerySelectorAll("a[href^='/md5/']"))
            {
                if (Md5Path().Match(link.GetAttribute("href") ?? "") is not { Success: true } match || records.Any(record => record.Md5 == match.Groups[1].Value))
                {
                    continue;
                }

                var lines = (link.TextContent ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var title = link.QuerySelector("h3")?.TextContent.Trim() is { Length: > 0 } heading ? heading : lines.FirstOrDefault();
                var facts = lines.FirstOrDefault(line => Extension().IsMatch(line)) ?? "";
                if (string.IsNullOrWhiteSpace(title) || Extension().Match(facts) is not { Success: true } extension)
                {
                    continue;
                }

                var author = link.QuerySelector(".italic")?.TextContent.Trim() ?? lines.SkipWhile(line => line != title).Skip(1).FirstOrDefault(line => line != facts && !line.Contains('/') && line.Length <= 200);
                var language = Language().Match(facts) is { Success: true } code ? code.Groups[1].Value : null;
                records.Add(new AnnasArchiveRecord(match.Groups[1].Value, Limit(title, 500), author is null ? null : Limit(author, 200), extension.Groups[1].Value.ToLowerInvariant(), language,
                    ReadSize(facts)));
                if (records.Count >= 50)
                {
                    break;
                }
            }

            return records;
        }

        public async Task DownloadAsync(Uri site, string memberKey, string md5, string destination, CancellationToken ct)
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

            try
            {
                using var file = await DirectDownload.SendAsync(http, target, null, gates, ct);
                await DirectDownload.SaveAsync(file, destination, MaximumBytes, ct);
            }
            catch (DirectDownloadException exception)
            {
                throw new AnnasArchiveException(exception.Failure == DirectDownloadFailure.Gate ? AnnasArchiveFailure.Gate : AnnasArchiveFailure.Unavailable, "The file could not be downloaded.", exception);
            }
        }

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

        private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];

        [GeneratedRegex("^/md5/([0-9a-f]{32})$")]
        private static partial Regex Md5Path();

        [GeneratedRegex(@"\.(epub|pdf|mobi|azw3)\b", RegexOptions.IgnoreCase)]
        private static partial Regex Extension();

        [GeneratedRegex(@"\[([a-z]{2,3})\]")]
        private static partial Regex Language();

        [GeneratedRegex(@"([0-9]+(?:\.[0-9]+)?)\s*(KB|MB|GB)", RegexOptions.IgnoreCase)]
        private static partial Regex Size();
    }
}
