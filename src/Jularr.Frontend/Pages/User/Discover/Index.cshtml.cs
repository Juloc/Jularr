using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Data.User.Home.V1;
using Jularr.Backend.Data.User.Library.V1;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Service.User.Discover.V1;
using Jularr.Backend.Service.User.Home.V1;
using Jularr.Backend.Service.User.Library.V1;
using Jularr.Backend.Service.User.Profile.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.Discover
{
    public sealed class IndexModel(ReadDiscoverScopesV1 readScopes, ReadDiscoverFeedV1 feeds, SearchDiscoverV1 search,
        SearchDiscoverCatalogV1 catalogSearch, ReadDiscoverCatalogFeedV1 catalogFeeds, ReadContinueV1 continuation, ReadLibraryV1 library, ReadHomeLayoutV1 readLayout, UiText texts, FrontendAssets assets,
        IAntiforgery antiforgery) : JularrPage
    {
        private readonly List<(MediaType Type, DiscoverFeedType Feed, IReadOnlyList<DiscoverItemV1> Items)> _shelves = [];
        private readonly List<(DiscoverScopeV1 Scope, DiscoverFeedType Feed, IReadOnlyList<DiscoverCatalogItemV1> Items)> _catalogShelves = [];
        private IReadOnlyList<DiscoverItemV1>? _results;
        private IReadOnlyList<DiscoverCatalogItemV1>? _catalog;
        private IReadOnlyList<ContinueItemV1> _continue = [];
        private IUiComponent? _featured;
        private IReadOnlyList<LibraryItemV1> _recent = [];
        private IReadOnlyList<HomeShelfV1> _layout = [.. Enum.GetValues<HomeShelfType>().Select(type => new HomeShelfV1(type, false))];
        private bool _providerUnavailable;
        private IReadOnlyList<DiscoverScopeV1>? _scopes;

        [BindProperty(SupportsGet = true, Name = "type")]
        public string? TypeSlug { get; set; }

        // The profile's listed types and subtypes only.
        private IEnumerable<DiscoverScopeV1> Listed => _scopes ?? [];

        private DiscoverScopeV1? Selected => Listed.FirstOrDefault(type => type.Slug == TypeSlug);

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
                page.Content.Add(new P(new Text("media.notForProfile")).Attribute("role", "status"));
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

            // One row of media types; a ranking opened from its shelf names itself and leads back.
            page.Content.Add(new NavigationTabs([new NavigationTab("discover.all", Address(null, null, 1), selected is null), .. Listed.Select(type =>
                new NavigationTab("discover.type." + type.Slug, Address(type.Slug, null, 1), type.Slug == selected?.Slug))], "discover.mediaTypes", "media"));
            if (selected is not null && View is { } view && string.IsNullOrWhiteSpace(Query))
            {
                page.Content.Add(new Div(new A(new Text("common.back")).Class("button").Attribute("href", Address(selected.Slug, null, 1)),
                    new H2(new Text("discover.feed." + view))).Class("discover-view-heading"));
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

            // Featured is part of the initial response, never a late insertion above the shelves.
            if (_featured is not null)
            {
                page.Content.Add(_featured);
            }

            // Watching, listening and reading continue on their own shelves, in the profile's order.
            var continuing = _continue.Where(item => selected is null || item.MediaTypeId == selected.MediaTypeId).ToList();
            var canPlay = texts.CanPlay();
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            foreach (var shelf in _layout.Where(shelf => !shelf.IsHidden))
            {
                page.Content.Add(shelf.HomeShelfTypeId switch
                {
                    HomeShelfType.ContinueWatching => ContinueShelf("discover.continueWatching", canPlay ? continuing.Where(item => item.MediaAssetTypeId == MediaAssetType.Video) : []),
                    HomeShelfType.ContinueListening => ContinueShelf("discover.continueListening", canPlay ? continuing.Where(item => item.MediaAssetTypeId == MediaAssetType.Audio) : []),
                    HomeShelfType.ContinueReading => ContinueShelf("media.continueReading",
                        texts.CanRead() ? continuing.Where(item => ReadableMedia.IsReadable(item.MediaAssetTypeId)) : []),
                    HomeShelfType.RecentlyAdded => new MediaShelf(assets, "discover.recentlyAdded", "/Library", [.. _recent.Select(item => new MediaCard(assets, item.Title,
                        "/Details/" + item.Id.ToString(CultureInfo.InvariantCulture), item.PosterUrl, item.ReleaseYear?.ToString(CultureInfo.InvariantCulture), "media.state." + item.MediaStateTypeId)
                        .Bookmark(new MediaBookmark(assets, item.Id, item.IsWatchlisted, Request.Path + Request.QueryString, csrf)))]),
                    _ => Recommendations(selected?.Slug)
                });
            }
        }

        private IUiComponent Recommendations(string? slug)
        {
            var recommendations = new Page();
            DefineRecommendations(recommendations);
            if (_shelves.Count == 0 && _catalogShelves.Count == 0 && !_providerUnavailable)
            {
                recommendations.Content.Add(new A(new Text("discover.loadRecommendations")).Class("button").Attribute("data-discover-recommendations", "true")
                    .Attribute("href", Address(slug, null, 1) + (slug is null ? "?" : "&") + "handler=Recommendations"));
            }

            return new Div([.. recommendations.Content]).Attribute("id", "discover-recommendations");
        }

        private MediaShelf ContinueShelf(string titleKey, IEnumerable<ContinueItemV1> items) => new(assets, titleKey, null, [.. items.Select(item =>
        {
            var readable = ReadableMedia.IsReadable(item.MediaAssetTypeId);
            var card = new MediaCard(assets, item.Title, "/Details/" + item.WorkId.ToString(CultureInfo.InvariantCulture), item.PosterUrl,
                MediaUnitLabel.Format(texts, item.WorkUnitTypeId, item.UnitNumber, item.ParentUnitNumber), "media.state.InProgress", "media.continue",
                (readable ? "/Read/" : "/Player/") + item.MediaAssetId.ToString(CultureInfo.InvariantCulture))
                .Bookmark(new MediaBookmark(assets, item.WorkId));
            return readable ? card.ContinueReading(item.Progression) : card.Continue(item.PositionMs, item.DurationMs);
        })]);

        private void DefineRecommendations(Page page)
        {
            var selected = Selected;
            if (_providerUnavailable) page.Content.Add(new P(new Text("discover.providerUnavailable")).Attribute("role", "status"));
            // Types in the order of the media-type bar.
            foreach (var scope in Listed)
            {
                var shelves = scope.IsCatalog
                    ? _catalogShelves.Where(shelf => shelf.Scope == scope).Select(shelf => (shelf.Feed, Count: shelf.Items.Count, Cards: shelf.Items.Select(item => CatalogCard(scope, item))))
                    : _shelves.Where(shelf => shelf.Type == scope.MediaTypeId).Select(shelf => (shelf.Feed, Count: shelf.Items.Count, Cards: shelf.Items.Select(Card)));
                var shown = shelves.ToList();
                if (selected is null && shown.Any(shelf => shelf.Count > 0))
                {
                    page.Content.Add(new H2(new Text("discover.type." + scope.Slug)).Class("discover-media-heading"));
                }

                foreach (var (feed, _, cards) in shown)
                {
                    page.Content.Add(new MediaShelf(assets, "discover.feed." + feed, Address(scope.Slug, feed, 1), [.. cards]));
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

            var recommendations = new Page();
            DefineRecommendations(recommendations);
            return Partial("_UiComponent", new Div([.. recommendations.Content]).Attribute("id", "discover-recommendations"));
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
                    _catalog = await catalogSearch.ExecuteAsync<BaseDataList<DiscoverCatalogItemV1>>(new SearchDiscoverCatalogDataV1(scope.MediaTypeId, Query, scope.MediaSubtypeId),
                        HttpContext.RequestAborted);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(Query))
                {
                    _results = await search.ExecuteAsync<BaseDataList<DiscoverItemV1>>(new SearchDiscoverDataV1(Query, selected?.MediaTypeId, PageNumber), HttpContext.RequestAborted);
                    return;
                }

                if (selected is { IsCatalog: true } && View is { } catalogView && selected.Feeds.Contains(catalogView))
                {
                    _catalog = await catalogFeeds.ExecuteAsync<BaseDataList<DiscoverCatalogItemV1>>(new ReadDiscoverCatalogFeedDataV1(selected.MediaTypeId, catalogView, selected.MediaSubtypeId),
                        HttpContext.RequestAborted);
                    return;
                }

                if (selected is not null && View is { } view && selected.Feeds.Contains(view))
                {
                    _results = await feeds.ExecuteAsync<BaseDataList<DiscoverItemV1>>(new ReadDiscoverFeedDataV1(selected.MediaTypeId, view, PageNumber), HttpContext.RequestAborted);
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
                _recent = (await library.ExecuteAsync<PageResult<LibraryItemV1>>(new ReadLibraryParametersV1(selected?.MediaTypeId, pageSize: 20), HttpContext.RequestAborted)).Items;
            }

            if (View is null && string.IsNullOrWhiteSpace(Query))
            {
                await LoadFeaturedAsync(selected);
            }
        }

        private async Task LoadListedAsync() =>
            _scopes ??= await readScopes.ExecuteAsync<BaseDataList<DiscoverScopeV1>>(new DiscoverParametersV1(), HttpContext.RequestAborted);

        // Read a real permitted feed before first paint. The shared persisted feed read will
        // replace this single-source selection when the discovery snapshot contract is ready.
        private async Task LoadFeaturedAsync(DiscoverScopeV1? selected)
        {
            var scope = Listed.FirstOrDefault(type => (selected is null || type.Slug == selected.Slug)
                && type.Feeds.Contains(DiscoverFeedType.Trending));
            if (scope is null)
            {
                return;
            }

            try
            {
                if (scope.IsCatalog)
                {
                    var items = await catalogFeeds.ExecuteAsync<BaseDataList<DiscoverCatalogItemV1>>(
                        new ReadDiscoverCatalogFeedDataV1(scope.MediaTypeId, DiscoverFeedType.Trending, scope.MediaSubtypeId), HttpContext.RequestAborted);
                    // A random, unique choice per visit from the genuine authorized feed.
                    // The full eight-slide collection will be fed by the shared warm-snapshot reader.
                    var catalogPicks = HeroSelection.Choose(items
                        .Where(value => !string.IsNullOrWhiteSpace(value.Namespace) && !string.IsNullOrWhiteSpace(value.ExternalId))
                        .Select(value => new HeroCandidate<DiscoverCatalogItemV1>(
                            value.WorkId is { } id ? "work:" + id.ToString(CultureInfo.InvariantCulture)
                                : value.Namespace + ":" + value.ExternalId,
                            scope.Slug, DiscoverFeedType.Trending, false, value)));
                    var item = catalogPicks.FirstOrDefault()?.Content;
                    if (item is null)
                    {
                        return;
                    }

                    var href = item.WorkId is { } workId
                        ? "/Details/" + workId.ToString(CultureInfo.InvariantCulture)
                        : "/Discover/" + scope.Slug + "/" + Uri.EscapeDataString(item.Namespace) + "/" + Uri.EscapeDataString(item.ExternalId);
                    var requestable = item.WorkId is null && texts.CanRequest();
                    _featured = new MediaHero(item.Title, item.PosterUrl, null, null, requestable ? "request.create" : "media.details", href,
                        requestable ? href : null);
                    return;
                }

                var results = await feeds.ExecuteAsync<BaseDataList<DiscoverItemV1>>(
                    new ReadDiscoverFeedDataV1(scope.MediaTypeId, DiscoverFeedType.Trending), HttpContext.RequestAborted);
                var picks = HeroSelection.Choose(results.Where(item => item.WorkId is not null || item.TmdbId is not null)
                    .Select(item => new HeroCandidate<DiscoverItemV1>(
                        item.WorkId is { } id ? "work:" + id.ToString(CultureInfo.InvariantCulture)
                            : "tmdb:" + item.MediaTypeId + ":" + item.TmdbId!.Value.ToString(CultureInfo.InvariantCulture),
                        scope.Slug, DiscoverFeedType.Trending, false, item)));
                var featured = picks.FirstOrDefault()?.Content;
                if (featured is null)
                {
                    return;
                }

                var (actionKey, actionHref) = Action(featured);
                _featured = new MediaHero(featured.Title, featured.BackdropUrl ?? featured.PosterUrl, null, featured.Overview,
                    actionKey ?? "media.details", actionHref ?? DetailsHref(featured), actionKey is null ? null : DetailsHref(featured));
            }
            catch (ServiceUnavailableException)
            {
                // An optional Hero must never prevent local shelves from rendering.
            }
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
                        if (scope.IsCatalog)
                        {
                            _catalogShelves.Add((scope, feed, await catalogFeeds.ExecuteAsync<BaseDataList<DiscoverCatalogItemV1>>(new ReadDiscoverCatalogFeedDataV1(scope.MediaTypeId, feed, scope.MediaSubtypeId),
                                HttpContext.RequestAborted)));
                        }
                        else
                        {
                            _shelves.Add((scope.MediaTypeId, feed, await feeds.ExecuteAsync<BaseDataList<DiscoverItemV1>>(new ReadDiscoverFeedDataV1(scope.MediaTypeId, feed), HttpContext.RequestAborted)));
                        }
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
            var card = new MediaCard(assets, item.Title, DetailsHref(item), item.PosterUrl, item.ReleaseYear?.ToString(CultureInfo.InvariantCulture), "media.state." + item.MediaStateTypeId, actionKey, actionHref);
            if (item.WorkId is { } workId) card.Bookmark(new MediaBookmark(assets, workId));
            return card;
        }

        // A known title opens its Work; any other opens its request.
        private IUiComponent CatalogCard(DiscoverScopeV1 scope, DiscoverCatalogItemV1 item)
        {
            var meta = string.Join(" · ", new[] { item.ReleaseYear?.ToString(CultureInfo.InvariantCulture), item.Author }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (item.WorkId is { } workId)
            {
                return new MediaCard(assets, item.Title, "/Details/" + workId.ToString(CultureInfo.InvariantCulture), item.PosterUrl, meta, "discover.known")
                    .Bookmark(new MediaBookmark(assets, workId));
            }

            var href = "/Discover/" + scope.Slug + "/" + Uri.EscapeDataString(item.Namespace) + "/" + Uri.EscapeDataString(item.ExternalId);
            return new MediaCard(assets, item.Title, href, item.PosterUrl, meta, "media.state." + MediaStateType.NotRequested, texts.CanRequest() ? "request.create" : null, texts.CanRequest() ? href : null);
        }

        // The canonical next step: use what exists, request what is missing.
        private (string? Key, string? Href) Action(DiscoverItemV1 item) => item.MediaStateTypeId switch
        {
            MediaStateType.Available when item.PlexUrl is { } plex && !texts.CanPlay() => ("media.openInPlex", plex),
            MediaStateType.Available => ("media.watch", DetailsHref(item)),
            MediaStateType.NotRequested when texts.CanRequest() => ("request.create", DetailsHref(item)),
            _ => (null, null)
        };

        private string DetailsHref(DiscoverItemV1 item) => item.WorkId is { } workId
            ? "/Details/" + workId.ToString(CultureInfo.InvariantCulture)
            : "/Discover/" + Listed.First(scope => scope.MediaTypeId == item.MediaTypeId && !scope.IsCatalog).Slug + "/" + item.TmdbId!.Value.ToString(CultureInfo.InvariantCulture);

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
