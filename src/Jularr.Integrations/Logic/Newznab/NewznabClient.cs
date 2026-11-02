using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Xml;
using System.Xml.Linq;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Data.Newznab;
using Jularr.Integrations.Logic.Common;
using Jularr.Integrations.Logic.Usenet;

namespace Jularr.Integrations.Logic.Newznab
{
    // Direct Newznab indexer access through its caps and search XML API.
    internal sealed class NewznabClient(HttpClient httpClient, NewznabGate gate)
    {
        private static readonly (string Element, NewznabSearchMode Mode)[] Functions =
        [
            ("search", NewznabSearchMode.Search),
            ("tv-search", NewznabSearchMode.TvSearch),
            ("movie-search", NewznabSearchMode.Movie),
            ("book-search", NewznabSearchMode.Book),
            ("audio-search", NewznabSearchMode.Music)
        ];

        public async Task<NewznabCapabilities> ReadCapabilitiesAsync(NewznabConnection connection, CancellationToken ct)
        {
            var document = await ReadAsync(connection, "caps", [], ct);
            if (!string.Equals(document.Root?.Name.LocalName, "caps", StringComparison.OrdinalIgnoreCase))
            {
                throw new NewznabException(NewznabFailure.InvalidResponse, "The indexer answered with something other than a caps document.");
            }

            return ParseCapabilities(document, DateTimeOffset.UtcNow);
        }

        public async Task<IReadOnlyList<NewznabItem>> SearchAsync(NewznabConnection connection, NewznabQuery query, CancellationToken ct)
        {
            var parameters = new List<KeyValuePair<string, string>>();
            if (!string.IsNullOrWhiteSpace(query.Text))
            {
                parameters.Add(new("q", query.Text));
            }

            parameters.AddRange(query.Parameters.Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)));
            if (parameters.Count == 0)
            {
                return [];
            }

            if (query.Categories.Count > 0)
            {
                parameters.Add(new("cat", string.Join(',', query.Categories.Distinct().Select(category => category.ToString(CultureInfo.InvariantCulture)))));
            }

            parameters.Add(new("limit", query.Limit.ToString(CultureInfo.InvariantCulture)));
            if (query.Offset > 0)
            {
                parameters.Add(new("offset", query.Offset.ToString(CultureInfo.InvariantCulture)));
            }

            var document = await ReadAsync(connection, Function(query.Mode), parameters, ct);
            if (!string.Equals(document.Root?.Name.LocalName, "rss", StringComparison.OrdinalIgnoreCase))
            {
                throw new NewznabException(NewznabFailure.InvalidResponse, "The indexer answered with something other than Newznab XML.");
            }

            return ParseItems(connection.BaseUrl, document);
        }

        internal static string Function(NewznabSearchMode mode) => mode switch
        {
            NewznabSearchMode.TvSearch => "tvsearch",
            NewznabSearchMode.Movie => "movie",
            NewznabSearchMode.Book => "book",
            NewznabSearchMode.Music => "music",
            _ => "search"
        };

        // A custom path is kept; an address ending in /api gets no second one.
        internal static string ApiEndpoint(string baseUrl)
        {
            var trimmed = baseUrl.Trim().TrimEnd('/');
            return trimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + "/api";
        }

        internal static NewznabCapabilities ParseCapabilities(XDocument document, DateTimeOffset now)
        {
            var searching = Child(document.Root, "searching");
            var modes = new Dictionary<NewznabSearchMode, string[]>();
            foreach (var (name, mode) in Functions)
            {
                var element = Child(searching, name);
                if (element is null || !string.Equals(element.Attribute("available")?.Value, "yes", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                modes[mode] = (element.Attribute("supportedParams")?.Value ?? "q")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(parameter => parameter.ToLowerInvariant())
                    .Distinct()
                    .ToArray();
            }

            // Every server answers the plain text search.
            if (!modes.ContainsKey(NewznabSearchMode.Search))
            {
                modes[NewznabSearchMode.Search] = ["q"];
            }

            var categories = new List<int>();
            foreach (var category in Child(document.Root, "categories")?.Elements().Where(element => element.Name.LocalName == "category") ?? [])
            {
                if (PositiveInt(category.Attribute("id")?.Value) is { } parent)
                {
                    categories.Add(parent);
                    categories.AddRange(category.Elements().Where(element => element.Name.LocalName == "subcat").Select(element => PositiveInt(element.Attribute("id")?.Value)).OfType<int>());
                }
            }

            var server = Child(document.Root, "server");
            var title = server?.Attribute("title")?.Value?.Trim();
            var ids = categories.Distinct().Order().ToArray();
            return new NewznabCapabilities(now, modes, PositiveInt(Child(document.Root, "limits")?.Attribute("max")?.Value), ids.Length > 0 ? ids : null,
                string.IsNullOrEmpty(title) ? null : title, server?.Attribute("version")?.Value);
        }

        internal static IReadOnlyList<NewznabItem> ParseItems(string baseUrl, XDocument document)
        {
            var items = new List<NewznabItem>();
            foreach (var item in document.Descendants().Where(element => element.Name.LocalName == "item"))
            {
                var title = Text(item, "title");
                if (title is null)
                {
                    continue;
                }

                var attributes = item.Elements()
                    .Where(element => element.Name.LocalName == "attr")
                    .GroupBy(element => element.Attribute("name")?.Value ?? "", StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().Attribute("value")?.Value ?? "", StringComparer.OrdinalIgnoreCase);
                var enclosure = Child(item, "enclosure");
                var link = enclosure?.Attribute("url")?.Value ?? Text(item, "link");
                var size = (attributes.TryGetValue("size", out var sizeValue) ? Long(sizeValue) : null) ?? Long(enclosure?.Attribute("length")?.Value);
                var published = DateTimeOffset.TryParse(Text(item, "pubDate"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
                    ? date : (DateTimeOffset?)null;

                // Usenet only: magnet links are never accepted.
                items.Add(new NewznabItem(title, Text(item, "guid"), link is null ? null : ResolveUri(baseUrl, link), Text(item, "comments") ?? Text(item, "link"), size, published)
                {
                    IndexerFlags = attributes.TryGetValue("indexerflags", out var flags)
                        ? flags.Split([',', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToArray()
                        : []
                });
            }

            return items;
        }

        // The release's own link carries the indexer's access; Jularr keeps the NZB for its own downloader.
        public async Task DownloadNzbAsync(Uri nzb, string destination, CancellationToken ct, Func<long, IDisposable>? reserveWrite = null)
        {
            var host = NewznabGate.HostKey(nzb);
            await gate.WaitAsync(host, ct);
            try
            {
                using var response = await httpClient.GetAsync(nzb, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new NewznabException(NewznabFailure.AuthenticationFailed, "The indexer refused the NZB.") { StatusCode = response.StatusCode };
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw RateLimited(host, "The indexer is rate limited.", NewznabGate.ReadRetryAfter(response.Headers), response.StatusCode);
                }

                // A link that no longer exists is no outage of the indexer or the client.
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                {
                    throw new NewznabException(NewznabFailure.NotFound, "The link of the file no longer exists.") { StatusCode = response.StatusCode };
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new NewznabException(NewznabFailure.Unavailable, "The indexer did not deliver the NZB.") { StatusCode = response.StatusCode };
                }

                await DirectDownload.SaveAsync(response, destination, NzbReader.MaximumBytes, ct, reserveWrite);
            }
            catch (DirectDownloadException exception)
            {
                throw new NewznabException(NewznabFailure.InvalidResponse, "The NZB is too large.", null, exception);
            }
            catch (Exception exception) when (exception is HttpRequestException || exception is OperationCanceledException && !ct.IsCancellationRequested)
            {
                throw new NewznabException(NewznabFailure.Unavailable, "No connection, or no answer in time.", null, exception);
            }
        }

        private async Task<XDocument> ReadAsync(NewznabConnection connection, string function, IReadOnlyList<KeyValuePair<string, string>> parameters, CancellationToken ct)
        {
            // The API key travels in the query, as the protocol requires; it is never logged here.
            var query = new List<KeyValuePair<string, string>> { new("t", function), new("o", "xml") };
            if (!string.IsNullOrWhiteSpace(connection.ApiKey))
            {
                query.Insert(1, new("apikey", connection.ApiKey.Trim()));
            }
            query.AddRange(parameters);
            var uri = new Uri(ApiEndpoint(connection.BaseUrl) + "?" + string.Join('&', query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))), UriKind.Absolute);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));

            // Every call to the indexer keeps the shared pace.
            var host = NewznabGate.HostKey(uri);
            await gate.WaitAsync(host, ct);

            string body;
            HttpStatusCode status;
            TimeSpan? retryAfter;
            try
            {
                using var response = await httpClient.SendAsync(request, ct);
                status = response.StatusCode;
                retryAfter = NewznabGate.ReadRetryAfter(response.Headers);
                body = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception exception) when (exception is HttpRequestException || exception is OperationCanceledException && !ct.IsCancellationRequested)
            {
                throw new NewznabException(NewznabFailure.Unavailable, "No connection, or no answer in time.", null, exception);
            }

            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new NewznabException(NewznabFailure.AuthenticationFailed, "The indexer rejected the API key.");
            }

            if (status == HttpStatusCode.TooManyRequests)
            {
                throw RateLimited(host, "The indexer is rate limited.", retryAfter, status);
            }

            // Indexers answer errors as <error> documents with any HTTP status.
            XDocument document;
            try
            {
                if (!body.TrimStart().StartsWith('<'))
                {
                    throw new NewznabException(status == HttpStatusCode.OK ? NewznabFailure.InvalidResponse : NewznabFailure.Unavailable, "The indexer answered without XML.");
                }

                document = XDocument.Parse(body);
            }
            catch (XmlException exception)
            {
                throw new NewznabException(NewznabFailure.InvalidResponse, "The indexer answered with data that is not Newznab XML.", null, exception);
            }

            if (string.Equals(document.Root?.Name.LocalName, "error", StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(document.Root!.Attribute("code")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code);
                throw code switch
                {
                    >= 100 and < 200 => new NewznabException(NewznabFailure.AuthenticationFailed, "The indexer rejected the account."),
                    >= 200 and <= 203 => new NewznabException(NewznabFailure.ParametersRejected, "The indexer refused the request shape (t=" + function + ")."),
                    429 or 500 => RateLimited(host, "The indexer reached its request limit.", null, status),
                    _ => new NewznabException(NewznabFailure.Unavailable, "The indexer reported error " + code.ToString(CultureInfo.InvariantCulture) + ".")
                };
            }

            if (status != HttpStatusCode.OK)
            {
                throw new NewznabException(NewznabFailure.Unavailable, "The indexer answered HTTP " + ((int)status).ToString(CultureInfo.InvariantCulture) + ".");
            }

            return document;
        }

        // The whole host rests, not only this call; the exception carries the pause the indexer asked for.
        private NewznabException RateLimited(string host, string message, TimeSpan? retryAfter, HttpStatusCode status)
        {
            var pause = gate.Pause(host, retryAfter);
            return new NewznabException(NewznabFailure.RateLimited, message, retryAfter is null ? null : pause) { StatusCode = status };
        }

        private static XElement? Child(XElement? element, string name) => element?.Elements().FirstOrDefault(child => child.Name.LocalName == name);

        private static string? Text(XElement item, string name) => Child(item, name)?.Value.Trim() is { Length: > 0 } value ? value : null;

        private static long? Long(string? value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

        private static int? PositiveInt(string? value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : null;

        private static Uri? ResolveUri(string baseUrl, string value)
        {
            // A leading slash is relative to the indexer, never a local file path.
            if (!value.StartsWith('/') && Uri.TryCreate(value, UriKind.Absolute, out var absolute))
            {
                return absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps ? absolute : null;
            }

            return Uri.TryCreate(baseUrl.TrimEnd('/') + "/" + value.TrimStart('/'), UriKind.Absolute, out var relative) && (relative.Scheme == Uri.UriSchemeHttp || relative.Scheme == Uri.UriSchemeHttps)
                ? relative
                : null;
        }
    }
}
