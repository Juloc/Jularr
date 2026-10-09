using System.Globalization;
using Jularr.Web.Features.Acquisition.Core;
using System.Net;
using System.Net.Http.Headers;
using System.Xml.Linq;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Providers;

namespace Jularr.Web.Features.Acquisition.Indexers;

/// <summary>
/// Direct Newznab (usenet) indexer client using the caps/search XML API.
/// Every result carries the usenet protocol per
/// <see cref="AcquisitionCandidate.Protocol"/>. Its HTTP calls run through the
/// shared <see cref="ProviderExecutor"/> (#438) for timeouts and bounded retries;
/// per-entry health stays in <c>AcquisitionHealthStore</c>, so framework health
/// tracking is left off here (see <see cref="ExecutionPolicy"/>).
/// </summary>
public sealed class NewznabIndexer(HttpClient httpClient, ProviderExecutor executor) : IIndexer, IExternalProvider
{
    /// <summary>
    /// Behaviour-preserving policy: retries only transient network faults (one extra attempt) so
    /// every HTTP status is still surfaced to the caller exactly as before. Per-entry health is
    /// owned by <c>AcquisitionHealthStore</c>, so framework health/circuit is disabled here to
    /// keep a single source of truth for indexer health.
    /// </summary>
    public static readonly ProviderExecutionPolicy ExecutionPolicy = new()
    {
        MaxAttempts = 2,
        BaseBackoff = TimeSpan.FromMilliseconds(250),
        RetryServerErrors = false,
        HonorRateLimitGate = false,
        TrackHealth = false,
        ShortCircuitWhenUnavailable = false
    };

    public IndexerType Type => IndexerType.Newznab;

    public ExternalProviderDescriptor Descriptor { get; } =
        new(ProviderKeys.Newznab, "Newznab indexer", ProviderCapabilities.Search);

    public async Task<IndexerConnectionTestResult> TestAsync(
        IndexerEntry entry,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await executor.SendAsync(
                ProviderKeys.Newznab,
                httpClient,
                () => CreateRequest(entry, "caps", []),
                ExecutionPolicy,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return Failure(DescribeStatus(response.StatusCode), response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => IndexerCheckState.AuthenticationFailed,
                    HttpStatusCode.TooManyRequests => IndexerCheckState.RateLimited,
                    _ => IndexerCheckState.Unavailable
                });
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var document = XDocument.Parse(body);
            if (string.Equals(document.Root?.Name.LocalName, "error", StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(document.Root!.Attribute("code")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code);
                var description = document.Root.Attribute("description")?.Value ?? document.Root.Value.Trim();
                return code switch
                {
                    >= 100 and < 200 => Failure(description, IndexerCheckState.AuthenticationFailed),
                    429 or 500 => Failure("The indexer reported a reached request limit.", IndexerCheckState.RateLimited),
                    _ => Failure(description, IndexerCheckState.InvalidResponse)
                };
            }

            if (!string.Equals(document.Root?.Name.LocalName, "caps", StringComparison.OrdinalIgnoreCase))
            {
                return Failure("It answered with something other than a caps document. Check that the address is the indexer's API address.", IndexerCheckState.InvalidResponse);
            }

            var version = document.Root!
                .Elements()
                .FirstOrDefault(element => element.Name.LocalName == "server")?
                .Attribute("version")?.Value;

            return new IndexerConnectionTestResult(true, version, Capabilities: NewznabCapsParser.Parse(document, DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (System.Xml.XmlException)
        {
            return Failure("It answered with data that is not Newznab XML, for example a web page. Check that the address is the indexer's API address.", IndexerCheckState.InvalidResponse);
        }
        catch (Exception exception) when (exception is HttpRequestException or UriFormatException or OperationCanceledException)
        {
            return Failure("No connection, or no answer in time.", IndexerCheckState.Unavailable);
        }
    }

    private static IndexerConnectionTestResult Failure(string error, IndexerCheckState state) => new(false, Error: error, State: state);

    public async Task<IReadOnlyList<AcquisitionCandidate>> SearchAsync(
        IndexerEntry entry,
        IndexerSearchQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(query);

        var parameters = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrWhiteSpace(query.Query))
        {
            parameters.Add(new("q", query.Query));
        }

        parameters.AddRange((query.Parameters ?? []).Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)));
        if (parameters.Count == 0 && !query.Latest)
        {
            return [];
        }

        var label = string.IsNullOrWhiteSpace(query.Query) ? string.Join(' ', parameters.Select(pair => $"{pair.Key}={pair.Value}")) : query.Query;
        if (entry.Settings.Categories.Length > 0)
        {
            parameters.Add(new("cat", string.Join(',', entry.Settings.Categories.Distinct().Select(category => category.ToString(CultureInfo.InvariantCulture)))));
        }

        parameters.Add(new("limit", (query.Limit ?? entry.Settings.SearchLimit).ToString(CultureInfo.InvariantCulture)));
        if (query.Offset > 0)
        {
            parameters.Add(new("offset", query.Offset.ToString(CultureInfo.InvariantCulture)));
        }

        var function = Function(query.Mode);
        using var response = await executor.SendAsync(
            ProviderKeys.Newznab,
            httpClient,
            () => CreateRequest(entry, function, parameters),
            ExecutionPolicy,
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new IndexerAuthenticationException($"'{entry.Name}' rejected the API key.");
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new IndexerRateLimitedException($"'{entry.Name}' is rate limited.", RetryAfter(response));
        }

        try
        {
            // An error document counts whatever the HTTP status: indexers answer 200, 400 or 500 with the same <error> body.
            if (!response.IsSuccessStatusCode && !body.TrimStart().StartsWith('<'))
            {
                throw new IndexerException($"'{entry.Name}' search failed with HTTP {(int)response.StatusCode}.");
            }

            return ParseSearchResponse(entry, label, body, query.Mode, RequestShape(function, parameters));
        }
        catch (System.Xml.XmlException exception)
        {
            throw new IndexerException($"'{entry.Name}' returned a response that is not Newznab XML (the Base URL may point at a web page instead of the API).", exception);
        }
    }

    /// <summary>The request in words that are safe to show: the function and the parameters sent, never the API key.</summary>
    public static string RequestShape(string function, IEnumerable<KeyValuePair<string, string>> parameters) =>
        $"t={function} " + string.Join(' ', parameters.Select(pair => $"{pair.Key}={pair.Value}"));

    public static IReadOnlyList<AcquisitionCandidate> ParseSearchResponse(
        IndexerEntry entry,
        string query,
        string xml,
        IndexerSearchMode mode = IndexerSearchMode.Search,
        string requestShape = "")
    {
        var document = XDocument.Parse(xml);
        ThrowWhenError(entry, document, mode, requestShape);
        if (!string.Equals(document.Root?.Name.LocalName, "rss", StringComparison.OrdinalIgnoreCase))
        {
            throw new IndexerException($"'{entry.Name}' returned a response that is not Newznab XML (the Base URL may point at a web page instead of the API).");
        }

        var items = document.Descendants().Where(element => element.Name.LocalName == "item");
        var releases = new List<AcquisitionCandidate>();
        const string protocol = "usenet";
        var now = DateTimeOffset.UtcNow;

        foreach (var item in items)
        {
            var title = Text(item, "title");
            if (string.IsNullOrWhiteSpace(title) || !AnimeReleaseParser.TryParse(title, out var parsed))
            {
                continue;
            }

            var attrs = item.Elements()
                .Where(element => element.Name.LocalName == "attr")
                .ToDictionary(
                    element => element.Attribute("name")?.Value ?? string.Empty,
                    element => element.Attribute("value")?.Value ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase);

            var enclosure = item.Elements().FirstOrDefault(element => element.Name.LocalName == "enclosure");
            var link = enclosure?.Attribute("url")?.Value ?? Text(item, "link");
            var guid = Text(item, "guid");
            var publishedAt = ParseDate(Text(item, "pubDate"));
            var size = ReadLong(attrs, "size") ?? ReadLong(enclosure?.Attribute("length")?.Value);
            var seeders = ReadInt(attrs, "seeders");
            var peers = ReadInt(attrs, "peers");
            var leechers = ReadInt(attrs, "leechers") ?? (peers is int p && seeders is int s ? Math.Max(0, p - s) : null);

            Uri? downloadUri = null;
            string? magnetUri = null;
            if (!string.IsNullOrWhiteSpace(link))
            {
                if (link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    magnetUri = link;
                }
                else
                {
                    downloadUri = ResolveInternalUri(entry.Settings.BaseUrl, link);
                }
            }

            releases.Add(
                new AcquisitionCandidate(
                    title,
                    entry.Name,
                    null,
                    protocol,
                    size,
                    seeders,
                    leechers,
                    publishedAt,
                    publishedAt is { } published ? (int)Math.Max(0, (now - published).TotalDays) : null,
                    publishedAt is { } publishedH ? Math.Max(0, (now - publishedH).TotalHours) : null,
                    string.IsNullOrWhiteSpace(guid) ? null : guid,
                    Text(item, "comments") ?? Text(item, "link"),
                    parsed,
                    [query],
                    downloadUri,
                    magnetUri));
        }

        return releases;
    }

    /// <summary>The function name of the <c>t=</c> parameter for a search mode.</summary>
    public static string Function(IndexerSearchMode mode) =>
        mode switch
        {
            IndexerSearchMode.TvSearch => "tvsearch",
            IndexerSearchMode.Movie => "movie",
            IndexerSearchMode.Book => "book",
            IndexerSearchMode.Music => "music",
            _ => "search"
        };

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        return header?.Date is { } date && date > DateTimeOffset.UtcNow ? date - DateTimeOffset.UtcNow : null;
    }

    // Newznab reports many failures as an HTTP 200 <error code="..."/> document: 1xx is a credential or account problem, 200-203 a request the
    // indexer refuses by shape (missing or wrong parameter, a function it does not have), 429/500 a reached request limit. Anything else is an
    // ordinary failure of this indexer.
    private static void ThrowWhenError(IndexerEntry entry, XDocument document, IndexerSearchMode mode, string requestShape)
    {
        if (!string.Equals(document.Root?.Name.LocalName, "error", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = int.TryParse(document.Root!.Attribute("code")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code);
        var description = document.Root.Attribute("description")?.Value ?? (string.IsNullOrWhiteSpace(document.Root.Value) ? null : document.Root.Value.Trim());
        throw code switch
        {
            >= 100 and < 200 => new IndexerAuthenticationException($"'{entry.Name}' rejected the account: {description ?? "invalid credentials"}."),
            200 => Rejected(entry, IndexerRejection.MissingParameter, mode, requestShape, description, "needs a parameter that was not sent"),
            201 => Rejected(entry, IndexerRejection.IncorrectParameter, mode, requestShape, description, "refused a parameter value"),
            202 or 203 => Rejected(entry, IndexerRejection.UnsupportedFunction, mode, requestShape, description, "does not offer this search function"),
            429 or 500 => new IndexerRateLimitedException($"'{entry.Name}' reached its request limit.", null),
            _ => new IndexerException($"'{entry.Name}' reported an error: {description ?? code.ToString(CultureInfo.InvariantCulture)}.")
        };
    }

    private static IndexerRequestRejectedException Rejected(IndexerEntry entry, IndexerRejection reason, IndexerSearchMode mode, string shape, string? description, string meaning) =>
        new($"'{entry.Name}' {meaning} ({description ?? reason.ToString()}). Request: {shape.Trim()}.", reason, mode, shape, description);

    private static HttpRequestMessage CreateRequest(
        IndexerEntry entry,
        string operation,
        IReadOnlyList<KeyValuePair<string, string>> parameters)
    {
        var query = new List<KeyValuePair<string, string>>
        {
            new("t", operation),
            new("apikey", entry.ApiKey.Trim()),
            new("o", "xml")
        };
        query.AddRange(parameters);

        var uri = new Uri(
            $"{ApiEndpoint(entry.Settings.BaseUrl)}?" +
            string.Join(
                "&",
                query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")),
            UriKind.Absolute);

        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        return request;
    }

    /// <summary>The API address of a Base URL: a custom path is kept, and a Base URL that already ends in <c>/api</c> is not given a second one.</summary>
    public static string ApiEndpoint(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        return trimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? trimmed : $"{trimmed}/api";
    }

    private static string? Text(XElement item, string localName) =>
        item.Elements()
            .FirstOrDefault(element => element.Name.LocalName == localName)?
            .Value.Trim() is { Length: > 0 } value
            ? value
            : null;

    private static long? ReadLong(IReadOnlyDictionary<string, string> attrs, string name) =>
        attrs.TryGetValue(name, out var value) ? ReadLong(value) : null;

    private static long? ReadLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static int? ReadInt(IReadOnlyDictionary<string, string> attrs, string name) =>
        attrs.TryGetValue(name, out var value) &&
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;

    private static Uri? ResolveInternalUri(string baseUrl, string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute;
        }

        return Uri.TryCreate(
            $"{baseUrl.TrimEnd('/')}/{value.TrimStart('/')}",
            UriKind.Absolute,
            out var relative) &&
            (relative.Scheme == Uri.UriSchemeHttp || relative.Scheme == Uri.UriSchemeHttps)
            ? relative
            : null;
    }

    private static string DescribeStatus(HttpStatusCode statusCode) =>
        statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"HTTP {(int)statusCode}.",
            HttpStatusCode.NotFound => "HTTP 404: the API endpoint was not found. Check the address.",
            _ => $"HTTP {(int)statusCode}."
        };
}
