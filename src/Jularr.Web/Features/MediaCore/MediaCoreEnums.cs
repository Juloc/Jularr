using Jularr.Web.Features.Watchlist;

namespace Jularr.Web.Features.MediaCore;

/// <summary>
/// The six universal media types of the Jularr media core (#592, epic #556 §1). The model is
/// deliberately provider-independent: a <see cref="Work"/> is one of these types regardless of which
/// external provider (AniList, MAL, TMDB, TVDB, IMDb, Plex, Jellyfin, …) supplied its metadata.
/// </summary>
public enum WorkMediaType
{
    Movie,
    Series,
    Anime,
    Book,
    Manga,
    LightNovel,

    /// <summary>An album (the Work); its tracks are <see cref="WorkTrack"/> structure.</summary>
    Music
}

/// <summary>Kind of a <see cref="WorkTitle"/>. A work can hold many titles of each kind, one primary per work.</summary>
public enum WorkTitleType
{
    /// <summary>The single display title chosen for the work.</summary>
    Primary,

    /// <summary>The work's title in its original language of production.</summary>
    Original,

    /// <summary>Official English title.</summary>
    English,

    /// <summary>Latin transliteration of a non-Latin native title (e.g. anime romaji).</summary>
    Romaji,

    /// <summary>The title in the work's native script (e.g. Japanese, Korean).</summary>
    Native,

    /// <summary>An alternate/known-as title.</summary>
    Alternative,

    /// <summary>A region/language-localized title (BCP-47 language carries the locale).</summary>
    Localized,

    /// <summary>A provider synonym/search alias.</summary>
    Synonym,

    /// <summary>A short/abbreviated title.</summary>
    Short
}

/// <summary>
/// A typed edge in the internal work-relation graph (#592). Distinct from the provider-level
/// <c>MediaRelations</c> table (raw provider evidence): these edges are between resolved Jularr
/// <see cref="Work"/> ids and can be manually curated.
/// </summary>
public enum WorkRelationType
{
    Prequel,
    Sequel,
    Parent,
    SideStory,
    Alternative,
    SpinOff,
    Adaptation,
    Source,
    Summary,
    FullStory,
    Character,
    Contains,
    Remake,
    Other
}

/// <summary>
/// Which existing per-type table a <see cref="WorkSourceLink"/> bridges to. The link is a
/// non-invasive adapter: it references the legacy record's primary key without changing that table,
/// so every current per-type feature (Anime, Manga, Novel, Book) keeps working unchanged (#592).
/// </summary>
public enum WorkSourceKind
{
    Anime,
    NovelWork,
    BookEdition,
    MangaSeries,
    Episode,
    Movie,
    Series,
    Audiobook
}

/// <summary>Review state of a correctable provider mapping (external identity or relation).</summary>
public enum MappingReviewState
{
    /// <summary>Auto-derived, not yet reviewed.</summary>
    Unverified,

    /// <summary>Confirmed (auto-high-confidence or owner-confirmed).</summary>
    Confirmed,

    /// <summary>Flagged for the owner to resolve.</summary>
    NeedsReview,

    /// <summary>Explicitly rejected; kept as a tombstone so it is not re-suggested.</summary>
    Rejected
}

/// <summary>
/// Stable storage strings and cross-mapping for <see cref="WorkMediaType"/>. Storage strings are the
/// contract with the database and other features; never localise them. Bridges to the existing
/// <see cref="WatchlistMediaType"/> so the media core reuses Jularr's existing provider-identity
/// vocabulary instead of introducing a second one.
/// </summary>
public static class WorkMediaTypes
{
    public static IReadOnlyList<WorkMediaType> All { get; } = Enum.GetValues<WorkMediaType>();

    /// <summary>Stable lowercase storage form (matches <see cref="WatchlistMediaTypeNames"/> where they overlap).</summary>
    public static string ToStorage(WorkMediaType type) => type switch
    {
        WorkMediaType.Movie => "movie",
        WorkMediaType.Series => "series",
        WorkMediaType.Anime => "anime",
        WorkMediaType.Book => "book",
        WorkMediaType.Manga => "manga",
        WorkMediaType.LightNovel => "lightNovel",
        _ => type.ToString().ToLowerInvariant()
    };

    public static WorkMediaType? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "movie" or "movies" or "film" => WorkMediaType.Movie,
        "series" or "tv" or "show" => WorkMediaType.Series,
        "anime" => WorkMediaType.Anime,
        "book" or "books" => WorkMediaType.Book,
        "manga" => WorkMediaType.Manga,
        "lightnovel" or "light-novel" or "light_novel" or "novel" => WorkMediaType.LightNovel,
        "music" or "album" => WorkMediaType.Music,
        _ => null
    };

    /// <summary>The equivalent <see cref="WatchlistMediaType"/> (Series ↔ Tv) so provider identities interoperate.</summary>
    public static WatchlistMediaType ToWatchlist(WorkMediaType type) => type switch
    {
        WorkMediaType.Movie => WatchlistMediaType.Movie,
        WorkMediaType.Series => WatchlistMediaType.Tv,
        WorkMediaType.Anime => WatchlistMediaType.Anime,
        WorkMediaType.Book => WatchlistMediaType.Book,
        WorkMediaType.Manga => WatchlistMediaType.Manga,
        WorkMediaType.LightNovel => WatchlistMediaType.LightNovel,
        _ => WatchlistMediaType.Movie
    };

    public static WorkMediaType FromWatchlist(WatchlistMediaType type) => type switch
    {
        WatchlistMediaType.Movie => WorkMediaType.Movie,
        WatchlistMediaType.Tv => WorkMediaType.Series,
        WatchlistMediaType.Anime => WorkMediaType.Anime,
        WatchlistMediaType.Book => WorkMediaType.Book,
        WatchlistMediaType.Manga => WorkMediaType.Manga,
        WatchlistMediaType.LightNovel => WorkMediaType.LightNovel,
        _ => WorkMediaType.Movie
    };
}

/// <summary>Storage helpers for <see cref="WorkRelationType"/> that interoperate with existing provider relation strings.</summary>
public static class WorkRelationTypes
{
    /// <summary>Lowercase dashed storage form aligned with the provider relation vocabulary (e.g. "side-story").</summary>
    public static string ToStorage(WorkRelationType type) => type switch
    {
        WorkRelationType.SideStory => "side-story",
        WorkRelationType.SpinOff => "spin-off",
        WorkRelationType.FullStory => "full-story",
        _ => type.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// Parses a provider relation string (AniList <c>SIDE_STORY</c>, normalized <c>side-story</c>, …) to
    /// the internal typed edge; unknown strings map to <see cref="WorkRelationType.Other"/>.
    /// </summary>
    public static WorkRelationType Parse(string? value) => (value ?? "").Trim().ToLowerInvariant().Replace('_', '-') switch
    {
        "prequel" => WorkRelationType.Prequel,
        "sequel" => WorkRelationType.Sequel,
        "parent" or "parent-story" => WorkRelationType.Parent,
        "side-story" => WorkRelationType.SideStory,
        "alternative" or "alternative-version" or "alternative-setting" => WorkRelationType.Alternative,
        "spin-off" => WorkRelationType.SpinOff,
        "adaptation" => WorkRelationType.Adaptation,
        "source" => WorkRelationType.Source,
        "summary" or "compilation" => WorkRelationType.Summary,
        "full-story" => WorkRelationType.FullStory,
        "character" => WorkRelationType.Character,
        "contains" => WorkRelationType.Contains,
        "remake" => WorkRelationType.Remake,
        _ => WorkRelationType.Other
    };

    /// <summary>The opposite edge, so a directed relation can be stored/read symmetrically where it has an inverse.</summary>
    public static WorkRelationType Inverse(WorkRelationType type) => type switch
    {
        WorkRelationType.Prequel => WorkRelationType.Sequel,
        WorkRelationType.Sequel => WorkRelationType.Prequel,
        WorkRelationType.Parent => WorkRelationType.SideStory,
        WorkRelationType.SideStory => WorkRelationType.Parent,
        WorkRelationType.Adaptation => WorkRelationType.Source,
        WorkRelationType.Source => WorkRelationType.Adaptation,
        WorkRelationType.FullStory => WorkRelationType.Summary,
        WorkRelationType.Summary => WorkRelationType.FullStory,
        _ => type
    };
}
