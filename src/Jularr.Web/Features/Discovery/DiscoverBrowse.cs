using System.Globalization;
using Jularr.Web.Features.MediaCore;
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
/// What the Discover address asks for (docs/mockups/discover): the search text, the media-type scope, the browse view and the filters. Every
/// member round-trips through the address, so any view can be bookmarked and shared; the server keeps no state. The three concepts stay
/// apart: the <see cref="Category"/> (what am I browsing), the <see cref="Mode"/> (which ranking) and the filters (how it is narrowed). The
/// address keeps the scheme the shelf deep links and Home already use (<c>q</c>, <c>category</c>, <c>mode</c>, <c>genre</c>); the multi-valued
/// filters are comma separated (<c>genre=Fantasy,Sci-Fi&amp;status=ongoing&amp;from=2015&amp;to=2026&amp;avail=library,requested</c>).
/// </summary>
public sealed record DiscoverBrowseQuery
{
    public const string BasePath = "/";

    public string Text { get; init; } = "";

    public DiscoveryCategory Category { get; init; }

    /// <summary>The browse view (overview, trending, top, new, upcoming, popular, top rated) or the AniList list; a search text overrides it. Without one the scope opens its overview.</summary>
    public DiscoveryMode Mode { get; init; } = DiscoveryMode.Overview;

    public IReadOnlyList<string> Genres { get; init; } = [];

    public int? YearFrom { get; init; }

    public int? YearTo { get; init; }

    public IReadOnlyList<MediaReleaseStatus> Statuses { get; init; } = [];

    public IReadOnlyList<DiscoverAvailabilityFilter> Availabilities { get; init; } = [];

    public bool PreferredLanguage { get; init; }

    public bool IsSearch => Text.Length > 0;

    /// <summary>The constraints the providers can filter on, pushed into every provider page.</summary>
    public DiscoveryFilter ProviderFilter => new(Genres, YearFrom, YearTo, Statuses);

    /// <summary>The constraints only the local state can answer (what the library and the requests know); paging continues until enough titles match.</summary>
    public bool HasLocalFilters => Availabilities.Count > 0 || PreferredLanguage;

    /// <summary>How many filters are on; shown on the Filters button.</summary>
    public int ActiveFilterCount =>
        Genres.Count
        + (YearFrom is null && YearTo is null ? 0 : 1)
        + Statuses.Count
        + Availabilities.Count
        + (PreferredLanguage ? 1 : 0);

    /// <summary>The overview (shelves one below the other, of every type or of the selected one): no search text, no chosen browse view and no filter.</summary>
    public bool IsLanding => !IsSearch && Mode == DiscoveryMode.Overview && ActiveFilterCount == 0;

    public string Href => BuildHref(this);

    public DiscoveryRequest ToRequest() =>
        new(Text, Category, IsSearch ? DiscoveryMode.Search : Mode == DiscoveryMode.Overview ? DiscoveryMode.Trending : Mode) { Filter = ProviderFilter };

    /// <summary>The same view without any filter, keeping the search text, the scope and the browse view.</summary>
    public DiscoverBrowseQuery WithoutFilters() => this with
    {
        Genres = [],
        YearFrom = null,
        YearTo = null,
        Statuses = [],
        Availabilities = [],
        PreferredLanguage = false
    };

    /// <summary>The same view with one filter token removed (the token ids are those of <see cref="ActiveFilters"/>).</summary>
    public DiscoverBrowseQuery WithoutFilter(string token) => token switch
    {
        "year" => this with { YearFrom = null, YearTo = null },
        "pref" => this with { PreferredLanguage = false },
        _ when token.StartsWith("genre:", StringComparison.Ordinal) => this with { Genres = [.. Genres.Where(genre => genre != token[6..])] },
        _ when token.StartsWith("status:", StringComparison.Ordinal) => this with { Statuses = [.. Statuses.Where(status => StatusName(status) != token[7..])] },
        _ when token.StartsWith("avail:", StringComparison.Ordinal) => this with { Availabilities = [.. Availabilities.Where(availability => AvailabilityName(availability) != token[6..])] },
        _ => this
    };

    /// <summary>The active filters as removable tokens: a stable id (for <see cref="WithoutFilter"/>) and the kind that names its label.</summary>
    public IReadOnlyList<(string Token, string Kind, string Value)> ActiveFilters
    {
        get
        {
            var tokens = new List<(string, string, string)>();
            tokens.AddRange(Genres.Select(genre => ("genre:" + genre, "genre", genre)));
            if (YearFrom is not null || YearTo is not null)
            {
                tokens.Add(("year", "year", YearRangeText(YearFrom, YearTo)));
            }

            tokens.AddRange(Statuses.Select(status => ("status:" + StatusName(status), "status", StatusName(status))));
            tokens.AddRange(Availabilities.Select(availability => ("avail:" + AvailabilityName(availability), "avail", AvailabilityName(availability))));
            if (PreferredLanguage)
            {
                tokens.Add(("pref", "pref", "1"));
            }

            return tokens;
        }
    }

    public static string YearRangeText(int? from, int? to) => (from, to) switch
    {
        ({ } a, { } b) when a == b => a.ToString(CultureInfo.InvariantCulture),
        ({ } a, { } b) => $"{a}–{b}",
        ({ } a, null) => $"{a}–",
        (null, { } b) => $"–{b}",
        _ => ""
    };

    public static DiscoverBrowseQuery Parse(Func<string, string?> get)
    {
        ArgumentNullException.ThrowIfNull(get);

        // The shared parser normalises text, scope and browse view; without a text it never yields Search.
        var request = DiscoveryRequest.Parse(get("q"), get("category"), get("mode"));
        var parsedMode = DiscoveryRequest.Parse(null, get("category"), get("mode")).Mode;
        var asked = string.IsNullOrWhiteSpace(get("mode")) ? DiscoveryMode.Overview : string.Equals(get("mode")?.Trim(), "all", StringComparison.OrdinalIgnoreCase) ? DiscoveryMode.Overview : parsedMode;
        var mode = DiscoverBrowseModes.Supported(request.Category, asked) ? asked : DiscoveryMode.Overview;
        var legacyYear = Year(get("year"));
        var from = Year(get("from")) ?? legacyYear;
        var to = Year(get("to")) ?? legacyYear;
        if (from is { } low && to is { } high && low > high)
        {
            (from, to) = (high, low);
        }

        return new DiscoverBrowseQuery
        {
            Text = request.Query,
            Category = request.Category,
            Mode = mode,
            Genres = [.. Split(get("genre")).Select(DiscoveryRequest.NormalizeGenre).Where(genre => genre.Length > 0).Distinct()],
            YearFrom = from,
            YearTo = to,
            Statuses = [.. Split(get("status")).Select(StatusFrom).OfType<MediaReleaseStatus>().Distinct()],
            Availabilities = [.. Split(get("avail")).Select(AvailabilityFrom).Where(availability => availability != DiscoverAvailabilityFilter.Any).Distinct()],
            PreferredLanguage = get("pref") == "1"
        };
    }

    private static IEnumerable<string> Split(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(12);

    private static int? Year(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is >= 1900 and <= 2100 ? year : null;

    public static string CategoryName(DiscoveryCategory category) => category switch
    {
        DiscoveryCategory.Anime => "anime",
        DiscoveryCategory.Movie => "movie",
        DiscoveryCategory.Series => "tv",
        DiscoveryCategory.LightNovel => "light-novel",
        DiscoveryCategory.Manga => "manga",
        DiscoveryCategory.Book => "book",
        _ => "all"
    };

    public static string ModeName(DiscoveryMode mode) => mode switch
    {
        DiscoveryMode.Top => "top",
        DiscoveryMode.MyList => "my-list",
        DiscoveryMode.New => "new",
        DiscoveryMode.Upcoming => "upcoming",
        DiscoveryMode.Popular => "popular",
        DiscoveryMode.TopRated => "top-rated",
        DiscoveryMode.Overview => "all",
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

    private static DiscoverAvailabilityFilter AvailabilityFrom(string value) => value.ToLowerInvariant() switch
    {
        "library" => DiscoverAvailabilityFilter.InLibrary,
        "requested" => DiscoverAvailabilityFilter.Requested,
        "new" => DiscoverAvailabilityFilter.NotInLibrary,
        _ => DiscoverAvailabilityFilter.Any
    };

    private static MediaReleaseStatus? StatusFrom(string value) =>
        Enum.GetValues<MediaReleaseStatus>()
            .Cast<MediaReleaseStatus?>()
            .FirstOrDefault(status => string.Equals(StatusName(status!.Value), value, StringComparison.OrdinalIgnoreCase));

    private static string BuildHref(DiscoverBrowseQuery query)
    {
        var parts = new List<string>();
        void Add(string name, string value) => parts.Add(name + "=" + Uri.EscapeDataString(value).Replace("%2C", ","));

        if (query.Text.Length > 0)
        {
            Add("q", query.Text);
        }

        if (query.Category != DiscoveryCategory.All)
        {
            Add("category", CategoryName(query.Category));
        }

        if (query.Mode != DiscoveryMode.Overview)
        {
            Add("mode", ModeName(query.Mode));
        }

        if (query.Genres.Count > 0)
        {
            Add("genre", string.Join(',', query.Genres));
        }

        if (query.YearFrom is { } from)
        {
            Add("from", from.ToString(CultureInfo.InvariantCulture));
        }

        if (query.YearTo is { } to)
        {
            Add("to", to.ToString(CultureInfo.InvariantCulture));
        }

        if (query.Statuses.Count > 0)
        {
            Add("status", string.Join(',', query.Statuses.Select(StatusName)));
        }

        if (query.Availabilities.Count > 0)
        {
            Add("avail", string.Join(',', query.Availabilities.Select(AvailabilityName)));
        }

        if (query.PreferredLanguage)
        {
            Add("pref", "1");
        }

        return parts.Count == 0 ? BasePath : BasePath + "?" + string.Join('&', parts);
    }
}

/// <summary>
/// The browse views a media type really has (docs/mockups/discover): a view is only offered where its provider can answer it truthfully.
/// AniList (Anime, Manga, Light Novels) and TMDB (Movies, Series) rank by trend, score, start date, status and popularity; Open Library has a
/// global trending feed, an all-time edition-count ranking and a recently catalogued feed, but no usable rating feed.
/// </summary>
public static class DiscoverBrowseModes
{
    private static readonly DiscoveryMode[] Video = [DiscoveryMode.Overview, DiscoveryMode.Trending, DiscoveryMode.Top, DiscoveryMode.New, DiscoveryMode.Upcoming, DiscoveryMode.Popular, DiscoveryMode.TopRated];
    private static readonly DiscoveryMode[] AniList = [.. Video, DiscoveryMode.MyList];
    private static readonly DiscoveryMode[] Books = [DiscoveryMode.Overview, DiscoveryMode.Trending, DiscoveryMode.Popular, DiscoveryMode.New];

    /// <summary>The views of a scope in the order the browse navigation shows them; My List is included here and hidden when no account is connected.</summary>
    public static IReadOnlyList<DiscoveryMode> For(DiscoveryCategory category) => category switch
    {
        DiscoveryCategory.Anime or DiscoveryCategory.Manga or DiscoveryCategory.LightNovel => AniList,
        DiscoveryCategory.Movie or DiscoveryCategory.Series => Video,
        DiscoveryCategory.Book => Books,
        _ => AniList
    };

    public static bool Supported(DiscoveryCategory category, DiscoveryMode mode) =>
        mode == DiscoveryMode.Search || For(category).Contains(mode);

    /// <summary>The label key of a view in the browse navigation.</summary>
    public static string LabelKey(DiscoveryMode mode) => mode switch
    {
        DiscoveryMode.Top => "discover.tabs.top",
        DiscoveryMode.New => "discover.tabs.new",
        DiscoveryMode.Upcoming => "discover.tabs.upcoming",
        DiscoveryMode.Popular => "discover.tabs.popular",
        DiscoveryMode.TopRated => "discover.tabs.topRated",
        DiscoveryMode.Overview => "discover.categories.all",
        DiscoveryMode.MyList => "discover.tabs.myList",
        _ => "discover.tabs.trending"
    };

    /// <summary>The release statuses a source can filter on without guessing: AniList knows all five, TMDB distinguishes released from upcoming for movies and has a status for series; Open Library has none.</summary>
    public static IReadOnlyList<MediaReleaseStatus> StatusesFor(DiscoveryCategory category) => category switch
    {
        DiscoveryCategory.Movie => [MediaReleaseStatus.Upcoming, MediaReleaseStatus.Finished],
        DiscoveryCategory.Series => [MediaReleaseStatus.Ongoing, MediaReleaseStatus.Finished, MediaReleaseStatus.Upcoming, MediaReleaseStatus.Cancelled],
        DiscoveryCategory.Book => [],
        _ => [MediaReleaseStatus.Ongoing, MediaReleaseStatus.Finished, MediaReleaseStatus.Upcoming, MediaReleaseStatus.Hiatus, MediaReleaseStatus.Cancelled]
    };

    /// <summary>How far a view describes the viewer's region. No source of this application supplies a regional signal today.</summary>
    public static DiscoveryRankingScope ScopeOf(DiscoveryCategory category, DiscoveryMode mode) =>
        mode is DiscoveryMode.Search or DiscoveryMode.MyList ? DiscoveryRankingScope.Locale : DiscoveryRankingScope.Global;
}

/// <summary>The media-type switch under the search field: only the scopes with a discovery source behind them.</summary>
public static class DiscoverScopes
{
    public static IReadOnlyList<(DiscoveryCategory Category, string LabelKey)> Tabs { get; } =
    [
        (DiscoveryCategory.All, "discover.categories.all"),
        (DiscoveryCategory.Anime, "discover.categories.anime"),
        (DiscoveryCategory.Series, "search.type.series"),
        (DiscoveryCategory.Movie, "discover.categories.movies"),
        (DiscoveryCategory.LightNovel, "discover.categories.lightNovel"),
        (DiscoveryCategory.Book, "discover.categories.books"),
        (DiscoveryCategory.Manga, "reading.manga.title")
    ];

    /// <summary>Whether a tab is the active one.</summary>
    public static bool IsActive(DiscoveryCategory tab, DiscoveryCategory current) => tab == current;

    /// <summary>Whether the profile may browse the media type of a scope; a hidden type has no tab, no row and no result.</summary>
    public static bool IsVisible(DiscoveryCategory scope, IReadOnlySet<WorkMediaType> visible) => scope switch
    {
        DiscoveryCategory.All => true,
        DiscoveryCategory.Anime => visible.Contains(WorkMediaType.Anime),
        DiscoveryCategory.Movie => visible.Contains(WorkMediaType.Movie),
        DiscoveryCategory.Series => visible.Contains(WorkMediaType.Series),
        DiscoveryCategory.Manga => visible.Contains(WorkMediaType.Manga),
        DiscoveryCategory.LightNovel => visible.Contains(WorkMediaType.LightNovel),
        DiscoveryCategory.Book => visible.Contains(WorkMediaType.Book),
        _ => false
    };

    /// <summary>Whether a shelf row about <paramref name="row"/> belongs to the scope the visitor picked.</summary>
    public static bool Includes(DiscoveryCategory scope, DiscoveryCategory row) => scope switch
    {
        DiscoveryCategory.All => true,
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
