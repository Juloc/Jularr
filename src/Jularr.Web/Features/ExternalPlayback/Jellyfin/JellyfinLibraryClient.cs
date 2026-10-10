using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Jularr.Web.Features.ExternalPlayback.Jellyfin;

public sealed record JellyfinServerIdentity(Guid Id, string Name);
public sealed record JellyfinViewer(Guid Id, string Name);
public sealed record JellyfinLibrary(Guid Id, string Name, string CollectionType);
public sealed record JellyfinMediaItem(
    Guid Id,
    string Type,
    string Name,
    IReadOnlyDictionary<string, string> ProviderIds,
    Guid? SeriesId,
    int? SeasonNumber,
    int? EpisodeNumber);
public sealed record JellyfinItemPage(
    int TotalRecordCount,
    IReadOnlyList<JellyfinMediaItem> Items);

public sealed class JellyfinLibraryClient(HttpClient client)
{
    public const int MaxPageSize = 100;
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    public async Task<JellyfinServerIdentity> GetServerIdentityAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        using var json = await ReadAsync(endpoint, "System/Info/Public", null, cancellationToken);
        var id = ReadId(json.RootElement, "Id");
        if (id is null)
        {
            throw new InvalidDataException("Jellyfin did not return a stable server identity.");
        }

        return new JellyfinServerIdentity(id.Value, ReadString(json.RootElement, "ServerName"));
    }

    public async Task<JellyfinViewer> GetViewerAsync(
        Uri endpoint,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var json = await ReadAsync(endpoint, "Users/Me", accessToken, cancellationToken);
        var id = ReadId(json.RootElement, "Id");
        if (id is null)
        {
            throw new InvalidDataException("Jellyfin did not return a stable viewer identity.");
        }

        return new JellyfinViewer(id.Value, ReadString(json.RootElement, "Name"));
    }

    public async Task<IReadOnlyList<JellyfinLibrary>> GetViewsAsync(
        Uri endpoint,
        string accessToken,
        Guid viewerId,
        CancellationToken cancellationToken)
    {
        RequireId(viewerId, nameof(viewerId));
        using var json = await ReadAsync(
            endpoint, $"Users/{viewerId:N}/Views", accessToken, cancellationToken);
        if (!json.RootElement.TryGetProperty("Items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Jellyfin returned no library view list.");
        }

        var views = new List<JellyfinLibrary>();
        foreach (var item in items.EnumerateArray().Take(101))
        {
            if (views.Count == 100)
            {
                throw new InvalidDataException("Jellyfin returned too many library views.");
            }

            var id = ReadId(item, "Id");
            var type = ReadString(item, "CollectionType");
            if (id is not null && type is "movies" or "tvshows")
            {
                views.Add(new JellyfinLibrary(id.Value, ReadString(item, "Name"), type));
            }
        }

        return views.OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Id).ToArray();
    }

    public async Task<JellyfinItemPage> GetItemsAsync(
        Uri endpoint,
        string accessToken,
        Guid viewerId,
        Guid libraryId,
        int offset,
        int pageSize,
        CancellationToken cancellationToken)
    {
        RequireId(viewerId, nameof(viewerId));
        RequireId(libraryId, nameof(libraryId));
        if (offset < 0 || pageSize is < 1 or > MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        var path = $"Users/{viewerId:N}/Items?ParentId={libraryId:N}"
            + $"&StartIndex={offset}&Limit={pageSize}"
            + "&Recursive=true&IncludeItemTypes=Movie,Series"
            + "&Fields=ProviderIds&SortBy=SortName&SortOrder=Ascending";
        using var json = await ReadAsync(endpoint, path, accessToken, cancellationToken);
        if (!json.RootElement.TryGetProperty("Items", out var items) ||
            items.ValueKind != JsonValueKind.Array ||
            !json.RootElement.TryGetProperty("TotalRecordCount", out var count) ||
            count.ValueKind != JsonValueKind.Number ||
            !count.TryGetInt32(out var total) || total < 0)
        {
            throw new InvalidDataException("Jellyfin returned an invalid item page.");
        }

        if (items.GetArrayLength() > pageSize)
        {
            throw new InvalidDataException("Jellyfin exceeded the requested item page size.");
        }

        return new JellyfinItemPage(total,
            items.EnumerateArray().Select(ParseItem).OfType<JellyfinMediaItem>().ToArray());
    }

    public async Task<JellyfinMediaItem?> GetItemAsync(
        Uri endpoint,
        string accessToken,
        Guid viewerId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        RequireId(viewerId, nameof(viewerId));
        RequireId(itemId, nameof(itemId));
        using var json = await ReadAsync(
            endpoint, $"Users/{viewerId:N}/Items/{itemId:N}?Fields=ProviderIds",
            accessToken, cancellationToken);
        var item = ParseItem(json.RootElement);
        return item?.Id == itemId ? item : null;
    }

    private async Task<JsonDocument> ReadAsync(
        Uri endpoint,
        string relativePath,
        string? token,
        CancellationToken cancellationToken)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri ||
            endpoint.Scheme != Uri.UriSchemeHttps ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 ||
            endpoint.Fragment.Length != 0 || endpoint.HostNameType == UriHostNameType.Unknown)
        {
            throw new ArgumentException("A verified HTTPS Jellyfin server endpoint is required.", nameof(endpoint));
        }

        if (token is not null && (token.Length is < 1 or > 2048 ||
            token.Any(c => c is <= ' ' or > '~')))
        {
            throw new ArgumentException("A valid Jellyfin access token is required.", nameof(token));
        }

        var baseUri = new UriBuilder(endpoint)
        {
            Path = endpoint.AbsolutePath.TrimEnd('/') + "/",
            Query = "",
            Fragment = ""
        }.Uri;
        var destination = new Uri(baseUri, relativePath);
        if (!string.Equals(destination.Authority, baseUri.Authority, StringComparison.OrdinalIgnoreCase) ||
            !destination.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Jellyfin API path escaped its verified server.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, destination);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Emby-Token", token);
        }

        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new HttpRequestException("Jellyfin redirected a credentialed request.", null, response.StatusCode);
        }

        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(MaxResponseBytes, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static JellyfinMediaItem? ParseItem(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Object ||
            ReadId(source, "Id") is not { } id ||
            ReadString(source, "Type") is not ("Movie" or "Series" or "Episode"))
        {
            return null;
        }

        var asType = ReadString(source, "Type");
        var providerIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source.TryGetProperty("ProviderIds", out var providers) &&
            providers.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in providers.EnumerateObject().Take(32))
            {
                if (property.Name is not ("Tmdb" or "Imdb" or "Tvdb") ||
                    property.Value.ValueKind != JsonValueKind.String ||
                    property.Value.GetString() is not { Length: > 0 and <= 160 } value ||
                    !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                {
                    continue;
                }

                providerIds[property.Name.ToLowerInvariant()] = value;
            }
        }

        return new JellyfinMediaItem(
            id, asType, ReadString(source, "Name"),
            providerIds, ReadId(source, "SeriesId"),
            ReadNumber(source, "ParentIndexNumber"),
            ReadNumber(source, "IndexNumber"));
    }

    private static Guid? ReadId(JsonElement source, string propertyName) =>
        source.ValueKind == JsonValueKind.Object &&
        source.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        Guid.TryParse(value.GetString(), out var id) && id != Guid.Empty ? id : null;

    private static string ReadString(JsonElement source, string propertyName) =>
        source.ValueKind == JsonValueKind.Object &&
        source.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static int? ReadNumber(JsonElement source, string propertyName) =>
        source.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number) && number >= 0 ? number : null;

    private static void RequireId(Guid id, string name)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A stable Jellyfin identity is required.", name);
        }
    }
}
