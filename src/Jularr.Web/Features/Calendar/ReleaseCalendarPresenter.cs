using System.Globalization;
using Jularr.Web.Features.Localization;

namespace Jularr.Web.Features.Calendar;

/// <summary>
/// Display text for release events: labels come from the UI catalog, dates and times follow the
/// viewer's locale and time zone, and a date is never shown more precisely than it is known.
/// </summary>
public sealed class ReleaseCalendarPresenter
{
    private readonly UiTextBundle ui;

    /// <param name="playbackEnabled">Whether this instance plays: only then does an available episode open the player.</param>
    public ReleaseCalendarPresenter(UiTextBundle ui, TimeZoneInfo zone, DateTimeOffset now, bool playbackEnabled)
    {
        this.ui = ui;
        PlaybackEnabled = playbackEnabled;
        Zone = zone;
        Now = now;
        Culture = CultureFor(ui.Locale);
        Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
    }

    public CultureInfo Culture { get; }

    public bool PlaybackEnabled { get; }

    public TimeZoneInfo Zone { get; }

    public DateTimeOffset Now { get; }

    public DateOnly Today { get; }

    public DayOfWeek FirstDayOfWeek => Culture.DateTimeFormat.FirstDayOfWeek;

    public static CultureInfo CultureFor(string? locale)
    {
        try
        {
            return string.IsNullOrWhiteSpace(locale) ? CultureInfo.GetCultureInfo("en") : CultureInfo.GetCultureInfo(locale);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.GetCultureInfo("en");
        }
    }

    public string MediaLabel(ReleaseMediaType type) => ui[type switch
    {
        ReleaseMediaType.Anime => "calendar.media.anime",
        ReleaseMediaType.Tv => "calendar.media.tv",
        ReleaseMediaType.Movie => "calendar.media.movie",
        ReleaseMediaType.Manga => "calendar.media.manga",
        ReleaseMediaType.LightNovel => "calendar.media.lightNovel",
        _ => "calendar.media.book"
    }];

    public string FilterLabel(ReleaseMediaType type) => ui[type switch
    {
        ReleaseMediaType.Anime => "calendar.filter.anime",
        ReleaseMediaType.Tv => "calendar.filter.tv",
        ReleaseMediaType.Movie => "calendar.filter.movies",
        ReleaseMediaType.Manga => "calendar.filter.manga",
        ReleaseMediaType.LightNovel => "calendar.filter.lightNovels",
        _ => "calendar.filter.books"
    }];

    public string StateFilterLabel(ReleaseStateFilter filter) => ui[filter switch
    {
        ReleaseStateFilter.Monitored => "calendar.filter.monitored",
        ReleaseStateFilter.Missing => "calendar.filter.missing",
        ReleaseStateFilter.Available => "calendar.filter.available",
        _ => "calendar.filter.anyState"
    }];

    /// <summary>What is released, for example "Premiere · Episode 1", "S2 · Episode 5" or "Chapter 143".</summary>
    public string UnitLabel(ReleaseEvent release)
    {
        var unit = release.Unit is { } value ? UnitText(release.Kind, value) : null;
        var kind = release.Kind switch
        {
            ReleaseKind.SeasonPremiere => ui["calendar.kind.premiere"],
            ReleaseKind.SeriesStart => ui["calendar.kind.seriesStart"],
            ReleaseKind.Publication => ui["calendar.kind.publication"],
            ReleaseKind.Cinema => ui["calendar.kind.cinema"],
            ReleaseKind.Digital => ui["calendar.kind.digital"],
            ReleaseKind.Streaming => ui["calendar.kind.streaming"],
            ReleaseKind.Physical => ui["calendar.kind.physical"],
            _ => null
        };

        return string.Join(" · ", new[] { kind, unit }.Where(text => !string.IsNullOrEmpty(text)));
    }

    private string UnitText(ReleaseKind kind, ReleaseUnit unit)
    {
        var number = unit.Number.ToString("0.##", Culture);
        return kind switch
        {
            ReleaseKind.Chapter => ui.Format("calendar.unit.chapter", ("number", number)),
            ReleaseKind.Volume => ui.Format("calendar.unit.volume", ("number", number)),
            _ when unit.Season is > 1 => ui.Format("calendar.unit.seasonEpisode", ("season", unit.Season), ("number", number)),
            _ => ui.Format("calendar.unit.episode", ("number", number))
        };
    }

    /// <summary>The local time of an exact release; null for dates without a time.</summary>
    public string? TimeLabel(ReleaseDate date) =>
        date.Precision == ReleaseDatePrecision.DateTime
            ? TimeZoneInfo.ConvertTime(date.Instant!.Value, Zone).ToString("t", Culture)
            : null;

    /// <summary>The whole date at its precision: "29 September · 23:00", "October 2026", "Q4 2026", "2026" or "Date unknown".</summary>
    public string DateLabel(ReleaseDate date)
    {
        switch (date.Precision)
        {
            case ReleaseDatePrecision.DateTime:
            case ReleaseDatePrecision.Day:
                var day = date.ExactDay(Zone)!.Value;
                var text = ShortDay(day);
                return TimeLabel(date) is { } time ? $"{text} · {time}" : text;
            case ReleaseDatePrecision.Month:
                return new DateOnly(date.Year, date.Month!.Value, 1).ToString(Culture.DateTimeFormat.YearMonthPattern, Culture);
            case ReleaseDatePrecision.Quarter:
                return ui.Format("calendar.date.quarter", ("quarter", date.Quarter), ("year", date.Year));
            case ReleaseDatePrecision.Year:
                return date.Year.ToString(CultureInfo.InvariantCulture);
            default:
                return ui["calendar.date.unknown"];
        }
    }

    /// <summary>A day without weekday, with the year only when it is not the current one.</summary>
    public string ShortDay(DateOnly day)
    {
        var text = day.ToString(Culture.DateTimeFormat.MonthDayPattern, Culture);
        return day.Year == Today.Year ? text : $"{text} {day.Year}";
    }

    public string DayHeading(DateOnly day)
    {
        var offset = day.DayNumber - Today.DayNumber;
        var relative = offset switch
        {
            0 => ui["calendar.day.today"],
            1 => ui["calendar.day.tomorrow"],
            -1 => ui["calendar.day.yesterday"],
            _ => null
        };
        var weekday = Culture.DateTimeFormat.GetDayName(day.DayOfWeek);
        return relative is null ? $"{weekday}, {ShortDay(day)}" : $"{relative} · {ShortDay(day)}";
    }

    public string WeekdayShort(DayOfWeek day) => Culture.DateTimeFormat.GetAbbreviatedDayName(day);

    public string MonthTitle(DateOnly month) => month.ToString(Culture.DateTimeFormat.YearMonthPattern, Culture);

    public string RangeTitle(DateOnly start, DateOnly end) =>
        start.Year == end.Year && start.Year == Today.Year
            ? $"{start.ToString(Culture.DateTimeFormat.MonthDayPattern, Culture)} – {end.ToString(Culture.DateTimeFormat.MonthDayPattern, Culture)}"
            : $"{ShortDayWithYear(start)} – {ShortDayWithYear(end)}";

    private string ShortDayWithYear(DateOnly day) => $"{day.ToString(Culture.DateTimeFormat.MonthDayPattern, Culture)} {day.Year}";

    /// <summary>Label and tone (ok, warn, error, info or muted) of the local state.</summary>
    public (string Label, string Tone) State(ReleaseLocalStatus status) => status.State switch
    {
        ReleaseLocalState.Available => (ui["calendar.state.available"], "ok"),
        ReleaseLocalState.Missing => (ui["calendar.state.missing"], "error"),
        ReleaseLocalState.Failed => (ui["acquisition.state.needsAttention"], "error"),
        ReleaseLocalState.Wanted or ReleaseLocalState.Searching => (ui["acquisition.state.lookingForMedia"], "warn"),
        ReleaseLocalState.Grabbed => (ui["acquisition.state.gettingMedia"], "warn"),
        ReleaseLocalState.Monitored => (ui["calendar.state.monitored"], "info"),
        ReleaseLocalState.NotMonitored => (ui["calendar.state.notMonitored"], "muted"),
        ReleaseLocalState.Following => (ui["calendar.state.following"], "muted"),
        _ => status.InLibrary ? (ui["calendar.state.inLibrary"], "muted") : (ui["calendar.state.notInLibrary"], "muted")
    };

    /// <summary>Where the date comes from, so every date stays traceable to its source.</summary>
    public string ProviderLabel(string provider) => provider.ToLowerInvariant() switch
    {
        "anilist" => "AniList",
        "syosetu" => "Syosetu",
        _ => ui["calendar.provider.library"]
    };

    /// <summary>The canonical page of the event: the local episode when it exists, else the media page.</summary>
    public string? Href(ReleaseEvent release) =>
        !string.IsNullOrWhiteSpace(release.DetailsUrl)
            ? release.DetailsUrl
            : release.MediaType switch
            {
                ReleaseMediaType.Anime when PlaybackEnabled && release.UnitId is { } episodeId && release.Local.IsAvailable => $"/Library/Episode/{episodeId}",
                ReleaseMediaType.Anime => $"/Library/Anime/{release.MediaId}",
                ReleaseMediaType.Manga => $"/Manga/Series/{release.MediaId}",
                ReleaseMediaType.LightNovel => $"/Novels/Work/{release.MediaId}",
                ReleaseMediaType.Book => $"/Books/Library/{release.MediaId}",
                _ => null
            };

    public static string FilterValue(ReleaseMediaType type) => type switch
    {
        ReleaseMediaType.LightNovel => "lightNovel",
        _ => type.ToString().ToLowerInvariant()
    };

    public static ReleaseMediaType? ParseFilterValue(string? value) =>
        Enum.GetValues<ReleaseMediaType>().Cast<ReleaseMediaType?>()
            .FirstOrDefault(type => string.Equals(FilterValue(type!.Value), value, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The viewer's time zone: the IANA id the browser reports (cookie set by calendar.js), else the
/// server's zone (the container's TZ).
/// </summary>
public static class CalendarTimeZone
{
    public const string CookieName = "jularr-tz";

    public static TimeZoneInfo Resolve(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id) &&
            id.Length <= 64 &&
            id.All(character => char.IsAsciiLetterOrDigit(character) || character is '/' or '_' or '-' or '+') &&
            TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
        {
            return zone;
        }

        return TimeZoneInfo.Local;
    }

    public static TimeZoneInfo Resolve(HttpContext context) => Resolve(context.Request.Cookies[CookieName]);
}
