namespace Jularr.Web.Features.Discovery;

public enum DiscoveryCategory
{
    All,
    Anime,
    Movie,
    Series,
    LightNovel,
    Manga,
    Book,
    /// <summary>The combined "Books and Light Novels" scope of the Discover media-type switch.</summary>
    BooksAndLightNovels
}

public enum DiscoveryMode
{
    Trending,
    Top,
    MyList,
    Search,
    /// <summary>Recently published (#371); currently only Books sources this honestly
    /// (Open Library recent-subject data). Other categories fall back to their Top browse.</summary>
    New,

    /// <summary>Provider-backed titles announced for a future release.</summary>
    Upcoming
}

public sealed record DiscoveryRequest(
    string Query,
    DiscoveryCategory Category,
    DiscoveryMode Mode,
    string Genre = "")
{
    public static DiscoveryRequest Parse(
        string? query,
        string? category,
        string? mode,
        string? genre = null)
    {
        var normalizedQuery = NormalizeQuery(query);
        var normalizedCategory = ParseCategory(category);
        var normalizedMode = normalizedQuery.Length > 0
            ? DiscoveryMode.Search
            : ParseMode(mode);
        var normalizedGenre = NormalizeGenre(genre);

        return new DiscoveryRequest(
            normalizedQuery,
            normalizedCategory,
            normalizedMode,
            normalizedGenre);
    }

    public bool RequiresPersonalAniListAccount =>
        Mode == DiscoveryMode.MyList;

    /// <summary>
    /// Maps casual input ("sci-fi", "SLICE OF LIFE") to the genre Discover offers, spelled the way
    /// AniList spells it for <c>genre_in</c>. Empty when no genre was requested or it is not one Discover offers.
    /// </summary>
    public static string NormalizeGenre(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        // Only the genres Discover offers: free text would make every spelling a provider call of its own.
        var trimmed = value.Trim();
        return KnownGenres.FirstOrDefault(genre => string.Equals(genre, trimmed, StringComparison.OrdinalIgnoreCase)) ?? "";
    }

    /// <summary>The genres Discover offers, spelled the way AniList spells them (its genre filter is case-sensitive).</summary>
    public static IReadOnlyList<string> KnownGenres { get; } =
    [
        "Action", "Adventure", "Comedy", "Drama", "Fantasy", "Horror",
        "Mystery", "Romance", "Sci-Fi", "Slice of Life", "Sports", "Supernatural"
    ];

    public static string NormalizeQuery(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var parts = value
            .Trim()
            .Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries);

        return string.Join(" ", parts)
            .Truncate(120);
    }

    public static DiscoveryCategory ParseCategory(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "anime" => DiscoveryCategory.Anime,
            "movie" or "movies" => DiscoveryCategory.Movie,
            "tv" or "series" => DiscoveryCategory.Series,
            "novel" or "novels" or "lightnovel" or "light-novel" or "light-novels" =>
                DiscoveryCategory.LightNovel,
            "manga" => DiscoveryCategory.Manga,
            "book" or "books" => DiscoveryCategory.Book,
            "books-light-novels" => DiscoveryCategory.BooksAndLightNovels,
            _ => DiscoveryCategory.All
        };

    private static DiscoveryMode ParseMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "top" or "popular" => DiscoveryMode.Top,
            "my" or "my-list" or "mylist" => DiscoveryMode.MyList,
            "new" or "recent" or "recently-published" => DiscoveryMode.New,
            "upcoming" => DiscoveryMode.Upcoming,
            _ => DiscoveryMode.Trending
        };
}

public sealed record DiscoveryItem(
    string Id,
    string Category,
    string Provider,
    string ExternalId,
    string Title,
    string? NativeTitle,
    string? Description,
    string? CoverImageUrl,
    string? Format,
    string? Status,
    int? Year,
    int? Progress,
    int? TotalProgress,
    int? VolumeCount,
    string? ListStatus,
    IReadOnlyList<string> Genres,
    bool IsLocal,
    string? LocalUrl,
    string DetailsUrl,
    bool CanImportSource,
    // Status of the open add request for this title; set per response, never cached.
    string? RequestStatus = null,
    bool IsFollowed = false,
    Guid? LocalMediaId = null,
    // A franchise the profile follows that holds this title; set per response.
    Guid? FollowedFranchiseId = null,
    // Books only (#371): the source never fabricates either when it does not supply one.
    string? Author = null,
    double? Rating = null,
    // The wide artwork and the YouTube trailer id of the Preview (docs/mockups/media-preview, section 7); only what the source already returned, never a further call.
    string? BackdropUrl = null,
    string? TrailerKey = null,
    // The Detail of the canonical Work this title already has without being in the library (for example after a Request); set per response, never cached.
    string? WorkUrl = null);

internal static class DiscoveryStringExtensions
{
    public static string Truncate(this string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
