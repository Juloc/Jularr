using Jularr.Web.Features.Instance;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Web.Features.Search;

/// <summary>
/// The per-type source table a search hit came from. Books are the <c>NovelWorks</c> rows imported
/// through the Books catalog; <see cref="Novel"/> is every other <c>NovelWorks</c> row (light and web
/// novels). An audiobook is its own source table but shares the Book media type for permissions.
/// </summary>
public enum MediaSearchType
{
    Anime,
    Novel,
    Manga,
    Book,
    Movie,
    Series,
    Audiobook
}

/// <summary>What a search result row is: one canonical work, or a franchise that groups several works.</summary>
public enum MediaSearchResultKind
{
    Work,
    Franchise
}

/// <summary>
/// Mapping between the search source tables and the universal media core (#592). The media core is the
/// one vocabulary for permissions, so a source table is gated by the <see cref="WorkMediaType"/> it
/// belongs to; <see cref="WorkSourceKind"/> is how the same record is bridged to its canonical
/// <see cref="Work"/>.
/// </summary>
public static class MediaSearchTypes
{
    public static IReadOnlyList<MediaSearchType> All { get; } = Enum.GetValues<MediaSearchType>();

    public static WorkMediaType ToWorkMediaType(MediaSearchType type) => type switch
    {
        MediaSearchType.Anime => WorkMediaType.Anime,
        MediaSearchType.Novel => WorkMediaType.LightNovel,
        MediaSearchType.Manga => WorkMediaType.Manga,
        MediaSearchType.Book => WorkMediaType.Book,
        MediaSearchType.Audiobook => WorkMediaType.Book,
        MediaSearchType.Movie => WorkMediaType.Movie,
        MediaSearchType.Series => WorkMediaType.Series,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    public static InstanceModule ToInstanceModule(MediaSearchType type) =>
        type == MediaSearchType.Audiobook
            ? InstanceModule.Audiobook
            : InstanceModuleMedia.For(ToWorkMediaType(type));

    public static WorkSourceKind ToSourceKind(MediaSearchType type) => type switch
    {
        MediaSearchType.Anime => WorkSourceKind.Anime,
        MediaSearchType.Novel or MediaSearchType.Book => WorkSourceKind.NovelWork,
        MediaSearchType.Manga => WorkSourceKind.MangaSeries,
        MediaSearchType.Movie => WorkSourceKind.Movie,
        MediaSearchType.Series => WorkSourceKind.Series,
        MediaSearchType.Audiobook => WorkSourceKind.Audiobook,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    /// <summary>Stable lowercase form used in URLs and SQL; never localised.</summary>
    public static string ToStorage(MediaSearchType type) => type switch
    {
        MediaSearchType.Anime => "anime",
        MediaSearchType.Novel => "novel",
        MediaSearchType.Manga => "manga",
        MediaSearchType.Book => "book",
        MediaSearchType.Movie => "movie",
        MediaSearchType.Series => "series",
        MediaSearchType.Audiobook => "audiobook",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    public static MediaSearchType? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "anime" => MediaSearchType.Anime,
        "novel" or "lightnovel" or "light-novel" => MediaSearchType.Novel,
        "manga" => MediaSearchType.Manga,
        "book" or "books" => MediaSearchType.Book,
        "movie" or "movies" => MediaSearchType.Movie,
        "series" or "tv" => MediaSearchType.Series,
        "audiobook" or "audiobooks" => MediaSearchType.Audiobook,
        _ => null
    };

    /// <summary>The search types whose source tables belong to <paramref name="mediaType"/>.</summary>
    public static IReadOnlyList<MediaSearchType> ForWorkMediaType(WorkMediaType mediaType) =>
        [.. All.Where(type => ToWorkMediaType(type) == mediaType)];
}

/// <summary>
/// The Media Facts filters of a search (#426, #434). Every filter is optional and they combine with
/// AND. Facts that a source has no data for never match: a filter only ever keeps titles for which
/// the fact is actually known, it never guesses.
/// </summary>
/// <param name="Types">Restrict to these source types; empty or null means every visible type.</param>
/// <param name="Local">
/// <c>true</c> keeps titles with something playable or readable on this server, <c>false</c> keeps
/// titles that are in the library but have nothing playable or readable yet.
/// </param>
/// <param name="Monitored">Keeps only titles that are monitored for automatic acquisition (<c>true</c>) or not (<c>false</c>).</param>
/// <param name="Wanted">Keeps only titles with an open request or wanted units (<c>true</c>) or without (<c>false</c>).</param>
/// <param name="Language">A language tag; keeps titles with audio, subtitles or text in it.</param>
/// <param name="Genre">A genre name (case-insensitive); keeps titles that carry it.</param>
/// <param name="YearFrom">Inclusive lower bound of the release year.</param>
/// <param name="YearTo">Inclusive upper bound of the release year.</param>
public sealed record MediaSearchFilters(
    IReadOnlyCollection<MediaSearchType>? Types = null,
    bool? Local = null,
    bool? Monitored = null,
    bool? Wanted = null,
    string? Language = null,
    string? Genre = null,
    int? YearFrom = null,
    int? YearTo = null)
{
    public static MediaSearchFilters None { get; } = new();

    /// <summary>True when any filter beyond the source type is set; those need per-title facts.</summary>
    public bool HasFactFilters =>
        Local is not null
        || Monitored is not null
        || Wanted is not null
        || !string.IsNullOrWhiteSpace(Language)
        || !string.IsNullOrWhiteSpace(Genre)
        || YearFrom is not null
        || YearTo is not null;
}

/// <summary>
/// One search. <paramref name="VisibleMediaTypes"/> is the profile's browseable media types from
/// <c>IAppShellService</c>/<c>IMediaCapabilityService</c>; a source whose media type is not listed is
/// never queried, so a hidden type cannot leak through a title match. <c>null</c> means unrestricted
/// (only for callers that already are: a background job, or a test).
/// </summary>
public sealed record MediaSearchRequest(
    string? Query,
    MediaSearchFilters? Filters = null,
    IReadOnlyCollection<WorkMediaType>? VisibleMediaTypes = null,
    int Limit = MediaSearchService.DefaultLimit,
    int Offset = 0);

/// <summary>One source-table record that matched; a canonical result can collapse several.</summary>
public sealed record MediaSearchVariant(MediaSearchType Type, Guid Id, string Title, double Score);

/// <summary>
/// The facts about a result that the filters use. Aggregated over all variants of a canonical work:
/// local when any variant is, languages and genres are the union, the year is the earliest known.
/// </summary>
public sealed record MediaSearchFacts(
    int? Year,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Genres,
    bool HasLocalContent,
    bool Monitored,
    bool Wanted)
{
    public static MediaSearchFacts Unknown { get; } = new(null, [], [], false, false, false);
}

/// <summary>
/// One row of the result list. A <see cref="MediaSearchResultKind.Work"/> is one canonical work (all
/// source records that bridge to the same media-core <see cref="Work"/> are collapsed into it; a
/// record with no bridge stands alone). A <see cref="MediaSearchResultKind.Franchise"/> is a franchise
/// that matched by its own title or by the title of one of its works.
/// </summary>
/// <param name="Id">The best variant's record id for a work, the franchise id for a franchise.</param>
/// <param name="WorkId">The canonical media-core work; null when the record is not bridged yet.</param>
/// <param name="Type">The best variant's type; null for a franchise.</param>
/// <param name="MemberCount">Works in the franchise that the profile may see; 0 for a work.</param>
public sealed record MediaSearchResult(
    MediaSearchResultKind Kind,
    Guid Id,
    long? WorkId,
    MediaSearchType? Type,
    string Title,
    double Score,
    int MemberCount,
    IReadOnlyList<MediaSearchVariant> Variants,
    MediaSearchFacts Facts)
{
    /// <summary>The distinct types the row covers, best variant first (a book that is also an audiobook lists both).</summary>
    public IReadOnlyList<MediaSearchType> Types =>
        [.. Variants.Select(variant => variant.Type).Distinct()];
}

/// <summary>Values the filter controls can offer, taken from the results the current query and visibility produce.</summary>
public sealed record MediaSearchFacets(
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Genres,
    int? MinYear,
    int? MaxYear)
{
    public static MediaSearchFacets Empty { get; } = new([], [], null, null);
}

/// <summary>One page of results, ranked best first.</summary>
/// <param name="Total">Results that match the query and filters, within the bounded candidate window.</param>
/// <param name="Facets">Filter values seen among the candidates, before the fact filters narrowed them.</param>
public sealed record MediaSearchPage(
    IReadOnlyList<MediaSearchResult> Items,
    int Total,
    int Offset,
    int Limit,
    MediaSearchFacets Facets)
{
    public bool HasMore => Offset + Items.Count < Total;

    public static MediaSearchPage Empty(int limit, int offset = 0) =>
        new([], 0, offset, limit, MediaSearchFacets.Empty);
}
