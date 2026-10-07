using System.Globalization;
using Jularr.Web.Ui;

namespace Jularr.Web.Features.Discovery;

/// <summary>
/// The constraints a viewer narrows a browse view or a search with. Every field is pushed into the provider request where the provider can
/// filter on it, so paging stays truthful; <see cref="Statuses"/> and the year range are only offered for sources that filter on them.
/// </summary>
public sealed record DiscoveryFilter(
    IReadOnlyList<string> Genres,
    int? YearFrom = null,
    int? YearTo = null,
    IReadOnlyList<MediaReleaseStatus>? Statuses = null)
{
    public static DiscoveryFilter None { get; } = new([]);

    public IReadOnlyList<MediaReleaseStatus> StatusList => Statuses ?? [];

    public bool IsEmpty => Genres.Count == 0 && YearFrom is null && YearTo is null && StatusList.Count == 0;

    /// <summary>A stable text of the filter, part of the identity of a cached provider page.</summary>
    public string CacheKey =>
        string.Join(',', Genres.Order(StringComparer.Ordinal))
        + "|" + YearFrom?.ToString(CultureInfo.InvariantCulture) + "-" + YearTo?.ToString(CultureInfo.InvariantCulture)
        + "|" + string.Join(',', StatusList.Order());
}

/// <summary>How far a ranking can be trusted to describe where the viewer is: only a source with a real regional signal may be labelled regional.</summary>
public enum DiscoveryRankingScope
{
    /// <summary>The same ranking for everybody (AniList charts, TMDB trending, Open Library trending).</summary>
    Global,

    /// <summary>Titles and metadata come in the viewer's locale; the ranking itself is not regional.</summary>
    Locale,

    /// <summary>The source ranks for the viewer's own region.</summary>
    Regional
}

/// <summary>One provider page: its titles and whether the provider has a further page.</summary>
public sealed record DiscoveryProviderPage<T>(IReadOnlyList<T> Items, bool HasMore);

/// <summary>What the AniList discovery queries (anime, manga and light novels) ask for in one page.</summary>
public sealed record AniListDiscoveryOptions(
    int Page,
    int PerPage,
    string Search,
    DiscoveryMode Mode,
    DiscoveryFilter Filter,
    AniListDiscoveryKind Kind)
{
    /// <summary>The GraphQL variables of the shared page queries. A filter that is off is left out: AniList rejects an explicit null for some of them (format together with format_not_in).</summary>
    public IReadOnlyDictionary<string, object?> Variables()
    {
        var searching = Search.Length > 0;
        var statuses = Filter.StatusList.Count > 0
            ? Filter.StatusList.SelectMany(StatusNames).Distinct().ToArray()
            : searching ? null : ModeStatuses(Mode);
        var today = DateTime.UtcNow;
        int? startFrom = Filter.YearFrom is { } from ? (from - 1) * 10000 + 1231 : null;
        int? startTo = Filter.YearTo is { } to ? (to + 1) * 10000 + 101 : null;
        if (!searching && Mode == DiscoveryMode.New && Filter.YearTo is null)
        {
            // Recently started titles only: a future start date is an announcement, not a new release.
            startTo = today.Year * 10000 + today.Month * 100 + today.Day;
        }

        var variables = new Dictionary<string, object?>
        {
            ["page"] = Math.Max(1, Page),
            ["perPage"] = Math.Clamp(PerPage, 1, 50),
            ["search"] = searching ? Search : null,
            ["sort"] = searching ? null : Sorts(Mode),
            ["genre"] = Filter.Genres.Count == 0 ? null : Filter.Genres.ToArray(),
            ["status"] = statuses,
            ["startFrom"] = startFrom,
            ["startTo"] = startTo,
            ["format"] = Kind == AniListDiscoveryKind.LightNovel ? "NOVEL" : null,
            ["formatNot"] = Kind == AniListDiscoveryKind.Manga ? new[] { "NOVEL" } : null,
            ["popularityMin"] = !searching && Mode == DiscoveryMode.TopRated ? RatingAudience(Kind) : null
        };
        return variables.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private static string[] Sorts(DiscoveryMode mode) => mode switch
    {
        DiscoveryMode.Top => ["SCORE_DESC", "POPULARITY_DESC"],
        DiscoveryMode.TopRated => ["SCORE_DESC", "POPULARITY_DESC"],
        DiscoveryMode.Popular => ["POPULARITY_DESC"],
        DiscoveryMode.New => ["START_DATE_DESC"],
        DiscoveryMode.Upcoming => ["POPULARITY_DESC"],
        _ => ["TRENDING_DESC", "POPULARITY_DESC"]
    };

    private static string[]? ModeStatuses(DiscoveryMode mode) => mode switch
    {
        DiscoveryMode.Upcoming => ["NOT_YET_RELEASED"],
        DiscoveryMode.New => ["RELEASING", "FINISHED"],
        _ => null
    };

    /// <summary>The audience a title needs before its score counts as a rating of the crowd: Top rated is not the best score of three voters.</summary>
    private static int RatingAudience(AniListDiscoveryKind kind) => kind switch
    {
        AniListDiscoveryKind.Anime => 20000,
        AniListDiscoveryKind.Manga => 10000,
        _ => 3000
    };

    private static IEnumerable<string> StatusNames(MediaReleaseStatus status) => status switch
    {
        MediaReleaseStatus.Ongoing => ["RELEASING"],
        MediaReleaseStatus.Finished => ["FINISHED"],
        MediaReleaseStatus.Upcoming => ["NOT_YET_RELEASED"],
        MediaReleaseStatus.Hiatus => ["HIATUS"],
        _ => ["CANCELLED"]
    };
}

/// <summary>Reads the page info of an AniList page response.</summary>
public static class AniListPageInfo
{
    public static bool HasNextPage(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("data", out var data)
               && data.TryGetProperty("Page", out var page)
               && page.ValueKind == System.Text.Json.JsonValueKind.Object
               && page.TryGetProperty("pageInfo", out var info)
               && info.TryGetProperty("hasNextPage", out var next)
               && next.ValueKind == System.Text.Json.JsonValueKind.True;
    }
}

public enum AniListDiscoveryKind
{
    Anime,
    Manga,
    LightNovel,

    /// <summary>A search across both reading formats (the all-types search).</summary>
    Reading
}
