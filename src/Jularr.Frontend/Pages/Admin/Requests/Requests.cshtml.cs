using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Request.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Service.Admin.Request.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Requests
{
    public sealed class RequestsModel(ReadRequestsV1 read, UpdateRequestV1 update, IAntiforgery antiforgery) : JularrPage
    {
        private PageResult<RequestDetailsV1>? _requests;

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public RequestTabType Tab { get; set; }

        protected override void Define(Page page)
        {
            page.Title("admin.requests.title");
            page.Navigation("nav.admin");
            page.Asset("Framework/Components/LiveProgress.entry.ts");
            page.Content.Add(new NavigationTabs(Enum.GetValues<RequestTabType>()
                .Select(tab => new NavigationTab("request.tab." + tab, Address(tab: tab), tab == Tab)).ToArray(), "admin.requests.title"));

            // Real queue and moderation.
            if (_requests is { } requests)
            {
                var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
                page.Content.Add(new Table<RequestDetailsV1>(requests.Items)
                    .Text(item => item.CanonicalTitle, "table.work")
                    .Resource(item => item.MediaTypeId, "table.type", type => "media.type." + type)
                    .Text(item => item.RequesterName, "table.requester")
                    .Text(item => item.RequestedLanguage, "request.language")
                    .Text(item => item.CreatedAt, "table.requestedAt")
                    .Content("table.status", item => new LiveProgress(LiveKeys.AdminRequest(item.Id)).Request(item.AcquisitionRequestStatusTypeId, admin: true).Details())
                    .Content("common.actions", item => item.AcquisitionRequestStatusTypeId == AcquisitionRequestStatusType.Open
                        ? new Div(Decision(item, AcquisitionRequestStatusType.Approved, csrf), Decision(item, AcquisitionRequestStatusType.Rejected, csrf))
                        : new Span()));

                if (requests.Items.Count == 0) page.Content.Add(new P(new Text("request.empty")));
                if (requests.Page > 1) page.Content.Add(new A(new Text("common.previous")).Attribute("href", Address(requests.Page - 1, Tab)));
                if (requests.Items.Count == requests.PageSize) page.Content.Add(new A(new Text("common.next")).Attribute("href", Address(requests.Page + 1, Tab)));
            }

            if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
        }

        private Form<UpdateRequestDataV1> Decision(RequestDetailsV1 request, AcquisitionRequestStatusType status, string? csrf)
        {
            return new Form<UpdateRequestDataV1>(form =>
            {
                form.Hidden(item => item.AcquisitionRequestStatusTypeId);
                form.Hidden(item => item.Version);
                form.Submit(status == AcquisitionRequestStatusType.Approved ? "request.approve" : "request.reject");
            }, Address(PageNumber, Tab) + "&AcquisitionRequestId=" + request.Id, csrf,
                new UpdateRequestDataV1(status, request.Version));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync([BindRequired] long acquisitionRequestId, [BindRequired] AcquisitionRequestStatusType acquisitionRequestStatusTypeId, [BindRequired] long version)
        {
            await update.ExecuteAsync<UpdateResult>(new UpdateRequestParametersV1(acquisitionRequestId), new UpdateRequestDataV1(acquisitionRequestStatusTypeId, version), HttpContext.RequestAborted);
            return LocalRedirect(Address(PageNumber, Tab));
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            _requests = await read.ExecuteAsync<PageResult<RequestDetailsV1>>(new ReadRequestsParametersV1(PageNumber, requestTabTypeId: Tab), ct);
        }

        private static string Address(int pageNumber = 1, RequestTabType tab = RequestTabType.All) => "/Admin/Requests?PageNumber=" + pageNumber + "&Tab=" + tab;
    }
}
