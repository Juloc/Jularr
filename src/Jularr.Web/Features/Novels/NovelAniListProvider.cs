using Jularr.Web.Features.Discovery;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Web.Features.MediaMapping;
using Jularr.Web.Features.Metadata;

namespace Jularr.Web.Features.Novels;

public sealed class NovelMetadataProviderException(
    string message,
    Exception? innerException = null) : Exception(message, innerException);

public sealed record AniListReadingMediaCandidate(
    string ExternalId,
    string PreferredTitle,
    string? NativeTitle,
    string? Description,
    string? CoverImageUrl,
    string? BannerImageUrl,
    string? Format,
    string? Status,
    int? ChapterCount,
    int? VolumeCount,
    int? StartYear,
    IReadOnlyList<string> Genres)
{
    public bool IsNovel =>
        string.Equals(Format, "NOVEL", StringComparison.OrdinalIgnoreCase);
}

public sealed partial class NovelAniListProvider(
    HttpClient httpClient,
    ILogger<NovelAniListProvider> logger) : INovelMetadataProvider
{
    public const string ProviderKey = "anilist";
    private const int MaximumSearchLimit = 12;

    private const string SearchQuery = """
        query ($search: String!, $perPage: Int!) {
          Page(page: 1, perPage: $perPage) {
            media(search: $search, type: MANGA, format: NOVEL, isAdult: false) {
              id
              title { romaji english native }
              description(asHtml: false)
              coverImage { extraLarge large }
              bannerImage
              format
              status
              chapters
              volumes
              genres
              isAdult
            }
          }
        }
        """;

    private const string ReadingSearchQuery = """
        query ($search: String!, $perPage: Int!, $genre: [String]) {
          Page(page: 1, perPage: $perPage) {
            media(search: $search, type: MANGA, isAdult: false, genre_in: $genre) {
              id
              title { romaji english native }
              description(asHtml: false)
              coverImage { extraLarge large }
              bannerImage
              format
              status
              chapters
              volumes
              startDate { year }
              genres
              isAdult
            }
          }
        }
        """;

    /// <summary>One page of manga or light novels for Discover. The format filter is applied by AniList (manga excludes NOVEL, light novels require it), so a page never holds the other kind.</summary>
    private const string ReadingDiscoverQuery = """
        query ($page: Int!, $perPage: Int!, $search: String, $sort: [MediaSort!], $genre: [String], $status: [MediaStatus], $startFrom: FuzzyDateInt, $startTo: FuzzyDateInt, $format: MediaFormat, $formatNot: [MediaFormat], $popularityMin: Int) {
          Page(page: $page, perPage: $perPage) {
            pageInfo { hasNextPage }
            media(type: MANGA, isAdult: false, search: $search, sort: $sort, genre_in: $genre, status_in: $status, startDate_greater: $startFrom, startDate_lesser: $startTo, format: $format, format_not_in: $formatNot, popularity_greater: $popularityMin) {
              id
              title { romaji english native }
              description(asHtml: false)
              coverImage { extraLarge large }
              bannerImage
              format
              status
              chapters
              volumes
              startDate { year }
              genres
              isAdult
            }
          }
        }
        """;

    private const string ByIdQuery = """
        query ($id: Int!) {
          Media(id: $id, type: MANGA) {
            id
            title { romaji english native }
            description(asHtml: false)
            coverImage { extraLarge large }
            bannerImage
            format
            status
            chapters
            volumes
            isAdult
          }
        }
        """;

    private const string SequenceQuery = """
        query ($id: Int!) {
          Media(id: $id, type: MANGA) {
            id
            title { romaji english native }
            description(asHtml: false)
            coverImage { extraLarge large }
            bannerImage
            format
            status
            startDate { year }
            chapters
            volumes
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
                  seasonYear
                  startDate { year }
                  chapters
                  volumes
                  isAdult
                }
              }
            }
          }
        }
        """;

    public string Key => ProviderKey;

    public async Task<IReadOnlyList<NovelMetadataCandidate>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var normalized = query.Trim();
        if (normalized.Length == 0)
        {
            return [];
        }

        var json = await SendAsync(
            SearchQuery,
            new
            {
                search = normalized,
                perPage = Math.Clamp(limit, 1, MaximumSearchLimit)
            },
            cancellationToken);

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("Page", out var page) ||
            !page.TryGetProperty("media", out var media) ||
            media.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return media.EnumerateArray()
            .Select(ParseMedia)
            .Where(x => x is not null)
            .Cast<NovelMetadataCandidate>()
            .ToArray();
    }

    public Task<IReadOnlyList<AniListReadingMediaCandidate>> SearchReadingMediaAsync(
        string query,
        int limit,
        bool includeNovels,
        bool includeManga,
        CancellationToken cancellationToken) =>
        SearchReadingMediaAsync(query, limit, includeNovels, includeManga, null, cancellationToken);

    public async Task<IReadOnlyList<AniListReadingMediaCandidate>> SearchReadingMediaAsync(
        string query,
        int limit,
        bool includeNovels,
        bool includeManga,
        string? genre,
        CancellationToken cancellationToken)
    {
        var normalized = query.Trim();
        if (normalized.Length == 0 || (!includeNovels && !includeManga))
        {
            return [];
        }

        var json = await SendAsync(
            ReadingSearchQuery,
            new
            {
                search = normalized,
                perPage = Math.Clamp(limit, 1, 24),
                genre = GenreVariable(genre)
            },
            cancellationToken);

        return ParseReadingMediaResponse(
            json,
            includeNovels,
            includeManga);
    }

    /// <summary>One Discover page of manga or light novels (a browse view or a search) with the viewer's filters applied by AniList.</summary>
    public async Task<DiscoveryProviderPage<AniListReadingMediaCandidate>> DiscoverReadingPageAsync(
        AniListDiscoveryOptions options,
        CancellationToken cancellationToken)
    {
        var json = await SendAsync(ReadingDiscoverQuery, options.Variables(), cancellationToken);
        return new DiscoveryProviderPage<AniListReadingMediaCandidate>(ParseReadingMediaResponse(json, true, true), AniListPageInfo.HasNextPage(json));
    }

    // AniList treats a null genre_in as "no filter"; an empty array would match nothing.
    private static string[]? GenreVariable(string? genre) =>
        string.IsNullOrWhiteSpace(genre) ? null : [genre];

    public async Task<NovelMetadataCandidate?> GetAsync(
        string externalId,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(externalId, out var id) || id <= 0)
        {
            return null;
        }

        var json = await SendAsync(ByIdQuery, new { id }, cancellationToken);
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("Media", out var media) ||
            media.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return ParseMedia(media);
    }

    public Task<LinearRelationSequenceResult<NovelMetadataCandidate>> GetLinearSequenceAsync(
        string externalId,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(externalId, out var id) || id <= 0)
        {
            return Task.FromResult(
                new LinearRelationSequenceResult<NovelMetadataCandidate>(
                    [],
                    false,
                    "AniList novel ID is invalid."));
        }

        return LinearRelationSequence.ResolveAsync(
            id.ToString(),
            LoadSequenceNodeAsync,
            cancellationToken);
    }

    private async Task<LinearRelationNode<NovelMetadataCandidate>?> LoadSequenceNodeAsync(
        string externalId,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(externalId, out var id) || id <= 0)
        {
            return null;
        }

        var json = await SendAsync(
            SequenceQuery,
            new { id },
            cancellationToken);

        using var document = JsonDocument.Parse(json);
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

        var prequels = new List<string>();
        var sequels = new List<string>();

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
                    node.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var related = ParseMedia(node);
                if (related is null)
                {
                    continue;
                }

                if (relationType == "PREQUEL")
                {
                    prequels.Add(related.ExternalId);
                }
                else
                {
                    sequels.Add(related.ExternalId);
                }
            }
        }

        return new LinearRelationNode<NovelMetadataCandidate>(
            candidate,
            prequels.Distinct(StringComparer.Ordinal).ToArray(),
            sequels.Distinct(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// The manga or novel and its related AniList entries in one request. Adult entries are
    /// skipped; the entry itself is null when AniList does not return it.
    /// </summary>
    public async Task<AniListRelatedMedia> GetRelatedMediaAsync(
        string externalId,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(externalId, out var id) || id <= 0)
        {
            return new AniListRelatedMedia(null, []);
        }

        var json = await SendAsync(
            SequenceQuery,
            new { id },
            cancellationToken);

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("Media", out var media) ||
            media.ValueKind != JsonValueKind.Object)
        {
            return new AniListRelatedMedia(null, []);
        }

        var self = ReadMediaSummary(media, "MANGA");
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
        var title = FirstNonEmpty(
            ReadString(titleObject, "english"),
            ReadString(titleObject, "romaji"),
            native) ?? $"AniList {id}";

        string? cover = null;
        if (node.TryGetProperty("coverImage", out var coverElement) &&
            coverElement.ValueKind == JsonValueKind.Object)
        {
            cover = FirstNonEmpty(
                ReadString(coverElement, "extraLarge"),
                ReadString(coverElement, "large"));
        }

        var year = ReadInt(node, "seasonYear");
        if (year is null &&
            node.TryGetProperty("startDate", out var startDate) &&
            startDate.ValueKind == JsonValueKind.Object)
        {
            year = ReadInt(startDate, "year");
        }

        return new AniListMediaSummary(
            mediaType,
            id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            title,
            native,
            cover,
            ReadString(node, "format"),
            ReadString(node, "status"),
            year);
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
                throw new NovelMetadataProviderException(
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

                throw new NovelMetadataProviderException(
                    string.IsNullOrWhiteSpace(message)
                        ? "AniList returned a GraphQL error."
                        : $"AniList: {message}");
            }

            return body;
        }
        catch (NovelMetadataProviderException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(exception, "AniList novel metadata request failed.");
            throw new NovelMetadataProviderException(
                "AniList novel metadata is currently unavailable.",
                exception);
        }
    }

    public static IReadOnlyList<AniListReadingMediaCandidate> ParseReadingMediaResponse(
        string json,
        bool includeNovels,
        bool includeManga)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("Page", out var page) ||
            !page.TryGetProperty("media", out var media) ||
            media.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return media.EnumerateArray()
            .Select(ParseReadingMedia)
            .Where(x => x is not null)
            .Cast<AniListReadingMediaCandidate>()
            .Where(x => x.IsNovel ? includeNovels : includeManga)
            .ToArray();
    }

    private static AniListReadingMediaCandidate? ParseReadingMedia(JsonElement media)
    {
        if (!media.TryGetProperty("id", out var idElement) ||
            !idElement.TryGetInt32(out var id))
        {
            return null;
        }

        if (media.TryGetProperty("isAdult", out var adultElement) &&
            adultElement.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        var title = media.TryGetProperty("title", out var titleElement)
            ? titleElement
            : default;
        var english = ReadString(title, "english");
        var romaji = ReadString(title, "romaji");
        var native = ReadString(title, "native");
        var preferred = FirstNonEmpty(english, romaji, native) ?? $"AniList {id}";

        string? cover = null;
        if (media.TryGetProperty("coverImage", out var coverElement) &&
            coverElement.ValueKind == JsonValueKind.Object)
        {
            cover = FirstNonEmpty(
                ReadString(coverElement, "extraLarge"),
                ReadString(coverElement, "large"));
        }

        int? startYear = null;
        if (media.TryGetProperty("startDate", out var startDate) &&
            startDate.ValueKind == JsonValueKind.Object)
        {
            startYear = ReadInt(startDate, "year");
        }

        return new AniListReadingMediaCandidate(
            id.ToString(),
            preferred,
            native,
            NormalizeDescription(ReadString(media, "description")),
            cover,
            ReadString(media, "bannerImage"),
            ReadString(media, "format"),
            ReadString(media, "status"),
            ReadInt(media, "chapters"),
            ReadInt(media, "volumes"),
            startYear,
            ReadStringArray(media, "genres"));
    }

    internal static NovelMetadataCandidate? ParseMedia(JsonElement media)
    {
        if (!media.TryGetProperty("id", out var idElement) ||
            !idElement.TryGetInt32(out var id))
        {
            return null;
        }

        if (media.TryGetProperty("isAdult", out var adultElement) &&
            adultElement.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        var format = ReadString(media, "format");
        if (!string.Equals(format, "NOVEL", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var title = media.TryGetProperty("title", out var titleElement)
            ? titleElement
            : default;

        var english = ReadString(title, "english");
        var romaji = ReadString(title, "romaji");
        var native = ReadString(title, "native");
        var preferred = FirstNonEmpty(english, romaji, native) ?? $"AniList {id}";

        string? cover = null;
        if (media.TryGetProperty("coverImage", out var coverElement) &&
            coverElement.ValueKind == JsonValueKind.Object)
        {
            cover = FirstNonEmpty(
                ReadString(coverElement, "extraLarge"),
                ReadString(coverElement, "large"));
        }

        return new NovelMetadataCandidate(
            ProviderKey,
            id.ToString(),
            preferred,
            native,
            NormalizeDescription(ReadString(media, "description")),
            cover,
            ReadString(media, "bannerImage"),
            format,
            ReadString(media, "status"),
            ReadInt(media, "chapters"),
            ReadInt(media, "volumes"),
            ReadStringArray(media, "genres"));
    }

    private static string? NormalizeDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var decoded = WebUtility.HtmlDecode(HtmlTag().Replace(value, " "));
        var normalized = Whitespace().Replace(decoded, " ").Trim();
        return normalized.Length == 0 ? null : normalized;
    }

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

    private static int? ReadInt(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : null;

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement element,
        string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToArray();
    }


    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
