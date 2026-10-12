using Jularr.Backend.Data;
using Jularr.Backend.Data.User.Request.V1;
using Jularr.Backend.Service.User.Request.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.Requests
{
    public sealed class RequestsModel(ReadRequestsV1 read) : JularrPage
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
                page.Content.Add(new Table<RequestDetailsV1>(requests.Items)
                    .Text(item => item.CanonicalTitle, "table.work")
                    .Text(item => item.RequestedLanguage, "request.language")
                    .Resource(item => item.AcquisitionRequestStatusTypeId, "table.status", status => "request.status." + status)
                    .Text(item => item.CreatedAt, "table.requestedAt"));

                if (requests.Items.Count == 0) page.Content.Add(new P(new Text("request.empty")));
                if (requests.Page > 1) page.Content.Add(new A(new Text("common.previous")).Attribute("href", "/Requests?PageNumber=" + (requests.Page - 1)));
                if (requests.Items.Count == requests.PageSize) page.Content.Add(new A(new Text("common.next")).Attribute("href", "/Requests?PageNumber=" + (requests.Page + 1)));
            }

            if (Error is not null) page.Content.Add(new P(new Text(Error.ResourceKey)).Attribute("role", "alert"));
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
