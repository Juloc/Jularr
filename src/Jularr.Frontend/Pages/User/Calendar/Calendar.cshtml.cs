using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.User.Calendar.V1;
using Jularr.Backend.Service.User.Calendar.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.Calendar
{
    // Two weeks of upcoming releases, day by day.
    public sealed class CalendarModel(ReadCalendarV1 read, UiText texts) : JularrPage
    {
        private const int Days = 14;
        private IReadOnlyList<CalendarEntryV1> _entries = [];

        // First day shown, yyyy-MM-dd; today when absent.
        [BindProperty(SupportsGet = true)]
        public string? From { get; set; }

        private DateOnly Start => DateOnly.TryParseExact(From, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : DateOnly.FromDateTime(DateTime.UtcNow);

        protected override void Define(Page page)
        {
            page.Title("calendar.title");
            page.Navigation("calendar.title");
            page.Asset("Pages/User/Calendar/Calendar.entry.scss");
            page.Content.Add(new Nav(
                new A(new Text("calendar.earlier")).Class("button").Attribute("href", Address(Start.AddDays(-Days))),
                new A(new Text("calendar.today")).Class("button").Attribute("href", "/Calendar"),
                new A(new Text("calendar.later")).Class("button").Attribute("href", Address(Start.AddDays(Days)))).Class("calendar__navigation").Attribute("aria-label", texts["calendar.pages"]));

            if (_entries.Count == 0)
            {
                page.Content.Add(new P(new Text("calendar.empty")).Attribute("role", "status"));
                return;
            }

            foreach (var day in _entries.GroupBy(entry => entry.Day))
            {
                page.Content.Add(new Section(
                    new H2(new ValueText(day.Key.ToString("D", CultureInfo.CurrentCulture))),
                    new Ul([.. day.Select(entry => new Li([
                        new A(new ValueText(entry.Title)).Attribute("href", "/Details/" + entry.WorkId.ToString(CultureInfo.InvariantCulture)),
                        .. Unit(entry) is { Length: > 0 } unit ? new IUiComponent[] { new Span(new ValueText(unit)).Class("calendar__unit") } : [],
                        .. entry.IsAvailable ? new IUiComponent[] { new Span(new Text("library.present")).Class("calendar__state") } : []]).Class("calendar__entry"))])).Class("calendar__day"));
            }
        }

        private static string Address(DateOnly day) => "/Calendar?from=" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // A whole Work, such as a movie, has no unit line.
        private string Unit(CalendarEntryV1 entry) =>
            string.Join(" · ", new[] { MediaUnitLabel.Format(texts, entry.WorkUnitTypeId, entry.UnitNumber, entry.ParentUnitNumber), entry.UnitTitle }.OfType<string>());

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            _entries = await read.ExecuteAsync<BaseDataList<CalendarEntryV1>>(new ReadCalendarParametersV1(Start, Days), HttpContext.RequestAborted);
        }
    }
}
