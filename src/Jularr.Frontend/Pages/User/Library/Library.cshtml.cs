using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.User.Library.V1;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Service.Public.Authentication.V1;
using Jularr.Backend.Service.User.Library.V1;
using Jularr.Backend.Service.User.Profile.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.Library
{
    public sealed class LibraryModel(ReadAuthenticationV1 authentication, ReadLibraryV1 read, ReadProfileMediaV1 readMedia, UiText texts) : JularrPage
    {
        private PageResult<LibraryItemV1>? _items;
        private IReadOnlyList<ProfileMediaV1> _media = [];

        [BindProperty(SupportsGet = true, Name = "type")]
        public string? TypeSlug { get; set; }

        [BindProperty(SupportsGet = true, Name = "q")]
        public string? Query { get; set; }

        [BindProperty(SupportsGet = true, Name = "sort")]
        public LibrarySortType Sort { get; set; } = LibrarySortType.RecentlyAdded;

        [BindProperty(SupportsGet = true, Name = "p")]
        public int PageNumber { get; set; } = 1;

        protected override void Define(Page page)
        {
            page.Title("nav.library");
            page.Navigation("nav.library");
            page.Asset("Pages/User/Library/Library.entry.scss");
            if (_items is not { } items)
            {
                if (!texts.CanReadMedia()) page.Content.Add(new P(new Text("media.notForProfile")).Attribute("role", "status"));
                return;
            }

            // Scopes of the profile's visible media types, then a quiet toolbar and the grid.
            var scope = LibraryScopes.Find(TypeSlug).Slug;
            page.Content.Add(new NavigationTabs([.. LibraryScopes.Listed(_media).Select(item => new NavigationTab("library.scope." + item.Slug, Address(item.Slug, Query, Sort, 1), item.Slug == scope))],
                "discover.mediaTypes", "media"));
            page.Content.Add(new Div(
                new SearchBox("/Library", "library.search", Query, new Dictionary<string, string> { ["type"] = scope, ["sort"] = Sort.ToString() }),
                new Nav([.. Enum.GetValues<LibrarySortType>().Select(sort => new A(new Text("library.sort." + sort)).Class(sort == Sort ? "button button--primary" : "button")
                    .Attribute("href", Address(scope, Query, sort, 1)))]).Class("library-toolbar__sort").Attribute("aria-label", texts["library.sort"])).Class("library-toolbar"));

            if (items.Items.Count == 0)
            {
                page.Content.Add(new P(new Text(Query is null ? "library.userEmpty" : "discover.noResults")).Attribute("role", "status"));
            }
            else
            {
                page.Content.Add(new Div([.. items.Items.Select(item => new MediaCard(item.Title, "/Details/" + item.Id.ToString(CultureInfo.InvariantCulture), item.PosterUrl,
                    item.ReleaseYear?.ToString(CultureInfo.InvariantCulture), "media.state." + item.MediaStateTypeId))]).Class("media-grid"));
            }

            // A full page offers the next one.
            var paging = new List<IUiComponent>();
            if (items.Page > 1) paging.Add(new A(new Text("common.previous")).Class("button").Attribute("href", Address(scope, Query, Sort, items.Page - 1)));
            if (items.Items.Count == items.PageSize) paging.Add(new A(new Text("common.next")).Class("button").Attribute("href", Address(scope, Query, Sort, items.Page + 1)));
            if (paging.Count > 0) page.Content.Add(new Nav([.. paging]).Class("page-actions").Attribute("aria-label", texts["library.paging"]));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            if (!texts.CanReadMedia())
            {
                return;
            }

            var profileId = (await authentication.ExecuteAsync<AuthenticationResultV1>(cancellationToken: HttpContext.RequestAborted)).ProfileId
                ?? throw new UnauthorizedAccessException("An active profile is required.");
            _media = await readMedia.ExecuteAsync<BaseDataList<ProfileMediaV1>>(new ReadProfileParametersV1(profileId), HttpContext.RequestAborted);
            var scope = LibraryScopes.Find(TypeSlug);
            _items = await read.ExecuteAsync<PageResult<LibraryItemV1>>(new ReadLibraryParametersV1(scope.Type, string.IsNullOrWhiteSpace(Query) ? null : Query.Trim(), Sort,
                Math.Max(1, PageNumber), mediaSubtypeId: scope.Subtype), HttpContext.RequestAborted);
        }

        private static string Address(string scope, string? query, LibrarySortType sort, int page) =>
            "/Library?type=" + scope + "&sort=" + sort + (string.IsNullOrWhiteSpace(query) ? "" : "&q=" + Uri.EscapeDataString(query)) + (page > 1 ? "&p=" + page.ToString(CultureInfo.InvariantCulture) : "");
    }
}
