using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Admin.Wanted.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Wanted
{
    public sealed class WantedModel(ReadWantedV1 read, UpdateWantedV1 update, IAntiforgery antiforgery) : JularrPage
    {
        private PageResult<WantedDetailsV1>? _wanted;

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        protected override void Define(Page page)
        {
            page.Title("admin.wanted.title");
            page.Navigation("nav.admin");

            // Shared Work targets, not requests.
            if (_wanted is { } wanted)
            {
                var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
                page.Content.Add(new Table<WantedDetailsV1>(wanted.Items)
                    .Text(item => item.CanonicalTitle, "table.work")
                    .Content("request.language", item => new ValueText(string.Join(", ", item.RequestedLanguages)))
                    .Resource(item => item.IsPaused, "table.status", paused => paused ? "wanted.paused" : "wanted.requested")
                    .Content("common.actions", item => new Form<UpdateWantedDataV1>(form =>
                    {
                        form.Hidden(data => data.IsPaused);
                        form.Hidden(data => data.Version);
                        form.Submit(item.IsPaused ? "wanted.resume" : "wanted.pause");
                    }, Address(PageNumber) + "&WantedItemId=" + item.Id, csrf,
                        new UpdateWantedDataV1(!item.IsPaused, item.Version))));

                if (wanted.Items.Count == 0) page.Content.Add(new P(new Text("wanted.empty")));
                if (wanted.Page > 1) page.Content.Add(new A(new Text("common.previous")).Attribute("href", Address(wanted.Page - 1)));
                if (wanted.Items.Count == wanted.PageSize) page.Content.Add(new A(new Text("common.next")).Attribute("href", Address(wanted.Page + 1)));
            }

            if (Error is not null) page.Content.Add(new P(new Text(Error.ResourceKey)).Attribute("role", "alert"));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync([BindRequired] long wantedItemId, [BindRequired] bool isPaused, [BindRequired] long version)
        {
            await update.ExecuteAsync<UpdateResult>(new UpdateWantedParametersV1(wantedItemId), new UpdateWantedDataV1(isPaused, version), HttpContext.RequestAborted);
            return LocalRedirect(Address(PageNumber));
        }

        protected override async Task LoadDataAsync()
        {
            _wanted = await read.ExecuteAsync<PageResult<WantedDetailsV1>>(new ReadWantedParametersV1(PageNumber), HttpContext.RequestAborted);
        }

        private static string Address(int pageNumber) => "/Admin/Wanted?PageNumber=" + pageNumber;
    }
}
