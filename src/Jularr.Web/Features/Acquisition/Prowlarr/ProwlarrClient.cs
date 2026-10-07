using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Jularr.Web.Features.Acquisition.Prowlarr;

public interface IProwlarrClient
{
    Task<ProwlarrConnectionTestResult> TestAsync(
        ProwlarrConnection connection,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProwlarrReleaseCandidate>> SearchAsync(
        ProwlarrConnection connection,
        ProwlarrSearchQuery search,
        CancellationToken cancellationToken);
}

public sealed class ProwlarrClient(HttpClient httpClient) : IProwlarrClient
{
    public async Task<ProwlarrConnectionTestResult> TestAsync(
        ProwlarrConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateRequest(
                connection,
                HttpMethod.Get,
                "/api/v1/system/status");

            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new ProwlarrConnectionTestResult(
                    false,
                    Error: DescribeStatus(response.StatusCode));
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            var version = ReadString(document.RootElement, "version");

            return new ProwlarrConnectionTestResult(true, version);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            JsonException or
            UriFormatException)
        {
            return new ProwlarrConnectionTestResult(
                false,
                Error: "Prowlarr could not be reached or returned an invalid response.");
        }
    }

    public async Task<IReadOnlyList<ProwlarrReleaseCandidate>> SearchAsync(
        ProwlarrConnection connection,
        ProwlarrSearchQuery search,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(search);

        if (string.IsNullOrWhiteSpace(search.Query))
        {
            return [];
        }

        var settings = NormalizeAndValidate(connection.Settings);
        var path = BuildSearchPath(settings, search);

        using var request = CreateRequest(
            connection with { Settings = settings },
            HttpMethod.Get,
            path);

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new ProwlarrException(
                $"Prowlarr search failed with HTTP {(int)response.StatusCode}.");
        }

        try
        {
            return ParseSearchResponse(
                settings.BaseUrl,
                search.Query,
                body);
        }
        catch (JsonException exception)
        {
            throw new ProwlarrException(
                "Prowlarr returned invalid search JSON.",
                exception);
        }
    }

    public static IReadOnlyList<ProwlarrReleaseCandidate> ParseSearchResponse(
        string baseUrl,
        string query,
        string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Prowlarr search response must be an array.");
        }

        var releases = new List<ProwlarrReleaseCandidate>();

        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var title = ReadString(item, "title");
            if (string.IsNullOrWhiteSpace(title) ||
                !AnimeReleaseParser.TryParse(title, out var parsed))
            {
                continue;
            }

            var downloadUrl = ReadString(item, "downloadUrl");
            var magnetUrl = ReadString(item, "magnetUrl");

            releases.Add(
                new ProwlarrReleaseCandidate(
                    title,
                    ReadString(item, "indexer"),
                    ReadInt(item, "indexerId"),
                    ReadStringOrNumber(item, "protocol"),
                    ReadLong(item, "size"),
                    ReadInt(item, "seeders"),
                    ReadInt(item, "leechers"),
                    ReadDateTimeOffset(item, "publishDate"),
                    ReadInt(item, "age"),
                    ReadDouble(item, "ageHours"),
                    ReadString(item, "guid"),
                    ReadString(item, "infoUrl"),
                    parsed,
                    [query],
                    ResolveInternalUri(baseUrl, downloadUrl),
                    magnetUrl));
        }

        return releases;
    }

    public static ProwlarrSettings NormalizeAndValidate(ProwlarrSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!Uri.TryCreate(settings.BaseUrl?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                "Prowlarr Base URL must be an absolute HTTP(S) URL without credentials, query or fragment.",
                nameof(settings));
        }

        if (settings.SearchLimit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                "Prowlarr search limit must be between 1 and 1000.");
        }

        return settings with
        {
            BaseUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/'),
            Categories = (settings.Categories ?? []).Where(value => value > 0).Distinct().Order().ToArray(),
            IndexerIds = (settings.IndexerIds ?? []).Where(value => value > 0).Distinct().Order().ToArray()
        };
    }

    private static HttpRequestMessage CreateRequest(
        ProwlarrConnection connection,
        HttpMethod method,
        string pathAndQuery)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var settings = NormalizeAndValidate(connection.Settings);
        if (string.IsNullOrWhiteSpace(connection.ApiKey))
        {
            throw new ArgumentException(
                "Prowlarr API key is required.",
                nameof(connection));
        }

        var uri = new Uri(
            $"{settings.BaseUrl}/{pathAndQuery.TrimStart('/')}",
            UriKind.Absolute);

        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("X-Api-Key", connection.ApiKey.Trim());
        return request;
    }

    private static string BuildSearchPath(
        ProwlarrSettings settings,
        ProwlarrSearchQuery search)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("query", search.Query),
            new("type", "search"),
            new("limit", Math.Min(search.Limit ?? settings.SearchLimit, settings.SearchLimit).ToString(CultureInfo.InvariantCulture)),
            new("offset", Math.Max(0, search.Offset).ToString(CultureInfo.InvariantCulture))
        };

        parameters.AddRange(
            settings.IndexerIds.Select(
                id => new KeyValuePair<string, string>(
                    "indexerIds",
                    id.ToString(CultureInfo.InvariantCulture))));

        parameters.AddRange(
            settings.Categories.Select(
                category => new KeyValuePair<string, string>(
                    "categories",
                    category.ToString(CultureInfo.InvariantCulture))));

        return "/api/v1/search?" +
               string.Join(
                   "&",
                   parameters.Select(pair =>
                       $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    private static Uri? ResolveInternalUri(
        string baseUrl,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp ||
             absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute;
        }

        if (!Uri.TryCreate(
                $"{baseUrl.TrimEnd('/')}/{value.TrimStart('/')}",
                UriKind.Absolute,
                out var relativeResolved) ||
            (relativeResolved.Scheme != Uri.UriSchemeHttp &&
             relativeResolved.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return relativeResolved;
    }

    private static string DescribeStatus(HttpStatusCode statusCode) =>
        statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "Prowlarr rejected the API key.",
            HttpStatusCode.NotFound =>
                "Prowlarr API endpoint was not found. Check the Base URL.",
            _ =>
                $"Prowlarr returned HTTP {(int)statusCode}."
        };

    private static string? ReadString(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? ReadStringOrNumber(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static int? ReadInt(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
               int.TryParse(
                   value.GetString(),
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out number)
            ? number
            : null;
    }

    private static long? ReadLong(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
               long.TryParse(
                   value.GetString(),
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out number)
            ? number
            : null;
    }

    private static double? ReadDouble(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(
                   value.GetString(),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out number)
            ? number
            : null;
    }

    private static DateTimeOffset? ReadDateTimeOffset(
        JsonElement element,
        string propertyName)
    {
        var text = ReadString(element, propertyName);
        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var value)
            ? value
            : null;
    }
}
