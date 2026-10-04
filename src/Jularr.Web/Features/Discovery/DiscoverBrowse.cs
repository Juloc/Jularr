using System.Globalization;
using Jularr.Web.Ui;

namespace Jularr.Web.Features.Discovery;

public enum DiscoverAvailabilityFilter
{
    Any,

    /// <summary>Titles that are already in the library.</summary>
    InLibrary,

    /// <summary>Titles with an open request.</summary>
    Requested,

    /// <summary>Titles that are neither in the library nor requested.</summary>
    NotInLibrary
}

/// <summary>
/// What the Discover address asks for (docs/mockups/discover): the search text, the media-type scope and the
/// filter panel. Every member round-trips through the address, so any view can be bookmarked and shared; the
/// server keeps no state. The address keeps the scheme the shelf deep links and Home already use
/// (<c>q</c>, <c>category</c>, <c>mode</c>, <c>genre</c>).
/// </summary>
public sealed record DiscoverBrowseQuery
{
    public const string BasePath = "/Discover";

    public string Text { get; init; } = "";

    public DiscoveryCategory Category { get; init; }

    /// <summary>The browse ordering (trending, top, new) or the AniList list; a search text overrides it.</summary>
    public DiscoveryMode Mode { get; init; }

    public string Genre { get; init; } = "";

    public int? Year { get; init; }

    public MediaReleaseStatus? Status { get; init; }

    public DiscoverAvailabilityFilter Availability { get; init; }

    public bool PreferredLanguage { get; init; }

    public bool IsSearch => Text.Length > 0;

    /// <summary>The filters that narrow what was loaded; the browse ordering is not one of them.</summary>
    public bool HasPostFilters =>
        Year is not null || Status is not null || Availability != DiscoverAvailabilityFilter.Any || PreferredLanguage;

    /// <summary>How many filters are on; shown on the Filters button.</summary>
    public int ActiveFilterCount =>
        (Genre.Length > 0 ? 1 : 0)
        + (Year is null ? 0 : 1)
        + (Status is null ? 0 : 1)
        + (Availability == DiscoverAvailabilityFilter.Any ? 0 : 1)
        + (PreferredLanguage ? 1 : 0)
        + (!IsSearch && Mode == DiscoveryMode.MyList ? 1 : 0);

    /// <summary>The default landing (shelves): no search text, the trending ordering and no filter.</summary>
    public bool IsLanding => !IsSearch && Mode == DiscoveryMode.Trending && ActiveFilterCount == 0;

    public string Href => BuildHref(this);

    public DiscoveryRequest ToRequest() =>
        new(Text, Category, IsSearch ? DiscoveryMode.Search : Mode, Genre);

    /// <summary>The same view without any filter, keeping the search text, the scope and the ordering.</summary>
    public DiscoverBrowseQuery WithoutFilters() => this with
    {
        Genre = "",
        Year = null,
        Status = null,
        Availability = DiscoverAvailabilityFilter.Any,
        PreferredLanguage = false,
        Mode = Mode == DiscoveryMode.MyList ? DiscoveryMode.Trending : Mode
    };

    public static DiscoverBrowseQuery Parse(Func<string, string?> get)
    {
        ArgumentNullException.ThrowIfNull(get);

        // The shared parser normalises text, scope, ordering and genre; without a text it never yields Search.
        var request = DiscoveryRequest.Parse(get("q"), get("category"), get("mode"), get("genre"));
        var parsedMode = DiscoveryRequest.Parse(null, get("category"), get("mode")).Mode;
        var mode = parsedMode switch
        {
            DiscoveryMode.New when request.Category is not (DiscoveryCategory.Book or DiscoveryCategory.Movie or DiscoveryCategory.Series)
                => DiscoveryMode.Trending,
            DiscoveryMode.Upcoming when request.Category is not (DiscoveryCategory.Movie or DiscoveryCategory.Series)
                => DiscoveryMode.Trending,
            _ => parsedMode
        };

        return new DiscoverBrowseQuery
        {
            Text = request.Query,
            Category = request.Category,
            Mode = mode,
            Genre = request.Genre,
            Year = int.TryParse(get("year"), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
                   && year is >= 1900 and <= 2100
                ? year
                : null,
            Status = StatusFrom(get("status")),
            Availability = get("avail")?.Trim().ToLowerInvariant() switch
            {
                "library" => DiscoverAvailabilityFilter.InLibrary,
                "requested" => DiscoverAvailabilityFilter.Requested,
                "new" => DiscoverAvailabilityFilter.NotInLibrary,
                _ => DiscoverAvailabilityFilter.Any
            },
            PreferredLanguage = get("pref") == "1"
        };
    }

    public static string CategoryName(DiscoveryCategory category) => category switch
    {
        DiscoveryCategory.Anime => "anime",
        DiscoveryCategory.Movie => "movie",
        DiscoveryCategory.Series => "tv",
        DiscoveryCategory.LightNovel => "light-novel",
        DiscoveryCategory.Manga => "manga",
        DiscoveryCategory.Book => "book",
        DiscoveryCategory.BooksAndLightNovels => "books-light-novels",
        _ => "all"
    };

    public static string ModeName(DiscoveryMode mode) => mode switch
    {
        DiscoveryMode.Top => "top",
        DiscoveryMode.MyList => "my-list",
        DiscoveryMode.New => "new",
        DiscoveryMode.Upcoming => "upcoming",
        _ => "trending"
    };

    public static string StatusName(MediaReleaseStatus status) => status.ToString().ToLowerInvariant();

    public static string AvailabilityName(DiscoverAvailabilityFilter availability) => availability switch
    {
        DiscoverAvailabilityFilter.InLibrary => "library",
        DiscoverAvailabilityFilter.Requested => "requested",
        DiscoverAvailabilityFilter.NotInLibrary => "new",
        _ => ""
    };

    private static MediaReleaseStatus? StatusFrom(string? value) =>
        Enum.GetValues<MediaReleaseStatus>()
            .Cast<MediaReleaseStatus?>()
            .FirstOrDefault(status => string.Equals(
                StatusName(status!.Value), value?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string BuildHref(DiscoverBrowseQuery query)
    {
        var parts = new List<string>();
        void Add(string name, string value) => parts.Add(name + "=" + Uri.EscapeDataString(value));

        if (query.Text.Length > 0)
        {
            Add("q", query.Text);
        }

        if (query.Category != DiscoveryCategory.All)
        {
            Add("category", CategoryName(query.Category));
        }

        if (query.Mode != DiscoveryMode.Trending)
        {
            Add("mode", ModeName(query.Mode));
        }

        if (query.Genre.Length > 0)
        {
            Add("genre", query.Genre);
        }

        if (query.Year is { } year)
        {
            Add("year", year.ToString(CultureInfo.InvariantCulture));
        }

        if (query.Status is { } status)
        {
            Add("status", StatusName(status));
        }

        if (query.Availability != DiscoverAvailabilityFilter.Any)
        {
            Add("avail", AvailabilityName(query.Availability));
        }

        if (query.PreferredLanguage)
        {
            Add("pref", "1");
        }

        return parts.Count == 0 ? BasePath : BasePath + "?" + string.Join('&', parts);
    }
}

/// <summary>The media-type switch under the search field: only the scopes with a discovery source behind them.</summary>
public static class DiscoverScopes
{
    public static IReadOnlyList<(DiscoveryCategory Category, string LabelKey)> Tabs { get; } =
    [
        (DiscoveryCategory.All, "discover.categories.all"),
        (DiscoveryCategory.Anime, "discover.categories.anime"),
        (DiscoveryCategory.Movie, "search.type.movie"),
        (DiscoveryCategory.Series, "search.type.series"),
        (DiscoveryCategory.BooksAndLightNovels, "discover.categories.booksLightNovels"),
        (DiscoveryCategory.Manga, "reading.manga.title")
    ];

    /// <summary>Whether a tab is the active one; the single-type scopes reached from a shelf link belong to the combined tab.</summary>
    public static bool IsActive(DiscoveryCategory tab, DiscoveryCategory current) =>
        tab == current
        || (tab == DiscoveryCategory.BooksAndLightNovels
            && current is DiscoveryCategory.Book or DiscoveryCategory.LightNovel);

    /// <summary>Whether a shelf row about <paramref name="row"/> belongs to the scope the visitor picked.</summary>
    public static bool Includes(DiscoveryCategory scope, DiscoveryCategory row) => scope switch
    {
        DiscoveryCategory.All => true,
        DiscoveryCategory.BooksAndLightNovels => row is DiscoveryCategory.Book
            or DiscoveryCategory.LightNovel
            or DiscoveryCategory.BooksAndLightNovels,
        _ => scope == row
    };
}

/// <summary>The catalog keys of the genres the filter offers; provider genres outside the list are not shown.</summary>
public static class DiscoverGenres
{
    public static string? Key(string? genre) => genre?.Trim().ToLowerInvariant() switch
    {
        "action" => "discover.genre.action",
        "adventure" => "discover.genre.adventure",
        "comedy" => "discover.genre.comedy",
        "drama" => "discover.genre.drama",
        "fantasy" => "discover.genre.fantasy",
        "horror" => "discover.genre.horror",
        "mystery" => "discover.genre.mystery",
        "romance" => "discover.genre.romance",
        "sci-fi" => "discover.genre.sciFi",
        "slice of life" => "discover.genre.sliceOfLife",
        "sports" => "discover.genre.sports",
        "supernatural" => "discover.genre.supernatural",
        _ => null
    };
}
