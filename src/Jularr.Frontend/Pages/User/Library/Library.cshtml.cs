using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.User.Library.V1;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Service.Public.Authentication.V1;
using Jularr.Backend.Service.User.Library.V1;
using Jularr.Backend.Service.User.Profile.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Antiforgery;

namespace Jularr.Frontend.Pages.User.Library
{
    public sealed class LibraryModel(ReadAuthenticationV1 authentication, ReadLibraryV1 read, ReadProfileMediaV1 readMedia, UiText texts, FrontendAssets assets, IAntiforgery antiforgery) : JularrPage
    {
        private static readonly (string Slug, MediaType? Type, MediaSubtype? Subtype)[] Scopes =
        [
            ("all", null, null), ("movies", MediaType.Movie, null), ("series", MediaType.Series, null), ("anime", null, MediaSubtype.Anime), ("books", MediaType.Book, null),
            ("manga", null, MediaSubtype.Manga), ("lightnovels", null, MediaSubtype.LightNovel), ("music", MediaType.Music, null), ("games", MediaType.Game, null)
        ];

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
            page.Asset("Framework/Components/MediaCard.entry.ts");
            if (_items is not { } items)
            {
                return;
            }

            // Scopes of the profile's visible media types, then a quiet toolbar and the grid.
            var scope = Scopes.FirstOrDefault(item => item.Slug == TypeSlug).Slug ?? "all";
            page.Content.Add(new NavigationTabs([.. Scopes.Where(IsListed).Select(item => new NavigationTab("library.scope." + item.Slug, Address(item.Slug, Query, Sort, 1), item.Slug == scope))],
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
                var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
                page.Content.Add(new Div([.. items.Items.Select(item =>
                {
                    var href = "/Details/" + item.Id.ToString(CultureInfo.InvariantCulture);
                    var card = new MediaCard(item.Title, href, item.PosterUrl, item.ReleaseYear?.ToString(CultureInfo.InvariantCulture), "media.state." + item.MediaStateTypeId)
                        .Actions(new Form<UpdateWatchlistDataV1>(form =>
                        {
                            form.Hidden(data => data.IsWatchlisted);
                            form.Submit(item.IsWatchlisted ? "media.watchlist.remove" : "media.watchlist.add");
                        }, href, csrf, new UpdateWatchlistDataV1(!item.IsWatchlisted)));
                    if (item.MediaStateTypeId == MediaStateType.Requested) card.Availability(new MediaAvailability(assets, MediaAvailabilityState.Requested));
                    return card;
                })]).Class("media-grid"));
            }

            // A full page offers the next one.
            var paging = new List<IUiComponent>();
            if (items.Page > 1) paging.Add(new A(new Text("common.previous")).Class("button").Attribute("href", Address(scope, Query, Sort, items.Page - 1)));
            if (items.Items.Count == items.PageSize) paging.Add(new A(new Text("common.next")).Class("button").Attribute("href", Address(scope, Query, Sort, items.Page + 1)));
            if (paging.Count > 0) page.Content.Add(new Nav([.. paging]).Class("library-paging").Attribute("aria-label", texts["library.paging"]));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            if (texts.Capabilities?.CanReadMedia is not true)
            {
                return;
            }

            var profileId = (await authentication.ExecuteAsync<AuthenticationResultV1>(cancellationToken: HttpContext.RequestAborted)).ProfileId
                ?? throw new UnauthorizedAccessException("An active profile is required.");
            _media = await readMedia.ExecuteAsync<BaseDataList<ProfileMediaV1>>(new ReadProfileParametersV1(profileId), HttpContext.RequestAborted);
            var scope = Scopes.FirstOrDefault(item => item.Slug == TypeSlug);
            _items = await read.ExecuteAsync<PageResult<LibraryItemV1>>(new ReadLibraryParametersV1(scope.Type, string.IsNullOrWhiteSpace(Query) ? null : Query.Trim(), Sort,
                Math.Max(1, PageNumber), mediaSubtypeId: scope.Subtype), HttpContext.RequestAborted);
        }

        private bool IsListed((string Slug, MediaType? Type, MediaSubtype? Subtype) scope) =>
            scope.Slug == "all" || _media.Any(item => !item.IsHidden && item.Key == (scope.Subtype?.ToString() ?? scope.Type?.ToString()));

        private static string Address(string scope, string? query, LibrarySortType sort, int page) =>
            "/Library?type=" + scope + "&sort=" + sort + (string.IsNullOrWhiteSpace(query) ? "" : "&q=" + Uri.EscapeDataString(query)) + (page > 1 ? "&p=" + page.ToString(CultureInfo.InvariantCulture) : "");
    }
}
