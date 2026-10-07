using Jularr.Web.Features.Discovery;
using System.Net.Http.Json;
using System.Text.Json;

namespace Jularr.Web.Features.Metadata;

public sealed class MetadataProviderException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class AniListMetadataProvider(
    HttpClient httpClient,
    ILogger<AniListMetadataProvider> logger) : IAnimeMetadataProvider
{
    public const string ProviderKey = "anilist";
    private const int MaximumSearchLimit = 12;
    private const int MaximumBrowseLimit = 24;

    private const string SearchQuery = """
        query ($search: String!, $perPage: Int!, $genre: [String]) {
          Page(page: 1, perPage: $perPage) {
            media(search: $search, type: ANIME, isAdult: false, genre_in: $genre) {
              id
              title { romaji english native }
              description(asHtml: false)
              coverImage { extraLarge large }
              bannerImage
              trailer { id site }
              format
              status
              season
              seasonYear
              averageScore
              episodes
              duration
              isAdult
            }
          }
        }
        """;

    /// <summary>One page of anime for Discover: every filter is optional (a null variable is no filter to AniList) and the page reports whether another follows.</summary>
    private const string DiscoverQuery = """
        query ($page: Int!, $perPage: Int!, $search: String, $sort: [MediaSort!], $genre: [String], $status: [MediaStatus], $startFrom: FuzzyDateInt, $startTo: FuzzyDateInt, $popularityMin: Int) {
          Page(page: $page, perPage: $perPage) {
            pageInfo { hasNextPage }
            media(type: ANIME, isAdult: false, search: $search, sort: $sort, genre_in: $genre, status_in: $status, startDate_greater: $startFrom, startDate_lesser: $startTo, popularity_greater: $popularityMin) {
              id
              title { romaji english native }
              description(asHtml: false)
              coverImage { extraLarge large }
              bannerImage
              trailer { id site }
              format
              status
              season
              seasonYear
              averageScore
              episodes
              duration
              isAdult
            }
          }
        }
        """;

    private const string ByIdQuery = """
        query ($id: Int!) {
          Media(id: $id, type: ANIME) {
            id
            title { romaji english native }
            description(asHtml: false)
            coverImage { extraLarge large }
            bannerImage
            format
            status
            season
            seasonYear
            averageScore
            episodes
            duration
            isAdult
          }
        }
        """;

    private const string ByMalIdQuery = """
        query ($idMal: Int!) {
          Media(idMal: $idMal, type: ANIME) {
            id
            title { romaji english native }
            description(asHtml: false)
            coverImage { extraLarge large }
            bannerImage
            format
            status
            season
            seasonYear
            averageScore
            episodes
            duration
            isAdult
          }
        }
        """;

    private const string SequenceQuery = """
        query ($id: Int!) {
          Media(id: $id, type: ANIME) {
            id
            title { romaji english native }
            description(asHtml: false)
            coverImage { extraLarge large }
            bannerImage
            format
            status
            season
            seasonYear
            averageScore
            episodes
            duration
            isAdult
            relations {
              edges {
                relationType
                node {
                  id
                  type
                  title { romaji english native }
                  description(asHtml: false)
                  coverImage { extraLarge large }
                  bannerImage
                  format
                  status
                  season
                  seasonYear
                  averageScore
                  episodes
                  duration
                  isAdult
                }
              }
            }
          }
        }
        """;

    public string Key => ProviderKey;

    public async Task<IReadOnlyList<AnimeMetadataCandidate>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken) =>
        await SearchAsync(query, limit, null, cancellationToken);

    public async Task<IReadOnlyList<AnimeMetadataCandidate>> SearchAsync(
        string query,
        int limit,
        string? genre,
        CancellationToken cancellationToken)
    {
        var normalized = query.Trim();
        if (normalized.Length == 0)
        {
            return [];
        }

        var response = await SendAsync(
            SearchQuery,
            new
            {
                search = normalized,
                perPage = Math.Clamp(limit, 1, MaximumSearchLimit),
                genre = GenreVariable(genre)
            },
            cancellationToken);

        return ParseSearchResponse(response);
    }

    /// <summary>One Discover page (a browse view or a search) with the viewer's filters applied by AniList itself, so paging stays truthful.</summary>
    public async Task<DiscoveryProviderPage<AnimeMetadataCandidate>> DiscoverPageAsync(
        AniListDiscoveryOptions options,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(DiscoverQuery, options.Variables(), cancellationToken);
        return new DiscoveryProviderPage<AnimeMetadataCandidate>(ParseSearchResponse(response), AniListPageInfo.HasNextPage(response));
    }

    // AniList treats a null genre_in as "no filter"; an empty array would match nothing.
    private static string[]? GenreVariable(string? genre) =>
        string.IsNullOrWhiteSpace(genre) ? null : [genre];

    public async Task<AnimeMetadataCandidate?> GetAsync(
        string externalId,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(externalId, out var id) || id <= 0)
        {
            return null;
        }

        var response = await SendAsync(
            ByIdQuery,
            new { id },
            cancellationToken);

        return ParseMediaResponse(response);
    }

    // Looks an AniList entry up by its MyAnimeList ID, for a local NFO that only carries a MAL ID.
    public async Task<AnimeMetadataCandidate?> GetByMalIdAsync(
        string malId,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(malId, out var idMal) || idMal <= 0)
        {
            return null;
        }

        var response = await SendAsync(
            ByMalIdQuery,
            new { idMal },
            cancellationToken);

        return ParseMediaResponse(response);
    }

    public async Task<IReadOnlyList<AnimeMetadataCandidate>> GetLinearSequenceAsync(
        string externalId,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(externalId, out var rootId) || rootId <= 0)
        {
            return [];
        }

        var cache = new Dictionary<int, SequenceMedia>();

        async Task<SequenceMedia?> LoadAsync(int id)
        {
            if (cache.TryGetValue(id, out var cached))
            {
                return cached;
            }

            var response = await SendAsync(
                SequenceQuery,
                new { id },
                cancellationToken);

            using var document = JsonDocument.Parse(response);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("Media", out var media) ||
                media.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var candidate = ParseMedia(media);
            if (candidate is null)
            {
                return null;
            }

            var prequels = new List<int>();
            var sequels = new List<int>();

            if (media.TryGetProperty("relations", out var relations) &&
                relations.ValueKind == JsonValueKind.Object &&
                relations.TryGetProperty("edges", out var edges) &&
                edges.ValueKind == JsonValueKind.Array)
            {
                foreach (var edge in edges.EnumerateArray())
                {
                    var relationType = ReadString(edge, "relationType");
                    if (relationType is not ("PREQUEL" or "SEQUEL") ||
                        !edge.TryGetProperty("node", out var node) ||
                        node.ValueKind != JsonValueKind.Object ||
                        !string.Equals(
                            ReadString(node, "type"),
                            "ANIME",
                            StringComparison.OrdinalIgnoreCase) ||
                        (node.TryGetProperty("isAdult", out var adult) &&
                         adult.ValueKind == JsonValueKind.True) ||
                        !node.TryGetProperty("id", out var idElement) ||
                        !idElement.TryGetInt32(out var relationId))
                    {
                        continue;
                    }

                    if (relationType == "PREQUEL")
                    {
                        prequels.Add(relationId);
                    }
                    else
                    {
                        sequels.Add(relationId);
                    }
                }
            }

            var loaded = new SequenceMedia(
                candidate,
                prequels.Distinct().ToArray(),
                sequels.Distinct().ToArray());
            cache[id] = loaded;
            return loaded;
        }

        var root = await LoadAsync(rootId);
        if (root is null)
        {
            return [];
        }

        const int maximumEntries = 12;
        var before = new List<AnimeMetadataCandidate>();
        var visited = new HashSet<int> { rootId };
        var current = root;

        while (before.Count < maximumEntries - 1 &&
               current.Prequels.Count == 1)
        {
            var previousId = current.Prequels[0];
            if (!visited.Add(previousId))
            {
                break;
            }

            var previous = await LoadAsync(previousId);
            if (previous is null)
            {
                break;
            }

            before.Insert(0, previous.Candidate);
            current = previous;
        }

        var result = new List<AnimeMetadataCandidate>(before.Count + 1);
        result.AddRange(before);
        result.Add(root.Candidate);

        current = root;
        while (result.Count < maximumEntries &&
               current.Sequels.Count == 1)
        {
            var nextId = current.Sequels[0];
            if (!visited.Add(nextId))
            {
                break;
            }

            var next = await LoadAsync(nextId);
            if (next is null)
            {
                break;
            }

            result.Add(next.Candidate);
            current = next;
        }

        return result;
    }

    public async Task<IReadOnlyList<AniListAnimeRelation>> GetRelatedAnimeAsync(
        string externalId,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(externalId, out var id) || id <= 0)
        {
            return [];
        }

        var response = await SendAsync(
            SequenceQuery,
            new { id },
            cancellationToken);

        using var document = JsonDocument.Parse(response);
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("Media", out var media) ||
            media.ValueKind != JsonValueKind.Object ||
            !media.TryGetProperty("relations", out var relations) ||
            relations.ValueKind != JsonValueKind.Object ||
            !relations.TryGetProperty("edges", out var edges) ||
            edges.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<AniListAnimeRelation>();
        foreach (var edge in edges.EnumerateArray())
        {
            var relationType = ReadString(edge, "relationType");
            if (string.IsNullOrWhiteSpace(relationType) ||
                !edge.TryGetProperty("node", out var node) ||
                node.ValueKind != JsonValueKind.Object ||
                !string.Equals(
                    ReadString(node, "type"),
                    "ANIME",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = ParseMedia(node);
            if (candidate is null)
            {
                continue;
            }

            result.Add(new AniListAnimeRelation(
                relationType,
                candidate));
        }

        return result;
    }

    /// <summary>
    /// The anime and its related AniList entries in one request. Adult entries are skipped; the
    /// entry itself is null when AniList does not return it.
    /// </summary>
    public async Task<AniListRelatedMedia> GetRelatedMediaAsync(
        string externalId,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(externalId, out var id) || id <= 0)
        {
            return new AniListRelatedMedia(null, []);
        }

        var response = await SendAsync(
            SequenceQuery,
            new { id },
            cancellationToken);

        using var document = JsonDocument.Parse(response);
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("Media", out var media) ||
            media.ValueKind != JsonValueKind.Object)
        {
            return new AniListRelatedMedia(null, []);
        }

        var self = ReadMediaSummary(media, "ANIME");
        if (!media.TryGetProperty("relations", out var relations) ||
            relations.ValueKind != JsonValueKind.Object ||
            !relations.TryGetProperty("edges", out var edges) ||
            edges.ValueKind != JsonValueKind.Array)
        {
            return new AniListRelatedMedia(self, []);
        }

        var result = new List<AniListMediaRelation>();
        foreach (var edge in edges.EnumerateArray())
        {
            var relationType = ReadString(edge, "relationType");
            if (string.IsNullOrWhiteSpace(relationType) ||
                !edge.TryGetProperty("node", out var node) ||
                node.ValueKind != JsonValueKind.Object ||
                ReadMediaSummary(node, ReadString(node, "type")) is not { } related)
            {
                continue;
            }

            result.Add(new AniListMediaRelation(relationType, related));
        }

        return new AniListRelatedMedia(self, result);
    }

    private static AniListMediaSummary? ReadMediaSummary(JsonElement node, string? mediaType)
    {
        if (mediaType is not ("ANIME" or "MANGA") ||
            (node.TryGetProperty("isAdult", out var adult) && adult.ValueKind == JsonValueKind.True) ||
            !node.TryGetProperty("id", out var idElement) ||
            !idElement.TryGetInt32(out var id))
        {
            return null;
        }

        var titleObject = node.TryGetProperty("title", out var titleElement)
            ? titleElement
            : default;
        var native = ReadString(titleObject, "native");
        var title = AnimeMetadataTitles.Choose(
            ReadString(titleObject, "english"),
            ReadString(titleObject, "romaji"),
            native,
            $"AniList {id}");

        string? cover = null;
        if (node.TryGetProperty("coverImage", out var coverElement) &&
            coverElement.ValueKind == JsonValueKind.Object)
        {
            cover = ReadString(coverElement, "extraLarge")
                ?? ReadString(coverElement, "large");
        }

        return new AniListMediaSummary(
            mediaType,
            id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            title,
            native,
            cover,
            ReadString(node, "format"),
            ReadString(node, "status"),
            ReadInt(node, "seasonYear"));
    }

    private async Task<string> SendAsync(
        string query,
        object variables,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "",
                new { query, variables },
                cancellationToken);

            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "AniList metadata request failed with HTTP {StatusCode}.",
                    (int)response.StatusCode);
                throw new MetadataProviderException(
                    $"AniList returned HTTP {(int)response.StatusCode}.");
            }

            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Array &&
                errors.GetArrayLength() > 0)
            {
                var message = errors[0].TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : null;

                throw new MetadataProviderException(
                    string.IsNullOrWhiteSpace(message)
                        ? "AniList returned a GraphQL error."
                        : $"AniList: {message}");
            }

            return body;
        }
        catch (MetadataProviderException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            TaskCanceledException or
            JsonException)
        {
            logger.LogWarning(exception, "AniList metadata request failed.");
            throw new MetadataProviderException(
                "AniList metadata is currently unavailable.",
                exception);
        }
    }

    public static IReadOnlyList<AnimeMetadataCandidate> ParseSearchResponse(string json)
    {
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("Page", out var page) ||
            page.ValueKind == JsonValueKind.Null ||
            !page.TryGetProperty("media", out var media) ||
            media.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return media.EnumerateArray()
            .Select(ParseMedia)
            .Where(candidate => candidate is not null)
            .Cast<AnimeMetadataCandidate>()
            .ToArray();
    }

    public static AnimeMetadataCandidate? ParseMediaResponse(string json)
    {
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("Media", out var media) ||
            media.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return ParseMedia(media);
    }

    private static AnimeMetadataCandidate? ParseMedia(JsonElement media)
    {
        if (!media.TryGetProperty("id", out var idElement) ||
            !idElement.TryGetInt32(out var id))
        {
            return null;
        }

        if (media.TryGetProperty("isAdult", out var adultElement) &&
            adultElement.ValueKind is JsonValueKind.True)
        {
            return null;
        }

        var title = media.TryGetProperty("title", out var titleElement)
            ? titleElement
            : default;

        var romaji = ReadString(title, "romaji");
        var english = ReadString(title, "english");
        var native = ReadString(title, "native");
        var preferred = AnimeMetadataTitles.Choose(
            english,
            romaji,
            native,
            $"AniList {id}");

        string? cover = null;
        if (media.TryGetProperty("coverImage", out var coverElement) &&
            coverElement.ValueKind == JsonValueKind.Object)
        {
            cover = ReadString(coverElement, "large")
                ?? ReadString(coverElement, "extraLarge");
        }

        return new AnimeMetadataCandidate(
            ProviderKey,
            id.ToString(),
            preferred,
            romaji,
            english,
            native,
            ReadString(media, "description"),
            cover,
            ReadString(media, "bannerImage"),
            ReadString(media, "format"),
            ReadString(media, "status"),
            ReadString(media, "season"),
            ReadInt(media, "seasonYear"),
            ReadInt(media, "episodes"),
            ReadInt(media, "duration"),
            ReadInt(media, "averageScore"),
            ReadYouTubeTrailerKey(media));
    }

    /// <summary>AniList names a trailer by site and id; only a YouTube video id is ever used, because that is the only embed the Preview offers.</summary>
    private static string? ReadYouTubeTrailerKey(JsonElement media) =>
        media.TryGetProperty("trailer", out var trailer)
        && trailer.ValueKind == JsonValueKind.Object
        && string.Equals(ReadString(trailer, "site"), "youtube", StringComparison.OrdinalIgnoreCase)
            ? ReadString(trailer, "id")
            : null;

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private sealed record SequenceMedia(
        AnimeMetadataCandidate Candidate,
        IReadOnlyList<int> Prequels,
        IReadOnlyList<int> Sequels);

    private static int? ReadInt(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : null;
}
