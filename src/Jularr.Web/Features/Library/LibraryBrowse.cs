using System.Globalization;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Playback;
using Jularr.Web.Ui;

namespace Jularr.Web.Features.Library;

/// <summary>One title of the Library grid: the banner-card facts plus what browsing needs on top.</summary>
/// <param name="PlayableUnits">Episodes (or specials, for a title that only has those) with a file.</param>
/// <param name="MissingUnits">Known episodes without a file, plus the ones a finished title's provider lists beyond the library.</param>
/// <param name="LastWatchedAt">The last meaningful watch progress of the profile; null when never watched.</param>
/// <param name="WorkId">The canonical identity of the title; the Card href of an Anime is still keyed by its legacy record.</param>
/// <param name="RuntimeMinutes">The runtime of a Movie from its analysed file; null for episodic titles and when unknown.</param>
/// <param name="RemainingMinutes">What is left of a Movie that is in progress; null otherwise.</param>
public sealed record LibraryCardEntry(
    MediaBannerCardData Card,
    string? PosterUrl,
    string? Format,
    int PlayableUnits,
    int MissingUnits,
    DateTime AddedAt,
    DateTime? LastWatchedAt,
    Guid WorkId,
    WorkMediaType MediaType,
    int? RuntimeMinutes,
    int? RemainingMinutes);

/// <summary>The entries of one Library read; <see cref="Degraded"/> when supporting details (open requests) could not be loaded.</summary>
public sealed record LibraryEntries(IReadOnlyList<LibraryCardEntry> Entries, bool Degraded);

public enum LibrarySort
{
    Recent,
    Title,
    LastWatched,
    YearNewest,
    YearOldest,
    ProgressHighest,
    ProgressLowest,
    Rating
}

public enum LibraryLayout
{
    Grid,
    List
}

public enum LibraryProgressState
{
    NotStarted,
    InProgress,
    Completed
}

public enum LibraryAvailabilityState
{
    /// <summary>Every known unit has a file.</summary>
    Complete,

    /// <summary>Some units have a file, others are missing.</summary>
    Partial,

    /// <summary>An open request is on its way (searching, downloading, importing).</summary>
    Requested,

    /// <summary>Nothing has a file and nothing is requested.</summary>
    Missing
}

/// <summary>The preferred audio and subtitle language of the profile, the basis of the language-first card.</summary>
public sealed record LibraryLanguagePreference(string? Audio, string? Subtitle)
{
    public static LibraryLanguagePreference None { get; } = new(null, null);

    public bool IsSet => Audio is not null || Subtitle is not null;

    /// <summary>The preferred codes, upper-case, audio first.</summary>
    public IReadOnlyList<string> Codes =>
    [
        .. new[] { Audio, Subtitle }
            .Where(code => code is not null)
            .Select(code => code!.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
    ];

    /// <summary>Normalises the stored preference; "subtitles off" and unknown values mean no preference.</summary>
    public static LibraryLanguagePreference From(string? audio, string? subtitle)
    {
        static string? Clean(string? value) =>
            PlaybackLanguages.Normalize(value) is { } code && code != PlaybackLanguages.SubtitlesOff ? code : null;

        return new LibraryLanguagePreference(Clean(audio), Clean(subtitle));
    }

    /// <summary>Whether the title has the preferred audio or the preferred subtitle language.</summary>
    public bool IsAvailableIn(LibraryCardEntry entry) =>
        (Audio is not null && LibraryBrowse.Has(entry.Card.AudioLanguages, Audio))
        || (Subtitle is not null && LibraryBrowse.Has(entry.Card.SubtitleLanguages, Subtitle));
}

/// <summary>What the Library grid is narrowed and ordered by; every member round-trips through the address.</summary>
public sealed record LibraryBrowseQuery
{
    public bool Collections { get; init; }

    /// <summary>The media-type scope of the Library (a tab, not a filter); null shows every video type.</summary>
    public WorkMediaType? MediaType { get; init; }

    public LibrarySort Sort { get; init; }
    public LibraryLayout Layout { get; init; }
    public IReadOnlyList<LibraryProgressState> Progress { get; init; } = [];
    public IReadOnlyList<LibraryAvailabilityState> Availability { get; init; } = [];
    public bool PreferredLanguage { get; init; }
    public string? AudioLanguage { get; init; }
    public string? SubtitleLanguage { get; init; }
    public int? Year { get; init; }
    public MediaReleaseStatus? Status { get; init; }
    public string? Format { get; init; }

    /// <summary>How many filters are on; shown on the Filters button.</summary>
    public int ActiveFilterCount =>
        Progress.Count + Availability.Count + (PreferredLanguage ? 1 : 0)
        + (AudioLanguage is null ? 0 : 1) + (SubtitleLanguage is null ? 0 : 1)
        + (Year is null ? 0 : 1) + (Status is null ? 0 : 1) + (Format is null ? 0 : 1);

    public LibraryBrowseQuery WithoutFilters() => this with
    {
        Progress = [],
        Availability = [],
        PreferredLanguage = false,
        AudioLanguage = null,
        SubtitleLanguage = null,
        Year = null,
        Status = null,
        Format = null
    };
}

/// <summary>How many titles each filter option would match, and which options the library offers at all.</summary>
public sealed record LibraryFacets(
    IReadOnlyDictionary<LibraryProgressState, int> Progress,
    IReadOnlyDictionary<LibraryAvailabilityState, int> Availability,
    int PreferredLanguage,
    IReadOnlyList<string> AudioLanguages,
    IReadOnlyList<string> SubtitleLanguages,
    IReadOnlyList<int> Years,
    IReadOnlyList<MediaReleaseStatus> Statuses,
    IReadOnlyList<string> Formats);

/// <summary>What the Library body shows, decided from the read and the query.</summary>
public enum LibraryPageState
{
    /// <summary>The read failed.</summary>
    Error,

    /// <summary>Nothing is in the library.</summary>
    Empty,

    /// <summary>Titles exist but the filters match none.</summary>
    NoResults,

    /// <summary>Titles are shown, but supporting details could not be loaded.</summary>
    Degraded,

    Ready
}

/// <summary>The small availability indicator of a card; complete titles have none.</summary>
public sealed record LibraryAvailabilityIndicator(LibraryAvailabilityState State, string Label)
{
    public string CssModifier => State.ToString().ToLowerInvariant();
}

public sealed record LibraryCardAction(string Label, string Url);

/// <summary>One media-type tab of the Library: the video scopes are addresses of this page, the others lead to their own page.</summary>
public sealed record LibraryScopeTab(string LabelKey, string Href, bool IsActive);

/// <summary>Everything one Library card renders, resolved and localised; build it with <see cref="Create"/>.</summary>
public sealed record LibraryCardView(
    string Title,
    string Href,
    string? PosterUrl,
    string Initial,
    string? StatusText,
    int? ProgressPercent,
    string? ProgressAria,
    string? ProgressText,
    AnimeLanguageChips Audio,
    AnimeLanguageChips Subtitles,
    bool PreferredMissing,
    LibraryAvailabilityIndicator? Availability,
    LibraryCardAction? Action)
{
    public bool HasLanguages => Audio.Any || Subtitles.Any;

    public static LibraryCardView Create(LibraryCardEntry entry, LibraryLanguagePreference preference, UiTextBundle ui)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(preference);
        ArgumentNullException.ThrowIfNull(ui);

        var card = entry.Card;
        var progress = card.Progress;
        var culture = Culture(ui.Locale);
        var isMovie = entry.MediaType == WorkMediaType.Movie;

        string? statusText;
        int? percent = null;
        string? progressAria = null;
        string? progressText = null;
        LibraryCardAction? action = null;

        var yearText = card.Year is > 0 ? card.Year.Value.ToString(culture) : null;

        // A Movie has no next unit: it shows what it is (year and runtime) until it is resumed or finished.
        var movieFacts = string.Join(" · ", new[] { yearText, entry.RuntimeMinutes is > 0 ? FormatRuntime(entry.RuntimeMinutes.Value) : null }.Where(fact => fact is not null));
        string? movieStatus = movieFacts.Length > 0 ? movieFacts : null;

        if (progress is null)
        {
            statusText = isMovie ? movieStatus : yearText;
        }
        else
        {
            var number = progress.NextNumber.ToString("0.##", culture);
            statusText = (isMovie, progress.State) switch
            {
                (true, MediaBannerProgressState.InProgress) when entry.RemainingMinutes is int left =>
                    ui.Format("library.browse.card.minutesLeft", ("minutes", left)),
                (true, MediaBannerProgressState.Completed) => ui["library.browse.card.completed"],
                (true, _) => movieStatus,
                (false, MediaBannerProgressState.NotStarted) => ui["library.browse.card.notStarted"],
                (false, MediaBannerProgressState.Completed) => ui["library.browse.card.completed"],
                _ => progress.NextSeason is int season
                    ? ui.Format("library.browse.card.seasonEpisode", ("season", season), ("number", number))
                    : ui.Format("library.browse.card.episode", ("number", number))
            };

            if (progress.State != MediaBannerProgressState.NotStarted && progress.Percent is int value)
            {
                percent = Math.Clamp(value, 0, 100);
                progressAria = ui.Format("library.mediaCard.progressWatched", ("percent", percent.Value));
                progressText = ui.Format("library.mediaCard.percent", ("percent", percent.Value));
            }

            // Only Anime has a player route to open from the card; Movies and Series play from their detail page.
            if (entry.MediaType == WorkMediaType.Anime)
            {
                action = new LibraryCardAction(
                    ui[progress.State switch
                    {
                        MediaBannerProgressState.NotStarted => "library.mediaCard.startWatching",
                        MediaBannerProgressState.Completed => "library.mediaCard.watchAgain",
                        _ => "library.mediaCard.continueWatching"
                    }],
                    progress.NextUrl);
            }
        }

        var audio = AnimeDetailView.Chips(ToSet(card.AudioLanguages), preference.Audio);
        var subtitles = AnimeDetailView.Chips(ToSet(card.SubtitleLanguages), preference.Subtitle);

        return new LibraryCardView(
            card.Title,
            card.Href,
            entry.PosterUrl,
            InitialOf(card.Title),
            statusText,
            percent,
            progressAria,
            progressText,
            audio,
            subtitles,
            preference.IsSet && (audio.Any || subtitles.Any) && !preference.IsAvailableIn(entry),
            Indicator(entry, ui),
            action);
    }

    /// <summary>
    /// The canonical detail address of a title. An Anime is still keyed by its legacy record id until its
    /// detail page moves to the Work; Movies and Series are keyed by the Work.
    /// </summary>
    public static string DetailHref(WorkMediaType mediaType, Guid id) => mediaType switch
    {
        WorkMediaType.Anime => $"/Library/Anime/{id}",
        WorkMediaType.Series => $"/Library/Series/{id}",
        WorkMediaType.Movie => $"/Library/Movie/{id}",
        _ => throw new ArgumentOutOfRangeException(nameof(mediaType))
    };

    private static string FormatRuntime(int minutes) =>
        minutes >= 60 ? $"{minutes / 60}h {minutes % 60:00}m" : $"{minutes}m";

    private static string InitialOf(string title)
    {
        var trimmed = title.Trim();
        return trimmed.Length == 0 ? "·" : StringInfo.GetNextTextElement(trimmed).ToUpperInvariant();
    }

    private static IReadOnlySet<string> ToSet(IReadOnlyList<string>? languages) =>
        new HashSet<string>(languages ?? [], StringComparer.Ordinal);

    private static LibraryAvailabilityIndicator? Indicator(LibraryCardEntry entry, UiTextBundle ui)
    {
        if (LibraryBrowse.IndicatorOf(entry) is not { } state)
        {
            return null;
        }

        return state switch
        {
            LibraryAvailabilityState.Requested => new(
                state,
                ui["requests.status." + AcquisitionAccessNames.Status(entry.Card.Availability!.Request!.Value)]),
            LibraryAvailabilityState.Partial => new(state, ui["library.browse.availability.partial"]),
            LibraryAvailabilityState.Missing => new(state, ui["library.browse.availability.missing"]),
            _ => null
        };
    }

    private static CultureInfo Culture(string locale)
    {
        try
        {
            return string.IsNullOrWhiteSpace(locale) ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(locale);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}

/// <summary>
/// Pure rules behind the Library page (docs/mockups/library): reading the address, filtering, ordering,
/// option counts and the state of the body. Nothing here reads the database, so every rule is tested
/// on its own. The address is the only state, so every view is bookmarkable.
/// </summary>
public static class LibraryBrowse
{
    public const string BasePath = "/Library";

    private static readonly LibraryProgressState[] ProgressOrder =
        [LibraryProgressState.NotStarted, LibraryProgressState.InProgress, LibraryProgressState.Completed];

    private static readonly LibraryAvailabilityState[] AvailabilityOrder =
    [
        LibraryAvailabilityState.Complete, LibraryAvailabilityState.Partial,
        LibraryAvailabilityState.Requested, LibraryAvailabilityState.Missing
    ];

    private static readonly LibrarySort[] SortOrder =
    [
        LibrarySort.Recent, LibrarySort.Title, LibrarySort.LastWatched, LibrarySort.YearNewest,
        LibrarySort.YearOldest, LibrarySort.ProgressHighest, LibrarySort.ProgressLowest, LibrarySort.Rating
    ];

    /// <summary>The media types this page serves, in tab order.</summary>
    public static IReadOnlyList<WorkMediaType> VideoMediaTypes { get; } = [WorkMediaType.Anime, WorkMediaType.Series, WorkMediaType.Movie];

    /// <summary>Every sort in menu order.</summary>
    public static IReadOnlyList<LibrarySort> Sorts => SortOrder;

    public static IReadOnlyList<LibraryProgressState> ProgressStates => ProgressOrder;

    public static IReadOnlyList<LibraryAvailabilityState> AvailabilityStates => AvailabilityOrder;

    public static string SortName(LibrarySort sort) => sort switch
    {
        LibrarySort.Title => "title",
        LibrarySort.LastWatched => "watched",
        LibrarySort.YearNewest => "year-desc",
        LibrarySort.YearOldest => "year-asc",
        LibrarySort.ProgressHighest => "progress-desc",
        LibrarySort.ProgressLowest => "progress-asc",
        LibrarySort.Rating => "rating",
        _ => "recent"
    };

    public static string SortKey(LibrarySort sort) => sort switch
    {
        LibrarySort.Title => "library.browse.sort.title",
        LibrarySort.LastWatched => "library.browse.sort.lastWatched",
        LibrarySort.YearNewest => "library.browse.sort.yearNewest",
        LibrarySort.YearOldest => "library.browse.sort.yearOldest",
        LibrarySort.ProgressHighest => "library.browse.sort.progressHighest",
        LibrarySort.ProgressLowest => "library.browse.sort.progressLowest",
        LibrarySort.Rating => "library.browse.sort.rating",
        _ => "library.browse.sort.recent"
    };

    public static string ProgressName(LibraryProgressState state) => state switch
    {
        LibraryProgressState.InProgress => "inprogress",
        LibraryProgressState.Completed => "completed",
        _ => "notstarted"
    };

    public static string ProgressKey(LibraryProgressState state) => "library.browse.progress." + ProgressName(state);

    public static string AvailabilityName(LibraryAvailabilityState state) => state.ToString().ToLowerInvariant();

    public static string AvailabilityKey(LibraryAvailabilityState state) =>
        "library.browse.availabilityFilter." + AvailabilityName(state);

    public static string StatusName(MediaReleaseStatus status) => status.ToString().ToLowerInvariant();

    public static string StatusKey(MediaReleaseStatus status) => status switch
    {
        MediaReleaseStatus.Ongoing => "library.mediaCard.status.ongoing",
        MediaReleaseStatus.Finished => "library.mediaCard.status.finished",
        MediaReleaseStatus.Upcoming => "library.mediaCard.status.upcoming",
        MediaReleaseStatus.Hiatus => "library.mediaCard.status.hiatus",
        _ => "library.mediaCard.status.cancelled"
    };

    /// <summary>Reads the address. Unknown values are ignored, so a stale bookmark still opens the plain Library.</summary>
    /// <param name="read">The values of one query key, empty when absent.</param>
    public static LibraryBrowseQuery Parse(Func<string, IReadOnlyList<string>> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        string? First(string key) => read(key).Select(x => x?.Trim()).FirstOrDefault(x => !string.IsNullOrEmpty(x));

        var sortValue = First("sort")?.ToLowerInvariant();
        var yearText = First("year");
        var format = First("format")?.ToUpperInvariant();
        var statusText = First("status")?.ToLowerInvariant();
        var mediaType = WorkMediaTypes.Parse(First("type"));

        return new LibraryBrowseQuery
        {
            Collections = string.Equals(First("section"), "collections", StringComparison.OrdinalIgnoreCase),
            MediaType = mediaType is { } scope && VideoMediaTypes.Contains(scope) ? scope : null,
            Sort = SortOrder.FirstOrDefault(sort => SortName(sort) == sortValue),
            Layout = string.Equals(First("view"), "list", StringComparison.OrdinalIgnoreCase)
                ? LibraryLayout.List
                : LibraryLayout.Grid,
            Progress = [.. ProgressOrder.Where(state => read("progress").Any(x => Matches(x, ProgressName(state))))],
            Availability = [.. AvailabilityOrder.Where(state => read("avail").Any(x => Matches(x, AvailabilityName(state))))],
            PreferredLanguage = First("pref") is "1" or "true",
            AudioLanguage = Language(First("audio")),
            SubtitleLanguage = Language(First("sub")),
            Year = int.TryParse(yearText, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
                && year is >= 1900 and <= 2200 ? year : null,
            Status = Enum.GetValues<MediaReleaseStatus>()
                .Cast<MediaReleaseStatus?>()
                .FirstOrDefault(status => StatusName(status!.Value) == statusText),
            Format = format is { Length: > 0 and <= 16 } && format.All(c => c is >= 'A' and <= 'Z' or '_') ? format : null
        };
    }

    /// <summary>The address of a view: only what differs from the plain Library is written.</summary>
    public static string Href(LibraryBrowseQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var parts = new List<string>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                parts.Add($"{key}={Uri.EscapeDataString(value)}");
            }
        }

        if (query.Collections)
        {
            Add("section", "collections");
            return BasePath + "?" + string.Join('&', parts);
        }

        if (query.MediaType is { } mediaType)
        {
            Add("type", WorkMediaTypes.ToStorage(mediaType));
        }

        if (query.Sort != LibrarySort.Recent)
        {
            Add("sort", SortName(query.Sort));
        }

        if (query.Layout == LibraryLayout.List)
        {
            Add("view", "list");
        }

        foreach (var state in query.Progress)
        {
            Add("progress", ProgressName(state));
        }

        foreach (var state in query.Availability)
        {
            Add("avail", AvailabilityName(state));
        }

        if (query.PreferredLanguage)
        {
            Add("pref", "1");
        }

        Add("audio", query.AudioLanguage);
        Add("sub", query.SubtitleLanguage);
        Add("year", query.Year?.ToString(CultureInfo.InvariantCulture));
        Add("status", query.Status is { } status ? StatusName(status) : null);
        Add("format", query.Format);

        return parts.Count == 0 ? BasePath : BasePath + "?" + string.Join('&', parts);
    }

    /// <summary>The state of the body from the read and the query.</summary>
    public static LibraryPageState ResolveState(bool failed, bool degraded, int total, int shown)
    {
        if (failed)
        {
            return LibraryPageState.Error;
        }

        if (total == 0)
        {
            return LibraryPageState.Empty;
        }

        if (shown == 0)
        {
            return LibraryPageState.NoResults;
        }

        return degraded ? LibraryPageState.Degraded : LibraryPageState.Ready;
    }

    public static LibraryProgressState ProgressOf(LibraryCardEntry entry) =>
        entry.Card.Progress?.State switch
        {
            MediaBannerProgressState.InProgress => LibraryProgressState.InProgress,
            MediaBannerProgressState.Completed => LibraryProgressState.Completed,
            _ => LibraryProgressState.NotStarted
        };

    /// <summary>Whether a title belongs under an availability filter; a title can match several.</summary>
    public static bool Matches(LibraryCardEntry entry, LibraryAvailabilityState state) => state switch
    {
        LibraryAvailabilityState.Complete => entry.PlayableUnits > 0 && entry.MissingUnits == 0,
        LibraryAvailabilityState.Partial => entry.PlayableUnits > 0 && entry.MissingUnits > 0,
        LibraryAvailabilityState.Requested => HasOpenRequest(entry),
        _ => entry.PlayableUnits == 0 && !HasOpenRequest(entry)
    };

    /// <summary>The single state a card shows: a request first, then partial, then nothing available; complete titles show none.</summary>
    public static LibraryAvailabilityState? IndicatorOf(LibraryCardEntry entry)
    {
        if (HasOpenRequest(entry))
        {
            return LibraryAvailabilityState.Requested;
        }

        if (entry.PlayableUnits == 0)
        {
            return LibraryAvailabilityState.Missing;
        }

        return entry.MissingUnits > 0 ? LibraryAvailabilityState.Partial : null;
    }

    /// <summary>Titles matching the filters, in the order of the sort.</summary>
    public static IReadOnlyList<LibraryCardEntry> Apply(
        IEnumerable<LibraryCardEntry> entries,
        LibraryBrowseQuery query,
        LibraryLanguagePreference preference)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(preference);

        return Sort([.. Scope(entries, query).Where(entry => Matches(entry, query, preference))], query.Sort);
    }

    /// <summary>The titles of the selected media-type tab, before any filter.</summary>
    public static IReadOnlyList<LibraryCardEntry> Scope(IEnumerable<LibraryCardEntry> entries, LibraryBrowseQuery query)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(query);

        return [.. entries.Where(entry => query.MediaType is null || entry.MediaType == query.MediaType)];
    }

    /// <summary>
    /// The media-type tabs: All (only when several video types are visible) and each visible video type as a scope of this page,
    /// then the other Library destinations the profile may browse. Sort and filters stay when switching scope.
    /// </summary>
    public static IReadOnlyList<LibraryScopeTab> ScopeTabs(
        LibraryBrowseQuery query,
        IReadOnlyCollection<WorkMediaType> visibleVideoTypes,
        IEnumerable<UiNavigationItem> otherTabs)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(visibleVideoTypes);
        ArgumentNullException.ThrowIfNull(otherTabs);

        var video = VideoMediaTypes
            .Where(visibleVideoTypes.Contains)
            .Select(type => new LibraryScopeTab(ScopeKey(type), Href(query with { MediaType = type }), query.MediaType == type))
            .ToList();
        if (video.Count > 1)
        {
            video.Insert(0, new LibraryScopeTab("library.browse.scope.all", Href(query with { MediaType = null }), query.MediaType is null));
        }

        return [.. video, .. otherTabs.Select(tab => new LibraryScopeTab(tab.LabelKey, tab.Href, false))];
    }

    public static string ScopeKey(WorkMediaType type) => type switch
    {
        WorkMediaType.Series => "library.browse.scope.series",
        WorkMediaType.Movie => "library.browse.scope.movie",
        _ => "library.browse.scope.anime"
    };
    public static bool Matches(LibraryCardEntry entry, LibraryBrowseQuery query, LibraryLanguagePreference preference) =>
        (query.Progress.Count == 0 || query.Progress.Contains(ProgressOf(entry)))
        && (query.Availability.Count == 0 || query.Availability.Any(state => Matches(entry, state)))
        && (!query.PreferredLanguage || preference.IsAvailableIn(entry))
        && (query.AudioLanguage is null || Has(entry.Card.AudioLanguages, query.AudioLanguage))
        && (query.SubtitleLanguage is null || Has(entry.Card.SubtitleLanguages, query.SubtitleLanguage))
        && (query.Year is null || entry.Card.Year == query.Year)
        && (query.Status is null || MediaBannerCardModel.MapStatus(entry.Card.ProviderStatus) == query.Status)
        && (query.Format is null || FormatOf(entry) == query.Format);

    public static IReadOnlyList<LibraryCardEntry> Sort(IReadOnlyList<LibraryCardEntry> entries, LibrarySort sort)
    {
        // Every order falls back to the title so equal values never shuffle between requests.
        static string Title(LibraryCardEntry entry) => entry.Card.Title;
        var byTitle = StringComparer.OrdinalIgnoreCase;

        return sort switch
        {
            LibrarySort.Title => [.. entries.OrderBy(Title, byTitle)],
            LibrarySort.LastWatched => [.. entries
                .OrderBy(x => x.LastWatchedAt is null)
                .ThenByDescending(x => x.LastWatchedAt)
                .ThenBy(Title, byTitle)],
            LibrarySort.YearNewest => [.. entries
                .OrderBy(x => x.Card.Year is null)
                .ThenByDescending(x => x.Card.Year)
                .ThenBy(Title, byTitle)],
            LibrarySort.YearOldest => [.. entries
                .OrderBy(x => x.Card.Year is null)
                .ThenBy(x => x.Card.Year)
                .ThenBy(Title, byTitle)],
            LibrarySort.ProgressHighest => [.. entries.OrderByDescending(ProgressPercent).ThenBy(Title, byTitle)],
            LibrarySort.ProgressLowest => [.. entries.OrderBy(ProgressPercent).ThenBy(Title, byTitle)],
            LibrarySort.Rating => [.. entries
                .OrderBy(x => x.Card.AverageScore is null)
                .ThenByDescending(x => x.Card.AverageScore)
                .ThenBy(Title, byTitle)],
            _ => [.. entries.OrderByDescending(x => x.AddedAt).ThenBy(Title, byTitle)]
        };
    }

    /// <summary>Watched share of a title, 0 when nothing was started.</summary>
    public static int ProgressPercent(LibraryCardEntry entry) =>
        ProgressOf(entry) switch
        {
            LibraryProgressState.Completed => 100,
            LibraryProgressState.InProgress => entry.Card.Progress?.Percent ?? 0,
            _ => 0
        };

    /// <summary>Counts per option over the whole library, and the values the select filters offer.</summary>
    public static LibraryFacets Facets(IReadOnlyCollection<LibraryCardEntry> entries, LibraryLanguagePreference preference)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(preference);

        static IReadOnlyList<string> Distinct(IEnumerable<IReadOnlyList<string>?> lists) =>
        [
            .. lists
                .SelectMany(list => list ?? [])
                .Select(code => code.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];

        return new LibraryFacets(
            ProgressOrder.ToDictionary(state => state, state => entries.Count(x => ProgressOf(x) == state)),
            AvailabilityOrder.ToDictionary(state => state, state => entries.Count(x => Matches(x, state))),
            preference.IsSet ? entries.Count(preference.IsAvailableIn) : 0,
            Distinct(entries.Select(x => x.Card.AudioLanguages)),
            Distinct(entries.Select(x => x.Card.SubtitleLanguages)),
            [.. entries.Where(x => x.Card.Year is > 0).Select(x => x.Card.Year!.Value).Distinct().OrderDescending()],
            [.. entries
                .Select(x => MediaBannerCardModel.MapStatus(x.Card.ProviderStatus))
                .Where(x => x is not null)
                .Select(x => x!.Value)
                .Distinct()
                .Order()],
            [.. entries
                .Select(FormatOf)
                .Where(format => format is not null && Jularr.Web.Features.Watchlist.WatchlistLabels.FormatKey(format) is not null)
                .Select(format => format!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)]);
    }

    /// <summary>Case-insensitive membership of a language code in a card's language list.</summary>
    public static bool Has(IReadOnlyList<string>? languages, string code) =>
        languages is not null && languages.Any(x => string.Equals(x, code, StringComparison.OrdinalIgnoreCase));

    private static string? FormatOf(LibraryCardEntry entry) =>
        string.IsNullOrWhiteSpace(entry.Format) ? null : entry.Format.Trim().ToUpperInvariant();

    private static string? Language(string? value) =>
        PlaybackLanguages.Normalize(value) is { } code && code != PlaybackLanguages.SubtitlesOff ? code : null;

    private static bool Matches(string value, string name) =>
        string.Equals(value?.Trim(), name, StringComparison.OrdinalIgnoreCase);

    private static bool HasOpenRequest(LibraryCardEntry entry) =>
        entry.Card.Availability?.Request is { } status && AcquisitionAccessNames.IsOpen(status);
}
