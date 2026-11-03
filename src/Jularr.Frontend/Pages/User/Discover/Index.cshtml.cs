using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Data.User.Home.V1;
using Jularr.Backend.Data.User.Library.V1;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.User.Discover.V1;
using Jularr.Backend.Service.User.Home.V1;
using Jularr.Backend.Service.User.Library.V1;
using Jularr.Backend.Service.User.Profile.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jularr.Frontend.Pages.User.Discover
{
    public sealed partial class IndexModel(ReadDiscoverScopesV1 readScopes, ReadDiscoverShelvesV1 readShelves, SuggestDiscoverV1 suggest, ReadDiscoverFeedV1 feeds, SearchDiscoverV1 search,
        SearchDiscoverCatalogV1 catalogSearch, ReadDiscoverCatalogFeedV1 catalogFeeds, ReadContinueV1 continuation, ReadLibraryV1 library, ReadHomeLayoutV1 readLayout, UiText texts, FrontendAssets assets,
        IAntiforgery antiforgery, ILogger<IndexModel> log, ReadWorkAvailabilityStatesV1 readAvailability, ReadDiscoverFilteredV1 filtered, ReadDiscoverFilterOptionsV1 filterOptions)
        : JularrPage
    {
        // Shelves of providers are asked together, a few at a time, and none holds the page longer than this; slower ones are logged.
        private const int ShelfConcurrency = 6;
        private static readonly TimeSpan ShelfLimit = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan HeroLimit = TimeSpan.FromSeconds(2.5);
        private static readonly TimeSpan SlowShelf = TimeSpan.FromMilliseconds(700);
        private const int SearchRowSize = 20;

        private const string HeroPreferences = "discover-hero";

        // The most highlights of one type a device can choose to show.
        private const int HeroPerType = 5;

        // The lists by their address, in the order of the feed chips; AniList's lists first.
        private static readonly (DiscoverFeedType Feed, string Slug)[] FeedSlugs =
        [
            (DiscoverFeedType.Trending, "trending"), (DiscoverFeedType.Season, "season"), (DiscoverFeedType.NextSeason, "next-season"), (DiscoverFeedType.Popular, "popular"),
            (DiscoverFeedType.Manhwa, "manhwa"), (DiscoverFeedType.Top100, "top100"), (DiscoverFeedType.Top, "top"), (DiscoverFeedType.New, "new"), (DiscoverFeedType.Upcoming, "upcoming")
        ];

        // The orders of a ranking by their address.
        private static readonly (DiscoverSort Sort, string Slug)[] SortSlugs =
        [
            (DiscoverSort.Trending, "trending"), (DiscoverSort.Popularity, "popularity"), (DiscoverSort.Score, "score"), (DiscoverSort.Favourites, "favourites"),
            (DiscoverSort.Added, "added"), (DiscoverSort.Released, "released"), (DiscoverSort.Title, "title")
        ];

        // Narō's catalog name: its titles are web novels, not published light novels.
        private const string WebNovelSource = "syosetu";

        private readonly List<(MediaType Type, DiscoverFeedType Feed, IReadOnlyList<DiscoverItemV1> Items)> _shelves = [];
        private readonly List<(DiscoverScopeV1 Scope, DiscoverFeedType Feed, IReadOnlyList<DiscoverCatalogItemV1> Items)> _catalogShelves = [];
        private IReadOnlyList<DiscoverItemV1>? _results;
        private IReadOnlyList<(DiscoverScopeV1 Scope, DiscoverCatalogItemV1 Item)>? _catalog;
        private IReadOnlyList<ContinueItemV1> _continue = [];
        private string? _csrf;
        private IReadOnlyList<HeroSlide> _hero = [];
        private IReadOnlyList<LibraryItemV1> _recent = [];
        private readonly Dictionary<long, WorkAvailabilityStateV1> _availabilityByWork = [];
        private IReadOnlyList<HomeShelfV1> _layout = [.. Enum.GetValues<HomeShelfType>().Select(type => new HomeShelfV1(type, false))];
        private bool _providerUnavailable;
        private bool _searchIncomplete;
        private IReadOnlyList<DiscoverSuggestionV1> _known = [];
        private string _providerNotice = "discover.providerUnavailable";
        private IReadOnlyList<DiscoverScopeV1>? _scopes;
        private string? _feed;
        private string? _sort;
        private string? _availability;

        // One or more scope slugs, as a comma list or repeated; none or every listed one means all.
        [BindProperty(SupportsGet = true, Name = "type")]
        public string[]? TypeSlugs { get; set; }

        [BindProperty(SupportsGet = true, Name = "view")]
        public DiscoverFeedType? View { get; set; }

        // Unknown or empty values mean no choice, as for a reset form.
        [BindProperty(SupportsGet = true, Name = "feed")]
        public string? Feed { get => _feed; set => _feed = Known(value, [.. FeedSlugs.Select(feed => feed.Slug), "continue"]); }

        // Another order than the ranking's own; only a ranking's grid has one.
        [BindProperty(SupportsGet = true, Name = "sort")]
        public string? Sort { get => _sort; set => _sort = Known(value, [.. SortSlugs.Select(sort => sort.Slug)]); }

        [BindProperty(SupportsGet = true, Name = "availability")]
        public string? Availability { get => _availability; set => _availability = Known(value, "local", "request"); }

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

        private DiscoverFeedType? FeedType => FeedSlugs.Where(feed => feed.Slug == Feed).Select(feed => (DiscoverFeedType?)feed.Feed).FirstOrDefault();

        // The order the open ranking is read in, when its scope offers it and it is not the ranking's own.
        private DiscoverSort? ChosenSort => Selected is { } scope && View is { } view && SortSlugs.Where(sort => sort.Slug == Sort).Select(sort => (DiscoverSort?)sort.Sort).FirstOrDefault() is { } chosen
            && scope.Sorts.Contains(chosen) && chosen != OwnSort(scope, view) ? chosen : null;

        private static DiscoverSort? OwnSort(DiscoverScopeV1 scope, DiscoverFeedType feed) => Array.IndexOf(scope.Feeds, feed) is >= 0 and var index ? scope.FeedSorts[index] : null;

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
            page.Asset("Pages/User/Discover/FilterRow.entry.ts");
            page.Asset("Pages/User/Discover/FilterRow.entry.scss");
            page.Asset("Framework/Components/Drawer.entry.ts");
            page.Asset("Framework/Components/LiveProgress.entry.ts");
            page.Asset("Framework/Components/ChoiceSelect.entry.ts");
            page.Asset("Framework/Components/LiveSearch.entry.ts");

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

            // One row of media types and the filters under it; a ranking opened from its shelf names itself and leads back.
            page.Content.Add(TypeBar());
            if (FilterRow() is { } filters) page.Content.Add(filters);
            if (selected is not null && View is { } view && !searching)
            {
                var heading = new List<IUiComponent>
                {
                    new A(new Text("common.back")).Class("button").Attribute("href", Address(Current with { View = null, Sort = null, Page = 1 })),
                    new H2(new Text("discover.feed." + view))
                };
                if (selected.Sorts.Length > 0) heading.Add(SortSelect(selected, view));
                page.Content.Add(new Div([.. heading]).Class("discover-view-heading"));
            }

            if (_providerUnavailable && (_results is not null || _catalog is not null))
            {
                page.Content.Add(new P(new Text(_providerNotice)).Attribute("role", "status"));
            }

            // A search of several types shows each type as its own row.
            if (searching && selected is null)
            {
                if (_searchIncomplete) page.Content.Add(new P(new Text("discover.providerUnavailable")).Attribute("role", "status"));
                var rows = SearchRows();
                if (rows.Count == 0) page.Content.Add(new P(new Text("discover.noResults")).Attribute("role", "status"));
                page.Content.AddRange(rows);
                return;
            }

            // Every result is a Work and opens its page; filtered titles keep the highlights above them.
            if (_catalog is not null || _results is not null)
            {
                if (Filtering && !searching && PageNumber == 1)
                {
                    page.Content.Add(new HeroCarousel(assets, _hero, HeroPreferences, "discover.hero.empty"));
                    if (FilterHeading() is { } heading) page.Content.Add(heading);
                }

                var cards = new List<IUiComponent>();
                cards.AddRange(Filtered(_results ?? []).Select(Card));
                cards.AddRange(Filtered(_catalog ?? []).Select(entry => CatalogCard(entry.Scope, entry.Item)));
                // A ranking pages on while its provider answers, a page the filter empties included, and says where it ends; a search pages while its page is full.
                var browsing = (View is not null || Filtering) && !searching;
                if (cards.Count == 0 && (PageNumber == 1 || !browsing))
                {
                    page.Content.Add(new P(new Text(searching ? "discover.noResults" : "discover.empty")).Attribute("role", "status"));
                    if (PageNumber > 1) page.Content.Add(new A(new Text("common.previous")).Class("button").Attribute("href", Address(Current with { Page = PageNumber - 1 })));
                    return;
                }

                page.Content.Add(new Div([.. cards]).Class("media-grid"));

                var answered = (_results?.Count ?? 0) + (_catalog?.Count ?? 0);
                if (browsing || _catalog is null)
                {
                    var paging = new List<IUiComponent>();
                    if (PageNumber > 1) paging.Add(new A(new Text("common.previous")).Class("button").Attribute("href", Address(Current with { Page = PageNumber - 1 })));
                    if (!_providerUnavailable && PageNumber < 500 && (browsing ? answered > 0 : answered >= 20))
                    {
                        paging.Add(new A(new Text("list.loadMore")).Class("button").Attribute("data-discover-next", "true").Attribute("data-navigation", "full")
                            .Attribute("data-retry-label", texts["discover.retry"]).Attribute("href", Address(Current with { Page = PageNumber + 1 })));
                    }
                    else if (browsing && !_providerUnavailable)
                    {
                        paging.Add(new P(new Text("discover.end")).Class("discover-end").Attribute("role", "status"));
                    }

                    var nav = new Nav([.. paging]).Class("discover-paging").Attribute("aria-label", texts["discover.pages"]);
                    if (paging.Count > 0) nav.Attribute("data-automatic", "");
                    page.Content.Add(nav);
                }

                return;
            }

            // The highlights are part of the initial response, never a late insertion above the shelves; they keep their place when nothing is highlighted.
            page.Content.Add(new HeroCarousel(assets, _hero, HeroPreferences, "discover.hero.empty"));

            page.Content.Add(Toolbar());
            if (texts.CanRequest()) page.Content.Add(MediaAvailability.RequestTemplates(assets));

            // Watching, listening and reading continue on their own shelves, in the profile's order.
            var continuing = _continue.Where(item => MediaTypeChosen(item.MediaTypeId, item.MediaSubtypeId)).ToList();
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
                    HomeShelfType.RecentlyAdded => new MediaShelf(assets, "discover.recentlyAdded", "/Library", [.. _recent.Where(item => MediaTypeChosen(item.MediaTypeId, item.MediaSubtypeId)).Select(item => MediaCard.For(assets, item)
                        .Bookmark(new MediaBookmark(assets, item.Id, item.IsWatchlisted, Request.Path + Request.QueryString, csrf)))]),
                    _ => Recommendations()
                });
            }
        }

        // The shared media-type bar; the tools open the filter and the highlight settings.
        private IUiComponent TypeBar()
        {
            var narrowed = (Availability is not null ? 1 : 0) + FilterCount;
            var filter = new List<IUiComponent> { new Icon("filter", assets, 16) };
            if (narrowed > 0) filter.Add(new Span(new ValueText(narrowed.ToString(CultureInfo.CurrentCulture))).Class("capsule-bar__badge"));
            // The filter row's More opens the filter when the row is there.
            var tools = new List<IUiComponent>
            {
                Drawer.Opener(new Button(new Icon("sliders", assets, 16)).IconOnly(texts["discover.view.open"]).Class("button button--icon capsule-bar__tool"), "discover-panel", "view")
            };
            if (Offered is null || !string.IsNullOrWhiteSpace(Query))
            {
                tools.Add(Drawer.Opener(new Button([.. filter]).IconOnly(texts["discover.filter.open"]).Class("button button--icon capsule-bar__tool"), "discover-panel", "filter"));
            }

            tools.Add(new Drawer(assets, "discover-panel", [FilterPane(), ViewPane()]));
            return new MediaTypeBar(assets, [.. Listed.Select(scope => scope.Slug)], [.. Chosen.Select(scope => scope.Slug)], "discover.type.", types => Address(Current with { Types = types, View = null, Sort = null, Filters = false, Page = 1 }), tools);
        }

        // Feed lists and whether the shelves wrap into a grid; each shelf keeps its list's order.
        private IUiComponent Toolbar()
        {
            var feedTabs = FeedOptions(Chosen).Select(option => new NavigationTab(option.LabelKey, Address(Current with { Feed = option.Slug, Page = 1 }), Feed == option.Slug)).ToList();
            return new Div(new NavigationTabs(feedTabs, "discover.feeds", "chips"),
                new Div(new Button(new Icon("grid", assets, 18), new Icon("list", assets, 18)).IconOnly(texts["discover.view.toggle"]).Class("button button--icon discover-toolbar__layout")
                    .Attribute("data-discover-layout", "")).Class("discover-toolbar__settings")).Class("discover-toolbar");
        }

        // The orders a ranking's scope offers; its own order is the empty choice, so its address stays short. Every other choice stays as a hidden field.
        private ChoiceSelect SortSelect(DiscoverScopeV1 scope, DiscoverFeedType view)
        {
            var own = OwnSort(scope, view);
            var shown = ChosenSort ?? own;
            var keep = new Dictionary<string, string> { ["type"] = scope.Slug, ["view"] = view.ToString() };
            if (Availability is not null) keep["availability"] = Availability;
            KeepFilters(keep);
            return new ChoiceSelect("/", "sort", "discover.sort", [.. SortSlugs.Where(sort => scope.Sorts.Contains(sort.Sort))
                .Select(sort => new ChoiceOption("discover.sort." + sort.Sort, sort.Sort == own ? "" : sort.Slug, sort.Sort == shown))], keep);
        }

        // The lists of the feed chips and of the filter: all, then each one a chosen type has, then Continue when something continues.
        private List<(string? Slug, string LabelKey)> FeedOptions(IEnumerable<DiscoverScopeV1> scopes)
        {
            var options = new List<(string? Slug, string LabelKey)> { (null, "discover.all") };
            var supported = scopes.SelectMany(scope => scope.Feeds).ToHashSet();
            foreach (var (feed, slug) in FeedSlugs)
            {
                if (supported.Contains(feed)) options.Add((slug, "discover.feed." + feed));
            }

            if (_continue.Count > 0) options.Add(("continue", "discover.feed.Continue"));
            return options;
        }

        // The types a ranking exists for, as the script of the filter needs them; none for all and Continue, which every type has.
        private string? FeedScopes(string? feedSlug) => feedSlug is null or "continue" ? null
            : string.Join(',', Listed.Where(scope => scope.Feeds.Any(feed => FeedSlug(feed) == feedSlug)).Select(scope => scope.Slug));

        private static string FeedSlug(DiscoverFeedType feed) => FeedSlugs.First(entry => entry.Feed == feed).Slug;

        // Types per area, availability, release year and feed; one GET form, so the address holds the choice.
        private DrawerPane FilterPane()
        {
            var chosen = Chosen.Select(scope => scope.Slug).ToHashSet();
            var chooser = new List<IUiComponent>
            {
                new Span(new Text("discover.chooseMedia")).Class("drawer__field-title"),
                new Button(new Icon("grid", assets, 16), new Text("discover.allTypes")).Class("button discover-all").Attribute("data-discover-all", "").Attribute("aria-pressed", IsAll ? "true" : "false")
            };
            foreach (var area in MediaAreas.All)
            {
                var members = area.Slugs.Where(slug => Listed.Any(scope => scope.Slug == slug)).ToList();
                if (members.Count == 0) continue;

                var all = members.All(chosen.Contains);
                chooser.Add(new ChoiceGroup(assets, area.LabelKey, "type", [.. members.Select(slug => new ChoicePick(slug, chosen.Contains(slug), "discover.type." + slug))],
                    new ChoiceOptions(true, ChoiceLayout.Section, area.Icon, "discover.selected",
                        Aside: [new Button(new Text(all ? "discover.clearAll" : "discover.pickAll")).Class("button button--compact discover-pick").Attribute("data-discover-pick", "")
                            .Attribute("data-pick-label", texts["discover.pickAll"]).Attribute("data-clear-label", texts["discover.clearAll"])])));
            }

            // The media types come first, then the local availability and the filters the chosen types have data for (the row's own travel along hidden),
            // then the rankings of the types (those a chosen type lacks leave the list as the types change).
            var body = new List<IUiComponent>
            {
                new Div([.. chooser]).Class("drawer__section"),
                new ChoiceGroup(assets, "discover.filter.availability", "availability", [new("", Availability is null, "discover.all"), new("local", Availability == "local", "discover.known"),
                    new("request", Availability == "request", "discover.availability.request")])
            };
            body.AddRange(MoreFilters());
            var feedOptions = FeedOptions(Chosen);
            if (feedOptions.Count > 1)
            {
                body.Add(new ChoiceGroup(assets, "discover.filter.feed", "feed", [.. feedOptions.Select(option => new ChoicePick(option.Slug ?? "", Feed == option.Slug, option.LabelKey,
                    FeedScopes(option.Slug)))]));
            }

            if (!string.IsNullOrWhiteSpace(Query)) body.Add(new Input().Attribute("type", "hidden").Attribute("name", "q").Attribute("value", Query.Trim()));

            return new DrawerPane("filter", "discover.tab.filter", "discover.filter.title", "filter", body,
            [
                new A(new Text("discover.reset")).Class("button").Attribute("href", Address(new State([], null, null, null, false, Query?.Trim(), null, 1))),
                new Button(new Icon("check", assets, 16), new Text("discover.applyClose")).Primary().Submit()
            ], "/");
        }

        // How many highlights show, whether they turn, and the layout of the shelves are the device's own choice.
        private DrawerPane ViewPane() => new("view", "discover.tab.view", "discover.view.title", "sliders",
        [
            new Div(
                new ChoiceGroup(assets, "discover.hero.perType", "hero-perType", [.. Enumerable.Range(1, HeroPerType).Select(count => new ChoicePick(count.ToString(CultureInfo.InvariantCulture), count == 3))],
                    new ChoiceOptions(Attributes: new Dictionary<string, string> { ["data-hero-pref"] = "perType" })),
                new Div(HeroToggle("discover.hero.continue", "withContinue"), HeroToggle("discover.hero.auto", "auto")).Class("discover-switches")).Class("drawer__section"),
            new ChoiceGroup(assets, "discover.layout", "discover-layout", [new("rows", true, "discover.layout.rows"), new("grid", false, "discover.layout.grid")],
                new ChoiceOptions(Attributes: new Dictionary<string, string> { ["data-discover-layout-choice"] = "" }))
        ],
        [
            new Button(new Text("discover.reset")).Attribute("data-hero-reset", ""),
            new Button(new Icon("check", assets, 16), new Text("discover.applyClose")).Primary().Attribute("data-drawer-close", "")
        ]);

        private static IUiComponent HeroToggle(string labelKey, string preference)
        {
            var toggle = new Toggle(labelKey);
            toggle.Control.Attribute("data-hero-pref", preference);
            return toggle;
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
                .Bookmark(new MediaBookmark(assets, item.WorkId)).HoverPreview(MediaPreviewAddress.Work(item.WorkId));
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

                // A shelf goes on with the next pages of its ranking.
                foreach (var (feed, cards) in shown)
                {
                    var ranking = Current with { Types = [scope.Slug], Feed = null, View = feed, Page = 1 };
                    page.Content.Add(new MediaShelf(assets, "discover.feed." + feed, Address(ranking), [.. cards], Address(ranking with { Page = 2 })));
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
            await LoadAvailabilityAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnGetResultsAsync()
        {
            if (!texts.CanReadMedia()) return Forbid();

            await LoadDataAsync();
            await LoadAvailabilityAsync();
            PreparePage();
            return Partial("_UiComponent", Definition.Content[0]);
        }

        // Titles Discover keeps, while typing in the header search: grouped by type in the service's order, each opening its Work, then all results.
        public async Task<IActionResult> OnGetSuggestionsAsync()
        {
            if (!texts.CanReadMedia()) return Forbid();
            if (string.IsNullOrWhiteSpace(Query)) return Partial("_UiComponent", new Div().Class("app-search__results"));

            await LoadListedAsync();
            var found = await suggest.ExecuteAsync<BaseDataList<DiscoverSuggestionV1>>(new SuggestDiscoverDataV1(Query, Selected?.Slug), HttpContext.RequestAborted);
            var groups = found.Where(item => Chosen.Any(scope => scope.Slug == item.ScopeSlug)).GroupBy(item => item.ScopeSlug).Select(group => new Div([
                new A(new Icon(MediaAreas.Icons.GetValueOrDefault(group.Key, "search"), assets, 16), new Span(new Text("discover.type." + group.Key)).Class("app-search__type"),
                    new Span(new Text("common.seeAll"), new Icon("chevron-right", assets, 14)).Class("app-search__more"))
                    .Class("app-search__head").Attribute("href", Address(Current with { Types = [group.Key], Page = 1 })),
                .. group.Select(Suggestion)]).Class("app-search__group"));
            var all = new A(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["search.all"], Query.Trim()))).Class("app-search__all").Attribute("href", Address(Current with { Page = 1 }));
            return Partial("_UiComponent", new Div([.. groups, all]).Class("app-search__results").Attribute("aria-label", texts["discover.suggestions"]));
        }

        private IUiComponent Suggestion(DiscoverSuggestionV1 item)
        {
            IUiComponent poster = item.PosterUrl is { } url
                ? new Img().Class("app-search__poster").Attribute("data-no-cover", "poster").Attribute("src", CoverPaths.Sized(url, 120)).Attribute("alt", "").Attribute("loading", "lazy")
                : new NoCover(assets, NoCoverKind.Poster, "app-search__poster");
            // Year · genre · rating as on the cards; whatever is unknown is left out.
            var facts = new List<IUiComponent>();
            if (item.ReleaseYear is { } year) facts.Add(new Span(new ValueText(year.ToString(CultureInfo.InvariantCulture))).Class("app-search__fact"));
            if (item.Genre is { } genre) facts.Add(new Span(new ValueText(genre)).Class("app-search__fact"));
            if (item.Rating is { } rating) facts.Add(new Span(new Icon("star", assets, 11), new ValueText(rating.ToString("0.0", CultureInfo.CurrentCulture))).Class("app-search__fact"));
            if (item.MediaStateTypeId == MediaStateType.Available) facts.Add(new Span(new Text("discover.known")).Class("app-search__known"));
            return new A(poster, new Span(new Strong([.. Highlighted(item.Title)]), new Span([.. facts]).Class("app-search__facts")).Class("app-search__text"))
                .Class("app-search__hit").Attribute("href", "/Details/" + item.WorkId.ToString(CultureInfo.InvariantCulture));
        }

        // The typed words marked in a title, each where it begins a word of the title, else where it first appears.
        private IEnumerable<IUiComponent> Highlighted(string title)
        {
            var marks = new List<(int Start, int End)>();
            foreach (var word in Regex.Matches(Query!, @"[\p{L}\p{N}]+").Select(match => match.Value))
            {
                var first = title.IndexOf(word, StringComparison.CurrentCultureIgnoreCase);
                var at = first;
                while (at > 0 && char.IsLetterOrDigit(title[at - 1])) at = title.IndexOf(word, at + 1, StringComparison.CurrentCultureIgnoreCase);
                var start = at >= 0 ? at : first;
                if (start >= 0) marks.Add((start, Math.Min(start + word.Length, title.Length)));
            }

            var written = 0;
            foreach (var (start, end) in marks.OrderBy(mark => mark.Start))
            {
                if (end <= written) continue;
                var from = Math.Max(start, written);
                if (from > written) yield return new ValueText(title[written..from]);
                yield return new Mark(new ValueText(title[from..end]));
                written = end;
            }

            if (written < title.Length) yield return new ValueText(title[written..]);
        }

        public async Task<IActionResult> OnGetRecommendationsAsync()
        {
            if (!texts.CanReadMedia()) return Forbid();
            await LoadRecommendationsAsync();

            // Native navigation keeps full SSR.
            if (Request.Headers["X-Jularr-Fragment"] != "recommendations")
            {
                await LoadDataAsync();
                await LoadAvailabilityAsync();
                PreparePage();
                return Page();
            }

            await LoadAvailabilityAsync();
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
            await LoadFilterOptionsAsync();

            var selected = Selected;
            try
            {
                if (!string.IsNullOrWhiteSpace(Query))
                {
                    await SearchAsync(selected);
                    return;
                }

                // Filtered titles replace the shelves; the first page keeps the highlights, read below.
                if (Filtering)
                {
                    await LoadFilteredAsync();
                    if (PageNumber > 1) return;
                }
                else if (selected is { IsCatalog: true } && View is { } catalogView && selected.Feeds.Contains(catalogView))
                {
                    _catalog = [.. (await catalogFeeds.ExecuteAsync<BaseDataList<DiscoverCatalogItemV1>>(new ReadDiscoverCatalogFeedDataV1(selected.MediaTypeId, catalogView, selected.MediaSubtypeId,
                        PageNumber, ChosenSort), HttpContext.RequestAborted)).Select(item => (selected, item))];
                    return;
                }

                if (!Filtering && selected is not null && View is { } view && selected.Feeds.Contains(view))
                {
                    _results = await feeds.ExecuteAsync<BaseDataList<DiscoverItemV1>>(new ReadDiscoverFeedDataV1(selected.MediaTypeId, view, PageNumber, ChosenSort), HttpContext.RequestAborted);
                    return;
                }
            }
            catch (ServiceUnavailableException exception)
            {
                // A search of series alone names the missing TheTVDB key.
                _providerUnavailable = true;
                if (exception.Code == "tvdb_key_missing") _providerNotice = "discover.tvdbKeyMissing";
                _results = selected is { IsCatalog: true } ? null : [];
                _catalog = selected is { IsCatalog: true } ? [] : null;
                return;
            }

            // Local shelves never wait for the provider; what does not depend on another read is read together.
            if (texts.CanArrangeHome())
            {
                _layout = await readLayout.ExecuteAsync<BaseDataList<HomeShelfV1>>(new HomeLayoutParametersV1(), HttpContext.RequestAborted);
            }

            // The highlights continue and fill up from the same reads as the shelves, also while those shelves are hidden.
            var hero = (View is null || Filtering) && string.IsNullOrWhiteSpace(Query);
            var shown = _layout.Where(shelf => !shelf.IsHidden).Select(shelf => shelf.HomeShelfTypeId).ToHashSet();
            var continueTask = ReadWhenAsync((texts.CanPlay() || texts.CanRead()) && (hero || shown.Overlaps([HomeShelfType.ContinueWatching, HomeShelfType.ContinueListening, HomeShelfType.ContinueReading])),
                () => continuation.ExecuteAsync<BaseDataList<ContinueItemV1>>(new ReadContinueParametersV1(), HttpContext.RequestAborted));

            // The chosen types narrow the read: a type without its subtypes, a subtype across its types.
            var recentTypes = IsAll ? null : Chosen.Where(scope => scope.MediaSubtypeId is null).Select(scope => scope.MediaTypeId).ToArray();
            var recentSubtypes = IsAll ? null : Chosen.Where(scope => scope.MediaSubtypeId is not null).Select(scope => scope.MediaSubtypeId!.Value).ToArray();
            var recentTask = ReadWhenAsync(hero || shown.Contains(HomeShelfType.RecentlyAdded),
                () => library.ExecuteAsync<PageResult<LibraryItemV1>>(new ReadLibraryParametersV1(recentTypes, pageSize: 20, mediaSubtypeIds: recentSubtypes), HttpContext.RequestAborted));
            var heroFeed = Feed is null ? DiscoverFeedType.Trending : FeedType;
            var keptTask = ReadWhenAsync(hero && heroFeed is not null,
                () => readShelves.ExecuteAsync<BaseDataList<DiscoverShelfV1>>(new ReadDiscoverShelvesDataV1(selected?.Slug), HttpContext.RequestAborted));
            await Task.WhenAll(continueTask, recentTask, keptTask);

            _continue = (await continueTask) ?? _continue;
            _recent = (await recentTask)?.Items ?? _recent;
            if (hero)
            {
                await LoadHeroAsync(await keptTask);
            }
        }

        // A read that is only started when its shelf is shown; otherwise null.
        private static async Task<TResult?> ReadWhenAsync<TResult>(bool shown, Func<Task<TResult>> read) where TResult : class => shown ? await read() : null;

        // Provider metadata stays cached; current local status is one bounded read for all cards.
        private async Task LoadAvailabilityAsync()
        {
            var ids = (_results ?? []).Concat(_shelves.SelectMany(shelf => shelf.Items)).Select(item => item.WorkId)
                .Concat((_catalog ?? []).Select(entry => entry.Item.WorkId))
                .Concat(_catalogShelves.SelectMany(shelf => shelf.Items).Select(item => item.WorkId))
                .Concat(_known.Select(item => item.WorkId))
                .Distinct().ToArray();
            foreach (var batch in ids.Chunk(500))
            {
                var states = await readAvailability.ExecuteAsync<BaseDataList<WorkAvailabilityStateV1>>(new ReadWorkAvailabilityStatesParametersV1(batch), HttpContext.RequestAborted);
                foreach (var state in states) _availabilityByWork[state.Id] = state;
            }
        }

        // One scope searches its own catalog or TMDB and pages; several ask each chosen catalog and TMDB for films and series together, once,
        // and a provider that fails leaves the other rows.
        private async Task SearchAsync(DiscoverScopeV1? selected)
        {
            if (selected is { IsCatalog: true })
            {
                _catalog = [.. (await catalogSearch.ExecuteAsync<BaseDataList<DiscoverCatalogItemV1>>(new SearchDiscoverCatalogDataV1(selected.MediaTypeId, Query!, selected.MediaSubtypeId),
                    HttpContext.RequestAborted)).Select(item => (selected, item))];
                return;
            }

            if (selected is not null)
            {
                _results = await search.ExecuteAsync<BaseDataList<DiscoverItemV1>>(new SearchDiscoverDataV1(Query!, selected.MediaTypeId, PageNumber), HttpContext.RequestAborted);
                return;
            }

            // The titles Jularr keeps answer too, so what the suggestions showed is never missing when a provider does not answer.
            var known = suggest.ExecuteAsync<BaseDataList<DiscoverSuggestionV1>>(new SuggestDiscoverDataV1(Query!), HttpContext.RequestAborted);
            using var gate = new SemaphoreSlim(ShelfConcurrency);
            var catalogs = Chosen.Where(scope => scope.IsCatalog).Select(scope => SearchLimitedAsync(gate, async token =>
                (await catalogSearch.ExecuteAsync<BaseDataList<DiscoverCatalogItemV1>>(new SearchDiscoverCatalogDataV1(scope.MediaTypeId, Query!, scope.MediaSubtypeId), token))
                    .Select(item => (scope, item)).ToList())).ToList();
            var provider = Chosen.Any(scope => !scope.IsCatalog)
                ? SearchLimitedAsync(gate, token => search.ExecuteAsync<BaseDataList<DiscoverItemV1>>(new SearchDiscoverDataV1(Query!), token))
                : Task.FromResult<BaseDataList<DiscoverItemV1>?>(new BaseDataList<DiscoverItemV1>([]));
            await Task.WhenAll(catalogs.Cast<Task>().Append(provider).Append(known));

            _catalog = [.. catalogs.SelectMany(task => task.Result ?? [])];
            _results = provider.Result is { } found ? [.. found] : [];
            _searchIncomplete = provider.Result is null || catalogs.Any(task => task.Result is null);
            _known = await known;
        }

        // One provider's search within the shared limits; null when it failed or answered too late.
        private async Task<TResult?> SearchLimitedAsync<TResult>(SemaphoreSlim gate, Func<CancellationToken, Task<TResult>> read) where TResult : class
        {
            var requestAborted = HttpContext.RequestAborted;
            await gate.WaitAsync(requestAborted);
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
                limit.CancelAfter(ShelfLimit);
                return await read(limit.Token);
            }
            catch (Exception exception) when (exception is ServiceUnavailableException || exception is OperationCanceledException && !requestAborted.IsCancellationRequested)
            {
                log.LogWarning("Discover search answered nothing: {Reason}", exception.Message);
                return null;
            }
            finally
            {
                gate.Release();
            }
        }

        // Each chosen type's results as one row: the kept titles, then the providers' others; the best match first and local titles before others that match
        // as well (a chosen sort replaces this). The type with the best match leads, then the order of the media-type bar. See all searches that type alone.
        private List<MediaShelf> SearchRows()
        {
            var text = Query!.Trim();
            int Relevance(string title) => title.Equals(text, StringComparison.CurrentCultureIgnoreCase) ? 0 : title.StartsWith(text, StringComparison.CurrentCultureIgnoreCase) ? 1 : 2;

            var rows = Chosen.Select((scope, order) =>
            {
                var kept = _known.Where(item => item.ScopeSlug == scope.Slug
                        && Fits(item.MediaStateTypeId == MediaStateType.Available, item.MediaStateTypeId is null or MediaStateType.NotRequested, item.ReleaseYear))
                    .Select(item => (item.WorkId, item.Title, item.ReleaseYear, Local: item.MediaStateTypeId == MediaStateType.Available, Card: KnownCard(scope, item)));
                var found = scope.IsCatalog
                    ? Filtered((_catalog ?? []).Where(entry => entry.Scope == scope))
                        .Select(entry => (entry.Item.WorkId, entry.Item.Title, entry.Item.ReleaseYear, Local: entry.Item.MediaStateTypeId == MediaStateType.Available, Card: CatalogCard(scope, entry.Item)))
                    : Filtered((_results ?? []).Where(item => item.MediaTypeId == scope.MediaTypeId))
                        .Select(item => (item.WorkId, item.Title, item.ReleaseYear, Local: item.MediaStateTypeId == MediaStateType.Available, Card: Card(item)));
                var hits = kept.Concat(found).DistinctBy(hit => hit.WorkId);
                var ordered = (Sort switch
                {
                    "newest" => hits.OrderByDescending(hit => hit.ReleaseYear),
                    "az" => hits.OrderBy(hit => hit.Title, StringComparer.CurrentCultureIgnoreCase),
                    _ => hits.OrderBy(hit => Relevance(hit.Title)).ThenBy(hit => !hit.Local)
                }).Take(SearchRowSize).ToList();
                return (Scope: scope, Order: order, Hits: ordered);
            }).Where(row => row.Hits.Count > 0).OrderBy(row => row.Hits.Min(hit => Relevance(hit.Title))).ThenBy(row => row.Order);

            return [.. rows.Select(row => new MediaShelf(assets, "discover.type." + row.Scope.Slug, Address(Current with { Types = [row.Scope.Slug], Page = 1 }), [.. row.Hits.Select(hit => hit.Card)]))];
        }

        private async Task LoadListedAsync() =>
            _scopes ??= await readScopes.ExecuteAsync<BaseDataList<DiscoverScopeV1>>(new DiscoverParametersV1(), HttpContext.RequestAborted);

        // Per chosen type its continued titles, then its best titles of the feed, then what the library added last, each title once; the device shows the first few
        // of each type, one place after the other across the types. Read from the whole catalogs; series, which TheTVDB never gives whole, from Discover's series list
        // (kept or asked live); one source of the first other type answers when none was found.
        private async Task LoadHeroAsync(BaseDataList<DiscoverShelfV1>? kept)
        {
            var featured = new Dictionary<DiscoverScopeV1, List<(long WorkId, HeroSlide Slide)>>();
            var feed = Feed is null ? DiscoverFeedType.Trending : FeedType;
            if (feed is { } heroFeed && kept is not null)
            {
                foreach (var scope in Chosen.Where(scope => scope.Feeds.Contains(heroFeed)))
                {
                    // Titles with artwork lead; the others fill up.
                    if (kept.FirstOrDefault(shelf => shelf.ScopeSlug == scope.Slug && shelf.DiscoverFeedTypeId == heroFeed) is { } shelf)
                    {
                        featured[scope] = scope.IsCatalog
                            ? [.. Filtered(shelf.CatalogItems.Select(item => (scope, item))).Select(entry => entry.Item).OrderBy(item => item.PosterUrl is null).Take(HeroPerType)
                                .Select(item => (item.WorkId, CatalogSlide(scope, item, heroFeed)))]
                            : [.. Filtered(shelf.Items).OrderBy(item => item.BackdropUrl is null && item.PosterUrl is null).Take(HeroPerType).Select(item => (item.WorkId, ItemSlide(scope, item, heroFeed)))];
                    }
                    else if (IsSeriesList(scope) && await ReadHeroFeedAsync(scope, heroFeed) is { } listed)
                    {
                        featured[scope] = [.. Filtered(listed).OrderBy(item => item.BackdropUrl is null && item.PosterUrl is null).Take(HeroPerType).Select(item => (item.WorkId, ItemSlide(scope, item, heroFeed)))];
                    }
                }

                // The series list does not stand in for the other types.
                if (heroFeed == DiscoverFeedType.Trending && !featured.Any(entry => entry.Value.Count > 0 && !IsSeriesList(entry.Key)))
                {
                    await AddFallbackSlidesAsync(featured);
                }
            }

            // Continued titles count only while the device shows them, so a type keeps enough others to fill its places; Pass is the place within the type.
            var perScope = new List<List<HeroSlide>>();
            foreach (var scope in Chosen)
            {
                var own = new List<(long WorkId, HeroSlide Slide)>();
                if (Feed is null or "continue")
                {
                    own.AddRange(_continue.Where(item => InScope(scope, item.MediaTypeId, item.MediaSubtypeId)).Take(HeroPerType).Select(item => (item.WorkId, ContinueSlide(scope, item))));
                }

                own.AddRange(featured.GetValueOrDefault(scope) ?? []);
                if (Feed is null)
                {
                    own.AddRange(_recent.Where(item => InScope(scope, item.MediaTypeId, item.MediaSubtypeId) && Fits(true, item.MediaStateTypeId == MediaStateType.NotRequested, item.ReleaseYear))
                        .Select(item => (item.Id, RecentSlide(scope, item))));
                }

                var unique = own.DistinctBy(entry => entry.WorkId).Select(entry => entry.Slide).ToList();
                perScope.Add([.. unique.Where(slide => slide.IsContinue).Concat(unique.Where(slide => !slide.IsContinue).Take(HeroPerType)).Select((slide, pass) => slide with { Pass = pass })]);
            }

            var slides = new List<HeroSlide>();
            for (var pass = 0; perScope.Any(own => own.Count > pass); pass++)
            {
                slides.AddRange(perScope.Where(own => own.Count > pass).Select(own => own[pass]));
            }

            _hero = slides;
        }

        // Series come from TMDB's lists as TheTVDB's titles, never from a catalog read whole.
        private static bool IsSeriesList(DiscoverScopeV1 scope) => scope is { IsCatalog: false, MediaTypeId: MediaType.Series };

        // Discover's own list of the type: the kept one while the Caching module keeps lists, else the provider's answer; a provider that answers late never holds the page.
        private async Task<IReadOnlyList<DiscoverItemV1>?> ReadHeroFeedAsync(DiscoverScopeV1 scope, DiscoverFeedType feed)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            limit.CancelAfter(HeroLimit);
            try
            {
                return await feeds.ExecuteAsync<BaseDataList<DiscoverItemV1>>(new ReadDiscoverFeedDataV1(scope.MediaTypeId, feed), limit.Token);
            }
            catch (Exception exception) when (exception is ServiceUnavailableException || exception is OperationCanceledException && !HttpContext.RequestAborted.IsCancellationRequested)
            {
                return null;
            }
        }

        // Without a catalog read whole, the first other type that has trending titles asks its provider; the series list was asked already.
        private async Task AddFallbackSlidesAsync(Dictionary<DiscoverScopeV1, List<(long WorkId, HeroSlide Slide)>> featured)
        {
            var scope = Chosen.FirstOrDefault(scope => scope.Feeds.Contains(DiscoverFeedType.Trending) && !IsSeriesList(scope));
            if (scope is null)
            {
                return;
            }

            // A provider that answers late never holds the page's HTML.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            limit.CancelAfter(HeroLimit);
            try
            {
                if (scope.IsCatalog)
                {
                    var items = await catalogFeeds.ExecuteAsync<BaseDataList<DiscoverCatalogItemV1>>(
                        new ReadDiscoverCatalogFeedDataV1(scope.MediaTypeId, DiscoverFeedType.Trending, scope.MediaSubtypeId), limit.Token);
                    featured[scope] = [.. Filtered(items.Select(value => (scope, value))).Select(entry => entry.Item).OrderBy(value => value.PosterUrl is null).Take(HeroPerType)
                        .Select(item => (item.WorkId, CatalogSlide(scope, item, DiscoverFeedType.Trending)))];
                    return;
                }

                var results = await feeds.ExecuteAsync<BaseDataList<DiscoverItemV1>>(new ReadDiscoverFeedDataV1(scope.MediaTypeId, DiscoverFeedType.Trending), limit.Token);
                featured[scope] = [.. Filtered(results).OrderBy(value => value.BackdropUrl is null && value.PosterUrl is null).Take(HeroPerType)
                    .Select(item => (item.WorkId, ItemSlide(scope, item, DiscoverFeedType.Trending)))];
            }
            catch (Exception exception) when (exception is ServiceUnavailableException || exception is OperationCanceledException && !HttpContext.RequestAborted.IsCancellationRequested)
            {
                // Optional highlights must never prevent local shelves from rendering.
            }
        }

        private string Overline(DiscoverScopeV1 scope, string feedKey) => texts["discover.type." + scope.Slug] + " · " + texts[feedKey];

        // The title opens its Work, where one nobody asked for yet is requested.
        private HeroSlide CatalogSlide(DiscoverScopeV1 scope, DiscoverCatalogItemV1 item, DiscoverFeedType feed)
        {
            var href = CatalogHref(item);
            var requestable = item.MediaStateTypeId == MediaStateType.NotRequested && texts.CanRequest();
            return new HeroSlide(item.Title, null, item.PosterUrl, Overline(scope, "discover.feed." + feed), item.Author, requestable ? "discover.action.request" : "media.details", href,
                requestable ? "plus" : null, requestable ? href : null, scope.Slug, 0);
        }

        private HeroSlide ItemSlide(DiscoverScopeV1 scope, DiscoverItemV1 item, DiscoverFeedType feed)
        {
            var (actionKey, actionHref) = Action(item);
            return new HeroSlide(item.Title, item.BackdropUrl, item.PosterUrl, Overline(scope, "discover.feed." + feed), item.Overview, ShortKey(actionKey) ?? "media.details", actionHref ?? DetailsHref(item),
                actionKey switch { "request.create" => "plus", "media.watch" => "play", _ => null }, actionKey is null ? null : DetailsHref(item), scope.Slug, 0);
        }

        private HeroSlide ContinueSlide(DiscoverScopeV1 scope, ContinueItemV1 item)
        {
            var readable = ReadableMedia.IsReadable(item.MediaAssetTypeId);
            var listening = item.MediaAssetTypeId == MediaAssetType.Audio;
            return new HeroSlide(item.Title, null, item.PosterUrl, Overline(scope, "discover.feed.Continue"), null,
                readable ? "discover.action.continueRead" : listening ? "discover.action.continueHear" : "media.continue",
                (readable ? "/Read/" : "/Player/") + item.MediaAssetId.ToString(CultureInfo.InvariantCulture), readable ? "book" : "play",
                "/Details/" + item.WorkId.ToString(CultureInfo.InvariantCulture), scope.Slug, 0, true);
        }

        // A title the library added lately opens its Work.
        private HeroSlide RecentSlide(DiscoverScopeV1 scope, LibraryItemV1 item) =>
            new(item.Title, null, item.PosterUrl, Overline(scope, "discover.recentlyAdded"), null, "media.details", "/Details/" + item.Id.ToString(CultureInfo.InvariantCulture), null, null, scope.Slug, 0);

        // One shelf of a provider: what it answered, or that it did not answer in time.
        private sealed record ShelfAnswer(DiscoverScopeV1 Scope, DiscoverFeedType Feed, IReadOnlyList<DiscoverItemV1>? Items, IReadOnlyList<DiscoverCatalogItemV1>? Catalog, bool Failed);

        private async Task LoadRecommendationsAsync()
        {
            await LoadListedAsync();
            var selected = Selected;
            var started = Stopwatch.GetTimestamp();

            // Catalogs read whole answer all their shelves at once; the others ask their providers shelf by shelf, together.
            var whole = await readShelves.ExecuteAsync<BaseDataList<DiscoverShelfV1>>(new ReadDiscoverShelvesDataV1(selected?.Slug), HttpContext.RequestAborted);
            var wholeMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            using var gate = new SemaphoreSlim(ShelfConcurrency);
            var answers = new List<Task<ShelfAnswer>>();
            foreach (var scope in Chosen)
            {
                foreach (var feed in scope.Feeds.Where(feed => FeedType is null || feed == FeedType))
                {
                    answers.Add(whole.FirstOrDefault(shelf => shelf.ScopeSlug == scope.Slug && shelf.DiscoverFeedTypeId == feed) is { } kept
                        ? Task.FromResult(new ShelfAnswer(scope, feed, scope.IsCatalog ? null : kept.Items, scope.IsCatalog ? kept.CatalogItems : null, false))
                        : ReadProviderShelfAsync(scope, feed, gate));
                }
            }

            // In the order of the media-type bar.
            foreach (var answer in await Task.WhenAll(answers))
            {
                if (answer.Failed) _providerUnavailable = true;
                else if (answer.Catalog is not null) _catalogShelves.Add((answer.Scope, answer.Feed, answer.Catalog));
                else if (answer.Items is not null) _shelves.Add((answer.Scope.MediaTypeId, answer.Feed, answer.Items));
            }

            var total = Stopwatch.GetElapsedTime(started);
            HttpContext.Response.Headers["Server-Timing"] = $"local;dur={wholeMilliseconds}, shelves;dur={(long)total.TotalMilliseconds}";
            if (total > SlowShelf) log.LogInformation("Discover shelves: {Shelves} shelves in {Milliseconds} ms ({Local} ms for the local ones)", answers.Count, (long)total.TotalMilliseconds, wholeMilliseconds);
        }

        private async Task<ShelfAnswer> ReadProviderShelfAsync(DiscoverScopeV1 scope, DiscoverFeedType feed, SemaphoreSlim gate)
        {
            var requestAborted = HttpContext.RequestAborted;
            await gate.WaitAsync(requestAborted);
            var started = Stopwatch.GetTimestamp();
            var outcome = "answered";
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
                limit.CancelAfter(ShelfLimit);
                if (scope.IsCatalog)
                {
                    return new ShelfAnswer(scope, feed, null, await catalogFeeds.ExecuteAsync<BaseDataList<DiscoverCatalogItemV1>>(new ReadDiscoverCatalogFeedDataV1(scope.MediaTypeId, feed, scope.MediaSubtypeId), limit.Token), false);
                }

                return new ShelfAnswer(scope, feed, await feeds.ExecuteAsync<BaseDataList<DiscoverItemV1>>(new ReadDiscoverFeedDataV1(scope.MediaTypeId, feed), limit.Token), null, false);
            }
            catch (Exception exception) when (exception is ServiceUnavailableException || exception is OperationCanceledException && !requestAborted.IsCancellationRequested)
            {
                // Other shelves remain useful.
                outcome = exception is ServiceUnavailableException ? "unavailable" : "timed out";
                return new ShelfAnswer(scope, feed, null, null, true);
            }
            finally
            {
                gate.Release();
                var elapsed = Stopwatch.GetElapsedTime(started);
                if (elapsed > SlowShelf) log.LogWarning("Slow Discover shelf {Scope}/{Feed}: {Outcome} after {Milliseconds} ms", scope.Slug, feed, outcome, (long)elapsed.TotalMilliseconds);
            }
        }

        // The availability and year choices narrow what is listed; the provider or the catalog orders it.
        private IEnumerable<DiscoverItemV1> Filtered(IEnumerable<DiscoverItemV1> items) =>
            items.Where(item => Fits(item.MediaStateTypeId == MediaStateType.Available, item.MediaStateTypeId == MediaStateType.NotRequested, item.ReleaseYear));

        private IEnumerable<(DiscoverScopeV1 Scope, DiscoverCatalogItemV1 Item)> Filtered(IEnumerable<(DiscoverScopeV1 Scope, DiscoverCatalogItemV1 Item)> items) =>
            items.Where(entry => Fits(entry.Item.MediaStateTypeId == MediaStateType.Available, entry.Item.MediaStateTypeId == MediaStateType.NotRequested, entry.Item.ReleaseYear));

        private bool Fits(bool isLocal, bool canBeRequested, int? releaseYear) =>
            (Availability != "local" || isLocal) && (Availability != "request" || canBeRequested);

        // The token of the quick request on the cards, taken once per request.
        private string Csrf => _csrf ??= antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? "";

        // A Work belongs to the scope of its subtype, else of its type: series without anime, books without manga and light novels.
        private bool MediaTypeChosen(MediaType type, MediaSubtype? subtype) => IsAll || Chosen.Any(scope => InScope(scope, type, subtype));

        private static bool InScope(DiscoverScopeV1 scope, MediaType type, MediaSubtype? subtype) => scope.Slug == LibraryScopes.Slug(type, subtype);

        private IUiComponent Card(DiscoverItemV1 item)
        {
            var (actionKey, actionHref) = Action(item);
            var card = MediaCard.For(assets, item, DetailsHref(item));
            if (_availabilityByWork.TryGetValue(item.WorkId, out var state))
                card.Availability(new MediaAvailability(assets, state.AvailabilityStateId, state.AvailableUnits, state.MonitoredUnits));
            if (actionKey == "media.request") card.Request(item.WorkId, Csrf);
            else if (item.MediaStateTypeId == MediaStateType.Requested && texts.CanRequest()) card.Request(item.WorkId, null, AcquisitionRequestStatusType.Open);
            else if (actionKey is not null && actionHref is not null) card.Action(ShortKey(actionKey)!, actionHref);
            return card.Bookmark(new MediaBookmark(assets, item.WorkId));
        }

        // The title opens its Work, where one nobody asked for yet is requested.
        private IUiComponent CatalogCard(DiscoverScopeV1 scope, DiscoverCatalogItemV1 item)
        {
            var href = CatalogHref(item);
            // A Narō web novel says so beside the published light novels; a catalog that names the format shows it instead of the type.
            var label = item.Namespace == WebNovelSource ? "discover.webNovel"
                : item.CatalogFormatId is { } format ? "discover.format." + format : MediaTypeLabel.Key(scope.MediaTypeId, scope.MediaSubtypeId);
            var card = new MediaCard(assets, item.Title, href, item.PosterUrl).Facts(item.ReleaseYear, label, item.Rating)
                .HoverPreview(MediaPreviewAddress.Work(item.WorkId)).State(item.MediaStateTypeId);
            if (_availabilityByWork.TryGetValue(item.WorkId, out var availability))
                card.Availability(new MediaAvailability(assets, availability.AvailabilityStateId, availability.AvailableUnits, availability.MonitoredUnits));
            if (item.MediaStateTypeId == MediaStateType.NotRequested && texts.CanRequest()) card.Request(item.WorkId, Csrf);
            else if (item.MediaStateTypeId == MediaStateType.Requested && texts.CanRequest()) card.Request(item.WorkId, null, AcquisitionRequestStatusType.Open);
            return card.Bookmark(new MediaBookmark(assets, item.WorkId));
        }

        // A kept title found by the search, shown like a catalog card.
        private IUiComponent KnownCard(DiscoverScopeV1 scope, DiscoverSuggestionV1 item)
        {
            var href = "/Details/" + item.WorkId.ToString(CultureInfo.InvariantCulture);
            var state = item.MediaStateTypeId ?? MediaStateType.NotRequested;
            var card = new MediaCard(assets, item.Title, href, item.PosterUrl).Facts(item.ReleaseYear, MediaTypeLabel.Key(scope.MediaTypeId, scope.MediaSubtypeId), item.Rating)
                .HoverPreview(MediaPreviewAddress.Work(item.WorkId)).State(state);
            if (_availabilityByWork.TryGetValue(item.WorkId, out var availability))
                card.Availability(new MediaAvailability(assets, availability.AvailabilityStateId, availability.AvailableUnits, availability.MonitoredUnits));
            if (state == MediaStateType.NotRequested && texts.CanRequest()) card.Action("discover.action.request", href);
            return card.Bookmark(new MediaBookmark(assets, item.WorkId));
        }

        // An anime entry opens its series at its season.
        private static string CatalogHref(DiscoverCatalogItemV1 item) =>
            "/Details/" + item.WorkId.ToString(CultureInfo.InvariantCulture) + (item.SeasonNumber is { } season ? "?season=" + season.ToString(CultureInfo.InvariantCulture) : "");

        // The word on an action is short: the icon and the verb.
        private static string? ShortKey(string? key) => key switch
        {
            "request.create" => "discover.action.request",
            "media.watch" => "discover.action.watch",
            _ => key
        };

        // The canonical next step: use what exists, request what is missing.
        private (string? Key, string? Href) Action(DiscoverItemV1 item) => item.MediaStateTypeId switch
        {
            MediaStateType.Available when item.PlexUrl is { } plex && !texts.CanPlay() => ("media.openInPlex", plex),
            MediaStateType.Available => ("media.watch", DetailsHref(item)),
            MediaStateType.NotRequested when texts.CanRequest() => ("media.request", DetailsHref(item)),
            _ => (null, null)
        };

        private static string DetailsHref(DiscoverItemV1 item) => "/Details/" + item.WorkId.ToString(CultureInfo.InvariantCulture);

        // Everything the address holds; links change one part and keep the rest.
        // Filters keeps the chosen filters; another media type starts without them.
        private sealed record State(IReadOnlyList<string> Types, string? Feed, string? Sort, string? Availability, bool Filters, string? Query, DiscoverFeedType? View, int Page);

        private State Current => new(IsAll ? [] : [.. Chosen.Select(scope => scope.Slug)], Feed,
            (ChosenSort ?? FilterSort) is { } sort ? SortSlugs.First(entry => entry.Sort == sort).Slug : null, Availability, true, Query?.Trim(), View, PageNumber);

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
            if (state.Sort is not null && (state.View is not null || state.Filters && Filtering) && !searching) values.Add("sort=" + Uri.EscapeDataString(state.Sort));
            if (state.Availability is not null) values.Add("availability=" + Uri.EscapeDataString(state.Availability));
            if (state.Filters) values.AddRange(FilterValues());
            if (searching) values.Add("q=" + Uri.EscapeDataString(state.Query!));
            if (state.Page > 1) values.Add("p=" + state.Page.ToString(CultureInfo.InvariantCulture));
            return values.Count == 0 ? "/" : "/?" + string.Join('&', values);
        }
    }
}
