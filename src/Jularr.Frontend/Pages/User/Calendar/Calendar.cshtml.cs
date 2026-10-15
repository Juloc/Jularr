using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.User.Calendar.V1;
using Jularr.Backend.Service.User.Calendar.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.Calendar
{
    // Releases of the profile's titles: a month grid by default, or two weeks day by day.
    public sealed class CalendarModel(ReadCalendarV1 read, UiText texts) : JularrPage
    {
        private const int AgendaDays = 14;
        private const int ShownPerDay = 3;
        private IReadOnlyList<CalendarEntryV1> _entries = [];

        [BindProperty(SupportsGet = true, Name = "view")]
        public string? View { get; set; }

        // The month shown, yyyy-MM; the agenda's first day, yyyy-MM-dd; today when absent.
        [BindProperty(SupportsGet = true)]
        public string? Month { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? From { get; set; }

        [BindProperty(SupportsGet = true, Name = "type")]
        public MediaType? Type { get; set; }

        private bool IsAgenda => View == "agenda";

        private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

        private DateOnly FirstOfMonth => DateOnly.TryParseExact(Month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month) ? month : new DateOnly(Today.Year, Today.Month, 1);

        private DateOnly AgendaStart => DateOnly.TryParseExact(From, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : Today;

        // Whole weeks from Monday that hold the month.
        private DateOnly GridStart => FirstOfMonth.AddDays(-(((int)FirstOfMonth.DayOfWeek + 6) % 7));

        private int GridDays
        {
            get
            {
                var last = FirstOfMonth.AddMonths(1).AddDays(-1);
                return last.AddDays(6 - ((int)last.DayOfWeek + 6) % 7).DayNumber - GridStart.DayNumber + 1;
            }
        }

        protected override void Define(Page page)
        {
            page.Title("calendar.title");
            page.Navigation("calendar.title");
            page.Asset("Pages/User/Calendar/Calendar.entry.scss");

            // View, then the period with its steps, then the media types that have releases in it.
            page.Content.Add(new NavigationTabs([new NavigationTab("calendar.view.month", Address(view: null), !IsAgenda), new NavigationTab("calendar.view.agenda", Address(view: "agenda"), IsAgenda)],
                "calendar.title", "view"));
            page.Content.Add(new Nav(
                new A(new Text("calendar.earlier")).Class("button").Attribute("href", IsAgenda ? Address(from: AgendaStart.AddDays(-AgendaDays)) : Address(month: FirstOfMonth.AddMonths(-1))),
                new Span(new ValueText(IsAgenda ? AgendaStart.ToString("d", CultureInfo.CurrentCulture) + " – " + AgendaStart.AddDays(AgendaDays - 1).ToString("d", CultureInfo.CurrentCulture)
                    : FirstOfMonth.ToString("MMMM yyyy", CultureInfo.CurrentCulture))).Class("calendar__period"),
                new A(new Text("calendar.later")).Class("button").Attribute("href", IsAgenda ? Address(from: AgendaStart.AddDays(AgendaDays)) : Address(month: FirstOfMonth.AddMonths(1))),
                new A(new Text("calendar.today")).Class("button").Attribute("href", IsAgenda ? Address(view: "agenda") : Address(view: null))).Class("calendar__navigation")
                .Attribute("aria-label", texts["calendar.pages"]));
            var types = _entries.Select(entry => entry.MediaTypeId).Distinct().Order().ToArray();
            if (types.Length > 1)
            {
                page.Content.Add(new NavigationTabs([new NavigationTab("discover.all", Address(type: null, keepType: false), Type is null),
                    .. types.Select(type => new NavigationTab(MediaTypeLabel.Key(type, null), Address(type: type, keepType: false), type == Type))], "discover.mediaTypes", "media"));
            }

            var entries = _entries.Where(entry => Type is null || entry.MediaTypeId == Type).ToArray();
            if (IsAgenda)
            {
                DefineAgenda(page, entries);
            }
            else
            {
                DefineMonth(page, entries);
            }
        }

        private void DefineAgenda(Page page, IReadOnlyList<CalendarEntryV1> entries)
        {
            if (entries.Count == 0)
            {
                page.Content.Add(new P(new Text("calendar.empty")).Attribute("role", "status"));
                return;
            }

            foreach (var day in entries.GroupBy(entry => entry.Day))
            {
                page.Content.Add(new Section(
                    new H2(new ValueText(day.Key.ToString("D", CultureInfo.CurrentCulture))),
                    new Ul([.. day.Select(Entry)])).Class("calendar__day"));
            }
        }

        // Each day lists its first releases; more open the agenda of that day.
        private void DefineMonth(Page page, IReadOnlyList<CalendarEntryV1> entries)
        {
            var byDay = entries.ToLookup(entry => entry.Day);
            var weekdays = Enumerable.Range(0, 7).Select(offset => new Div(new ValueText(GridStart.AddDays(offset).ToString("ddd", CultureInfo.CurrentCulture))).Class("calendar__weekday")
                .Attribute("aria-hidden", "true"));
            var days = Enumerable.Range(0, GridDays).Select(offset =>
            {
                var day = GridStart.AddDays(offset);
                var releases = byDay[day].ToArray();
                var content = new List<IUiComponent> { new Span(new ValueText(day.Day.ToString(CultureInfo.CurrentCulture))).Class("calendar__date") };
                if (releases.Length > 0)
                {
                    content.Add(new Ul([.. releases.Take(ShownPerDay).Select(Entry)]));
                }

                if (releases.Length > ShownPerDay)
                {
                    content.Add(new A(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["calendar.more"], releases.Length - ShownPerDay))).Class("calendar__more")
                        .Attribute("href", Address(view: "agenda", from: day)));
                }

                var cell = new Section([.. content]).Class("calendar__cell" + (day.Month == FirstOfMonth.Month ? "" : " calendar__cell--outside") + (releases.Length == 0 ? " calendar__cell--empty" : ""))
                    .Attribute("aria-label", day.ToString("D", CultureInfo.CurrentCulture));
                return day == Today ? cell.Attribute("aria-current", "date") : cell;
            });
            page.Content.Add(new Div([.. weekdays, .. days]).Class("calendar__grid"));
            if (entries.Count == 0)
            {
                page.Content.Add(new P(new Text("calendar.empty")).Attribute("role", "status"));
            }
        }

        private IUiComponent Entry(CalendarEntryV1 entry)
        {
            var parts = new List<IUiComponent>();
            if (entry.PosterUrl is { } poster)
            {
                parts.Add(new MediaImage(poster, entry.Title, 32, 48));
            }

            parts.Add(new A(new ValueText(entry.Title)).Attribute("href", "/Details/" + entry.WorkId.ToString(CultureInfo.InvariantCulture)));
            if (Unit(entry) is { Length: > 0 } unit) parts.Add(new Span(new ValueText(unit)).Class("calendar__unit"));
            if (entry.IsAvailable) parts.Add(new Span(new Text("library.present")).Class("calendar__state"));
            return new Li([.. parts]).Class("calendar__entry");
        }

        // A whole Work, such as a movie, has no unit line.
        private string Unit(CalendarEntryV1 entry) =>
            string.Join(" · ", new[] { MediaUnitLabel.Format(texts, entry.WorkUnitTypeId, entry.UnitNumber, entry.ParentUnitNumber), entry.UnitTitle }.OfType<string>());

        // The view, period and type stay unless a link changes them.
        private string Address(string? view = "", DateOnly? month = null, DateOnly? from = null, MediaType? type = null, bool keepType = true)
        {
            var shownView = view == "" ? View : view;
            var values = new List<string>();
            if (shownView == "agenda")
            {
                values.Add("view=agenda");
                if (from is { } day) values.Add("from=" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
            else if (month is { } first)
            {
                values.Add("month=" + first.ToString("yyyy-MM", CultureInfo.InvariantCulture));
            }

            if ((keepType ? Type : type) is { } shownType) values.Add("type=" + shownType);
            return values.Count == 0 ? "/Calendar" : "/Calendar?" + string.Join('&', values);
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            _entries = await read.ExecuteAsync<BaseDataList<CalendarEntryV1>>(IsAgenda ? new ReadCalendarParametersV1(AgendaStart, AgendaDays) : new ReadCalendarParametersV1(GridStart, GridDays),
                HttpContext.RequestAborted);
        }
    }
}
