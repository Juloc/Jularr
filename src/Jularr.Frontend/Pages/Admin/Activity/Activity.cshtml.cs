using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Service.Admin.Activity.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Activity
{
    public sealed class ActivityModel(ReadActivityV1 read, RetryActivityV1 retry, DismissActivityV1 dismiss, SaveActivitySourceLinkV1 sourceLink, IAntiforgery antiforgery) : JularrPage
    {
        private PageResult<ActivityItemV1>? _items;

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public ActivityTabType Tab { get; set; } = ActivityTabType.Activity;

        protected override void Define(Page page)
        {
            page.Title("admin.activity.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Activity/Activity.entry.scss");
            page.Content.Add(new NavigationTabs(Enum.GetValues<ActivityTabType>()
                .Select(tab => new NavigationTab("activity.tab." + tab, Address(tab: tab), tab == Tab)).ToArray(), "admin.activity.title"));

            if (_items is { } items)
            {
                var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
                var table = new Table<ActivityItemV1>(items.Items)
                    .Resource(item => item.OperationTypeId, "table.type", type => "operation.type." + type)
                    .Content("activity.item", item => item.WorkId is { } workId
                        ? new Div(new A(new ValueText(item.WorkTitle ?? "")).Attribute("href", "/Admin/Works/" + workId.ToString(CultureInfo.InvariantCulture)), new Span(new ValueText(item.Title))).Class("activity-item")
                        : new ValueText(item.Title))
                    .Resource(item => item.OperationStatusTypeId, "table.status", status => "operation.status." + status);
                table = Tab == ActivityTabType.Activity
                    ? table.Content("activity.client", item => item.DownloadStateTypeId is { } state
                            ? new Span(new Text("activity.state." + state), new ValueText(item.ConnectionName is { } name ? " · " + name : ""))
                            : new ValueText(item.ConnectionName ?? ""))
                        .Content("activity.progress", item => new ValueText(Progress(item)))
                        .Content("activity.eta", item => new ValueText(Eta(item.EtaSeconds)))
                    : table.Content("activity.reason", item => item.ReasonKey is { } key ? new Text(Reason(item.OperationTypeId, key)) : new Span())
                        .Content("activity.size", item => new ValueText(Size(item.SizeBytes)));
                table = table.Text(item => item.CreatedAt, "operation.created");
                if (Tab == ActivityTabType.ToDo)
                {
                    // Failures can be retried or put aside; waiting imports clear themselves.
                    table = table.Content("common.actions", item => item.OperationStatusTypeId == OperationStatusType.Failed
                        ? new Div(Action(item, "Retry", "activity.retry", csrf), Action(item, "Dismiss", "activity.dismiss", csrf))
                        : item.ManualUrl is { } manual ? ManualLink(item, manual, csrf) : new Span());
                }

                page.Content.Add(table);
                if (items.Items.Count == 0) page.Content.Add(new P(new Text("activity.empty." + Tab)));
                if (items.Page > 1) page.Content.Add(new A(new Text("common.previous")).Attribute("href", Address(items.Page - 1)));
                if (items.Items.Count == items.PageSize) page.Content.Add(new A(new Text("common.next")).Attribute("href", Address(items.Page + 1)));
            }

            if (Error is not null) page.Content.Add(new P(new Text(Error.ResourceKey)).Attribute("role", "alert"));
        }

        private Form<ActivityOperationParametersV1> Action(ActivityItemV1 item, string handler, string labelKey, string? csrf) =>
            new(form => form.Submit(labelKey), Address(handler: handler) + "&operationId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf, new ActivityOperationParametersV1(item.Id));

        // The person passes the source's check in their own browser; the server takes the file link from there.
        private IUiComponent ManualLink(ActivityItemV1 item, string manual, string? csrf) => new SourceConfirmation(
            new A(new Text("activity.openSource")).Class("button").Attribute("href", manual).Attribute("target", "_blank").Attribute("rel", "noopener noreferrer"),
            new Form<SaveActivitySourceLinkDataV1>(form =>
            {
                form.Text(data => data.SourceUrl, "activity.fileLink");
                form.Submit("activity.continueDownload");
            }, Address(handler: "SourceLink") + "&operationId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf, new SaveActivitySourceLinkDataV1(""),
                Error?.Field == nameof(SaveActivitySourceLinkDataV1.SourceUrl) ? Error : null));

        // Scans report service errors; downloads report acquisition outcomes.
        private static string Reason(OperationType type, string key) => type == OperationType.LibraryScan ? "errors." + key : "acquisition.outcome." + key;

        // "42 % · 3.1 of 7.4 GB"; sizes only where the client reports them.
        private static string Progress(ActivityItemV1 item)
        {
            var percent = item.ProgressPercent is { } value ? value.ToString(CultureInfo.InvariantCulture) + " %" : "";
            if (item.SizeBytes is not { } size)
            {
                return percent;
            }

            var done = item.RemainingBytes is { } remaining ? Math.Max(0, size - remaining) : (long?)null;
            var amount = done is { } bytes ? Size(bytes) + " / " + Size(size) : Size(size);
            return percent.Length == 0 ? amount : percent + " · " + amount;
        }

        private static string Size(long? bytes) => bytes switch
        {
            null => "",
            >= 1L << 30 => (bytes.Value / (double)(1L << 30)).ToString("0.0 'GB'", CultureInfo.InvariantCulture),
            _ => (bytes.Value / (double)(1L << 20)).ToString("0 'MB'", CultureInfo.InvariantCulture)
        };

        private static string Eta(int? seconds) => seconds switch
        {
            null => "",
            >= 3600 => (seconds.Value / 3600).ToString(CultureInfo.InvariantCulture) + " h " + (seconds.Value % 3600 / 60).ToString(CultureInfo.InvariantCulture) + " min",
            >= 60 => (seconds.Value / 60).ToString(CultureInfo.InvariantCulture) + " min",
            _ => "< 1 min"
        };

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

        public async Task<IActionResult> OnPostSourceLinkAsync([BindRequired] long operationId, string? sourceUrl)
        {
            await sourceLink.ExecuteAsync<UpdateResult>(new ActivityOperationParametersV1(operationId), new SaveActivitySourceLinkDataV1(sourceUrl ?? ""), HttpContext.RequestAborted);
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
