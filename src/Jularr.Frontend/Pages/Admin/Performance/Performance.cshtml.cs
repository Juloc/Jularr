using System.Globalization;
using Jularr.Backend.Data.Admin.System.V1;
using Jularr.Backend.Service.Admin.System.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Performance
{
    // How hard the process works: the processor and memory now, what answers take on average and how many fail, the load of the last 24 hours and what each service took.
    public sealed class PerformanceModel(ReadResourceSamplesV1 readResources, ReadServiceTimingsV1 readTimings, UiText texts, FrontendAssets assets) : JularrPage
    {
        private ResourceHistoryV1? _resources;
        private ServiceTimingsV1? _timings;

        protected override void Define(Page page)
        {
            page.Title("admin.performance.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Performance/Performance.entry.scss");
            if (_resources is not { } resources || _timings is not { } timings) return;

            var latest = resources.Samples.Count > 0 ? resources.Samples[^1] : null;
            var window = new Text("performance.window");
            page.Content.Add(new KpiTiles(
                new KpiTile("performance.kpi.processor", new ValueText(latest is null ? "–" : latest.ProcessorPercent.ToString("0.#", CultureInfo.CurrentCulture) + " %"), new Icon("activity", assets, 18),
                    KpiTone.Accent, window),
                new KpiTile("performance.kpi.memory", new ValueText(latest is null ? "–" : ByteSize.Format(latest.MemoryBytes)), new Icon("server", assets, 18), KpiTone.Info, window),
                new KpiTile("performance.kpi.response", new ValueText(timings.Calls == 0 ? "–" : timings.AverageMilliseconds.ToString("N0", CultureInfo.CurrentCulture) + " ms"), new Icon("pulse", assets, 18),
                    KpiTone.Good, window),
                new KpiTile("performance.kpi.errors", new ValueText(timings.Calls == 0 ? "–" : ((double)timings.Errors / timings.Calls).ToString("P1", CultureInfo.CurrentCulture)), new Icon("info", assets, 18),
                    timings.Errors > 0 ? KpiTone.Warn : KpiTone.Good, window)));
            if (resources.Samples.Count > 0)
            {
                page.Content.Add(new UiSection("performance.history", [new ResourceChart(resources.Samples, resources.AvailableMemoryBytes)]));
            }

            page.Content.Add(new UiSection("performance.services", [timings.Services.Count > 0
                ? new Table<ServiceTimingV1>(timings.Services).Dense()
                    .Text(item => item.Service, "performance.service")
                    .Content("performance.calls", item => new ValueText(item.Count.ToString("N0", CultureInfo.CurrentCulture)))
                    .Content("performance.average", item => new ValueText(item.AverageMilliseconds.ToString("N0", CultureInfo.CurrentCulture) + " ms"))
                    .Content("performance.maximum", item => new ValueText(item.MaximumMilliseconds.ToString("N0", CultureInfo.CurrentCulture) + " ms"))
                    .Content("performance.errorCount", item => new ValueText(item.Errors.ToString("N0", CultureInfo.CurrentCulture)))
                : new P(new Text("performance.empty"))]));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            _resources = await readResources.ExecuteAsync<ResourceHistoryV1>(new ReadPerformanceParametersV1(), HttpContext.RequestAborted);
            _timings = await readTimings.ExecuteAsync<ServiceTimingsV1>(new ReadPerformanceParametersV1(), HttpContext.RequestAborted);
        }
    }
}
