using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Data.User.Home.V1;
using Jularr.Backend.Data.User.Library.V1;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Service.Public.Authentication.V1;
using Jularr.Backend.Service.User.Discover.V1;
using Jularr.Backend.Service.User.Home.V1;
using Jularr.Backend.Service.User.Library.V1;
using Jularr.Backend.Service.User.Profile.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.Discover
{
    public sealed class IndexModel(ReadAuthenticationV1 authentication, ReadProfileMediaV1 readMedia, ReadDiscoverFeedV1 feeds, SearchDiscoverV1 search,
        SearchDiscoverCatalogV1 catalogSearch, ReadContinueV1 continuation, ReadLibraryV1 library, ReadHomeLayoutV1 readLayout, UiText texts, FrontendAssets assets) : JularrPage
    {
        private readonly List<(MediaType Type, DiscoverFeedType Feed, IReadOnlyList<DiscoverItemV1> Items)> _shelves = [];
        private IReadOnlyList<DiscoverItemV1>? _results;
        private IReadOnlyList<DiscoverCatalogItemV1>? _catalog;
        private IReadOnlyList<ContinueItemV1> _continue = [];
        private IReadOnlyList<LibraryItemV1> _recent = [];
        private IReadOnlyList<HomeShelfV1> _layout = [.. Enum.GetValues<HomeShelfType>().Select(type => new HomeShelfV1(type, false))];
        private bool _providerUnavailable;
        private HashSet<string>? _listed;

        [BindProperty(SupportsGet = true, Name = "type")]
        public string? TypeSlug { get; set; }

        // The profile's listed types and subtypes only.
        private IEnumerable<DiscoverType> Listed => DiscoverTypes.All.Where(type => _listed?.Contains(type.Type.ToString()) is true
            && (type.Subtype is null || _listed.Contains(type.Subtype.Value.ToString())));

        private DiscoverType? Selected => Listed.FirstOrDefault(type => type.Slug == TypeSlug);

        [BindProperty(SupportsGet = true, Name = "view")]
        public DiscoverFeedType? View { get; set; }

        [BindProperty(SupportsGet = true, Name = "q")]
        public string? Query { get; set; }

        [BindProperty(SupportsGet = true, Name = "p")]
        public int PageNumber { get; set; } = 1;

        protected override void Define(Page page)
        {
            page.Title("discover.title");
            page.Navigation("nav.home");
            if (!texts.CanReadMedia())
            {
                return;
            }

            page.Asset("Pages/User/Discover/Index.entry.scss");
            page.Asset("Pages/User/Discover/Index.entry.ts");

            // One server-rendered result region.
            var body = new Page();
            DefineContent(body);
            body.Content.Add(new P().Class("discover-feedback").Attribute("role", "status"));
            page.Content.Add(new Div([.. body.Content]).Attribute("id", "discover-results")
                .Attribute("data-load-error", texts["discover.loadFailed"]).Attribute("data-provider-unavailable", _providerUnavailable ? "true" : "false"));
        }

        private void DefineContent(Page page)
        {
            var selected = Selected;

            // Media type, then browse view; filters stay separate.
            page.Content.Add(new NavigationTabs([new NavigationTab("discover.all", Address(null, null, 1), selected is null), .. Listed.Select(type =>
                new NavigationTab("discover.type." + type.Slug, Address(type.Slug, null, 1), type.Slug == selected?.Slug))], "discover.mediaTypes", "media"));
            if (selected is { IsCatalog: false } && string.IsNullOrWhiteSpace(Query))
            {
                page.Content.Add(new NavigationTabs([new NavigationTab("discover.overview", "/?type=" + selected.Slug, View is null), .. selected.Feeds.Select(feed =>
                    new NavigationTab("discover.feed." + feed, Address(selected.Slug, feed, 1), View == feed))], "discover.browseViews", "browse"));
            }

            if (_providerUnavailable && (_results is not null || _catalog is not null))
            {
                page.Content.Add(new P(new Text("discover.providerUnavailable")).Attribute("role", "status"));
            }

            // Catalog titles become Works only through a request.
            if (_catalog is { } catalog)
            {
                page.Content.Add(catalog.Count == 0
                    ? new P(new Text("discover.noResults")).Attribute("role", "status")
                    : new Div([.. catalog.Select(item => CatalogCard(selected!, item))]).Class("media-grid"));
                return;
            }

            // Search results and full browse views are grids.
            if (_results is { } results)
            {
                if (results.Count == 0)
                {
                    page.Content.Add(new P(new Text(string.IsNullOrWhiteSpace(Query) ? "discover.empty" : "discover.noResults")).Attribute("role", "status"));
                    if (PageNumber > 1) page.Content.Add(new A(new Text("common.previous")).Class("button").Attribute("href", Address(selected?.Slug, View, PageNumber - 1)));
                    return;
                }

                page.Content.Add(new Div([.. results.Select(Card)]).Class("media-grid"));
                var paging = new List<IUiComponent>();
                if (PageNumber > 1) paging.Add(new A(new Text("common.previous")).Class("button").Attribute("href", Address(selected?.Slug, View, PageNumber - 1)));
                if (!_providerUnavailable && PageNumber < 500 && results.Count >= 20)
                {
                    paging.Add(new A(new Text("common.next")).Class("button").Attribute("data-discover-next", "true").Attribute("href", Address(selected?.Slug, View, PageNumber + 1)));
                }

                page.Content.Add(new Nav([.. paging]).Class("discover-paging").Attribute("aria-label", texts["discover.pages"]));

                return;
            }

            // Local content arrives first.
            var featured = new Page();
            DefineFeatured(featured);
            page.Content.Add(new Div([.. featured.Content]).Attribute("id", "discover-featured"));

            // Watching, listening and reading continue on their own shelves, in the profile's order.
            var continuing = _continue.Where(item => selected is null || item.MediaTypeId == selected.Type).ToList();
            var canPlay = texts.CanPlay();
            foreach (var shelf in _layout.Where(shelf => !shelf.IsHidden))
            {
                page.Content.Add(shelf.HomeShelfTypeId switch
                {
                    HomeShelfType.ContinueWatching => ContinueShelf("discover.continueWatching", canPlay ? continuing.Where(item => item.MediaAssetTypeId == MediaAssetType.Video) : []),
                    HomeShelfType.ContinueListening => ContinueShelf("discover.continueListening", canPlay ? continuing.Where(item => item.MediaAssetTypeId == MediaAssetType.Audio) : []),
                    HomeShelfType.ContinueReading => ContinueShelf("media.continueReading",
                        texts.CanRead() ? continuing.Where(item => ReadableMedia.IsReadable(item.MediaAssetTypeId)) : []),
                    HomeShelfType.RecentlyAdded => new MediaShelf(assets, "discover.recentlyAdded", "/Library", [.. _recent.Select(item => new MediaCard(item.Title,
                        "/Details/" + item.Id.ToString(CultureInfo.InvariantCulture), item.PosterUrl, item.ReleaseYear?.ToString(CultureInfo.InvariantCulture), "media.state." + item.MediaStateTypeId))]),
                    _ => Recommendations(selected?.Slug)
                });
            }
        }

        private IUiComponent Recommendations(string? slug)
        {
            var recommendations = new Page();
            DefineRecommendations(recommendations);
            if (_shelves.Count == 0 && !_providerUnavailable)
            {
                recommendations.Content.Add(new A(new Text("discover.loadRecommendations")).Class("button").Attribute("data-discover-recommendations", "true")
                    .Attribute("href", Address(slug, null, 1) + (slug is null ? "?" : "&") + "handler=Recommendations"));
            }

            return new Div([.. recommendations.Content]).Attribute("id", "discover-recommendations");
        }

        private MediaShelf ContinueShelf(string titleKey, IEnumerable<ContinueItemV1> items) => new(assets, titleKey, null, [.. items.Select(item =>
        {
            var readable = ReadableMedia.IsReadable(item.MediaAssetTypeId);
            var card = new MediaCard(item.Title, "/Details/" + item.WorkId.ToString(CultureInfo.InvariantCulture), item.PosterUrl,
                MediaUnitLabel.Format(texts, item.WorkUnitTypeId, item.UnitNumber, item.ParentUnitNumber), "media.state.InProgress", "media.continue",
                (readable ? "/Read/" : "/Player/") + item.MediaAssetId.ToString(CultureInfo.InvariantCulture));
            return readable ? card.ContinueReading(item.Progression) : card.Continue(item.PositionMs, item.DurationMs);
        })]);

        private void DefineFeatured(Page page)
        {
            // Provider chooses the featured title.
            if (_shelves.SelectMany(shelf => shelf.Items).FirstOrDefault(item => item.BackdropUrl is not null) is { } featured)
            {
                var (actionKey, actionHref) = Action(featured);
                page.Content.Add(new MediaHero(featured.Title, featured.BackdropUrl, featured.ReleaseYear?.ToString(CultureInfo.InvariantCulture), featured.Overview,
                    actionKey ?? "media.details", actionHref ?? DetailsHref(featured), actionKey is null ? null : DetailsHref(featured)));
            }
        }

        private void DefineRecommendations(Page page)
        {
            var selected = Selected;
            if (_providerUnavailable) page.Content.Add(new P(new Text("discover.providerUnavailable")).Attribute("role", "status"));
            foreach (var group in _shelves.GroupBy(shelf => shelf.Type))
            {
                var slug = DiscoverTypes.For(group.Key).Slug;
                if (selected is null && group.Any(shelf => shelf.Items.Count > 0))
                {
                    page.Content.Add(new H2(new Text("discover.type." + slug)).Class("discover-media-heading"));
                }

                foreach (var (_, feed, items) in group)
                {
                    page.Content.Add(new MediaShelf(assets, "discover.feed." + feed, Address(slug, feed, 1), [.. items.Select(Card)]));
                }
            }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnGetResultsAsync()
        {
            if (!texts.CanReadMedia()) return Forbid();

            await LoadDataAsync();
            PreparePage();
            return Partial("_UiComponent", Definition.Content[0]);
        }

        public async Task<IActionResult> OnGetRecommendationsAsync()
        {
            if (!texts.CanReadMedia()) return Forbid();
            await LoadRecommendationsAsync();

            // Native navigation keeps full SSR.
            if (Request.Headers["X-Jularr-Fragment"] != "recommendations")
            {
                await LoadDataAsync();
                PreparePage();
                return Page();
            }

            var featured = new Page();
            var recommendations = new Page();
            DefineFeatured(featured);
            DefineRecommendations(recommendations);
            return Partial("_UiComponent", new Div(
                new Div([.. featured.Content]).Attribute("id", "discover-featured"),
                new Div([.. recommendations.Content]).Attribute("id", "discover-recommendations")));
        }

        protected override async Task LoadDataAsync()
        {
            if (!texts.CanReadMedia())
            {
                return;
            }

            await LoadListedAsync();

            var selected = Selected;
            try
            {
                if (!string.IsNullOrWhiteSpace(Query) && selected is { IsCatalog: true } scope)
                {
                    _catalog = await catalogSearch.ExecuteAsync<BaseDataList<DiscoverCatalogItemV1>>(new SearchDiscoverCatalogDataV1(scope.Type, Query, scope.Subtype),
                        HttpContext.RequestAborted);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(Query))
                {
                    _results = await search.ExecuteAsync<BaseDataList<DiscoverItemV1>>(new SearchDiscoverDataV1(Query, selected?.Type, PageNumber), HttpContext.RequestAborted);
                    return;
                }

                if (selected is not null && View is { } view && selected.Feeds.Contains(view))
                {
                    _results = await feeds.ExecuteAsync<BaseDataList<DiscoverItemV1>>(new ReadDiscoverFeedDataV1(selected.Type, view, PageNumber), HttpContext.RequestAborted);
                    return;
                }
            }
            catch (ServiceUnavailableException)
            {
                _providerUnavailable = true;
                _results = selected is { IsCatalog: true } ? null : [];
                _catalog = selected is { IsCatalog: true } ? [] : null;
                return;
            }

            // Local shelves never wait for the provider.
            if (texts.CanArrangeHome())
            {
                _layout = await readLayout.ExecuteAsync<BaseDataList<HomeShelfV1>>(new HomeLayoutParametersV1(), HttpContext.RequestAborted);
            }

            var shown = _layout.Where(shelf => !shelf.IsHidden).Select(shelf => shelf.HomeShelfTypeId).ToHashSet();
            if ((texts.CanPlay() || texts.CanRead())
                && shown.Overlaps([HomeShelfType.ContinueWatching, HomeShelfType.ContinueListening, HomeShelfType.ContinueReading]))
            {
                _continue = await continuation.ExecuteAsync<BaseDataList<ContinueItemV1>>(new ReadContinueParametersV1(), HttpContext.RequestAborted);
            }

            if (shown.Contains(HomeShelfType.RecentlyAdded))
            {
                _recent = (await library.ExecuteAsync<PageResult<LibraryItemV1>>(new ReadLibraryParametersV1(selected?.Type, pageSize: 20), HttpContext.RequestAborted)).Items;
            }
        }

        private async Task LoadListedAsync()
        {
            if (_listed is not null)
            {
                return;
            }

            var profileId = (await authentication.ExecuteAsync<AuthenticationResultV1>(cancellationToken: HttpContext.RequestAborted)).ProfileId
                ?? throw new UnauthorizedAccessException("An active profile is required.");
            var media = await readMedia.ExecuteAsync<BaseDataList<ProfileMediaV1>>(new ReadProfileParametersV1(profileId), HttpContext.RequestAborted);
            _listed = [.. media.Where(item => !item.IsHidden).Select(item => item.Key)];
        }

        private async Task LoadRecommendationsAsync()
        {
            await LoadListedAsync();
            var selected = Selected;
            foreach (var scope in Listed.Where(type => selected is null || type.Slug == selected.Slug))
            {
                foreach (var feed in scope.Feeds)
                {
                    try
                    {
                        _shelves.Add((scope.Type, feed, await feeds.ExecuteAsync<BaseDataList<DiscoverItemV1>>(new ReadDiscoverFeedDataV1(scope.Type, feed), HttpContext.RequestAborted)));
                    }
                    catch (ServiceUnavailableException)
                    {
                        _providerUnavailable = true;
                        // Other shelves remain useful.
                    }
                }
            }
        }

        private IUiComponent Card(DiscoverItemV1 item)
        {
            var (actionKey, actionHref) = Action(item);
            return new MediaCard(item.Title, DetailsHref(item), item.PosterUrl, item.ReleaseYear?.ToString(CultureInfo.InvariantCulture), "media.state." + item.MediaStateTypeId, actionKey, actionHref);
        }

        // A known title opens its Work; any other opens its request.
        private IUiComponent CatalogCard(DiscoverType scope, DiscoverCatalogItemV1 item)
        {
            var meta = string.Join(" · ", new[] { item.ReleaseYear?.ToString(CultureInfo.InvariantCulture), item.Author }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (item.WorkId is { } workId)
            {
                return new MediaCard(item.Title, "/Details/" + workId.ToString(CultureInfo.InvariantCulture), null, meta, "discover.known");
            }

            var href = "/Discover/" + scope.Slug + "/" + Uri.EscapeDataString(item.Namespace) + "/" + Uri.EscapeDataString(item.ExternalId) + "?title=" + Uri.EscapeDataString(item.Title)
                + (meta.Length == 0 ? "" : "&meta=" + Uri.EscapeDataString(meta));
            return new MediaCard(item.Title, href, null, meta, "media.state." + MediaStateType.NotRequested, texts.CanRequest() ? "request.create" : null, texts.CanRequest() ? href : null);
        }

        // The canonical next step: use what exists, request what is missing.
        private (string? Key, string? Href) Action(DiscoverItemV1 item) => item.MediaStateTypeId switch
        {
            MediaStateType.Available when item.PlexUrl is { } plex && !texts.CanPlay() => ("media.openInPlex", plex),
            MediaStateType.Available => ("media.watch", DetailsHref(item)),
            MediaStateType.NotRequested when texts.CanRequest() => ("request.create", DetailsHref(item)),
            _ => (null, null)
        };

        private static string DetailsHref(DiscoverItemV1 item) => item.WorkId is { } workId
            ? "/Details/" + workId.ToString(CultureInfo.InvariantCulture)
            : "/Discover/" + DiscoverTypes.For(item.MediaTypeId).Slug + "/" + item.TmdbId!.Value.ToString(CultureInfo.InvariantCulture);

        private string Address(string? slug, DiscoverFeedType? feed, int page)
        {
            var values = new List<string>();
            if (slug is not null) values.Add("type=" + Uri.EscapeDataString(slug));
            if (feed is not null && string.IsNullOrWhiteSpace(Query)) values.Add("view=" + feed);
            if (!string.IsNullOrWhiteSpace(Query)) values.Add("q=" + Uri.EscapeDataString(Query.Trim()));
            if (page > 1) values.Add("p=" + page.ToString(CultureInfo.InvariantCulture));
            return values.Count == 0 ? "/" : "/?" + string.Join('&', values);
        }
    }
}
