using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Admin.System.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Service.Admin.Activity.V1;
using Jularr.Backend.Service.Admin.System.V1;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Modules.Live;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Dashboard
{
    // The Administration's first page: four figures, then what the system and the downloads are doing, which services answer and where the media are kept.
    public sealed class DashboardModel(ReadDashboardV1 readDashboard, ReadHealthV1 readHealth, CheckHealthNowV1 checkHealth, ReadActivitySummaryV1 readSummary, ReadActivityV1 readActivity,
        ReadResourceSamplesV1 readResources, IAntiforgery antiforgery, UiText texts, FrontendAssets assets) : JularrPage
    {
        private const string Address = "/Admin/Dashboard";
        private DashboardV1? _dashboard;
        private HealthV1? _health;
        private ActivitySummaryV1? _summary;
        private PageResult<ActivityItemV1>? _activity;
        private ResourceHistoryV1? _resources;

        private bool ShowsDownloads => texts.Can("media.read", "Acquisition");

        protected override void Define(Page page)
        {
            page.Title("admin.dashboard.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Dashboard/Dashboard.entry.scss");
            page.Asset("Framework/Components/LiveProgress.entry.ts");
            page.Asset("Framework/Components/Truncated.entry.ts");
            if (_dashboard is not { } dashboard || _health is not { } health) return;

            page.Content.Add(new KpiTiles([.. Tiles(dashboard, health)]));
            var left = new List<IUiComponent>();
            if (_resources is { Samples.Count: > 0 } resources)
            {
                left.Add(new UiSection("dashboard.performance", [new ResourceChart(resources.Samples, resources.AvailableMemoryBytes)], action: new Span(new Text("dashboard.performance.window"))));
            }

            if (_activity is { } activity)
            {
                left.Add(new UiSection("dashboard.activity", [new Div(activity.Items.Count > 0 ? activity.Items.Select(Download).ToArray() : [new P(new Text("dashboard.activity.empty"))]).Class("dashboard-recent")],
                    action: Link("dashboard.activity.open", "/Admin/Activity")));
            }

            var right = new List<IUiComponent> { Services(health) };
            if (dashboard.Storage.Count > 0)
            {
                right.Add(new UiSection("dashboard.storage", [new Div([.. dashboard.Storage.Select(Storage)]).Class("dashboard-storage")], action: Link("dashboard.storage.open", "/Admin/Storage")));
            }

            page.Content.Add(new Div(new Div([.. left]).Class("dashboard__stack"), new Div([.. right]).Class("dashboard__stack")).Class("dashboard"));
            if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
        }

        // System status from the health check, the media, the running downloads (live) and the requests that wait.
        private IEnumerable<KpiTile> Tiles(DashboardV1 dashboard, HealthV1 health)
        {
            var failing = health.Services.Count(service => service.HealthStateTypeId == HealthStateType.Unavailable);
            yield return new KpiTile("dashboard.kpi.status", new Text(health.IsStable ? "dashboard.status.stable" : "dashboard.status.warning"), new Icon("status", assets, 18),
                health.IsStable ? UiTone.Success : UiTone.Warning,
                health.IsStable ? new Text(health.CheckedAt is null ? "dashboard.status.unchecked" : "dashboard.status.stable.foot")
                    : new ValueText(string.Format(CultureInfo.CurrentCulture, texts["dashboard.status.warning.foot"], failing)),
                health.IsStable ? UiTone.Success : UiTone.Warning);
            yield return new KpiTile("dashboard.kpi.media", new ValueText(dashboard.Works.ToString("N0", CultureInfo.CurrentCulture)), new Icon("library", assets, 18), UiTone.Accent,
                new ValueText(string.Format(CultureInfo.CurrentCulture, texts["dashboard.media.files"], dashboard.Files.ToString("N0", CultureInfo.CurrentCulture))));
            if (ShowsDownloads && _summary is { } summary)
            {
                yield return new KpiTile("dashboard.kpi.downloads", Live("active", LiveEntities.Count(summary.ActiveCount)), new Icon("download", assets, 18), UiTone.Info,
                    new Span(Live("errors", LiveEntities.Count(summary.ErrorCount)), new ValueText(" "), new Text("dashboard.downloads.failed")), summary.ErrorCount > 0 ? UiTone.Warning : null);
            }

            if (texts.Can("requests.read", "Requests"))
            {
                yield return new KpiTile("dashboard.kpi.requests", new ValueText(dashboard.OpenRequests.ToString("N0", CultureInfo.CurrentCulture)), new Icon("requests", assets, 18), UiTone.Warning,
                    new Text("dashboard.requests.foot"), dashboard.OpenRequests > 0 ? UiTone.Warning : null);
            }
        }

        private static IUiComponent Live(string name, string text) => new Span(new ValueText(text)).Attribute("data-live", LiveKeys.Queue).Attribute("data-live-text", name);

        private static IUiComponent Link(string labelKey, string href) => new A(new Text(labelKey)).Class("button button--soft").Attribute("href", href);

        // The last downloads with where they come from and how far they are; the stream keeps the numbers current.
        private IUiComponent Download(ActivityItemV1 item)
        {
            var failed = item.OperationStatusTypeId == OperationStatusType.Failed;
            var source = LiveFacts.Join(item.ConnectionName, item.IndexerName);
            var says = item.ReasonKey is not null && (item.IsWaiting || failed || item.OperationStatusTypeId is OperationStatusType.Queued or OperationStatusType.Running);
            var bar = new Progress().Attribute("max", "100").TextAttribute("aria-label", "live.progress").Class("live-progress__bar").Attribute("data-live-value", "percent");
            return new Div(
                new Span(new Icon(failed ? "info" : "download", assets, 15)).Class("dashboard-recent__symbol"),
                new Div(new Strong(new Truncated(item.OperationTypeId == OperationType.Job ? texts["jobs.job." + item.Title] : item.WorkTitle ?? item.Title)),new Span(new Truncated(string.IsNullOrEmpty(source) ? texts["operation.type." + item.OperationTypeId] : source)).Class("dashboard-recent__facts"))
                    .Class("dashboard-recent__text"),
                new Div(new Span(new ValueText(LiveEntities.OperationStatus(texts, item.IsWaiting, item.OperationStatusTypeId, says ? null : item.DownloadStateTypeId))).Class("live-progress__phase")
                        .Attribute("data-live-text", "status"),
                    !says && item.ProgressPercent is { } percent ? bar.Attribute("value", percent.ToString(CultureInfo.InvariantCulture)) : bar.Attribute("hidden", "hidden"))
                    .Class("live-progress dashboard-recent__state").Attribute("data-live", LiveKeys.Operation(item.Id))).Class("dashboard-recent__row");
        }

        // Every configured service with what its last check said; Check now asks all of them again.
        private IUiComponent Services(HealthV1 health)
        {
            IUiComponent action = texts.Can("instance.settings.update")
                ? new PostForm(Address + "?handler=Check", antiforgery.GetAndStoreTokens(HttpContext).RequestToken, [], new Button(new Icon("sync", assets, 15), new Text("dashboard.services.check")).Soft().Compact().Submit())
                : new Span();
            return new UiSection("dashboard.services", [health.Services.Count > 0
                ? new Div([.. health.Services.Select(Service)]).Class("dashboard-services")
                : new P(new Text("dashboard.services.empty"))], action: action);
        }

        private IUiComponent Service(HealthServiceV1 service)
        {
            var (tone, chip) = service.HealthStateTypeId switch
            {
                HealthStateType.Available => ("good", UiTone.Success),
                HealthStateType.Unavailable => ("warn", UiTone.Warning),
                _ => ("accent", UiTone.Accent)
            };
            IUiComponent checkedAt = service.CheckedAt is { } at
                ? LocalTime.Format(texts["dashboard.services.checked"], at, "time")
                : new ValueText(texts["dashboard.services.never"]);
            var detail = new List<IUiComponent> { new Strong(new Truncated(service.Name ?? "")), new Span(new ValueText(texts["health.type." + service.HealthServiceTypeId] + " · "), checkedAt).Class("dashboard-services__facts") };
            if (ErrorText(service) is { } error) detail.Add(new Span(new Truncated(error)).Class("dashboard-services__facts dashboard-services__error"));
            return new Div(new Span(new Icon(ServiceIcon(service.HealthServiceTypeId), assets, 15)).Class("dashboard-services__icon dashboard-services__icon--" + tone), new Div([.. detail]).Class("dashboard-services__text"),
                new StateChip(new Text("health.state." + service.HealthStateTypeId), chip)).Class("dashboard-services__row");
        }

        // Why a service did not answer, in the words of the error or the storage problem.
        private string? ErrorText(HealthServiceV1 service)
        {
            if (service.ErrorKey is not { } key) return null;

            var resource = service.HealthServiceTypeId == HealthServiceType.Storage ? "storage.problem." + key : "errors." + key;
            return texts.Has(resource) ? texts[resource] : null;
        }

        private static string ServiceIcon(HealthServiceType type) => type switch
        {
            HealthServiceType.Database => "server",
            HealthServiceType.Storage => "drive",
            HealthServiceType.Provider => "providers",
            HealthServiceType.MediaServer => "play",
            HealthServiceType.DownloadClient => "download",
            _ => "search"
        };

        // One connection's volume: how much it holds, divided into the media Jularr keeps there, other data and what is free.
        private IUiComponent Storage(DashboardStorageV1 mount)
        {
            if (mount.TotalBytes is not { } total || mount.FreeBytes is not { } free)
            {
                return new Div(new Div(new Span(new ValueText(mount.Name)), new Strong(new Text("dashboard.storage.unchecked"))).Class("dashboard-storage__line")).Class("dashboard-storage__mount");
            }

            var jularr = mount.VideoBytes + mount.BookBytes + mount.MusicBytes + mount.GameBytes;
            var other = Math.Max(0, total - free - jularr);
            double Share(long bytes) => total > 0 ? (double)bytes / total : 0;
            return new Div(
                new Div(new Span(new ValueText(mount.Name)), new Strong(new ValueText(ByteSize.Format(Math.Max(0, total - free)) + " / " + ByteSize.Format(total)))).Class("dashboard-storage__line"),
                new ShareBar([new(ShareTone.First, Share(mount.VideoBytes), "dashboard.storage.video"), new(ShareTone.Second, Share(mount.BookBytes), "dashboard.storage.books"),
                    new(ShareTone.Fourth, Share(mount.MusicBytes), "dashboard.storage.music"), new(ShareTone.Third, Share(mount.GameBytes), "dashboard.storage.games"),
                    new(ShareTone.Other, Share(other), "dashboard.storage.other"), new(ShareTone.Free, Share(free))], "dashboard.storage")).Class("dashboard-storage__mount");
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        // Asks every service again and shows the page with the new answers.
        public async Task<IActionResult> OnPostCheckAsync()
        {
            await checkHealth.ExecuteAsync<HealthV1>(new CheckHealthParametersV1(), default, HttpContext.RequestAborted);
            return LocalRedirect(Address);
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            _dashboard = await readDashboard.ExecuteAsync<DashboardV1>(new ReadDashboardParametersV1(), ct);
            _health = await readHealth.ExecuteAsync<HealthV1>(new ReadHealthParametersV1(), ct);
            if (ShowsDownloads)
            {
                _summary = await readSummary.ExecuteAsync<ActivitySummaryV1>(new ReadActivitySummaryParametersV1(), ct);
                _activity = await readActivity.ExecuteAsync<PageResult<ActivityItemV1>>(new ReadActivityParametersV1(ActivityFilterType.All, 1, 4), ct);
            }

            if (texts.CanDiagnose())
            {
                _resources = await readResources.ExecuteAsync<ResourceHistoryV1>(new ReadPerformanceParametersV1(), ct);
            }
        }
    }
}
