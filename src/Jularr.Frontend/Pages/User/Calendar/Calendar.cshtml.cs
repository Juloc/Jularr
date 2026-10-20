using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.User.Calendar.V1;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Service.Public.Authentication.V1;
using Jularr.Backend.Service.User.Calendar.V1;
using Jularr.Backend.Service.User.Profile.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.Calendar
{
    public enum CalendarView { Month, Week, Day }

    public enum CalendarStatus { Available, Requested, Watchlisted }

    // Releases of the profile's titles as a month, a week or a day. The address holds the whole view: view, date, media types, status, search and the day chosen in a month.
    public sealed partial class CalendarModel(ReadAuthenticationV1 authentication, ReadProfileMediaV1 readMedia, ReadCalendarV1 read, UiText texts, FrontendAssets assets) : JularrPage
    {
        private const int DefaultShown = 6;

        // The media types the capsule groups by area; a type the profile hides is left out.
        private static readonly (string Key, string Icon, string[] Scopes)[] Areas =
        [
            ("calendar.area.watch", "play", ["anime", "series", "movies"]), ("calendar.area.read", "reading", ["manga", "books", "lightnovels"]),
            ("calendar.area.hear", "headphones", ["music"]), ("calendar.area.play", "game", ["games"])
        ];

        private IReadOnlyList<CalendarEntryV1> _entries = [];
        private IReadOnlyList<string> _listed = [];

        [BindProperty(SupportsGet = true, Name = "view")]
        public string? View { get; set; }

        // The anchor of the period; today when absent.
        [BindProperty(SupportsGet = true, Name = "date")]
        public string? Date { get; set; }

        // The day chosen in a month, whose releases show below the grid.
        [BindProperty(SupportsGet = true, Name = "day")]
        public string? Day { get; set; }

        [BindProperty(SupportsGet = true, Name = "type")]
        public string[]? Types { get; set; }

        [BindProperty(SupportsGet = true, Name = "status")]
        public string? StatusName { get; set; }

        [BindProperty(SupportsGet = true, Name = "q")]
        public string? Search { get; set; }

        [BindProperty(SupportsGet = true, Name = "shown")]
        public int? Shown { get; set; }

        private sealed record Query(CalendarView View, DateOnly? Date, IReadOnlyList<string> Types, CalendarStatus? Status, string? Search, int Shown, DateOnly? Day);

        private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

        private static DateOnly? ParseDay(string? value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

        private CalendarView CurrentView => Enum.TryParse<CalendarView>(View, true, out var view) && Enum.IsDefined(view) ? view : CalendarView.Month;

        private DateOnly Anchor => ParseDay(Date) ?? Today;

        private CalendarStatus? CurrentStatus => Enum.TryParse<CalendarStatus>(StatusName, true, out var status) && Enum.IsDefined(status) ? status : null;

        private int CurrentShown => Shown is 3 or 4 ? Shown.Value : DefaultShown;

        // Every listed type when none (or all) is chosen.
        private IReadOnlyList<string> SelectedTypes => Types?.SelectMany(type => type.Split(',')).Where(type => _listed.Contains(type)).Distinct().ToArray() is { Length: > 0 } chosen ? chosen : _listed;

        private Query Current => new(CurrentView, ParseDay(Date), SelectedTypes, CurrentStatus, string.IsNullOrWhiteSpace(Search) ? null : Search.Trim(), CurrentShown, ParseDay(Day));

        private static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

        private DateOnly FirstOfMonth => new(Anchor.Year, Anchor.Month, 1);

        // Whole weeks from Monday that hold the month.
        private DateOnly GridStart => WeekStart(FirstOfMonth);

        private int GridDays => WeekStart(FirstOfMonth.AddMonths(1).AddDays(-1)).AddDays(7).DayNumber - GridStart.DayNumber;

        private (DateOnly From, int Days) Range => CurrentView == CalendarView.Month ? (GridStart, GridDays) : (WeekStart(Anchor), 7);

        protected override void Define(Page page)
        {
            page.Title("calendar.title");
            page.Navigation("calendar.title");
            page.Asset("Pages/User/Calendar/Calendar.entry.scss");
            page.Asset("Framework/Components/FilterDialog.entry.ts");
            page.Asset("Framework/Components/LinkMenu.entry.ts");

            var query = Current;
            var visible = _entries.Where(entry => query.Types.Contains(LibraryScopes.Slug(entry.MediaTypeId, entry.MediaSubtypeId)) && (query.Status is null || Status(entry) == query.Status)
                && (query.Search is null || (entry.Title + " " + Unit(entry)).Contains(query.Search, StringComparison.CurrentCultureIgnoreCase))).ToArray();

            var board = query.View switch
            {
                CalendarView.Week => WeekBoard(query, visible),
                CalendarView.Day => DayBoard(query, visible),
                _ => MonthBoard(query, visible)
            };
            page.Content.Add(new Div(Capsules(query), Toolbar(query), board, FilterDialogOf(query), ViewDialogOf(query)).Class("calendar"));
        }

        private IUiComponent Capsules(Query query)
        {
            var all = Areas.SelectMany(area => area.Scopes).Where(_listed.Contains).ToArray();
            var groups = Areas.Select(area => (area.Key, area.Icon, Scopes: area.Scopes.Where(_listed.Contains).ToArray())).Where(area => area.Scopes.Length > 0).Select(area =>
            {
                var chosen = area.Scopes.Count(query.Types.Contains);
                var solo = chosen == area.Scopes.Length && query.Types.Count == area.Scopes.Length;
                return new ChipGroup(area.Key, area.Icon, Address(query with { Types = area.Scopes }), chosen > 0, solo,
                    string.Format(CultureInfo.CurrentCulture, texts["calendar.areaState"], texts[area.Key], chosen, area.Scopes.Length),
                    [.. area.Scopes.Select(scope => new Chip("library.scope." + scope, Address(query with { Types = Toggle(query.Types, scope) }), query.Types.Contains(scope)))]);
            }).ToArray();
            var tools = new IUiComponent[]
            {
                new Button(new Icon("sliders", assets, 18)).IconOnly(texts["calendar.customize"]).Attribute("data-filter-open", "calendar-view"),
                new Button(new Icon("filter", assets, 18)).IconOnly(texts["calendar.filter"]).Attribute("data-filter-open", "calendar-filter")
            };
            return new ChipBar(assets, new Chip("common.all", Address(query with { Types = all }), query.Types.Count == all.Length), groups, "discover.mediaTypes", tools);
        }

        // A chosen type leaves the selection, an unchosen joins it; the last one never leaves.
        private IReadOnlyList<string> Toggle(IReadOnlyList<string> selected, string scope)
        {
            if (!selected.Contains(scope)) return [.. _listed.Where(type => type == scope || selected.Contains(type))];
            return selected.Count == 1 ? selected : [.. selected.Where(type => type != scope)];
        }

        private IUiComponent Toolbar(Query query)
        {
            var statuses = new List<NavigationTab> { new("calendar.status.all", Address(query with { Status = null }), query.Status is null) };
            statuses.AddRange(Enum.GetValues<CalendarStatus>().Select(status => new NavigationTab("calendar.status." + status, Address(query with { Status = status }), query.Status == status)));
            var views = Enum.GetValues<CalendarView>().Select(view => new NavigationTab("calendar.view." + view, Address(query with { View = view, Day = null }), query.View == view)).ToArray();
            var anchor = Anchor;
            var (earlier, later) = query.View switch
            {
                CalendarView.Week => (anchor.AddDays(-7), anchor.AddDays(7)),
                CalendarView.Day => (anchor.AddDays(-1), anchor.AddDays(1)),
                _ => (FirstOfMonth.AddMonths(-1), FirstOfMonth.AddMonths(1))
            };
            var navigation = new Nav(
                new A(new Icon("chevron-left", assets, 18)).Class("button button--icon").Attribute("href", Address(query with { Date = earlier, Day = null })).Attribute("aria-label", texts["calendar.earlier"]),
                new Span(new ValueText(Period(query))).Class("calendar__period").Attribute("aria-live", "polite"),
                new A(new Icon("chevron-right", assets, 18)).Class("button button--icon").Attribute("href", Address(query with { Date = later, Day = null })).Attribute("aria-label", texts["calendar.later"]),
                new A(new Text("calendar.today")).Class("button").Attribute("href", Address(query with { Date = null, Day = null }))).Class("calendar__navigation")
                .Attribute("aria-label", texts["calendar.pages"]);
            return new Div(new SearchBox("/Calendar", "calendar.search", query.Search, Kept(query, search: false)), new LinkMenu(assets, statuses, "calendar.status"),
                new NavigationTabs(views, "calendar.views", "segmented"), navigation).Class("calendar__toolbar");
        }

        private string Period(Query query)
        {
            var culture = CultureInfo.CurrentCulture;
            if (query.View == CalendarView.Day) return Anchor.ToString("ddd, d MMMM yyyy", culture);
            if (query.View == CalendarView.Month) return FirstOfMonth.ToString("MMMM yyyy", culture);
            var start = WeekStart(Anchor);
            var end = start.AddDays(6);
            return start.Month == end.Month ? start.Day.ToString(culture) + "–" + end.ToString("d MMMM yyyy", culture)
                : start.ToString("d MMM", culture) + " – " + end.ToString("d MMM yyyy", culture);
        }

        // The values a form does not edit itself stay in the address it submits to; the types travel as one comma list.
        private Dictionary<string, string> Kept(Query query, bool types = true, bool status = true, bool shown = true, bool search = true)
        {
            var kept = new Dictionary<string, string>();
            if (query.View != CalendarView.Month) kept["view"] = query.View.ToString().ToLowerInvariant();
            if (query.Date is { } date) kept["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (types && query.Types.Count < _listed.Count) kept["type"] = string.Join(',', query.Types);
            if (status && query.Status is { } value) kept["status"] = value.ToString().ToLowerInvariant();
            if (shown && query.Shown != DefaultShown) kept["shown"] = query.Shown.ToString(CultureInfo.InvariantCulture);
            if (search && query.Search is { } text) kept["q"] = text;
            return kept;
        }

        private IUiComponent FilterDialogOf(Query query)
        {
            var groups = new List<FilterGroup>();
            foreach (var area in Areas)
            {
                var scopes = area.Scopes.Where(_listed.Contains).ToArray();
                if (scopes.Length > 0) groups.Add(new FilterGroup(new Text(area.Key), [.. scopes.Select(scope => new FilterChoice("type", scope, new Text("library.scope." + scope), query.Types.Contains(scope), false))]));
            }

            groups.Add(new FilterGroup(new Text("calendar.status"), [new FilterChoice("status", "", new Text("calendar.status.all"), query.Status is null, true),
                .. Enum.GetValues<CalendarStatus>().Select(status => new FilterChoice("status", status.ToString().ToLowerInvariant(), new Text("calendar.status." + status), query.Status == status, true))]));
            return new FilterDialog(assets, "calendar-filter", "calendar.filter", "/Calendar", Kept(query, types: false, status: false), groups, Address(query with { Types = _listed, Status = null, Search = null }));
        }

        // Only the month shows more than a few releases a day.
        private IUiComponent ViewDialogOf(Query query)
        {
            var choices = new[] { 3, 4, DefaultShown }.Select(count => new FilterChoice("shown", count.ToString(CultureInfo.InvariantCulture),
                new ValueText(string.Format(CultureInfo.CurrentCulture, texts["calendar.shownOption"], count)), query.Shown == count, true)).ToArray();
            return new FilterDialog(assets, "calendar-view", "calendar.customize", "/Calendar", Kept(query, shown: false), [new FilterGroup(new Text("calendar.shown"), choices)], Address(query with { Shown = DefaultShown }));
        }

        // The view, period and filters stay unless a link changes them.
        private string Address(Query query)
        {
            var values = new List<string>();
            if (query.View != CalendarView.Month) values.Add("view=" + query.View.ToString().ToLowerInvariant());
            if (query.Date is { } date) values.Add("date=" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (query.Day is { } day) values.Add("day=" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (query.Types.Count < _listed.Count) values.AddRange(query.Types.Select(type => "type=" + type));
            if (query.Status is { } status) values.Add("status=" + status.ToString().ToLowerInvariant());
            if (query.Search is { } search) values.Add("q=" + Uri.EscapeDataString(search));
            if (query.Shown != DefaultShown) values.Add("shown=" + query.Shown.ToString(CultureInfo.InvariantCulture));
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
            var profileId = (await authentication.ExecuteAsync<AuthenticationResultV1>(cancellationToken: HttpContext.RequestAborted)).ProfileId
                ?? throw new UnauthorizedAccessException("An active profile is required.");
            var media = await readMedia.ExecuteAsync<BaseDataList<ProfileMediaV1>>(new ReadProfileParametersV1(profileId), HttpContext.RequestAborted);
            _listed = [.. LibraryScopes.Listed(media).Where(scope => scope.Slug != "all").Select(scope => scope.Slug)];
            var (from, days) = Range;
            _entries = await read.ExecuteAsync<BaseDataList<CalendarEntryV1>>(new ReadCalendarParametersV1(from, days), HttpContext.RequestAborted);
        }
    }
}
