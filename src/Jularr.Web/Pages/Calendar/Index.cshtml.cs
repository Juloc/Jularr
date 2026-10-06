using System.Globalization;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Calendar;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Calendar;

public enum CalendarView
{
    Agenda,
    Week,
    Month
}

public sealed class IndexModel(
    AppDbContext db,
    ReleaseCalendarService calendar,
    CurrentAccountContext account,
    TimeProvider clock,
    IInstanceModuleService modules) : PageModel
{
    public const int AgendaDays = 28;

    [BindProperty(SupportsGet = true, Name = "view")]
    public string? ViewName { get; set; }

    [BindProperty(SupportsGet = true, Name = "date")]
    public string? DateText { get; set; }

    [BindProperty(SupportsGet = true, Name = "type")]
    public string? TypeText { get; set; }

    [BindProperty(SupportsGet = true, Name = "state")]
    public string? StateText { get; set; }

    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public ReleaseCalendarPresenter Presenter { get; private set; } = null!;

    public CalendarView View { get; private set; }

    public DateOnly Anchor { get; private set; }

    /// <summary>The period the view is about (the month itself, not the padded grid).</summary>
    public DateOnly PeriodStart { get; private set; }

    public DateOnly PeriodEnd { get; private set; }

    public ReleaseMediaType? MediaType { get; private set; }

    public ReleaseStateFilter State { get; private set; }

    public IReadOnlyList<ReleaseMediaType> MediaTypes { get; private set; } = [];

    public ReleaseCalendarResult Result { get; private set; } = null!;

    public string ZoneId { get; private set; } = "";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var zone = CalendarTimeZone.Resolve(HttpContext);
        ZoneId = zone.Id;
        var now = clock.GetUtcNow();
        Presenter = new ReleaseCalendarPresenter(Ui, zone, now, await modules.IsEnabledAsync(InstanceModule.Playback, cancellationToken));

        View = Enum.TryParse<CalendarView>(ViewName, ignoreCase: true, out var view) ? view : CalendarView.Month;
        Anchor = DateOnly.TryParseExact(DateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var anchor)
            ? anchor
            : Presenter.Today;
        MediaTypes = await calendar.GetSupportedMediaTypesAsync(cancellationToken);
        MediaType = ReleaseCalendarPresenter.ParseFilterValue(TypeText) is { } type && MediaTypes.Contains(type) ? type : null;
        State = Enum.TryParse<ReleaseStateFilter>(StateText, ignoreCase: true, out var state) ? state : ReleaseStateFilter.All;

        var (start, end) = Range();
        var filter = new ReleaseCalendarFilter(
            MediaType is { } selected ? new HashSet<ReleaseMediaType> { selected } : new HashSet<ReleaseMediaType>(),
            State,
            account.ProfileId);
        Result = await calendar.GetAsync(start, end, zone, filter, now, View == CalendarView.Agenda, cancellationToken);
    }

    /// <summary>The queried days: the padded month grid, the week, or the agenda window.</summary>
    private (DateOnly Start, DateOnly End) Range()
    {
        switch (View)
        {
            case CalendarView.Month:
                PeriodStart = new DateOnly(Anchor.Year, Anchor.Month, 1);
                PeriodEnd = PeriodStart.AddMonths(1).AddDays(-1);
                return (WeekStart(PeriodStart), WeekStart(PeriodEnd).AddDays(6));
            case CalendarView.Week:
                PeriodStart = WeekStart(Anchor);
                PeriodEnd = PeriodStart.AddDays(6);
                return (PeriodStart, PeriodEnd);
            default:
                PeriodStart = Anchor;
                PeriodEnd = Anchor.AddDays(AgendaDays - 1);
                return (PeriodStart, PeriodEnd);
        }
    }

    public DateOnly WeekStart(DateOnly day)
    {
        var offset = ((int)day.DayOfWeek - (int)Presenter.FirstDayOfWeek + 7) % 7;
        return day.AddDays(-offset);
    }

    public string Title => View switch
    {
        CalendarView.Month => Presenter.MonthTitle(PeriodStart),
        _ => Presenter.RangeTitle(PeriodStart, PeriodEnd)
    };

    /// <summary>Releases whose date is only a period overlapping the shown period, or unknown (agenda only).</summary>
    public IReadOnlyList<ReleaseEvent> Imprecise =>
        Result.Imprecise
            .Where(release => release.Date.Precision == ReleaseDatePrecision.Unknown ||
                              release.Date.Overlaps(PeriodStart, PeriodEnd, Presenter.Zone))
            .ToArray();

    /// <summary>No release is known for the shown period (padding days of the month grid do not count).</summary>
    public bool IsPeriodEmpty =>
        Imprecise.Count == 0 &&
        !Result.Days.Any(day => day.Date >= PeriodStart && day.Date <= PeriodEnd && day.Events.Count > 0);

    public DateOnly Previous => View switch
    {
        CalendarView.Month => PeriodStart.AddMonths(-1),
        CalendarView.Week => PeriodStart.AddDays(-7),
        _ => Anchor.AddDays(-AgendaDays)
    };

    public DateOnly Next => View switch
    {
        CalendarView.Month => PeriodStart.AddMonths(1),
        CalendarView.Week => PeriodStart.AddDays(7),
        _ => Anchor.AddDays(AgendaDays)
    };

    public bool ShowsToday => Presenter.Today >= PeriodStart && Presenter.Today <= PeriodEnd && (View != CalendarView.Agenda || Anchor == Presenter.Today);

    /// <summary>A link to this page with some parameters changed; the rest are kept.</summary>
    public string Link(
        CalendarView? view = null,
        DateOnly? date = null,
        ReleaseMediaType? type = null,
        bool allTypes = false,
        ReleaseStateFilter? state = null,
        bool today = false)
    {
        var values = new List<string>();
        var targetView = view ?? View;
        if (targetView != CalendarView.Month)
        {
            values.Add("view=" + targetView.ToString().ToLowerInvariant());
        }

        var targetDate = today ? (DateOnly?)null : date ?? (Anchor == Presenter.Today ? null : Anchor);
        if (targetDate is { } day)
        {
            values.Add("date=" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        var targetType = allTypes ? null : type ?? MediaType;
        if (targetType is { } mediaType)
        {
            values.Add("type=" + ReleaseCalendarPresenter.FilterValue(mediaType));
        }

        var targetState = state ?? State;
        if (targetState != ReleaseStateFilter.All)
        {
            values.Add("state=" + targetState.ToString().ToLowerInvariant());
        }

        return values.Count == 0 ? "/Calendar" : "/Calendar?" + string.Join('&', values);
    }
}
