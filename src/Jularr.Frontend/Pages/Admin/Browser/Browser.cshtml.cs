using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Browser.V1;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Admin.Browser.V1;
using Jularr.Backend.Service.Admin.Wanted.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Browser
{
    // Temporary: the browser of the instance, what it does and what an administrator can make it do.
    public sealed class BrowserModel(ReadBrowserStatusV1 status, RunBrowserActionV1 run, ReadAcquisitionConnectionsV1 connections, ReadAcquisitionConnectionV1 connection,
        TestAcquisitionConnectionV1 test, UpdateAcquisitionConnectionV1 update, ReadWantedV1 wanted, SearchWantedV1 search, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private BrowserStatusV1? _status;
        private PageResult<AcquisitionConnectionDetailsV1>? _connections;
        private PageResult<WantedDetailsV1>? _wanted;

        protected override void Define(Page page)
        {
            page.Title("admin.browser.title");
            page.Navigation("nav.admin");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

            // What the browser is and what it did last.
            page.Content.Add(new H2(new Text("admin.browser.state")));
            page.Content.Add(new Table<BrowserEventV1>([.. _status?.Events.Items ?? []]).Text(item => item.Kind, "admin.browser.kind").Text(item => item.Message, "admin.browser.message").Text(item => item.At, "admin.browser.at"));
            // What the administrator can make it do.
            page.Content.Add(new H2(new Text("admin.browser.actions")));
            page.Content.Add(new Form<RunBrowserActionDataV1>(form =>
            {
                form.Select(item => item.Action, "admin.browser.action", [new SelectOption("start", texts["admin.browser.start"]), new SelectOption("stop", texts["admin.browser.stop"]),
                    new SelectOption("reset", texts["admin.browser.reset"]), new SelectOption("read", texts["admin.browser.read"])]);
                form.Text(item => item.Url, "admin.browser.url");
                form.Submit("admin.browser.run");
            }, "/Admin/Browser?handler=Action", csrf, new RunBrowserActionDataV1("start"), FormError));

            // The sources of the instance: every one of them can be checked and put out of the way.
            page.Content.Add(new H2(new Text("admin.browser.sources")));
            page.Content.Add(new Table<AcquisitionConnectionDetailsV1>([.. _connections?.Items ?? []])
                .Text(item => item.Name, "admin.browser.source")
                .Text(item => item.BaseUrl, "admin.browser.address")
                .Content("admin.browser.state", item => new ValueText(item.ErrorKey ?? "-"))
                .Content("common.actions", item => Actions(item, csrf)));

            // The requests of the instance: a person may search them by hand.
            page.Content.Add(new H2(new Text("admin.browser.wanted")));
            page.Content.Add(new Table<WantedDetailsV1>([.. _wanted?.Items ?? []])
                .Text(item => item.CanonicalTitle, "admin.browser.request")
                .Content("common.actions", item => new Form<BrowserParametersV1>(form => form.Submit("admin.browser.search"),
                    "/Admin/Browser?handler=Search&wantedItemId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf, new BrowserParametersV1())));
        }

        private IUiComponent Actions(AcquisitionConnectionDetailsV1 item, string csrf)
        {
            var buttons = new Div(
                new Form<BrowserParametersV1>(form => form.Submit("admin.browser.test"), "/Admin/Browser?handler=Test&acquisitionConnectionId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf, new BrowserParametersV1()),
                new Form<BrowserParametersV1>(form => form.Submit("admin.browser.off"), "/Admin/Browser?handler=Off&acquisitionConnectionId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf, new BrowserParametersV1()));
            return buttons;
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostActionAsync(string action, string? url)
        {
            var result = await run.ExecuteAsync<BrowserStatusV1>(new BrowserParametersV1(), new RunBrowserActionDataV1(action ?? "start", url), HttpContext.RequestAborted);
            Hint("admin.browser.ran");
            await LoadDataAsync();
            return Page();
        }

        public async Task<IActionResult> OnPostTestAsync(long acquisitionConnectionId)
        {
            var result = await test.ExecuteAsync<AcquisitionConnectionTestV1>(new AcquisitionConnectionParametersV1(acquisitionConnectionId), default, HttpContext.RequestAborted);
            HintText(result.IsAvailable ? texts["admin.browser.sourceOk"] : texts["admin.browser.sourceFailed"] + " " + (result.ErrorKey ?? "-"));
            await LoadDataAsync();
            return Page();
        }

        // The source stops searching by itself; the requests stay with the people.
        public async Task<IActionResult> OnPostOffAsync(long acquisitionConnectionId)
        {
            var details = await connection.ExecuteAsync<AcquisitionConnectionDetailsV1>(new AcquisitionConnectionParametersV1(acquisitionConnectionId), HttpContext.RequestAborted);
            await update.ExecuteAsync<UpdateResult>(new AcquisitionConnectionParametersV1(acquisitionConnectionId), new SaveAcquisitionConnectionDataV1(details.Name, details.AcquisitionConnectionTypeId, details.BaseUrl, null,
                false, details.Priority, details.Categories, details.DownloadCategory, details.RemotePath, details.LocalPath, details.Username, details.MaxConnections, null,
                details.MediaTypeIds, details.SearchesAutomatically, details.QueryLimit, details.GrabLimit, details.Host, details.Port, details.UseSsl, details.DownloadStorageMountId), HttpContext.RequestAborted);
            Hint("admin.browser.sourceOff");
            await LoadDataAsync();
            return Page();
        }

        // A person searches the request by hand.
        public async Task<IActionResult> OnPostSearchAsync(long wantedItemId)
        {
            var result = await search.ExecuteAsync<BaseDataList<WantedReleaseCandidateV1>>(new WantedItemParametersV1(wantedItemId), new SearchWantedDataV1(), HttpContext.RequestAborted);
            HintText(texts["admin.browser.searched"] + " " + result.Count);
            await LoadDataAsync();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            _status = await status.ExecuteAsync<BrowserStatusV1>(new BrowserParametersV1(), HttpContext.RequestAborted);
            _connections = await connections.ExecuteAsync<PageResult<AcquisitionConnectionDetailsV1>>(new ReadAcquisitionConnectionsParametersV1(1, 50), HttpContext.RequestAborted);
            _wanted = await wanted.ExecuteAsync<PageResult<WantedDetailsV1>>(new ReadWantedParametersV1(1, 50, wantedViewTypeId: WantedViewType.Missing), HttpContext.RequestAborted);
        }
    }
}
