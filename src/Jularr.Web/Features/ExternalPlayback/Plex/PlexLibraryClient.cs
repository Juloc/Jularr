using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Jularr.Web.Features.ExternalPlayback.Plex;

/// <summary>
/// Read-only Plex Media Server library adapter. The caller supplies a previously
/// verified HTTPS server URL and a user-scoped or administrator-scoped token.
/// Never accept arbitrary user-supplied URLs at a public HTTP endpoint.
/// </summary>
public sealed class PlexLibraryClient(HttpClient client)
{
    public const int MaxPageSize = 200;
    private const int MaxResponseBytes = 8 * 1024 * 1024;

    public async Task<IReadOnlyList<PlexLibrarySection>> GetSectionsAsync(
        Uri server,
        string accessToken,
        string clientIdentifier,
        CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync(
            server, "library/sections", accessToken, clientIdentifier, cancellationToken);

        if (!json.RootElement.TryGetProperty("MediaContainer", out var root) ||
            !root.TryGetProperty("Directory", out var sections) ||
            sections.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return sections.EnumerateArray()
            .Select(section => new PlexLibrarySection(
                GetString(section, "key"),
                GetString(section, "title"),
                GetString(section, "type")))
            .Where(section => IsSafeSectionId(section.Id) &&
                section.Type is "movie" or "show")
            .ToArray();
    }

    public async Task<PlexLibraryPage> GetItemsAsync(
        Uri server,
        string accessToken,
        string clientIdentifier,
        string sectionId,
        int offset,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (!IsSafeSectionId(sectionId))
        {
            throw new ArgumentException(
                "A numeric Plex library section is required.",
                nameof(sectionId));
        }

        if (offset < 0 || pageSize is < 1 or > MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize), "The Plex library page is out of range.");
        }

        var path = $"library/sections/{sectionId}/all"
            + $"?X-Plex-Container-Start={offset}&X-Plex-Container-Size={pageSize}";

        using var json = await GetJsonAsync(
            server, path, accessToken, clientIdentifier, cancellationToken);

        if (!json.RootElement.TryGetProperty("MediaContainer", out var root))
        {
            throw new InvalidDataException("Plex returned no library container.");
        }

        var total = GetInt(root, "totalSize") ?? 0;
        if (!root.TryGetProperty("Metadata", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return new PlexLibraryPage(total, []);
        }

        var entries = items.EnumerateArray()
            .Select(ToItem)
            .Where(item => item is not null)
            .Cast<PlexLibraryItem>()
            .ToArray();

        return new PlexLibraryPage(total, entries);
    }

    private async Task<JsonDocument> GetJsonAsync(
        Uri server,
        string path,
        string token,
        string identifier,
        CancellationToken cancellationToken)
    {
        if (!server.IsAbsoluteUri || server.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrWhiteSpace(server.UserInfo) ||
            server.Fragment.Length > 0 || server.Query.Length > 0)
        {
            throw new ArgumentException(
                "Use a previously verified HTTPS Plex server endpoint.",
                nameof(server));
        }

        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048 ||
            token.Any(c => c is <= ' ' or > '~'))
        {
            throw new ArgumentException("The Plex access token is invalid.", nameof(token));
        }

        if (string.IsNullOrWhiteSpace(identifier) || identifier.Length > 120 ||
            identifier.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
        {
            throw new ArgumentException("The Plex client identifier is invalid.", nameof(identifier));
        }

        var root = new UriBuilder(server) { Path = "/", Query = "", Fragment = "" }.Uri;
        var uri = new Uri(root, path);
        if (uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Authority, root.Authority, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Plex library URL escaped the server.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Plex-Product", "Jularr");
        request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", identifier);
        request.Headers.TryAddWithoutValidation("X-Plex-Token", token);

        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.MovedPermanently
            or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect)
        {
            throw new HttpRequestException("Plex redirected a credentialed request.");
        }

        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(MaxResponseBytes, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(
            stream, cancellationToken: cancellationToken);
    }

    private static PlexLibraryItem? ToItem(JsonElement item)
    {
        var ratingKey = GetString(item, "ratingKey");
        var mediaType = GetString(item, "type");
        if (string.IsNullOrWhiteSpace(ratingKey) ||
            !ratingKey.All(char.IsAsciiDigit) ||
            mediaType is not ("movie" or "show" or "episode"))
        {
            return null;
        }

        var ids = new List<PlexExternalId>();
        if (item.TryGetProperty("Guid", out var guids) &&
            guids.ValueKind == JsonValueKind.Array)
        {
            foreach (var guid in guids.EnumerateArray())
            {
                var raw = GetString(guid, "id");
                var index = raw.IndexOf("://", StringComparison.Ordinal);
                if (index <= 0 || index > 32)
                {
                    continue;
                }

                var provider = raw[..index].ToLowerInvariant();
                var externalId = raw[(index + 3)..];
                if (provider is "tmdb" or "imdb" or "tvdb" &&
                    externalId.Length is > 0 and <= 160 &&
                    externalId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                {
                    ids.Add(new PlexExternalId(provider, externalId));
                }
            }
        }

        return new PlexLibraryItem(
            ratingKey,
            mediaType,
            GetString(item, "title"),
            GetInt(item, "year"),
            ids.Distinct().ToArray());
    }

    private static bool IsSafeSectionId(string section) =>
        !string.IsNullOrWhiteSpace(section) && section.Length <= 12 &&
        section.All(char.IsAsciiDigit);

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";

    private static int? GetInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt32(out var value)
            ? value
            : null;
}

public sealed record PlexLibrarySection(string Id, string Title, string Type);
public sealed record PlexExternalId(string Provider, string Id);
public sealed record PlexLibraryItem(
    string RatingKey,
    string Type,
    string Title,
    int? Year,
    IReadOnlyList<PlexExternalId> ExternalIds);
public sealed record PlexLibraryPage(int TotalSize, IReadOnlyList<PlexLibraryItem> Items);
