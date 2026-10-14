using System.Globalization;
using Jularr.Backend.Data.Admin.System.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Admin.System.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.System
{
    // Tools, queues, storage, backups and the library's size at a glance; links lead to the pages that act on them.
    public sealed class SystemModel(ReadSystemHealthV1 read, ReadRecentLogsV1 readLogs, ReadResourceSamplesV1 readResources, ReadServiceTimingsV1 readTimings, UiText texts)
        : JularrPage
    {
        private IReadOnlyList<RecentLogV1> _logs = [];
        private IReadOnlyList<ResourceSampleV1> _resources = [];
        private IReadOnlyList<ServiceTimingV1> _timings = [];
        private SystemHealthV1? _health;

        protected override void Define(Page page)
        {
            page.Title("system.title");
            page.Navigation("nav.admin");
            if (_health is not { } health) return;

            page.Content.Add(new FactList([
                ("system.database", new ValueText(ByteSize.Format(health.DatabaseBytes))),
                ("system.profiles", new ValueText(health.Profiles.ToString(CultureInfo.CurrentCulture))),
                ("system.queued", Link(health.QueuedOperations.ToString(CultureInfo.CurrentCulture), "/Admin/Activity")),
                ("system.failed", Link(health.FailedOperations.ToString(CultureInfo.CurrentCulture), "/Admin/Activity")),
                ("system.storageOffline", Link(health.StorageOffline.ToString(CultureInfo.CurrentCulture), "/Admin/Storage")),
                ("system.lastBackup", Link(health.LastBackupAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? texts["system.never"], "/Admin/Backup"))]));
            page.Content.Add(new UiSection("system.tools", [new Table<ToolStateV1>(health.Tools)
                .Content("system.tool", item => new Text("system.tool." + item.Key))
                .Content("table.status", item => new Text(item.IsAvailable ? "system.configured" : "system.missing"))]));
            page.Content.Add(new UiSection("nav.library", [new Table<MediaTypeCountV1>(health.Library)
                .Content("table.type", item => new Text("media.type." + item.MediaTypeId))
                .Content("table.titles", item => new ValueText(item.Count.ToString(CultureInfo.CurrentCulture)))
                .Content("table.size", item => new ValueText(ByteSize.Format(item.Bytes)))]));

            // Load of this process over the last hours and what took the time during the last hour.
            var performance = new List<IUiComponent>();
            if (_resources.Count > 0)
            {
                var latest = _resources[^1];
                performance.Add(new FactList([
                    ("system.performance.processor", new ValueText(latest.ProcessorPercent.ToString("0.#", CultureInfo.CurrentCulture) + " %")),
                    ("system.performance.memory", new ValueText(ByteSize.Format(latest.MemoryBytes)))]));
                performance.Add(new Sparkline([.. _resources.Select(sample => (double)sample.ProcessorPercent)], "system.performance.processor"));
                performance.Add(new Sparkline([.. _resources.Select(sample => (double)sample.MemoryBytes)], "system.performance.memory"));
            }

            performance.Add(_timings.Count > 0
                ? new Table<ServiceTimingV1>(_timings)
                    .Text(item => item.Service, "system.performance.service")
                    .Content("system.performance.calls", item => new ValueText(item.Count.ToString(CultureInfo.CurrentCulture)))
                    .Content("system.performance.errors", item => new ValueText(item.Errors.ToString(CultureInfo.CurrentCulture)))
                    .Content("system.performance.average", item => new ValueText(item.AverageMilliseconds.ToString(CultureInfo.CurrentCulture) + " ms"))
                    .Content("system.performance.maximum", item => new ValueText(item.MaximumMilliseconds.ToString(CultureInfo.CurrentCulture) + " ms"))
                : new P(new Text("system.performance.empty")));
            page.Content.Add(new UiSection("system.performance", [.. performance]));

            // What went wrong since the start, newest first.
            page.Content.Add(new UiSection("system.logs", _logs.Count > 0
                ? [new Table<RecentLogV1>(_logs)
                    .Content("system.logs.at", item => new ValueText(item.At.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)))
                    .Content("system.logs.level", item => new Text("system.logs.level." + item.Level))
                    .Text(item => item.Category, "system.logs.area")
                    .Text(item => item.Message, "system.logs.message")]
                : [new P(new Text("system.logs.empty"))]));
        }

        private static IUiComponent Link(string value, string href) => new A(new ValueText(value)).Attribute("href", href);

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            _health = await read.ExecuteAsync<SystemHealthV1>(new ReadSystemHealthParametersV1(), HttpContext.RequestAborted);
            _logs = await readLogs.ExecuteAsync<BaseDataList<RecentLogV1>>(new ReadRecentLogsParametersV1(), HttpContext.RequestAborted);
            _resources = await readResources.ExecuteAsync<BaseDataList<ResourceSampleV1>>(new ReadPerformanceParametersV1(), HttpContext.RequestAborted);
            _timings = await readTimings.ExecuteAsync<BaseDataList<ServiceTimingV1>>(new ReadPerformanceParametersV1(), HttpContext.RequestAborted);
        }
    }
}
