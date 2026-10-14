using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Data.User.Library.V1;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Service.Public.Authentication.V1;
using Jularr.Backend.Service.User.Discover.V1;
using Jularr.Backend.Service.User.Library.V1;
using Jularr.Backend.Service.User.Profile.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.Discover
{
    public sealed class IndexModel(ReadAuthenticationV1 authentication, ReadProfileMediaV1 readMedia, ReadDiscoverFeedV1 feeds, SearchDiscoverV1 search, ReadContinueV1 continuation,
        ReadLibraryV1 library, UiText texts, FrontendAssets assets) : JularrPage
    {
        // Only types with a real discovery provider; Books, Manga and Light Novels follow with their providers.
        private static readonly (string Slug, MediaType Type, DiscoverFeedType[] Feeds)[] Types =
        [
            ("movies", MediaType.Movie, [DiscoverFeedType.Trending, DiscoverFeedType.Top, DiscoverFeedType.New, DiscoverFeedType.Upcoming]),
            ("series", MediaType.Series, [DiscoverFeedType.Trending, DiscoverFeedType.Top, DiscoverFeedType.New])
        ];

        private readonly List<(MediaType Type, DiscoverFeedType Feed, IReadOnlyList<DiscoverItemV1> Items)> _shelves = [];
        private IReadOnlyList<DiscoverItemV1>? _results;
        private IReadOnlyList<ContinueItemV1> _continue = [];
        private IReadOnlyList<LibraryItemV1> _recent = [];
        private bool _providerUnavailable;
        private HashSet<MediaType>? _listed;

        [BindProperty(SupportsGet = true, Name = "type")]
        public string? TypeSlug { get; set; }

        // The profile's listed types only.
        private IEnumerable<(string Slug, MediaType Type, DiscoverFeedType[] Feeds)> Listed => Types.Where(type => _listed?.Contains(type.Type) is true);

        private (string Slug, MediaType Type, DiscoverFeedType[] Feeds) Selected => Listed.FirstOrDefault(type => type.Slug == TypeSlug);

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
            if (texts.Capabilities?.CanReadMedia is not true)
            {
                return;
            }

            page.Asset("Pages/User/Discover/Index.entry.scss");
            page.Asset("Framework/Components/MediaCard.entry.ts");
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
            page.Content.Add(new NavigationTabs([new NavigationTab("discover.all", Address(null, null, 1), selected.Slug is null), .. Listed.Select(type =>
                new NavigationTab("discover.type." + type.Slug, Address(type.Slug, null, 1), type.Slug == selected.Slug))], "discover.mediaTypes", "media"));
            if (selected.Slug is not null && string.IsNullOrWhiteSpace(Query))
            {
                page.Content.Add(new NavigationTabs([new NavigationTab("discover.overview", "/?type=" + selected.Slug, View is null), .. selected.Feeds.Select(feed =>
                    new NavigationTab("discover.feed." + feed, Address(selected.Slug, feed, 1), View == feed))], "discover.browseViews", "browse"));
            }

            if (_providerUnavailable && _results is not null)
            {
                page.Content.Add(new P(new Text("discover.providerUnavailable")).Attribute("role", "status"));
            }

            // Search results and full browse views are grids.
            if (_results is { } results)
            {
                if (results.Count == 0)
                {
                    page.Content.Add(new P(new Text(string.IsNullOrWhiteSpace(Query) ? "discover.empty" : "discover.noResults")).Attribute("role", "status"));
                    if (PageNumber > 1) page.Content.Add(new A(new Text("common.previous")).Class("button").Attribute("href", Address(selected.Slug, View, PageNumber - 1)));
                    return;
                }

                page.Content.Add(new Div([.. results.Select(Card)]).Class("media-grid"));
                var paging = new List<IUiComponent>();
                if (PageNumber > 1) paging.Add(new A(new Text("common.previous")).Class("button").Attribute("href", Address(selected.Slug, View, PageNumber - 1)));
                if (!_providerUnavailable && PageNumber < 500 && results.Count >= 20)
                {
                    paging.Add(new A(new Text("common.next")).Class("button").Attribute("data-discover-next", "true").Attribute("href", Address(selected.Slug, View, PageNumber + 1)));
                }

                page.Content.Add(new Nav([.. paging]).Class("discover-paging").Attribute("aria-label", texts["discover.pages"]));

                return;
            }

            // Local content arrives first.
            var featured = new Page();
            DefineFeatured(featured);
            page.Content.Add(new Div([.. featured.Content]).Attribute("id", "discover-featured"));

            // Watching, listening and reading continue on their own shelves.
            var continuing = _continue.Where(item => selected.Slug is null || item.MediaTypeId == selected.Type).ToList();
            var canPlay = texts.Capabilities?.CanPlay is true;
            page.Content.Add(ContinueShelf("discover.continueWatching", canPlay ? continuing.Where(item => item.MediaAssetTypeId == MediaAssetType.Video) : []));
            page.Content.Add(ContinueShelf("discover.continueListening", canPlay ? continuing.Where(item => item.MediaAssetTypeId == MediaAssetType.Audio) : []));
            page.Content.Add(ContinueShelf("media.continueReading", texts.Capabilities?.CanRead is true ? continuing.Where(item => ReadableMedia.IsReadable(item.MediaAssetTypeId)) : []));
            page.Content.Add(new MediaShelf(assets, "discover.recentlyAdded", "/Library", [.. _recent.Select(item => new MediaCard(item.Title, "/Details/" + item.Id.ToString(CultureInfo.InvariantCulture),
                item.PosterUrl, item.ReleaseYear?.ToString(CultureInfo.InvariantCulture), "media.state." + item.MediaStateTypeId))]));

            var recommendations = new Page();
            DefineRecommendations(recommendations);
            if (_shelves.Count == 0 && !_providerUnavailable)
            {
                recommendations.Content.Add(new A(new Text("discover.loadRecommendations")).Class("button").Attribute("data-discover-recommendations", "true")
                    .Attribute("href", Address(selected.Slug, null, 1) + (selected.Slug is null ? "?" : "&") + "handler=Recommendations"));
            }
            page.Content.Add(new Div([.. recommendations.Content]).Attribute("id", "discover-recommendations"));
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
                var slug = Types.First(type => type.Type == group.Key).Slug;
                if (selected.Slug is null && group.Any(shelf => shelf.Items.Count > 0))
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
            if (texts.Capabilities?.CanReadMedia is not true) return Forbid();

            await LoadDataAsync();
            PreparePage();
            return Partial("_UiComponent", Definition.Content[0]);
        }

        public async Task<IActionResult> OnGetRecommendationsAsync()
        {
            if (texts.Capabilities?.CanReadMedia is not true) return Forbid();
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
            if (texts.Capabilities?.CanReadMedia is not true)
            {
                return;
            }

            await LoadListedAsync();

            var selected = Selected;
            try
            {
                if (!string.IsNullOrWhiteSpace(Query))
                {
                    _results = await search.ExecuteAsync<BaseDataList<DiscoverItemV1>>(new SearchDiscoverDataV1(Query.Trim(), selected.Slug is null ? null : selected.Type, PageNumber), HttpContext.RequestAborted);
                    return;
                }

                if (selected.Slug is not null && View is { } view && selected.Feeds.Contains(view))
                {
                    _results = await feeds.ExecuteAsync<BaseDataList<DiscoverItemV1>>(new ReadDiscoverFeedDataV1(selected.Type, view, PageNumber), HttpContext.RequestAborted);
                    return;
                }
            }
            catch (ServiceUnavailableException)
            {
                _providerUnavailable = true;
                _results = [];
                return;
            }

            // Local shelves never wait for the provider.
            if (texts.Capabilities?.CanPlay is true || texts.Capabilities?.CanRead is true)
            {
                _continue = await continuation.ExecuteAsync<BaseDataList<ContinueItemV1>>(new ReadContinueParametersV1(), HttpContext.RequestAborted);
            }

            _recent = (await library.ExecuteAsync<PageResult<LibraryItemV1>>(new ReadLibraryParametersV1(selected.Slug is null ? null : selected.Type, pageSize: 20), HttpContext.RequestAborted)).Items;
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
            _listed = [.. media.Where(item => !item.IsSubtype && !item.IsHidden).Select(item => Enum.Parse<MediaType>(item.Key))];
        }

        private async Task LoadRecommendationsAsync()
        {
            await LoadListedAsync();
            var selected = Selected;
            foreach (var (_, type, typeFeeds) in Listed.Where(type => selected.Slug is null || type.Slug == selected.Slug))
            {
                foreach (var feed in typeFeeds)
                {
                    try
                    {
                        _shelves.Add((type, feed, await feeds.ExecuteAsync<BaseDataList<DiscoverItemV1>>(new ReadDiscoverFeedDataV1(type, feed), HttpContext.RequestAborted)));
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
            var card = new MediaCard(item.Title, DetailsHref(item), item.PosterUrl, item.ReleaseYear?.ToString(CultureInfo.InvariantCulture),
                "media.state." + item.MediaStateTypeId, actionKey, actionHref).Information(item.Overview);
            if (item.MediaStateTypeId == MediaStateType.Requested) card.Availability(new MediaAvailability(assets, MediaAvailabilityState.Requested));
            return card;
        }

        // The canonical next step: use what exists, request what is missing.
        private (string? Key, string? Href) Action(DiscoverItemV1 item) => item.MediaStateTypeId switch
        {
            MediaStateType.Available when item.PlexUrl is { } plex && texts.Capabilities?.CanPlay is not true => ("media.openInPlex", plex),
            MediaStateType.Available => ("media.watch", DetailsHref(item)),
            MediaStateType.NotRequested when texts.Capabilities?.CanRequest is true => ("request.create", DetailsHref(item)),
            _ => (null, null)
        };

        private static string DetailsHref(DiscoverItemV1 item) => item.WorkId is { } workId
            ? "/Details/" + workId.ToString(CultureInfo.InvariantCulture)
            : "/Discover/" + Types.First(type => type.Type == item.MediaTypeId).Slug + "/" + item.TmdbId!.Value.ToString(CultureInfo.InvariantCulture);

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
