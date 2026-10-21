using System.Globalization;
using Jularr.Backend.Data.User.Calendar.V1;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.User.Calendar
{
    // The month, week and day boards; each releases links to its title's Details.
    public sealed partial class CalendarModel
    {
        // In the library wins over requested, which wins over watchlisted.
        private static CalendarStatus? Status(CalendarEntryV1 entry) =>
            entry.IsAvailable ? CalendarStatus.Available : entry.IsRequested ? CalendarStatus.Requested : entry.IsWatchlisted ? CalendarStatus.Watchlisted : null;

        // A whole Work, such as a movie, has no unit line.
        private string Unit(CalendarEntryV1 entry) =>
            string.Join(" · ", new[] { MediaUnitLabel.Format(texts, entry.WorkUnitTypeId, entry.UnitNumber, entry.ParentUnitNumber), entry.UnitTitle }.OfType<string>());

        private static string TypeClass(CalendarEntryV1 entry) => "calendar__type--" + LibraryScopes.Slug(entry.MediaTypeId, entry.MediaSubtypeId);

        private static string DetailsAddress(CalendarEntryV1 entry) => "/Details/" + entry.WorkId.ToString(CultureInfo.InvariantCulture);

        private string Count(int count) => count == 1 ? texts["calendar.count.one"] : string.Format(CultureInfo.CurrentCulture, texts["calendar.count"], count);

        private string LongDate(DateOnly day) => day.ToString("D", CultureInfo.CurrentCulture);

        private static IUiComponent Poster(CalendarEntryV1 entry, int width, int height) =>
            new Span(entry.PosterUrl is { } url ? new MediaImage(url, "", width, height) : new Span()).Class("calendar__poster");

        private IUiComponent StatusText(CalendarEntryV1 entry) =>
            Status(entry) is { } status ? new Span(new Text("calendar.status." + status)).Class("calendar__status") : new Span();

        // Seven columns of whole weeks; a day lists its first releases, more open the day below the grid.
        private IUiComponent MonthBoard(Query query, IReadOnlyList<CalendarEntryV1> visible)
        {
            var byDay = visible.ToLookup(entry => entry.Day);
            var focus = ParseDay(Day) ?? Anchor;
            var weekdays = Enumerable.Range(0, 7).Select(offset => (IUiComponent)new Span(new ValueText(GridStart.AddDays(offset).ToString("ddd", CultureInfo.CurrentCulture))).Class("calendar__weekday"));
            var days = Enumerable.Range(0, GridDays).Select(offset => MonthCell(query, GridStart.AddDays(offset), byDay[GridStart.AddDays(offset)].ToArray(), focus));
            var grid = new Div(new Div([.. weekdays]).Class("calendar__weekdays").Attribute("aria-hidden", "true"), new Div([.. days]).Class("calendar__grid")).Class("calendar__board");
            return new Div(grid, DayPanel(query, focus, byDay[focus].ToArray(), true, ParseDay(Day) is not null)).Class("calendar__month");
        }

        private IUiComponent MonthCell(Query query, DateOnly day, CalendarEntryV1[] releases, DateOnly focus)
        {
            var pick = Address(query with { Day = day });
            var classes = "calendar__cell" + (day.Month == FirstOfMonth.Month ? "" : " calendar__cell--outside") + (day == Today ? " calendar__cell--today" : "") + (day == focus ? " calendar__cell--picked" : "");
            var number = new A(new ValueText(day.Day.ToString(CultureInfo.CurrentCulture))).Class("calendar__date").Attribute("href", pick)
                .Attribute("aria-label", string.Format(CultureInfo.CurrentCulture, texts["calendar.dayLabel"], LongDate(day), releases.Length));
            var content = new List<IUiComponent>
            {
                new Div(number, new Span(new ValueText(releases.Length == 0 ? "" : releases.Length.ToString(CultureInfo.CurrentCulture))).Class("calendar__count")).Class("calendar__date-top")
            };

            if (releases.Length > 0)
            {
                content.Add(new Ul([.. releases.Take(query.Shown).Select(entry => new Li(new A(Poster(entry, 22, 32), new Span(new Span(new ValueText(entry.Title)).Class("calendar__name"), new Span(new ValueText(Unit(entry))).Class("calendar__unit")).Class("calendar__text"))
                    .Class("calendar__event " + TypeClass(entry)).Attribute("href", DetailsAddress(entry)).Attribute("aria-label", (entry.Title + " " + Unit(entry)).Trim())).Class("calendar__item"))]).Class("calendar__events"));
                if (releases.Length > query.Shown)
                {
                    content.Add(new A(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["calendar.more"], releases.Length - query.Shown))).Class("calendar__more").Attribute("href", pick));
                }

                // Phones show coloured strips like a phone calendar; the chosen day lists them below.
                var strips = releases.Take(3).Select(entry => (IUiComponent)new A(new Span(new ValueText(entry.Title))).Class("calendar__strip " + TypeClass(entry)).Attribute("href", pick).Attribute("aria-label", entry.Title)).ToList();
                if (releases.Length > 3) strips.Add(new A(new ValueText("+" + (releases.Length - 3).ToString(CultureInfo.CurrentCulture))).Class("calendar__strips-more").Attribute("href", pick).Attribute("aria-label", Count(releases.Length)));
                content.Add(new Div([.. strips]).Class("calendar__strips"));
            }

            var cell = new Section([.. content]).Class(classes);
            return day == Today ? cell.Attribute("aria-current", "date") : cell;
        }

        // The day's releases, under the month grid or in the narrow week.
        private IUiComponent DayPanel(Query query, DateOnly day, CalendarEntryV1[] releases, bool head, bool chosen)
        {
            var children = new List<IUiComponent>();
            if (head)
            {
                children.Add(new Div(new Span(new ValueText(LongDate(day))), new Strong(new ValueText(Count(releases.Length)))).Class("calendar__panel-head"));
            }

            children.Add(releases.Length == 0 ? new P(new Text("calendar.dayEmpty")).Class("calendar__empty")
                : new Ul([.. releases.Select(entry => new Li(Row(entry)))]).Class("calendar__rows"));
            return new Section([.. children]).Class("calendar__panel" + (chosen ? " calendar__panel--chosen" : "")).Attribute("aria-label", LongDate(day));
        }

        private IUiComponent Row(CalendarEntryV1 entry) =>
            new A(Poster(entry, 44, 62), new Span(new Strong(new ValueText(entry.Title)), new Span(new ValueText(Unit(entry))).Class("calendar__unit"), StatusText(entry)).Class("calendar__row-text"),
                new Icon("chevron-right", assets, 16)).Class("calendar__row " + TypeClass(entry)).Attribute("href", DetailsAddress(entry));

        // A column per day; narrow containers show the week strip and the chosen day instead.
        private IUiComponent WeekBoard(Query query, IReadOnlyList<CalendarEntryV1> visible)
        {
            var byDay = visible.ToLookup(entry => entry.Day);
            var columns = Enumerable.Range(0, 7).Select(offset =>
            {
                var day = WeekStart(Anchor).AddDays(offset);
                var cards = byDay[day].Select(entry => (IUiComponent)new A(new Span(Poster(entry, 36, 50), new Span(new Strong(new ValueText(entry.Title)), new Span(new ValueText(Unit(entry))).Class("calendar__unit")).Class("calendar__card-text")).Class("calendar__card-art"), StatusText(entry))
                    .Class("calendar__card " + TypeClass(entry)).Attribute("href", DetailsAddress(entry)));
                var column = new Section(new Span(new ValueText(day.ToString("ddd", CultureInfo.CurrentCulture))).Class("calendar__week-name"),
                    new A(new ValueText(day.Day.ToString(CultureInfo.CurrentCulture))).Class("calendar__big-date").Attribute("href", Address(query with { View = CalendarView.Day, Date = day, Day = null }))
                        .Attribute("aria-label", LongDate(day)), new Div([.. cards]).Class("calendar__cards")).Class("calendar__week-day" + (day == Today ? " calendar__week-day--today" : ""));
                return day == Today ? column.Attribute("aria-current", "date") : column;
            });
            var narrow = new Div(DateStrip(query, byDay), DayPanel(query, Anchor, byDay[Anchor].ToArray(), true, true)).Class("calendar__board calendar__week-narrow");
            return new Div(new Div([.. columns]).Class("calendar__week"), narrow).Class("calendar__weekview");
        }

        private IUiComponent DayBoard(Query query, IReadOnlyList<CalendarEntryV1> visible)
        {
            var byDay = visible.ToLookup(entry => entry.Day);
            return new Div(DateStrip(query, byDay), DayPanel(query, Anchor, byDay[Anchor].ToArray(), false, true)).Class("calendar__board calendar__dayview");
        }

        // The seven days of the anchor's week; a dot marks days with releases.
        private IUiComponent DateStrip(Query query, ILookup<DateOnly, CalendarEntryV1> byDay)
        {
            var days = Enumerable.Range(0, 7).Select(offset =>
            {
                var day = WeekStart(Anchor).AddDays(offset);
                var link = new A(new Span(new ValueText(day.ToString("ddd", CultureInfo.CurrentCulture))), new Strong(new ValueText(day.Day.ToString(CultureInfo.CurrentCulture))),
                    new Span().Class("calendar__dot" + (byDay[day].Any() ? " calendar__dot--filled" : ""))).Class("calendar__strip-date" + (day == Anchor ? " calendar__strip-date--selected" : "") + (day == Today ? " calendar__strip-date--today" : ""))
                    .Attribute("href", Address(query with { Date = day, Day = null })).Attribute("aria-label", LongDate(day));
                return (IUiComponent)(day == Anchor ? link.Attribute("aria-current", "true") : link);
            });
            return new Nav([.. days]).Class("calendar__strip-dates").Attribute("aria-label", texts["calendar.weekDays"]);
        }
    }
}
