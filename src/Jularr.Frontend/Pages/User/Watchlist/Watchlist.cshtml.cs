using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Service.User.Media.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.Watchlist
{
    public sealed class WatchlistModel(ReadWatchlistV1 read) : JularrPage
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
                page.Content.Add(new Div([.. works.Items.Select(item => new MediaCard(item.CanonicalTitle, "/Details/" + item.Id.ToString(CultureInfo.InvariantCulture), item.PosterUrl,
                    item.ReleaseYear?.ToString(CultureInfo.InvariantCulture), MediaTypeLabel.Key(item.MediaTypeId, item.MediaSubtypeId)))]).Class("media-grid"));

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
            _works = await read.ExecuteAsync<PageResult<WorkCardV1>>(new ReadWatchlistParametersV1(PageNumber), HttpContext.RequestAborted);
        }
    }
}
