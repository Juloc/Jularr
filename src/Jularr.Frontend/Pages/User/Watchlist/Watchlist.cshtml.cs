using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.User.Library.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Service.Public.Authentication.V1;
using Jularr.Backend.Service.User.Media.V1;
using Jularr.Backend.Service.User.Profile.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.User.Watchlist
{
    // The profile's watchlist by media type, in the chosen order, each title with its state and a way off the list.
    public sealed class WatchlistModel(ReadAuthenticationV1 authentication, ReadWatchlistV1 read, ReadProfileMediaV1 readMedia, UpdateWatchlistV1 update, IAntiforgery antiforgery,
        UiText texts, FrontendAssets assets) : JularrPage
    {
        private PageResult<LibraryItemV1>? _works;
        private IReadOnlyList<ProfileMediaV1> _media = [];

        [BindProperty(SupportsGet = true, Name = "type")]
        public string? TypeSlug { get; set; }

        [BindProperty(SupportsGet = true, Name = "sort")]
        public LibrarySortType Sort { get; set; } = LibrarySortType.RecentlyAdded;

        [BindProperty(SupportsGet = true, Name = "p")]
        public int PageNumber { get; set; } = 1;

        protected override void Define(Page page)
        {
            page.Title("watchlist.title");
            page.Navigation("nav.watchlist");
            page.Asset("Pages/User/Watchlist/Watchlist.entry.scss");
            if (_works is not { } works) return;

            var scope = LibraryScopes.Find(TypeSlug).Slug;
            page.Content.Add(new NavigationTabs([.. LibraryScopes.Listed(_media).Select(item => new NavigationTab("library.scope." + item.Slug, Address(item.Slug, Sort, 1), item.Slug == scope))],
                "discover.mediaTypes", "media"));
            page.Content.Add(new Nav([.. Enum.GetValues<LibrarySortType>().Select(sort => new A(new Text("library.sort." + sort)).Class(sort == Sort ? "button button--primary" : "button")
                .Attribute("href", Address(scope, sort, 1)))]).Class("page-actions").Attribute("aria-label", texts["library.sort"]));

            // Removing changes only this list.
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            page.Content.Add(new Div([.. works.Items.Select(item => new MediaCard(assets, item.Title, "/Details/" + item.Id.ToString(CultureInfo.InvariantCulture), item.PosterUrl,
                item.ReleaseYear?.ToString(CultureInfo.InvariantCulture), "media.state." + item.MediaStateTypeId)
                .Bookmark(new MediaBookmark(assets, item.Id, item.IsWatchlisted, Request.Path + Request.QueryString, csrf)))])
                .Class("media-grid"));

            if (works.Items.Count == 0) page.Content.Add(new P(new Text("media.watchlist.empty")));
            var paging = new List<IUiComponent>();
            if (works.Page > 1) paging.Add(new A(new Text("common.previous")).Class("button").Attribute("href", Address(scope, Sort, works.Page - 1)));
            if (works.Items.Count == works.PageSize) paging.Add(new A(new Text("common.next")).Class("button").Attribute("href", Address(scope, Sort, works.Page + 1)));
            if (paging.Count > 0) page.Content.Add(new Nav([.. paging]).Class("page-actions").Attribute("aria-label", texts["library.paging"]));
            if (FormError is not null) page.Content.Add(new P(ErrorText.Of(FormError)).Attribute("role", "alert"));
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
            return LocalRedirect(Address(LibraryScopes.Find(TypeSlug).Slug, Sort, PageNumber));
        }

        protected override async Task LoadDataAsync()
        {
            var profileId = (await authentication.ExecuteAsync<AuthenticationResultV1>(cancellationToken: HttpContext.RequestAborted)).ProfileId
                ?? throw new UnauthorizedAccessException("An active profile is required.");
            _media = await readMedia.ExecuteAsync<BaseDataList<ProfileMediaV1>>(new ReadProfileParametersV1(profileId), HttpContext.RequestAborted);
            var scope = LibraryScopes.Find(TypeSlug);
            _works = await read.ExecuteAsync<PageResult<LibraryItemV1>>(new ReadWatchlistParametersV1(Math.Max(1, PageNumber), 48, scope.Type, scope.Subtype, Sort),
                HttpContext.RequestAborted);
        }

        private static string Address(string scope, LibrarySortType sort, int page) =>
            "/Watchlist?type=" + scope + "&sort=" + sort + (page > 1 ? "&p=" + page.ToString(CultureInfo.InvariantCulture) : "");
    }
}
