using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.User.Library.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Service.User.Media.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.Franchise
{
    // A film series' titles the profile may see.
    public sealed class FranchiseModel(ReadFranchiseV1 read, ReadFranchiseWorksV1 readWorks, FrontendAssets assets, IAntiforgery antiforgery) : JularrPage
    {
        private FranchiseV1? _franchise;
        private PageResult<LibraryItemV1>? _works;

        [BindProperty(SupportsGet = true)]
        public long Id { get; set; }

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        protected override void Define(Page page)
        {
            page.Title("franchise.title", _franchise?.Name);
            page.Parent("nav.library", "/Library");
            page.Navigation("nav.library");
            page.Asset("Pages/User/Franchise/Franchise.entry.scss");
            page.Asset("Framework/Components/MediaCard.entry.ts");
            if (_franchise is { } franchise && _works is { } works)
            {
                var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
                page.Content.Add(new Div([.. works.Items.Select(item => MediaCard.For(assets, item)
                    .Bookmark(new MediaBookmark(assets, item.Id, item.IsWatchlisted, Request.Path + Request.QueryString, csrf)))]).Class("media-grid"));
                page.Content.Add(new Span().Paged(works.Items.Count, works.PageSize));
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
            _franchise = await read.ExecuteAsync<FranchiseV1>(new FranchiseParametersV1(Id), HttpContext.RequestAborted);
            _works = await readWorks.ExecuteAsync<PageResult<LibraryItemV1>>(new FranchiseWorksParametersV1(Id, Math.Max(1, PageNumber)), HttpContext.RequestAborted);
        }

        private string Address(int page) => "/Franchise/" + Id.ToString(CultureInfo.InvariantCulture) + "?pageNumber=" + page.ToString(CultureInfo.InvariantCulture);
    }
}
