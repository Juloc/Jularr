using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Service.User.Media.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Antiforgery;

namespace Jularr.Frontend.Pages.User.Watchlist
{
    public sealed class WatchlistModel(ReadWatchlistV1 read, IAntiforgery antiforgery) : JularrPage
    {
        private PageResult<WorkDetailsV1>? _works;

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        protected override void Define(Page page)
        {
            page.Title("watchlist.title");
            page.Navigation("nav.watchlist");
            page.Asset("Pages/User/Watchlist/Watchlist.entry.scss");
            page.Asset("Framework/Components/MediaCard.entry.ts");

            // Profile-owned list only.
            if (_works is { } works)
            {
                var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
                page.Content.Add(new Div([.. works.Items.Select(item =>
                {
                    var href = "/Details/" + item.Id.ToString(CultureInfo.InvariantCulture);
                    return new MediaCard(item.CanonicalTitle, href, item.PosterUrl, item.ReleaseYear?.ToString(CultureInfo.InvariantCulture), MediaTypeLabel.Key(item.MediaTypeId, item.MediaSubtypeId))
                        .Information(item.Overview).Actions(new Form<UpdateWatchlistDataV1>(form =>
                        {
                            form.Hidden(data => data.IsWatchlisted);
                            form.Submit("media.watchlist.remove");
                        }, href, csrf, new UpdateWatchlistDataV1(false)));
                })]).Class("media-grid"));

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

        protected override async Task LoadDataAsync()
        {
            _works = await read.ExecuteAsync<PageResult<WorkDetailsV1>>(new ReadWatchlistParametersV1(PageNumber), HttpContext.RequestAborted);
        }
    }
}
