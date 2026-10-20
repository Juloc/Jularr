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
    public sealed class IndexModel(ReadDiscoverScopesV1 readScopes, ReadDiscoverShelvesV1 readShelves, SuggestDiscoverV1 suggest, ReadDiscoverFeedV1 feeds, SearchDiscoverV1 search,
        SearchDiscoverCatalogV1 catalogSearch, ReadDiscoverCatalogFeedV1 catalogFeeds, ReadContinueV1 continuation, ReadLibraryV1 library, ReadHomeLayoutV1 readLayout, UiText texts, FrontendAssets assets,
        IAntiforgery antiforgery) : JularrPage
    {
        // The media-type bar groups the scopes by what a person does with them, in this order.
        private static readonly (string LabelKey, string Icon, string[] Slugs)[] Areas =
        [
            ("discover.area.watch", "play", ["movies", "series", "anime"]),
            ("discover.area.read", "books", ["books", "manga", "lightnovels"]),
            ("discover.area.hear", "headphones", ["music"]),
            ("discover.area.play", "gamepad", ["games"])
        ];

        private const string HeroPreferences = "discover-hero";

        private readonly List<(MediaType Type, DiscoverFeedType Feed, IReadOnlyList<DiscoverItemV1> Items)> _shelves = [];
        private readonly List<(DiscoverScopeV1 Scope, DiscoverFeedType Feed, IReadOnlyList<DiscoverCatalogItemV1> Items)> _catalogShelves = [];
        private IReadOnlyList<DiscoverItemV1>? _results;
        private IReadOnlyList<(DiscoverScopeV1 Scope, DiscoverCatalogItemV1 Item)>? _catalog;
        private IReadOnlyList<ContinueItemV1> _continue = [];
        private IReadOnlyList<HeroSlide> _hero = [];
        private IReadOnlyList<LibraryItemV1> _recent = [];
        private IReadOnlyList<HomeShelfV1> _layout = [.. Enum.GetValues<HomeShelfType>().Select(type => new HomeShelfV1(type, false))];
        private bool _providerUnavailable;
        private IReadOnlyList<DiscoverScopeV1>? _scopes;
        private string? _feed;
        private string? _sort;
        private string? _availability;
        private string? _year;

        // One or more scope slugs, as a comma list or repeated; none or every listed one means all.
        [BindProperty(SupportsGet = true, Name = "type")]
        public string[]? TypeSlugs { get; set; }

        [BindProperty(SupportsGet = true, Name = "view")]
        public DiscoverFeedType? View { get; set; }

        // Unknown or empty values mean no choice, as for a reset form.
        [BindProperty(SupportsGet = true, Name = "feed")]
        public string? Feed { get => _feed; set => _feed = Known(value, "trending", "new", "top", "upcoming", "continue"); }

        [BindProperty(SupportsGet = true, Name = "sort")]
        public string? Sort { get => _sort; set => _sort = Known(value, "newest", "az"); }

        [BindProperty(SupportsGet = true, Name = "availability")]
        public string? Availability { get => _availability; set => _availability = Known(value, "local", "request"); }

        [BindProperty(SupportsGet = true, Name = "year")]
        public string? Year { get => _year; set => _year = Known(value, "2020", "old"); }

        [BindProperty(SupportsGet = true, Name = "q")]
        public string? Query { get; set; }

        [BindProperty(SupportsGet = true, Name = "p")]
        public int PageNumber { get; set; } = 1;

        // The profile's listed types and subtypes only.
        private IEnumerable<DiscoverScopeV1> Listed => _scopes ?? [];

        private HashSet<string> Requested => [.. (TypeSlugs ?? []).SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))];

        private bool IsAll
        {
            get
            {
                var chosen = Listed.Count(scope => Requested.Contains(scope.Slug));
                return chosen == 0 || chosen == Listed.Count();
            }
        }

        // The scopes shown: every listed one, or the chosen ones.
        private IReadOnlyList<DiscoverScopeV1> Chosen => [.. Listed.Where(scope => IsAll || Requested.Contains(scope.Slug))];

        // Set when exactly one scope is chosen: rankings, catalog search and the back link need a single one.
        private DiscoverScopeV1? Selected => !IsAll && Chosen.Count == 1 ? Chosen[0] : null;

        private DiscoverFeedType? FeedType => Feed switch
        {
            "trending" => DiscoverFeedType.Trending,
            "top" => DiscoverFeedType.Top,
            "new" => DiscoverFeedType.New,
            "upcoming" => DiscoverFeedType.Upcoming,
            _ => null
        };

        private bool ContinueOnly => Feed == "continue";

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
            page.Asset("Pages/User/Discover/Controls.entry.ts");
            page.Asset("Framework/Components/Drawer.entry.ts");
            page.Asset("Framework/Components/ChoiceSelect.entry.ts");

            // One server-rendered result region.
            var body = new Page();
            DefineContent(body);
            body.Content.Add(new P().Class("discover-feedback").Attribute("role", "status"));
            page.Content.Add(new Div([.. body.Content]).Attribute("id", "discover-results").Class("discover-results")
                .Attribute("data-load-error", texts["discover.loadFailed"]).Attribute("data-provider-unavailable", _providerUnavailable ? "true" : "false"));
        }

        private void DefineContent(Page page)
        {
            var selected = Selected;
            var searching = !string.IsNullOrWhiteSpace(Query);

            // One row of media types; a ranking opened from its shelf names itself and leads back.
            page.Content.Add(MediaTypeBar());
            if (selected is not null && View is { } view && !searching)
            {
                page.Content.Add(new Div(new A(new Text("common.back")).Class("button").Attribute("href", Address(Current with { View = null, Page = 1 })),
                    new H2(new Text("discover.feed." + view))).Class("discover-view-heading"));
            }

            if (_providerUnavailable && (_results is not null || _catalog is not null))
            {
                page.Content.Add(new P(new Text("discover.providerUnavailable")).Attribute("role", "status"));
            }

            // Catalog titles become Works only through a request.
            if (_catalog is not null || _results is not null)
            {
                var cards = new List<IUiComponent>();
                cards.AddRange(Filtered(_results ?? []).Select(Card));
                cards.AddRange(Filtered(_catalog ?? []).Select(entry => CatalogCard(entry.Scope, entry.Item)));
                if (cards.Count == 0)
                {
                    page.Content.Add(new P(new Text(searching ? "discover.noResults" : "discover.empty")).Attribute("role", "status"));
                    if (PageNumber > 1) page.Content.Add(new A(new Text("common.previous")).Class("button").Attribute("href", Address(Current with { Page = PageNumber - 1 })));
                    return;
                }

                page.Content.Add(new Div([.. cards]).Class("media-grid"));

                // Catalogs answer in one read; provider results page.
                if (_results is { } results && _catalog is null)
                {
                    var paging = new List<IUiComponent>();
                    if (PageNumber > 1) paging.Add(new A(new Text("common.previous")).Class("button").Attribute("href", Address(Current with { Page = PageNumber - 1 })));
                    if (!_providerUnavailable && PageNumber < 500 && results.Count >= 20)
                    {
                        paging.Add(new A(new Text("common.next")).Class("button").Attribute("data-discover-next", "true").Attribute("data-navigation", "full")
                            .Attribute("href", Address(Current with { Page = PageNumber + 1 })));
                    }

                    page.Content.Add(new Nav([.. paging]).Class("discover-paging").Attribute("aria-label", texts["discover.pages"]));
                }

                return;
            }

            // The highlights are part of the initial response, never a late insertion above the shelves.
            if (_hero.Count > 0)
            {
                page.Content.Add(new HeroCarousel(assets, _hero, HeroPreferences));
            }

            page.Content.Add(Toolbar());

            // Watching, listening and reading continue on their own shelves, in the profile's order.
            var continuing = _continue.Where(item => MediaTypeChosen(item.MediaTypeId)).ToList();
            var canPlay = texts.CanPlay();
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            foreach (var shelf in _layout.Where(shelf => !shelf.IsHidden))
            {
                // A feed shows only its own lists: Continue the continue shelves, the others the recommendations.
                var isContinue = shelf.HomeShelfTypeId is HomeShelfType.ContinueWatching or HomeShelfType.ContinueListening or HomeShelfType.ContinueReading;
                var isRecommendations = shelf.HomeShelfTypeId is not (HomeShelfType.ContinueWatching or HomeShelfType.ContinueListening or HomeShelfType.ContinueReading or HomeShelfType.RecentlyAdded);
                if (ContinueOnly ? !isContinue : Feed is not null && !isRecommendations)
                {
                    continue;
                }

                page.Content.Add(shelf.HomeShelfTypeId switch
                {
                    HomeShelfType.ContinueWatching => ContinueShelf("discover.continueWatching", canPlay ? continuing.Where(item => item.MediaAssetTypeId == MediaAssetType.Video) : []),
                    HomeShelfType.ContinueListening => ContinueShelf("discover.continueListening", canPlay ? continuing.Where(item => item.MediaAssetTypeId == MediaAssetType.Audio) : []),
                    HomeShelfType.ContinueReading => ContinueShelf("media.continueReading",
                        texts.CanRead() ? continuing.Where(item => ReadableMedia.IsReadable(item.MediaAssetTypeId)) : []),
                    HomeShelfType.RecentlyAdded => new MediaShelf(assets, "discover.recentlyAdded", "/Library", [.. _recent.Where(item => MediaTypeChosen(item.MediaTypeId)).Select(item => MediaCard.For(assets, item)
                        .Bookmark(new MediaBookmark(assets, item.Id, item.IsWatchlisted, Request.Path + Request.QueryString, csrf)))]),
                    _ => Recommendations()
                });
            }
        }

        // All, then a capsule per area whose types are chosen one by one; the tools open the filter and the highlight settings.
        private IUiComponent MediaTypeBar()
        {
            var listed = Listed.Select(scope => scope.Slug).ToList();
            var chosen = Chosen.Select(scope => scope.Slug).ToHashSet();

            // Chosen entries, or none for all of them.
            string Types(IEnumerable<string> slugs)
            {
                var wanted = listed.Where(slug => slugs.Contains(slug)).ToList();
                return Address(Current with { Types = wanted.Count == listed.Count ? [] : wanted, View = null, Page = 1 });
            }

            var groups = new List<CapsuleGroup>();
            foreach (var (labelKey, icon, slugs) in Areas)
            {
                var members = slugs.Where(listed.Contains).ToList();
                if (members.Count == 0) continue;

                var shown = members.Count(chosen.Contains);
                var solo = !IsAll && chosen.Count == members.Count && shown == members.Count;
                var state = IsAll || shown == 0 ? CapsuleGroupState.Idle : solo ? CapsuleGroupState.Solo : shown < members.Count ? CapsuleGroupState.Partial : CapsuleGroupState.Included;
                groups.Add(new CapsuleGroup(labelKey, icon, Types(solo ? listed : members), state, [.. members.Select(slug =>
                    new CapsuleItem("discover.type." + slug, Types(IsAll ? [slug] : chosen.Contains(slug) ? chosen.Except([slug]) : chosen.Append(slug)), !IsAll && chosen.Contains(slug), !IsAll && !chosen.Contains(slug)))]));
            }

            var narrowed = (Availability is not null ? 1 : 0) + (Year is not null ? 1 : 0);
            var filter = new List<IUiComponent> { new Icon("filter", assets) };
            if (narrowed > 0) filter.Add(new Span(new ValueText(narrowed.ToString(CultureInfo.CurrentCulture))).Class("capsule-bar__badge"));
            var tools = new List<IUiComponent>
            {
                Drawer.Opener(new Button(new Icon("sliders", assets)).IconOnly(texts["discover.view.open"]).Class("button button--icon capsule-bar__tool"), "discover-view"),
                Drawer.Opener(new Button([.. filter]).IconOnly(texts["discover.filter.open"]).Class("button button--icon capsule-bar__tool"), "discover-filter"),
                ViewDrawer(),
                FilterDrawer()
            };
            return new CapsuleBar(assets, "discover.mediaTypes", "discover.all", Types([]), IsAll, groups, tools);
        }

        // Feed lists, how they are ordered and whether the shelves wrap into a grid.
        private IUiComponent Toolbar()
        {
            var feedTabs = new List<NavigationTab> { new("discover.all", Address(Current with { Feed = null, Page = 1 }), Feed is null) };
            var supported = Chosen.SelectMany(scope => scope.Feeds).ToHashSet();
            foreach (var (feed, slug) in new[] { (DiscoverFeedType.Trending, "trending"), (DiscoverFeedType.New, "new"), (DiscoverFeedType.Top, "top"), (DiscoverFeedType.Upcoming, "upcoming") })
            {
                if (supported.Contains(feed)) feedTabs.Add(new NavigationTab("discover.feed." + feed, Address(Current with { Feed = slug, Page = 1 }), Feed == slug));
            }

            if (_continue.Count > 0) feedTabs.Add(new NavigationTab("discover.feed.Continue", Address(Current with { Feed = "continue", Page = 1 }), ContinueOnly));

            // The sort keeps every other choice of the address as hidden fields.
            var keep = new Dictionary<string, string>();
            if (TypeParameter(Current.Types) is { } types) keep["type"] = types;
            if (Feed is not null) keep["feed"] = Feed;
            if (Availability is not null) keep["availability"] = Availability;
            if (Year is not null) keep["year"] = Year;
            return new Div(new NavigationTabs(feedTabs, "discover.feeds", "chips"),
                new Div(new ChoiceSelect("/", "sort", "discover.sort", [new("discover.sort.ranking", "", Sort is null), new("discover.sort.newest", "newest", Sort == "newest"),
                    new("discover.sort.az", "az", Sort == "az")], keep),
                    new Button(new Icon("grid", assets), new Icon("list", assets)).IconOnly(texts["discover.view.toggle"]).Class("button button--icon discover-toolbar__layout")
                        .Attribute("data-discover-layout", "")).Class("discover-toolbar__settings")).Class("discover-toolbar");
        }

        // Which highlights show and for how long is the device's own choice.
        private IUiComponent ViewDrawer() => new Drawer(assets, "discover-view", "discover.view.title",
        [
            new ChoiceGroup("discover.hero.perType", "hero-perType", [new("1", false, "discover.hero.perType.1"), new("2", true, "discover.hero.perType.2"), new("3", false, "discover.hero.perType.3")],
                attributes: new Dictionary<string, string> { ["data-hero-pref"] = "perType" }),
            new ChoiceGroup("discover.hero.max", "hero-max", [new("8", false), new("19", true), new("27", false), new("0", false, "discover.all")],
                attributes: new Dictionary<string, string> { ["data-hero-pref"] = "max" }),
            new Div(HeroToggle("discover.hero.continue", "withContinue"), HeroToggle("discover.hero.auto", "auto")).Class("discover-switches")
        ],
        [
            new Button(new Text("discover.reset")).Attribute("data-hero-reset", ""),
            new Button(new Text("common.close")).Primary().Attribute("data-drawer-close", "")
        ]);

        private static IUiComponent HeroToggle(string labelKey, string preference)
        {
            var toggle = new Toggle(labelKey);
            toggle.Control.Attribute("data-hero-pref", preference);
            return toggle;
        }

        // Types per area, availability and release year; one GET form, so the address holds the choice.
        private IUiComponent FilterDrawer()
        {
            var chosen = Chosen.Select(scope => scope.Slug).ToHashSet();
            var body = new List<IUiComponent>();
            foreach (var (labelKey, _, slugs) in Areas)
            {
                var members = slugs.Where(slug => Listed.Any(scope => scope.Slug == slug)).ToList();
                if (members.Count == 0) continue;

                var all = members.All(chosen.Contains);
                body.Add(new ChoiceGroup(labelKey, "type", [.. members.Select(slug => new ChoicePick(slug, chosen.Contains(slug), "discover.type." + slug))], true,
                    [new Button(new Text(all ? "discover.clearAll" : "discover.pickAll")).Class("button button--compact discover-pick").Attribute("data-discover-pick", "")
                        .Attribute("data-pick-label", texts["discover.pickAll"]).Attribute("data-clear-label", texts["discover.clearAll"])]));
            }

            body.Add(new ChoiceGroup("discover.filter.availability", "availability", [new("", Availability is null, "discover.all"), new("local", Availability == "local", "discover.known"),
                new("request", Availability == "request", "discover.availability.request")]));
            body.Add(new ChoiceGroup("discover.filter.year", "year", [new("", Year is null, "discover.all"), new("2020", Year == "2020", "discover.year.since2020"),
                new("old", Year == "old", "discover.year.before2020")]));
            if (Feed is not null) body.Add(new Input().Attribute("type", "hidden").Attribute("name", "feed").Attribute("value", Feed));
            if (Sort is not null) body.Add(new Input().Attribute("type", "hidden").Attribute("name", "sort").Attribute("value", Sort));
            if (!string.IsNullOrWhiteSpace(Query)) body.Add(new Input().Attribute("type", "hidden").Attribute("name", "q").Attribute("value", Query.Trim()));

            return new Drawer(assets, "discover-filter", "discover.filter.title", body,
            [
                new A(new Text("discover.reset")).Class("button").Attribute("href", "/"),
                new Button(new Text("common.apply")).Primary().Submit()
            ], "/");
        }

        private IUiComponent Recommendations()
        {
            var recommendations = new Page();
            DefineRecommendations(recommendations);
            if (_shelves.Count == 0 && _catalogShelves.Count == 0 && !_providerUnavailable)
            {
                var address = Address(Current with { View = null, Page = 1 });
                recommendations.Content.Add(new A(new Text("discover.loadRecommendations")).Class("button").Attribute("data-discover-recommendations", "true")
                    .Attribute("href", address + (address.Contains('?') ? "&" : "?") + "handler=Recommendations"));
            }

            return new Div([.. recommendations.Content]).Attribute("id", "discover-recommendations");
        }

        private MediaShelf ContinueShelf(string titleKey, IEnumerable<ContinueItemV1> items) => new(assets, titleKey, null, [.. items.Select(item =>
        {
            var readable = ReadableMedia.IsReadable(item.MediaAssetTypeId);
            var card = new MediaCard(assets, item.Title, "/Details/" + item.WorkId.ToString(CultureInfo.InvariantCulture), item.PosterUrl)
                .Facts(item.ReleaseYear, MediaTypeLabel.Key(item.MediaTypeId, item.MediaSubtypeId), null).Resume(ContinueDetail.Format(key => texts[key], item))
                .Action("media.continue", (readable ? "/Read/" : "/Player/") + item.MediaAssetId.ToString(CultureInfo.InvariantCulture))
                .Bookmark(new MediaBookmark(assets, item.WorkId));
            return readable ? card.ContinueReading(item.Progression) : card.Continue(item.PositionMs, item.DurationMs);
        })]);

        private void DefineRecommendations(Page page)
        {
            var selected = Selected;
            if (_providerUnavailable) page.Content.Add(new P(new Text("discover.providerUnavailable")).Attribute("role", "status"));

            // Types in the order of the media-type bar.
            foreach (var scope in Chosen)
            {
                var shelves = scope.IsCatalog
                    ? _catalogShelves.Where(shelf => shelf.Scope == scope).Select(shelf => (shelf.Feed, Cards: Filtered(shelf.Items.Select(item => (scope, item))).Select(entry => CatalogCard(scope, entry.Item)).ToList()))
                    : _shelves.Where(shelf => shelf.Type == scope.MediaTypeId).Select(shelf => (shelf.Feed, Cards: Filtered(shelf.Items).Select(Card).ToList()));
                var shown = shelves.Where(shelf => FeedType is null || shelf.Feed == FeedType).ToList();
                if (selected is null && shown.Any(shelf => shelf.Cards.Count > 0))
                {
                    page.Content.Add(new H2(new Text("discover.type." + scope.Slug)).Class("discover-media-heading"));
                }

                foreach (var (feed, cards) in shown)
                {
                    page.Content.Add(new MediaShelf(assets, "discover.feed." + feed, Address(Current with { Types = [scope.Slug], Feed = null, View = feed, Page = 1 }), [.. cards]));
                }
            }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();

            // The filter form sends repeated and empty values; the address is shown in its shortest form.
            var canonical = Address(Current);
            if (Listed.Any() && Request.Query["handler"].Count == 0 && Request.Path.Value + Request.QueryString.Value != canonical)
            {
                return Redirect(canonical);
            }
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

        // Titles Discover keeps, while typing; a known title opens its Work.
        public async Task<IActionResult> OnGetSuggestionsAsync()
        {
            if (!texts.CanReadMedia()) return Forbid();
            if (string.IsNullOrWhiteSpace(Query)) return Partial("_UiComponent", new Div().Class("discover-suggestions__list"));

            await LoadListedAsync();
            var found = await suggest.ExecuteAsync<BaseDataList<DiscoverSuggestionV1>>(new SuggestDiscoverDataV1(Query, Selected?.Slug), HttpContext.RequestAborted);
            return Partial("_UiComponent", new Div([.. found.Where(item => Chosen.Any(scope => scope.Slug == item.ScopeSlug)).Select(Suggestion)]).Class("discover-suggestions__list")
                .Attribute("aria-label", texts["discover.suggestions"]));
        }

        private IUiComponent Suggestion(DiscoverSuggestionV1 item)
        {
            var scope = Listed.First(scope => scope.Slug == item.ScopeSlug);
            var href = item.WorkId is { } workId ? "/Details/" + workId.ToString(CultureInfo.InvariantCulture)
                : scope.IsCatalog ? "/Discover/" + scope.Slug + "/" + Uri.EscapeDataString(item.Namespace) + "/" + Uri.EscapeDataString(item.ExternalId)
                : "/Discover/" + scope.Slug + "/" + Uri.EscapeDataString(item.ExternalId);
            var meta = texts["discover.type." + scope.Slug] + (item.ReleaseYear is { } year ? " · " + year.ToString(CultureInfo.InvariantCulture) : "");
            IUiComponent poster = item.PosterUrl is { } url
                ? new Img().Class("discover-suggestion__poster").Attribute("src", url).Attribute("alt", "").Attribute("loading", "lazy")
                : new Span().Class("discover-suggestion__poster");
            return new A(poster, new Span(new Strong(new ValueText(item.Title)), new Span(new ValueText(meta))).Class("discover-suggestion__text"),
                new Span(new Icon("chevron-left", assets, 16)).Class("discover-suggestion__arrow"))
                .Class("discover-suggestion").Attribute("href", href);
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
                if (!string.IsNullOrWhiteSpace(Query))
                {
                    await SearchAsync(selected);
                    return;
                }

                if (selected is { IsCatalog: true } && View is { } catalogView && selected.Feeds.Contains(catalogView))
                {
                    _catalog = [.. (await catalogFeeds.ExecuteAsync<BaseDataList<DiscoverCatalogItemV1>>(new ReadDiscoverCatalogFeedDataV1(selected.MediaTypeId, catalogView, selected.MediaSubtypeId),
                        HttpContext.RequestAborted)).Select(item => (selected, item))];
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
                // One chosen type narrows the read; several are filtered when shown.
                var recentType = Chosen.Select(scope => scope.MediaTypeId).Distinct().ToList() is [var only] ? only : (MediaType?)null;
                _recent = (await library.ExecuteAsync<PageResult<LibraryItemV1>>(new ReadLibraryParametersV1(recentType, pageSize: 20), HttpContext.RequestAborted)).Items;
            }

            if (View is null && string.IsNullOrWhiteSpace(Query))
            {
                await LoadHeroAsync(selected);
            }
        }

        // One scope searches its own catalog or TMDB; several search each catalog chosen and TMDB's answers for their types.
        private async Task SearchAsync(DiscoverScopeV1? selected)
        {
            if (selected is { IsCatalog: true })
            {
                _catalog = [.. (await catalogSearch.ExecuteAsync<BaseDataList<DiscoverCatalogItemV1>>(new SearchDiscoverCatalogDataV1(selected.MediaTypeId, Query!, selected.MediaSubtypeId),
                    HttpContext.RequestAborted)).Select(item => (selected, item))];
                return;
            }

            if (selected is not null || IsAll)
            {
                _results = await search.ExecuteAsync<BaseDataList<DiscoverItemV1>>(new SearchDiscoverDataV1(Query!, selected?.MediaTypeId, PageNumber), HttpContext.RequestAborted);
                return;
            }

            var found = new List<(DiscoverScopeV1 Scope, DiscoverCatalogItemV1 Item)>();
            foreach (var scope in Chosen.Where(scope => scope.IsCatalog))
            {
                found.AddRange((await catalogSearch.ExecuteAsync<BaseDataList<DiscoverCatalogItemV1>>(new SearchDiscoverCatalogDataV1(scope.MediaTypeId, Query!, scope.MediaSubtypeId),
                    HttpContext.RequestAborted)).Select(item => (scope, item)));
            }

            if (Chosen.Any(scope => !scope.IsCatalog))
            {
                _results = [.. (await search.ExecuteAsync<BaseDataList<DiscoverItemV1>>(new SearchDiscoverDataV1(Query!, null, PageNumber), HttpContext.RequestAborted))
                    .Where(item => Chosen.Any(scope => !scope.IsCatalog && scope.MediaTypeId == item.MediaTypeId))];
            }

            _catalog = found.Count > 0 || _results is null ? found : null;
        }

        private async Task LoadListedAsync() =>
            _scopes ??= await readScopes.ExecuteAsync<BaseDataList<DiscoverScopeV1>>(new DiscoverParametersV1(), HttpContext.RequestAborted);

        // Continued titles first, then each chosen type's best titles of the feed, one rank after the other across the types.
        // Read from the whole catalogs; one source of the first type answers when none was kept.
        private async Task LoadHeroAsync(DiscoverScopeV1? selected)
        {
            var slides = new List<HeroSlide>();
            if (Feed is null or "continue")
            {
                foreach (var item in _continue.Where(item => MediaTypeChosen(item.MediaTypeId)).Take(2))
                {
                    var scope = Chosen.FirstOrDefault(scope => scope.MediaTypeId == item.MediaTypeId);
                    var readable = ReadableMedia.IsReadable(item.MediaAssetTypeId);
                    slides.Add(new HeroSlide(item.Title, null, item.PosterUrl, Overline(scope, "discover.feed.Continue"), null, "media.continue",
                        (readable ? "/Read/" : "/Player/") + item.MediaAssetId.ToString(CultureInfo.InvariantCulture), "/Details/" + item.WorkId.ToString(CultureInfo.InvariantCulture),
                        scope?.Slug ?? "", 0, true));
                }
            }

            var feed = Feed is null ? DiscoverFeedType.Trending : FeedType;
            if (feed is { } heroFeed)
            {
                var kept = await readShelves.ExecuteAsync<BaseDataList<DiscoverShelfV1>>(new ReadDiscoverShelvesDataV1(selected?.Slug), HttpContext.RequestAborted);
                var perScope = new List<List<HeroSlide>>();
                foreach (var scope in Chosen.Where(scope => scope.Feeds.Contains(heroFeed)))
                {
                    if (kept.FirstOrDefault(shelf => shelf.ScopeSlug == scope.Slug && shelf.DiscoverFeedTypeId == heroFeed) is not { } shelf) continue;

                    // Titles with artwork lead; the others fill up.
                    var own = new List<HeroSlide>();
                    var items = scope.IsCatalog
                        ? Filtered(shelf.CatalogItems.Select(item => (scope, item))).Select(entry => entry.Item).OrderBy(item => item.PosterUrl is null).Take(3).Select((item, rank) => CatalogSlide(scope, item, heroFeed, rank))
                        : Filtered(shelf.Items).OrderBy(item => item.BackdropUrl is null && item.PosterUrl is null).Take(3).Select((item, rank) => ItemSlide(scope, item, heroFeed, rank));
                    own.AddRange(items);
                    if (own.Count > 0) perScope.Add(own);
                }

                for (var rank = 0; rank < 3; rank++)
                {
                    slides.AddRange(perScope.Where(own => own.Count > rank).Select(own => own[rank]));
                }

                if (!slides.Any(slide => !slide.IsContinue) && heroFeed == DiscoverFeedType.Trending)
                {
                    await AddFallbackSlideAsync(slides, selected);
                }
            }

            _hero = slides;
        }

        // Without a catalog read whole, the first type that has trending titles asks its provider for one.
        private async Task AddFallbackSlideAsync(List<HeroSlide> slides, DiscoverScopeV1? selected)
        {
            var scope = Chosen.FirstOrDefault(scope => scope.Feeds.Contains(DiscoverFeedType.Trending));
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
                    var item = Filtered(items.Select(value => (scope, value))).Select(entry => entry.Item).OrderBy(value => value.PosterUrl is null).FirstOrDefault();
                    if (item is not null) slides.Add(CatalogSlide(scope, item, DiscoverFeedType.Trending, 0));
                    return;
                }

                var results = await feeds.ExecuteAsync<BaseDataList<DiscoverItemV1>>(new ReadDiscoverFeedDataV1(scope.MediaTypeId, DiscoverFeedType.Trending), HttpContext.RequestAborted);
                var featured = Filtered(results).OrderBy(value => value.BackdropUrl is null && value.PosterUrl is null).FirstOrDefault();
                if (featured is not null) slides.Add(ItemSlide(scope, featured, DiscoverFeedType.Trending, 0));
            }
            catch (ServiceUnavailableException)
            {
                // Optional highlights must never prevent local shelves from rendering.
            }
        }

        private string Overline(DiscoverScopeV1? scope, string feedKey) => scope is null ? texts[feedKey] : texts["discover.type." + scope.Slug] + " · " + texts[feedKey];

        // A known title opens its Work; any other opens its request.
        private HeroSlide CatalogSlide(DiscoverScopeV1 scope, DiscoverCatalogItemV1 item, DiscoverFeedType feed, int rank)
        {
            var href = item.WorkId is { } workId
                ? "/Details/" + workId.ToString(CultureInfo.InvariantCulture)
                : "/Discover/" + scope.Slug + "/" + Uri.EscapeDataString(item.Namespace) + "/" + Uri.EscapeDataString(item.ExternalId);
            var requestable = item.WorkId is null && texts.CanRequest();
            return new HeroSlide(item.Title, null, item.PosterUrl, Overline(scope, "discover.feed." + feed), item.Author, requestable ? "request.create" : "media.details", href,
                requestable ? href : null, scope.Slug, rank);
        }

        private HeroSlide ItemSlide(DiscoverScopeV1 scope, DiscoverItemV1 item, DiscoverFeedType feed, int rank)
        {
            var (actionKey, actionHref) = Action(item);
            return new HeroSlide(item.Title, item.BackdropUrl, item.PosterUrl, Overline(scope, "discover.feed." + feed), item.Overview, actionKey ?? "media.details", actionHref ?? DetailsHref(item),
                actionKey is null ? null : DetailsHref(item), scope.Slug, rank);
        }

        private async Task LoadRecommendationsAsync()
        {
            await LoadListedAsync();
            var selected = Selected;

            // Catalogs read whole answer all their shelves at once; the others ask their providers shelf by shelf.
            var whole = await readShelves.ExecuteAsync<BaseDataList<DiscoverShelfV1>>(new ReadDiscoverShelvesDataV1(selected?.Slug), HttpContext.RequestAborted);
            foreach (var scope in Chosen)
            {
                foreach (var feed in scope.Feeds.Where(feed => FeedType is null || feed == FeedType))
                {
                    if (whole.FirstOrDefault(shelf => shelf.ScopeSlug == scope.Slug && shelf.DiscoverFeedTypeId == feed) is { } kept)
                    {
                        if (scope.IsCatalog) _catalogShelves.Add((scope, feed, kept.CatalogItems));
                        else _shelves.Add((scope.MediaTypeId, feed, kept.Items));
                        continue;
                    }

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

        // The availability and year choices narrow what is listed; the sort orders each list.
        private IEnumerable<DiscoverItemV1> Filtered(IEnumerable<DiscoverItemV1> items)
        {
            var narrowed = items.Where(item => Fits(item.MediaStateTypeId == MediaStateType.Available, item.MediaStateTypeId == MediaStateType.NotRequested, item.ReleaseYear));
            return Sort switch
            {
                "newest" => narrowed.OrderByDescending(item => item.ReleaseYear),
                "az" => narrowed.OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase),
                _ => narrowed
            };
        }

        private IEnumerable<(DiscoverScopeV1 Scope, DiscoverCatalogItemV1 Item)> Filtered(IEnumerable<(DiscoverScopeV1 Scope, DiscoverCatalogItemV1 Item)> items)
        {
            var narrowed = items.Where(entry => Fits(entry.Item.WorkId is not null, entry.Item.WorkId is null, entry.Item.ReleaseYear));
            return Sort switch
            {
                "newest" => narrowed.OrderByDescending(entry => entry.Item.ReleaseYear),
                "az" => narrowed.OrderBy(entry => entry.Item.Title, StringComparer.CurrentCultureIgnoreCase),
                _ => narrowed
            };
        }

        private bool Fits(bool isLocal, bool canBeRequested, int? releaseYear) =>
            (Availability != "local" || isLocal) && (Availability != "request" || canBeRequested)
            && (Year != "2020" || releaseYear >= 2020) && (Year != "old" || releaseYear < 2020);

        private bool MediaTypeChosen(MediaType type) => IsAll || Chosen.Any(scope => scope.MediaTypeId == type);

        private IUiComponent Card(DiscoverItemV1 item)
        {
            var (actionKey, actionHref) = Action(item);
            var card = MediaCard.For(assets, item, DetailsHref(item));
            if (actionKey is not null && actionHref is not null) card.Action(actionKey, actionHref);
            if (item.WorkId is { } workId) card.Bookmark(new MediaBookmark(assets, workId));
            return card;
        }

        // A known title opens its Work; any other opens its request.
        private IUiComponent CatalogCard(DiscoverScopeV1 scope, DiscoverCatalogItemV1 item)
        {
            var href = item.WorkId is { } workId ? "/Details/" + workId.ToString(CultureInfo.InvariantCulture)
                : "/Discover/" + scope.Slug + "/" + Uri.EscapeDataString(item.Namespace) + "/" + Uri.EscapeDataString(item.ExternalId);
            var card = new MediaCard(assets, item.Title, href, item.PosterUrl).Facts(item.ReleaseYear, MediaTypeLabel.Key(scope.MediaTypeId, scope.MediaSubtypeId), item.Rating);
            if (item.MediaStateTypeId is { } state) card.State(state);
            if (item.WorkId is { } known) return card.Bookmark(new MediaBookmark(assets, known));
            return texts.CanRequest() ? card.Action("media.request", href) : card;
        }

        // The canonical next step: use what exists, request what is missing.
        private (string? Key, string? Href) Action(DiscoverItemV1 item) => item.MediaStateTypeId switch
        {
            MediaStateType.Available when item.PlexUrl is { } plex && !texts.CanPlay() => ("media.openInPlex", plex),
            MediaStateType.Available => ("media.watch", DetailsHref(item)),
            MediaStateType.NotRequested when texts.CanRequest() => ("media.request", DetailsHref(item)),
            _ => (null, null)
        };

        private string DetailsHref(DiscoverItemV1 item) => item.WorkId is { } workId
            ? "/Details/" + workId.ToString(CultureInfo.InvariantCulture)
            : "/Discover/" + Listed.First(scope => scope.MediaTypeId == item.MediaTypeId && !scope.IsCatalog).Slug + "/" + item.TmdbId!.Value.ToString(CultureInfo.InvariantCulture);

        // Everything the address holds; links change one part and keep the rest.
        private sealed record State(IReadOnlyList<string> Types, string? Feed, string? Sort, string? Availability, string? Year, string? Query, DiscoverFeedType? View, int Page);

        private State Current => new(IsAll ? [] : [.. Chosen.Select(scope => scope.Slug)], Feed, Sort, Availability, Year, Query?.Trim(), View, PageNumber);

        private static string? Known(string? value, params string[] allowed) => allowed.Contains(value) ? value : null;

        // Types are left out for all of them.
        private string? TypeParameter(IReadOnlyList<string> types) => types.Count == 0 || Listed.All(scope => types.Contains(scope.Slug)) ? null : string.Join(',', types);

        private string Address(State state)
        {
            var values = new List<string>();
            if (TypeParameter(state.Types) is { } types) values.Add("type=" + Uri.EscapeDataString(types).Replace("%2C", ","));
            var searching = !string.IsNullOrWhiteSpace(state.Query);
            if (state.Feed is not null && !searching && state.View is null) values.Add("feed=" + Uri.EscapeDataString(state.Feed));
            if (state.View is not null && !searching) values.Add("view=" + state.View);
            if (state.Sort is not null) values.Add("sort=" + Uri.EscapeDataString(state.Sort));
            if (state.Availability is not null) values.Add("availability=" + Uri.EscapeDataString(state.Availability));
            if (state.Year is not null) values.Add("year=" + Uri.EscapeDataString(state.Year));
            if (searching) values.Add("q=" + Uri.EscapeDataString(state.Query!));
            if (state.Page > 1) values.Add("p=" + state.Page.ToString(CultureInfo.InvariantCulture));
            return values.Count == 0 ? "/" : "/?" + string.Join('&', values);
        }
    }
}
