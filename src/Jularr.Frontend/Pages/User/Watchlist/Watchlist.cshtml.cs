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
        private PageResult<WorkDetailsV1>? _works;

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        protected override void Define(Page page)
        {
            page.Title("watchlist.title");
            page.Navigation("nav.watchlist");

            // Profile-owned list only.
            if (_works is { } works)
            {
                page.Content.Add(new Table<WorkDetailsV1>(works.Items)
                    .Text(item => item.CanonicalTitle, "media.title")
                    .Resource(item => item.MediaTypeId, "media.type", type => "media.type." + type)
                    .Link(item => item.Id, "common.open", id => "/Details/" + id.ToString(CultureInfo.InvariantCulture), "common.open"));

                if (works.Items.Count == 0) page.Content.Add(new P(new Text("media.watchlist.empty")));
                if (works.Page > 1) page.Content.Add(new A(new Text("common.previous")).Attribute("href", "/Watchlist?pageNumber=" + (works.Page - 1)));
                if (works.Items.Count == works.PageSize) page.Content.Add(new A(new Text("common.next")).Attribute("href", "/Watchlist?pageNumber=" + (works.Page + 1)));
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
            _works = await read.ExecuteAsync<PageResult<WorkDetailsV1>>(new ReadWatchlistParametersV1(PageNumber), HttpContext.RequestAborted);
        }
    }
}
