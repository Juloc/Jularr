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

    public async Task<string> GetServerIdentityAsync(
        Uri server,
        string accessToken,
        string clientIdentifier,
        CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync(
            server, "identity", accessToken, clientIdentifier, cancellationToken);
        if (!json.RootElement.TryGetProperty("MediaContainer", out var root))
        {
            throw new InvalidDataException("Plex returned no server identity.");
        }

        var machineId = GetString(root, "machineIdentifier");
        if (machineId.Length is < 8 or > 160 ||
            !machineId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new InvalidDataException(
                "Plex returned an invalid server machine identifier.");
        }

        return machineId;
    }

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
            + $"?X-Plex-Container-Start={offset}&X-Plex-Container-Size={pageSize}&includeGuids=1";

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
            return new PlexLibraryPage(total, 0, []);
        }

        if (items.GetArrayLength() > pageSize)
        {
            throw new InvalidDataException("Plex exceeded the requested item page size.");
        }

        var entries = items.EnumerateArray()
            .Select(ToItem)
            .OfType<PlexLibraryItem>()
            .ToArray();

        return new PlexLibraryPage(total, items.GetArrayLength(), entries);
    }

    /// <summary>
    /// Short, explicit, user-triggered Plex search. The title is only a
    /// candidate retrieval hint; every result must be resolved by confirmed
    /// external GUIDs and independently reauthorized before handoff.
    /// No background full-library traversal occurs.
    /// </summary>
    public async Task<IReadOnlyList<PlexLibraryItem>> FindCandidatesAsync(
        Uri server,
        string accessToken,
        string clientIdentifier,
        string sectionId,
        string title,
        CancellationToken cancellationToken)
    {
        if (!IsSafeSectionId(sectionId) ||
            string.IsNullOrWhiteSpace(title) ||
            title.Length > 160)
        {
            throw new ArgumentException(
                "A valid Plex library section and title are required.");
        }

        const int maximumCandidates = 24;
        var path = $"library/sections/{sectionId}/all"
            + "?includeGuids=1"
            + "&X-Plex-Container-Start=0"
            + $"&X-Plex-Container-Size={maximumCandidates}"
            + "&title=" + Uri.EscapeDataString(title.Trim());

        using var json = await GetJsonAsync(
            server, path, accessToken, clientIdentifier, cancellationToken);
        if (!json.RootElement.TryGetProperty("MediaContainer", out var root) ||
            !root.TryGetProperty("Metadata", out var metadata) ||
            metadata.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return metadata.EnumerateArray()
            .Take(maximumCandidates)
            .Select(ToItem)
            .OfType<PlexLibraryItem>()
            .ToArray();
    }

    /// <summary>
    /// Re-reads an exact Plex item with the current profile's token. The
    /// returned library section must be checked against the intersection of
    /// Admin-approved and user-accessible libraries before offering playback.
    /// </summary>
    public async Task<PlexLibraryItem?> GetItemAsync(
        Uri server,
        string accessToken,
        string clientIdentifier,
        string ratingKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ratingKey) ||
            ratingKey.Length > 18 ||
            !ratingKey.All(char.IsAsciiDigit))
        {
            throw new ArgumentException(
                "The Plex item key must be numeric.", nameof(ratingKey));
        }

        using var json = await GetJsonAsync(
            server,
            $"library/metadata/{ratingKey}?includeGuids=1",
            accessToken,
            clientIdentifier,
            cancellationToken);

        if (!json.RootElement.TryGetProperty("MediaContainer", out var container) ||
            !container.TryGetProperty("Metadata", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // An exact item must not claim a different section from its container.
        var containerSection = GetNumericId(container, "librarySectionID");
        var matches = entries.EnumerateArray()
            .Select(ToItem)
            .OfType<PlexLibraryItem>()
            .Where(item => item.RatingKey == ratingKey)
            .Take(2)
            .ToArray();
        if (matches.Length != 1 ||
            container.TryGetProperty("librarySectionID", out _) && containerSection is null)
        {
            return null;
        }

        var item = matches[0];
        if (item.LibrarySectionId is { } itemSection &&
            containerSection is { } parentSection &&
            !string.Equals(itemSection, parentSection, StringComparison.Ordinal))
        {
            return null;
        }

        return item with { LibrarySectionId = item.LibrarySectionId ?? containerSection };
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

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(client.Timeout);

        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.MovedPermanently
            or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect)
        {
            throw new HttpRequestException("Plex redirected a credentialed request.");
        }

        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(MaxResponseBytes, timeout.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        return await JsonDocument.ParseAsync(
            stream, cancellationToken: timeout.Token);
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
            ids.Distinct().ToArray(),
            GetNumericId(item, "librarySectionID"));
    }

    private static bool IsSafeSectionId(string section) =>
        !string.IsNullOrWhiteSpace(section) && section.Length <= 12 &&
        section.All(char.IsAsciiDigit);

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";

    private static string? GetNumericId(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var id))
        {
            return null;
        }

        var value = id.ValueKind switch
        {
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Number when id.TryGetInt64(out var number) && number > 0 =>
                number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => null
        };
        return value is { Length: > 0 and <= 12 } &&
            value.All(char.IsAsciiDigit) ? value : null;
    }

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
    IReadOnlyList<PlexExternalId> ExternalIds,
    string? LibrarySectionId = null);
public sealed record PlexLibraryPage(int TotalSize, int ReturnedSize, IReadOnlyList<PlexLibraryItem> Items);
