using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.User.Request.V1;
using Jularr.Backend.Service.User.Request.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.User.Requests
{
    public sealed class RequestsModel(ReadRequestsV1 read, SaveRequestSourceLinkV1 sourceLink, IAntiforgery antiforgery) : JularrPage
    {
        private PageResult<RequestDetailsV1>? _requests;

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        protected override void Define(Page page)
        {
            page.Title("requests.title");
            page.Navigation("nav.home");

            // Active-profile requests only.
            if (_requests is { } requests)
            {
                var table = new Table<RequestDetailsV1>(requests.Items)
                    .Text(item => item.CanonicalTitle, "table.work")
                    .Text(item => item.RequestedLanguage, "request.language")
                    .Resource(item => item.AcquisitionRequestStatusTypeId, "table.status", status => "request.status." + status)
                    .Text(item => item.CreatedAt, "table.requestedAt");
                if (requests.Items.Any(item => item.ManualUrl is not null))
                {
                    var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
                    table = table.Content("common.actions", item => item is { ManualUrl: { } manual, OperationId: { } operationId } ? ManualLink(item.Id, operationId, manual, csrf) : new Span());
                }

                page.Content.Add(table);

                if (requests.Items.Count == 0) page.Content.Add(new P(new Text("request.empty")));
                if (requests.Page > 1) page.Content.Add(new A(new Text("common.previous")).Attribute("href", "/Requests?PageNumber=" + (requests.Page - 1)));
                if (requests.Items.Count == requests.PageSize) page.Content.Add(new A(new Text("common.next")).Attribute("href", "/Requests?PageNumber=" + (requests.Page + 1)));
            }

            if (Error is not null) page.Content.Add(new P(new Text(Error.ResourceKey)).Attribute("role", "alert"));
        }

        // The user passes the source's check in their own browser; the server takes the file link from there.
        private IUiComponent ManualLink(long requestId, long operationId, string manual, string? csrf) => new Div(
            new A(new Text("activity.openSource")).Class("button").Attribute("href", manual).Attribute("target", "_blank").Attribute("rel", "noopener noreferrer"),
            new Form<SaveRequestSourceLinkDataV1>(form =>
            {
                form.Hidden(data => data.OperationId);
                form.Text(data => data.SourceLinkUrl, "activity.fileLink");
                form.Submit("activity.continueDownload");
            }, "/Requests?handler=SourceLink&requestId=" + requestId.ToString(CultureInfo.InvariantCulture), csrf, new SaveRequestSourceLinkDataV1(operationId, ""),
                Error?.Field == nameof(SaveRequestSourceLinkDataV1.SourceLinkUrl) ? Error : null));

        public async Task<IActionResult> OnPostSourceLinkAsync([BindRequired] long requestId, [BindRequired] long operationId, string? sourceLinkUrl)
        {
            await sourceLink.ExecuteAsync<UpdateResult>(new RequestParametersV1(requestId), new SaveRequestSourceLinkDataV1(operationId, sourceLinkUrl ?? ""), HttpContext.RequestAborted);
            return LocalRedirect("/Requests");
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            _requests = await read.ExecuteAsync<PageResult<RequestDetailsV1>>(new ReadRequestsParametersV1(PageNumber), HttpContext.RequestAborted);
        }
    }
}
