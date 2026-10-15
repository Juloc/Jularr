using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Service.User.Media.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.User.Watchlist
{
    public sealed class WatchlistModel(ReadWatchlistV1 read, UpdateWatchlistV1 update, IAntiforgery antiforgery) : JularrPage
    {
        private PageResult<WorkCardV1>? _works;

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        protected override void Define(Page page)
        {
            page.Title("watchlist.title");
            page.Navigation("nav.watchlist");
            page.Asset("Pages/User/Watchlist/Watchlist.entry.scss");

            // Profile-owned list only.
            if (_works is { } works)
            {
                // Removing changes only this list.
                var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
                page.Content.Add(new Div([.. works.Items.Select(item => new Div(new MediaCard(item.CanonicalTitle, "/Details/" + item.Id.ToString(CultureInfo.InvariantCulture), item.PosterUrl,
                    item.ReleaseYear?.ToString(CultureInfo.InvariantCulture), MediaTypeLabel.Key(item.MediaTypeId, item.MediaSubtypeId)), new Form<UpdateWatchlistDataV1>(form =>
                    {
                        form.Hidden(data => data.IsWatchlisted);
                        form.Submit("media.remove", item.CanonicalTitle, valueHidden: true);
                    }, "/Watchlist?handler=Remove&workId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf, new UpdateWatchlistDataV1(false))).Class("media-grid__entry"))]).Class("media-grid"));

                if (works.Items.Count == 0) page.Content.Add(new P(new Text("media.watchlist.empty")));
                if (works.Page > 1) page.Content.Add(new A(new Text("common.previous")).Attribute("href", "/Watchlist?pageNumber=" + (works.Page - 1)));
                if (works.Items.Count == works.PageSize) page.Content.Add(new A(new Text("common.next")).Attribute("href", "/Watchlist?pageNumber=" + (works.Page + 1)));
            }

            if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostRemoveAsync([BindRequired] long workId)
        {
            await update.ExecuteAsync<UpdateResult>(new WorkParametersV1(workId), new UpdateWatchlistDataV1(false), HttpContext.RequestAborted);
            return LocalRedirect("/Watchlist?pageNumber=" + PageNumber.ToString(CultureInfo.InvariantCulture));
        }

        protected override async Task LoadDataAsync()
        {
            _works = await read.ExecuteAsync<PageResult<WorkCardV1>>(new ReadWatchlistParametersV1(PageNumber), HttpContext.RequestAborted);
        }
    }
}
