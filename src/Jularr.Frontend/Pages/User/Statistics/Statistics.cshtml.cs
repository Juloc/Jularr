using System.Globalization;
using Jularr.Backend.Data.User.Statistics.V1;
using Jularr.Backend.Service.User.Statistics.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.Statistics
{
    // The profile's own history in numbers, for a chosen period.
    public sealed class StatisticsModel(ReadProfileStatisticsV1 read, UiText texts) : JularrPage
    {
        private const int DefaultDays = 30;
        private static readonly int[] Periods = [7, DefaultDays, 365, 0];
        private ProfileStatisticsV1? _statistics;

        [BindProperty(SupportsGet = true)]
        public int Days { get; set; } = DefaultDays;

        protected override void Define(Page page)
        {
            page.Title("statistics.title");
            page.Navigation("statistics.title");
            page.Content.Add(new NavigationTabs([.. Periods.Select(days => new NavigationTab("statistics.period." + days.ToString(CultureInfo.InvariantCulture),
                "/Statistics?days=" + days.ToString(CultureInfo.InvariantCulture), days == Days))], "statistics.periods"));
            if (_statistics is not { } statistics) return;

            page.Content.Add(new FactList([
                ("statistics.watched", new ValueText(Duration(statistics.WatchedMs))),
                ("statistics.sessions", new ValueText(statistics.Sessions.ToString(CultureInfo.CurrentCulture))),
                ("statistics.games", new ValueText(Duration(statistics.GamePlayedMs))),
                .. statistics.Completed.Select(item => ("statistics.completed." + item.MediaTypeId, (IUiComponent)new ValueText(item.Count.ToString(CultureInfo.CurrentCulture))))]));
            if (statistics.TopWorks.Count > 0)
            {
                page.Content.Add(new UiSection("statistics.top", [new Ul([.. statistics.TopWorks.Select(work => new Li(
                    new A(new ValueText(work.Title)).Attribute("href", "/Details/" + work.WorkId.ToString(CultureInfo.InvariantCulture)),
                    new Span(new ValueText(" · " + Duration(work.DurationMs)))))])]));
            }
        }

        private string Duration(long milliseconds)
        {
            var time = TimeSpan.FromMilliseconds(milliseconds);
            return string.Format(CultureInfo.CurrentCulture, texts["statistics.hours"], (long)time.TotalHours, time.Minutes);
        }

        public async Task<IActionResult> OnGetAsync()
        {
            if (!Periods.Contains(Days)) Days = DefaultDays;
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            _statistics = await read.ExecuteAsync<ProfileStatisticsV1>(new ReadStatisticsParametersV1(Days), HttpContext.RequestAborted);
        }
    }
}
