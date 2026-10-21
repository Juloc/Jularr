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
    public sealed class ActivityModel(ReadActivityV1 read, RetryActivityV1 retry, DismissActivityV1 dismiss, SaveActivitySourceLinkV1 sourceLink, RemoveActivityDownloadV1 remove, PauseActivityDownloadV1 pause,
        RetryActivityImportV1 retryImport, PauseActivityQueueV1 pauseQueue, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private PageResult<ActivityItemV1>? _items;

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public ActivityTabType Tab { get; set; } = ActivityTabType.Queue;

        protected override void Define(Page page)
        {
            page.Title("admin.activity.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Activity/Activity.entry.scss");
            if (Tab == ActivityTabType.Queue) page.Asset("Framework/Components/LiveProgress.entry.ts");
            page.Content.Add(new NavigationTabs(Enum.GetValues<ActivityTabType>()
                .Select(tab => new NavigationTab("activity.tab." + tab, Address(tab: tab), tab == Tab)).ToArray(), "admin.activity.title"));

            if (_items is { } items)
            {
                var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
                var table = new Table<ActivityItemV1>(items.Items)
                    .Resource(item => item.OperationTypeId, "table.type", type => "operation.type." + type)
                    .Content("activity.item", item => item.OperationTypeId == OperationType.Job ? new Text("jobs.job." + item.Title)
                        : item.WorkId is { } workId
                        ? new Div(new A(new ValueText(item.WorkTitle ?? "")).Attribute("href", "/Admin/Works/" + workId.ToString(CultureInfo.InvariantCulture)), new Span(new ValueText(item.Title))).Class("activity-item")
                        : new ValueText(item.Title))
                    .Content("table.status", Status);
                table = Tab == ActivityTabType.Queue
                    ? table.Content("activity.details", Details)
                    : table.Content("activity.reason", item => item.ReasonKey is { } key ? new ValueText(LiveEntities.Reason(texts, item.OperationTypeId, key)) : new Span())
                        .Content("activity.size", item => new ValueText(Size(item.SizeBytes)));
                table = table.Text(item => item.CreatedAt, "operation.created");
                if (Tab == ActivityTabType.Queue)
                {
                    // What needs a person comes first; a failure is retried or put aside, a download is told to wait, read again or taken out.
                    table = table.Content("common.actions", item => Actions(item, csrf));
                }

                if (Tab == ActivityTabType.Queue && items.Items.Count > 0)
                {
                    page.Content.Add(new Div(PostForm("PauseAll", "activity.pauseAll", csrf), PostForm("ResumeAll", "activity.resumeAll", csrf)).Class("activity-actions activity-queue-actions"));
                }

                page.Content.Add(table);
                if (items.Items.Count == 0) page.Content.Add(new P(new Text("activity.empty." + Tab)));
                if (items.Page > 1) page.Content.Add(new A(new Text("common.previous")).Attribute("href", Address(items.Page - 1)));
                if (items.Items.Count == items.PageSize) page.Content.Add(new A(new Text("common.next")).Attribute("href", Address(items.Page + 1)));
            }

            if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
        }

        // A waiting or failed entry says why; a running one how far it is. The stream keeps both current.
        private IUiComponent Details(ActivityItemV1 item)
        {
            var says = item.ReasonKey is not null && (item.IsWaiting || item.OperationStatusTypeId == OperationStatusType.Failed);
            var facts = says ? "" : LiveFacts.Join(LiveFacts.Progress(item.ProgressPercent, item.SizeBytes, item.RemainingBytes), LiveFacts.Eta(item.EtaSeconds));
            return new Div(
                LivePart("state", says ? "" : LiveEntities.OperationState(texts, item.DownloadStateTypeId), "live-progress__phase"),
                new Span(new ValueText(item.ConnectionName ?? "")).Class("live-progress__facts"),
                LiveBar(item.ProgressPercent),
                LivePart("facts", facts, "live-progress__facts"),
                LivePart("reason", says ? LiveEntities.Reason(texts, item.OperationTypeId, item.ReasonKey) : "", "live-progress__facts"))
                .Class("live-progress").Attribute("data-live", LiveKeys.Operation(item.Id));
        }

        // The status column follows the same entity.
        private IUiComponent Status(ActivityItemV1 item)
        {
            var text = new Span(new Text(item.IsWaiting ? "activity.waiting" : "operation.status." + item.OperationStatusTypeId));
            return Tab == ActivityTabType.Queue ? text.Attribute("data-live", LiveKeys.Operation(item.Id)).Attribute("data-live-text", "status") : text;
        }

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

        private IUiComponent Actions(ActivityItemV1 item, string? csrf)
        {
            // A background job runs again by itself; the Jobs page shows it.
            if (item.OperationTypeId == OperationType.Job)
            {
                return new Div(new A(new Text("jobs.open")).Class("button").Attribute("href", "/Admin/Jobs"), Action(item, "Dismiss", "activity.dismiss", csrf)).Class("activity-actions");
            }

            if (item.OperationStatusTypeId == OperationStatusType.Failed)
            {
                return new Div(Action(item, "Retry", "activity.retry", csrf), Action(item, "Dismiss", "activity.dismiss", csrf)).Class("activity-actions");
            }

            if (item.OperationTypeId != OperationType.Download)
            {
                return new Span();
            }

            var actions = new List<IUiComponent>();
            if (item.ManualUrl is { } manual) actions.Add(ManualLink(item, manual, csrf));
            if (item.CanImportManually) actions.Add(new A(new Text("activity.importManually")).Class("button").Attribute("href", "/Admin/ManualImport?operationId=" + item.Id.ToString(CultureInfo.InvariantCulture)));
            if (item.CanRetryImport) actions.Add(Action(item, "RetryImport", "activity.retryImport", csrf));
            if (item.CanPause) actions.Add(item.DownloadStateTypeId == DownloadStateType.Paused ? Action(item, "Resume", "activity.resume", csrf) : Action(item, "Pause", "activity.pause", csrf));
            actions.Add(Confirmed(item, "Remove", "activity.remove", "activity.removeConfirm", csrf));
            actions.Add(Confirmed(item, "RemoveAndBlock", "activity.removeAndBlock", "activity.removeAndBlockConfirm", csrf));
            return new Div([.. actions]).Class("activity-actions");
        }

        private Form<ActivityOperationParametersV1> Action(ActivityItemV1 item, string handler, string labelKey, string? csrf) =>
            new(form => form.Submit(labelKey), Address(handler: handler) + "&operationId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf, new ActivityOperationParametersV1(item.Id));

        // Taking a job out deletes its partial files, so the button first asks: it opens the one that does it.
        private IUiComponent Confirmed(ActivityItemV1 item, string handler, string labelKey, string confirmKey, string? csrf) =>
            new Details(new Summary(new Text(labelKey)), Action(item, handler, confirmKey, csrf)).Class("activity-confirm");

        private IUiComponent PostForm(string handler, string labelKey, string? csrf) =>
            new Framework.PostForm(Address(handler: handler), csrf, [], new Button(new Text(labelKey)).Attribute("type", "submit"));

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
            _items = await read.ExecuteAsync<PageResult<ActivityItemV1>>(new ReadActivityParametersV1(Tab, PageNumber), HttpContext.RequestAborted);
        }

        private string Address(int? pageNumber = null, ActivityTabType? tab = null, string? handler = null) =>
            "/Admin/Activity?Tab=" + (tab ?? Tab) + "&PageNumber=" + (pageNumber ?? (tab is null ? PageNumber : 1)).ToString(CultureInfo.InvariantCulture)
            + (handler is null ? "" : "&handler=" + handler);
    }
}
