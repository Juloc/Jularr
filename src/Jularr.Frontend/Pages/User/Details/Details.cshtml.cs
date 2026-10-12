using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Service.User.Media.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.User.Details
{
    public sealed class DetailsModel(ReadWorkV1 read, UpdateWatchlistV1 update, IAntiforgery antiforgery) : JularrPage
    {
        private WorkDetailsV1? _work;

        [BindProperty(SupportsGet = true)]
        public long Id { get; set; }

        protected override void Define(Page page)
        {
            page.Title("details.title");
            page.Navigation("nav.home");

            // Persisted media identity.
            if (_work is { } work)
            {
                page.Content.Add(new P(new ValueText(work.CanonicalTitle)));
                page.Content.Add(new P(new Text("media.type." + work.MediaTypeId)));
                page.Content.Add(new Form<UpdateWatchlistDataV1>(form =>
                {
                    form.Hidden(item => item.IsWatchlisted);
                    form.Submit(work.IsWatchlisted ? "media.watchlist.remove" : "media.watchlist.add");
                }, "/Details/" + work.Id.ToString(CultureInfo.InvariantCulture), antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                    new UpdateWatchlistDataV1(!work.IsWatchlisted), Error));
            }
            else if (Error is not null) page.Content.Add(new P(new Text(Error.ResourceKey)).Attribute("role", "alert"));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync([BindRequired] bool isWatchlisted)
        {
            await update.ExecuteAsync<UpdateResult>(new WorkParametersV1(Id), new UpdateWatchlistDataV1(isWatchlisted), HttpContext.RequestAborted);
            return RedirectToPage("/User/Details/Details", new { id = Id });
        }

        protected override async Task LoadDataAsync()
        {
            _work = await read.ExecuteAsync<WorkDetailsV1>(new WorkParametersV1(Id), HttpContext.RequestAborted);
        }
    }
}
