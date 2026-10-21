using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Service.Admin.Activity.V1;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Modules.Live;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Activity
{
    // The queue: four live numbers, one row of filters, and the downloads and jobs with what a person can do with each.
    public sealed class ActivityModel(ReadActivityV1 read, ReadActivitySummaryV1 summary, RetryActivityV1 retry, DismissActivityV1 dismiss, SaveActivitySourceLinkV1 sourceLink, RemoveActivityDownloadV1 remove,
        PauseActivityDownloadV1 pause, RetryActivityImportV1 retryImport, PauseActivityQueueV1 pauseQueue, IAntiforgery antiforgery, UiText texts, FrontendAssets assets) : JularrPage
    {
        private PageResult<ActivityItemV1>? _items;
        private ActivitySummaryV1? _summary;

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public ActivityFilterType Filter { get; set; } = ActivityFilterType.All;

        // The entry whose panel is open.
        [BindProperty(SupportsGet = true, Name = "operation")]
        public long? OpenOperationId { get; set; }

        private bool IsHistory => Filter == ActivityFilterType.History;

        protected override void Define(Page page)
        {
            page.Title("admin.activity.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Activity/Activity.entry.scss");
            page.Asset("Framework/Components/LiveProgress.entry.ts");
            page.Asset("Framework/Components/Truncated.entry.ts");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

            if (_summary is { } numbers) page.Content.Add(new Div(Tiles(numbers)).Class("activity-tiles"));
            page.Content.Add(Bar(csrf));

            if (_items is { } items)
            {
                page.Content.Add(new Div(IsHistory ? HistoryTable(items) : QueueTable(items, csrf)).Class(IsHistory ? "activity-list" : "activity-list activity-list--queue"));
                if (items.Items.Count == 0) page.Content.Add(new P(new Text(IsHistory ? "activity.empty.History" : "activity.empty.Queue")));
                if (items.Page > 1) page.Content.Add(new A(new Text("common.previous")).Attribute("href", Address(items.Page - 1)));
                if (items.Items.Count == items.PageSize) page.Content.Add(new A(new Text("common.next")).Attribute("href", Address(items.Page + 1)));
                if (OpenOperationId is { } open && items.Items.FirstOrDefault(item => item.Id == open) is { } selected) page.Content.Add(Panel(selected, csrf));
            }

            if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
        }

        // What runs, how fast, what is downloaded but not yet imported, what failed: the stream keeps them current.
        private IUiComponent Tiles(ActivitySummaryV1 numbers) => new FactList(
        [
            ("activity.kpi.active", Live("active", LiveEntities.Count(numbers.ActiveCount))),
            ("activity.kpi.speed", Live("speed", LiveFacts.Speed(numbers.BytesPerSecond))),
            ("activity.kpi.import", Live("import", LiveEntities.Count(numbers.ImportCount))),
            ("activity.kpi.errors", Live("errors", LiveEntities.Count(numbers.ErrorCount)))
        ]);

        private static IUiComponent Live(string name, string text) => new Span(new ValueText(text)).Attribute("data-live", LiveKeys.Queue).Attribute("data-live-text", name);

        // The one row: the filters, and the whole queue's pause and resume.
        private IUiComponent Bar(string? csrf)
        {
            var filters = new[] { ActivityFilterType.All, ActivityFilterType.Active, ActivityFilterType.Waiting, ActivityFilterType.Import, ActivityFilterType.Errors, ActivityFilterType.History };
            var tabs = new NavigationTabs([.. filters.Select(filter => new NavigationTab("activity.filter." + filter, Address(filter: filter), filter == Filter))], "admin.activity.title", "activity");
            return IsHistory
                ? new Div(tabs).Class("activity-bar")
                : new Div(tabs, new Div(QueueButton("PauseAll", "pause", "activity.pauseAll", csrf), QueueButton("ResumeAll", "play", "activity.resumeAll", csrf)).Class("activity-bar__tools")).Class("activity-bar");
        }

        private IUiComponent QueueButton(string handler, string icon, string labelKey, string? csrf) =>
            new Framework.PostForm(Address(handler: handler), csrf, [], IconButton(icon, labelKey));

        private IUiComponent QueueTable(PageResult<ActivityItemV1> items, string? csrf) => new Table<ActivityItemV1>(items.Items)
            .Content("activity.item", Job)
            .Content("table.status", Status)
            .Content("activity.source", Source)
            .Content("activity.transfer", Transfer)
            .Content("common.actions", item => RowActions(item, csrf));

        private IUiComponent HistoryTable(PageResult<ActivityItemV1> items) => new Table<ActivityItemV1>(items.Items)
            .Resource(item => item.OperationTypeId, "table.type", type => "operation.type." + type)
            .Content("activity.item", Job)
            .Content("table.status", item => new Text(item.IsWaiting ? "activity.waiting" : "operation.status." + item.OperationStatusTypeId))
            .Content("activity.reason", item => item.ReasonKey is { } key ? new Truncated(LiveEntities.Reason(texts, item.OperationTypeId, key)) : new Span())
            .Content("activity.size", item => new ValueText(Size(item.SizeBytes)))
            .Text(item => item.CreatedAt, "operation.created");

        // The work first, the release below it.
        private IUiComponent Job(ActivityItemV1 item) => item.OperationTypeId == OperationType.Job ? new Div(new Text("jobs.job." + item.Title)).Class("activity-job") : item.WorkId is { } workId
            ? new Div(new A(new Truncated(item.WorkTitle ?? "")).Attribute("href", "/Admin/Works/" + workId.ToString(CultureInfo.InvariantCulture)), new Div(new Truncated(item.Title)).Class("activity-job__release")).Class("activity-job")
            : new Div(new Truncated(item.Title)).Class("activity-job");

        // A waiting or failed entry says why; a running one how far it is. The stream keeps both current.
        private IUiComponent Status(ActivityItemV1 item)
        {
            var says = Says(item);
            return new Div(
                LivePart("status", LiveEntities.OperationStatus(texts, item.IsWaiting, item.OperationStatusTypeId, says ? null : item.DownloadStateTypeId), "live-progress__phase"),
                LiveBar(item.ProgressPercent),
                LivePart("reason", says ? LiveEntities.Reason(texts, item.OperationTypeId, item.ReasonKey) : "", "live-progress__facts"))
                .Class("live-progress activity-status").Attribute("data-live", LiveKeys.Operation(item.Id));
        }

        // The client and the indexer of a download; a scan or backup names its kind.
        private IUiComponent Source(ActivityItemV1 item) => item.OperationTypeId == OperationType.Download
            ? new Div(new Truncated(LiveFacts.Join(item.ConnectionName, item.IndexerName))).Class("activity-source")
            : new Text("operation.type." + item.OperationTypeId);

        // The pace above, the size and time left below.
        private IUiComponent Transfer(ActivityItemV1 item)
        {
            var says = Says(item);
            return new Div(
                LivePart("speed", says ? "" : LiveFacts.Speed(item.BytesPerSecond), "activity-transfer__speed"),
                LivePart("detail", says ? "" : LiveEntities.OperationDetail(item.ProgressPercent, item.SizeBytes, item.RemainingBytes, item.EtaSeconds), "activity-transfer__detail"))
                .Class("activity-transfer").Attribute("data-live", LiveKeys.Operation(item.Id));
        }

        private static bool Says(ActivityItemV1 item) => item.ReasonKey is not null && (item.IsWaiting || item.OperationStatusTypeId == OperationStatusType.Failed);

        private static IUiComponent LivePart(string name, string text, string className)
        {
            var part = new Span(new ValueText(text)).Class(className).Attribute("data-live-text", name);
            return text.Length == 0 ? part.Attribute("hidden", "hidden") : part;
        }

        private static IUiComponent LiveBar(int? percent)
        {
            var bar = new Progress().Attribute("max", "100").TextAttribute("aria-label", "live.progress").Class("live-progress__bar").Attribute("data-live-value", "percent");
            return percent is { } value ? bar.Attribute("value", value.ToString(CultureInfo.InvariantCulture)) : bar.Attribute("hidden", "hidden");
        }

        // The usual one click: read again or go on; everything else is in the panel.
        private IUiComponent RowActions(ActivityItemV1 item, string? csrf)
        {
            var actions = new List<IUiComponent>();
            // A background job runs again by itself; the Jobs page shows it.
            if (item.OperationStatusTypeId == OperationStatusType.Failed && item.OperationTypeId != OperationType.Job)
            {
                actions.Add(IconAction(item, "Retry", "sync", "activity.retry", csrf));
            }
            else if (item.OperationTypeId == OperationType.Download)
            {
                if (item.CanRetryImport) actions.Add(IconAction(item, "RetryImport", "sync", "activity.retryImport", csrf));
                if (item.CanImportManually) actions.Add(IconLink("folder", "activity.importManually", "/Admin/ManualImport?operationId=" + item.Id.ToString(CultureInfo.InvariantCulture)));
                if (item.CanPause) actions.Add(item.DownloadStateTypeId == DownloadStateType.Paused ? IconAction(item, "Resume", "play", "activity.resume", csrf) : IconAction(item, "Pause", "pause", "activity.pause", csrf));
            }

            actions.Add(IconLink("info", "activity.details", Address(operation: item.Id)));
            return new Div([.. actions]).Class("activity-actions");
        }

        private IUiComponent IconLink(string icon, string labelKey, string href) =>
            new A(new Icon(icon, assets, 18)).Class("button button--icon").Attribute("href", href).TextAttribute("aria-label", labelKey).TextAttribute("title", labelKey);

        private IUiComponent IconAction(ActivityItemV1 item, string handler, string icon, string labelKey, string? csrf) =>
            new Framework.PostForm(Address(handler: handler) + "&operationId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf, [], IconButton(icon, labelKey));

        private HtmlElement IconButton(string icon, string labelKey) => new Button(new Icon(icon, assets, 18)).IconOnly(texts[labelKey]).Submit().Attribute("title", texts[labelKey]);

        // One entry from the right: how far it is, what it is, and everything a person can do with it.
        private IUiComponent Panel(ActivityItemV1 item, string? csrf)
        {
            var close = Address();
            var facts = new List<IUiComponent>();
            if (item.WorkId is not null) facts.AddRange([new Dt(new Text("activity.release")), new Dd(new ValueText(item.Title))]);
            if (item.ConnectionName is { } client) facts.AddRange([new Dt(new Text("activity.client")), new Dd(new ValueText(client))]);
            if (item.IndexerName is { } indexer) facts.AddRange([new Dt(new Text("activity.indexer")), new Dd(new ValueText(indexer))]);
            if (item.SizeBytes is { } size) facts.AddRange([new Dt(new Text("activity.size")), new Dd(new ValueText(ByteSize.Format(size)))]);
            facts.AddRange([new Dt(new Text("operation.created")), new Dd(new ValueText(item.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)))]);

            var says = Says(item);
            var body = new List<IUiComponent>
            {
                new Div(
                    LivePart("status", LiveEntities.OperationStatus(texts, item.IsWaiting, item.OperationStatusTypeId, says ? null : item.DownloadStateTypeId), "live-progress__phase"),
                    LiveBar(item.ProgressPercent),
                    LivePart("speed", says ? "" : LiveFacts.Speed(item.BytesPerSecond), "live-progress__facts"),
                    LivePart("detail", says ? "" : LiveEntities.OperationDetail(item.ProgressPercent, item.SizeBytes, item.RemainingBytes, item.EtaSeconds), "live-progress__facts"),
                    LivePart("reason", says ? LiveEntities.Reason(texts, item.OperationTypeId, item.ReasonKey) : "", "live-progress__facts"))
                    .Class("live-progress").Attribute("data-live", LiveKeys.Operation(item.Id)),
                new Dl([.. facts]).Class("activity-drawer__facts"),
                new Div([.. PanelActions(item, csrf)]).Class("activity-drawer__actions")
            };

            return new Div(
                new A(new ValueText("")).Class("activity-drawer__scrim").Attribute("href", close).Attribute("tabindex", "-1").Attribute("aria-hidden", "true"),
                new Aside(
                    new Header(new Div(new H2(item.OperationTypeId == OperationType.Job ? new Text("jobs.job." + item.Title) : new ValueText(item.WorkTitle ?? item.Title)).Attribute("id", "activity-drawer-title"), new Span(new Text("operation.type." + item.OperationTypeId)).Class("activity-drawer__meta")),
                        new A(new Icon("close", assets, 18)).Class("button button--icon").Attribute("href", close).TextAttribute("aria-label", "common.close")).Class("activity-drawer__head"),
                    new Div([.. body]).Class("activity-drawer__body")).Class("activity-drawer").Attribute("role", "dialog").Attribute("aria-modal", "true").Attribute("aria-labelledby", "activity-drawer-title"))
                .Class("activity-drawer-host");
        }

        // A failure is retried or put aside; a waiting download is read again, assigned by hand or taken out; a running one is told to wait or taken out.
        private IEnumerable<IUiComponent> PanelActions(ActivityItemV1 item, string? csrf)
        {
            // A background job runs again by itself; the Jobs page shows it.
            if (item.OperationTypeId == OperationType.Job)
            {
                yield return new A(new Text("jobs.open")).Class("button").Attribute("href", "/Admin/Jobs");
                yield return Action(item, "Dismiss", "activity.dismiss", csrf);
                yield break;
            }

            if (item.OperationStatusTypeId == OperationStatusType.Failed)
            {
                yield return Action(item, "Retry", "activity.retry", csrf);
                yield return Action(item, "Dismiss", "activity.dismiss", csrf);
                yield break;
            }

            if (item.OperationTypeId != OperationType.Download)
            {
                yield break;
            }

            if (item.ManualUrl is { } manual) yield return ManualLink(item, manual, csrf);
            if (item.CanImportManually) yield return new A(new Text("activity.importManually")).Class("button").Attribute("href", "/Admin/ManualImport?operationId=" + item.Id.ToString(CultureInfo.InvariantCulture));
            if (item.CanRetryImport) yield return Action(item, "RetryImport", "activity.retryImport", csrf);
            if (item.CanPause) yield return item.DownloadStateTypeId == DownloadStateType.Paused ? Action(item, "Resume", "activity.resume", csrf) : Action(item, "Pause", "activity.pause", csrf);
            yield return Confirmed(item, "Remove", "activity.remove", "activity.removeConfirm", csrf);
            yield return Confirmed(item, "RemoveAndBlock", "activity.removeAndBlock", "activity.removeAndBlockConfirm", csrf);
        }

        private Form<ActivityOperationParametersV1> Action(ActivityItemV1 item, string handler, string labelKey, string? csrf) =>
            new(form => form.Submit(labelKey), Address(handler: handler) + "&operationId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf, new ActivityOperationParametersV1(item.Id));

        // Taking a job out deletes its partial files, so the button first asks: it opens the one that does it.
        private IUiComponent Confirmed(ActivityItemV1 item, string handler, string labelKey, string confirmKey, string? csrf) =>
            new Details(new Summary(new Text(labelKey)), Action(item, handler, confirmKey, csrf)).Class("activity-confirm");

        // The person passes the source's check in their own browser; the server takes the file link from there.
        private IUiComponent ManualLink(ActivityItemV1 item, string manual, string? csrf) => new Div(
            new A(new Text("activity.openSource")).Class("button").Attribute("href", manual).Attribute("target", "_blank").Attribute("rel", "noopener noreferrer"),
            new Form<SaveActivitySourceLinkDataV1>(form =>
            {
                form.Text(data => data.SourceLinkUrl, "activity.fileLink");
                form.Submit("activity.continueDownload");
            }, Address(handler: "SourceLink") + "&operationId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf, new SaveActivitySourceLinkDataV1(""),
                FormError?.Field == nameof(SaveActivitySourceLinkDataV1.SourceLinkUrl) ? FormError : null));

        private static string Size(long? bytes) => bytes is { } size ? ByteSize.Format(size) : "";

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostRetryAsync([BindRequired] long operationId)
        {
            await retry.ExecuteAsync<UpdateResult>(new ActivityOperationParametersV1(operationId), default, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostRemoveAsync([BindRequired] long operationId) => await RemoveAsync(operationId, false);

        public async Task<IActionResult> OnPostRemoveAndBlockAsync([BindRequired] long operationId) => await RemoveAsync(operationId, true);

        private async Task<IActionResult> RemoveAsync(long operationId, bool blocks)
        {
            await remove.ExecuteAsync<UpdateResult>(new ActivityOperationParametersV1(operationId), new RemoveActivityDownloadDataV1(blocks), HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostPauseAsync([BindRequired] long operationId) => await PauseAsync(operationId, true);

        public async Task<IActionResult> OnPostResumeAsync([BindRequired] long operationId) => await PauseAsync(operationId, false);

        private async Task<IActionResult> PauseAsync(long operationId, bool isPaused)
        {
            await pause.ExecuteAsync<UpdateResult>(new ActivityOperationParametersV1(operationId), new PauseActivityDownloadDataV1(isPaused), HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostPauseAllAsync() => await PauseQueueAsync(true);

        public async Task<IActionResult> OnPostResumeAllAsync() => await PauseQueueAsync(false);

        private async Task<IActionResult> PauseQueueAsync(bool isPaused)
        {
            await pauseQueue.ExecuteAsync<UpdateResult>(new PauseActivityQueueParametersV1(), new PauseActivityDownloadDataV1(isPaused), HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostRetryImportAsync([BindRequired] long operationId)
        {
            await retryImport.ExecuteAsync<UpdateResult>(new ActivityOperationParametersV1(operationId), default, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostSourceLinkAsync([BindRequired] long operationId, string? sourceLinkUrl)
        {
            await sourceLink.ExecuteAsync<UpdateResult>(new ActivityOperationParametersV1(operationId), new SaveActivitySourceLinkDataV1(sourceLinkUrl ?? ""), HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostDismissAsync([BindRequired] long operationId)
        {
            await dismiss.ExecuteAsync<UpdateResult>(new ActivityOperationParametersV1(operationId), default, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        protected override async Task LoadDataAsync()
        {
            _items = await read.ExecuteAsync<PageResult<ActivityItemV1>>(new ReadActivityParametersV1(Filter, Math.Max(1, PageNumber)), HttpContext.RequestAborted);
            _summary = await summary.ExecuteAsync<ActivitySummaryV1>(new ReadActivitySummaryParametersV1(), HttpContext.RequestAborted);
        }

        // The page's own address with what was chosen; defaults stay out of it.
        private string Address(int? pageNumber = null, ActivityFilterType? filter = null, string? handler = null, long? operation = null)
        {
            var query = new List<string>();
            if ((filter ?? Filter) != ActivityFilterType.All) query.Add("Filter=" + (filter ?? Filter));
            var number = pageNumber ?? (filter is null ? Math.Max(1, PageNumber) : 1);
            if (number > 1) query.Add("PageNumber=" + number.ToString(CultureInfo.InvariantCulture));
            if (handler is not null) query.Add("handler=" + handler);
            if (operation is { } open) query.Add("operation=" + open.ToString(CultureInfo.InvariantCulture));
            return "/Admin/Activity" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        }
    }
}
