using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Data.User.Library.V1;
using Jularr.Backend.Service.User.Discover.V1;
using Jularr.Backend.Service.User.Library.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.Discover
{
    public sealed class IndexModel(ReadDiscoverFeedV1 feeds, SearchDiscoverV1 search, ReadContinueV1 continuation, ReadLibraryV1 library, UiText texts) : JularrPage
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

        [BindProperty(SupportsGet = true, Name = "type")]
        public string? TypeSlug { get; set; }

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
            var selected = Types.FirstOrDefault(type => type.Slug == TypeSlug);

            // Media type, then browse view; filters stay separate.
            page.Content.Add(new NavigationTabs([new NavigationTab("discover.all", "/", selected.Slug is null), .. Types.Select(type =>
                new NavigationTab("discover.type." + type.Slug, "/?type=" + type.Slug, type.Slug == selected.Slug))], "discover.mediaTypes", "media"));
            if (selected.Slug is not null && Query is null)
            {
                page.Content.Add(new NavigationTabs([new NavigationTab("discover.overview", "/?type=" + selected.Slug, View is null), .. selected.Feeds.Select(feed =>
                    new NavigationTab("discover.feed." + feed, Address(selected.Slug, feed, 1), View == feed))], "discover.browseViews", "browse"));
            }

            if (_providerUnavailable)
            {
                page.Content.Add(new P(new Text("discover.providerUnavailable")).Attribute("role", "status"));
            }

            // Search results and full browse views are grids.
            if (_results is { } results)
            {
                if (results.Count == 0)
                {
                    page.Content.Add(new P(new Text(Query is null ? "discover.empty" : "discover.noResults")).Attribute("role", "status"));
                    return;
                }

                page.Content.Add(new Div([.. results.Select(Card)]).Class("media-grid"));
                if (Query is null && View is { } view && results.Count >= 20)
                {
                    page.Content.Add(new A(new Text("common.next")).Class("button").Attribute("href", Address(selected.Slug!, view, PageNumber + 1)));
                }

                return;
            }

            // Landing: featured title, continue, library news and the ranked shelves.
            if (_shelves.SelectMany(shelf => shelf.Items).FirstOrDefault(item => item.BackdropUrl is not null) is { } featured)
            {
                var (actionKey, actionHref) = Action(featured);
                page.Content.Add(new MediaHero(featured.Title, featured.BackdropUrl, featured.ReleaseYear?.ToString(CultureInfo.InvariantCulture), featured.Overview,
                    actionKey ?? "media.details", actionHref ?? DetailsHref(featured), actionKey is null ? null : DetailsHref(featured)));
            }

            page.Content.Add(new MediaShelf("discover.continue", null, [.. _continue.Where(item => item.MediaAssetTypeId != MediaAssetType.Ebook || texts.Capabilities?.CanRead is true).Select(item => new MediaCard(item.Title, "/Details/" + item.WorkId.ToString(CultureInfo.InvariantCulture),
                item.PosterUrl, MediaUnitLabel.Format(texts, item.WorkUnitTypeId, item.UnitNumber, item.ParentUnitNumber), "media.state.InProgress", "media.continue",
                (item.MediaAssetTypeId == MediaAssetType.Ebook ? "/Read/" : "/Player/") + item.MediaAssetId.ToString(CultureInfo.InvariantCulture)))]));
            page.Content.Add(new MediaShelf("discover.recentlyAdded", "/Library", [.. _recent.Select(item => new MediaCard(item.Title, "/Details/" + item.Id.ToString(CultureInfo.InvariantCulture),
                item.PosterUrl, item.ReleaseYear?.ToString(CultureInfo.InvariantCulture), "media.state." + item.MediaStateTypeId))]));
            foreach (var group in _shelves.GroupBy(shelf => shelf.Type))
            {
                var slug = Types.First(type => type.Type == group.Key).Slug;
                if (selected.Slug is null && group.Any(shelf => shelf.Items.Count > 0))
                {
                    page.Content.Add(new H2(new Text("discover.type." + slug)).Class("discover-media-heading"));
                }

                foreach (var (_, feed, items) in group)
                {
                    page.Content.Add(new MediaShelf("discover.feed." + feed, Address(slug, feed, 1), [.. items.Select(Card)]));
                }
            }
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

            var selected = Types.FirstOrDefault(type => type.Slug == TypeSlug);
            try
            {
                if (!string.IsNullOrWhiteSpace(Query))
                {
                    _results = await search.ExecuteAsync<BaseDataList<DiscoverItemV1>>(new SearchDiscoverDataV1(Query.Trim(), selected.Slug is null ? null : selected.Type), HttpContext.RequestAborted);
                    return;
                }

                if (selected.Slug is not null && View is { } view && selected.Feeds.Contains(view))
                {
                    _results = await feeds.ExecuteAsync<BaseDataList<DiscoverItemV1>>(new ReadDiscoverFeedDataV1(selected.Type, view, Math.Clamp(PageNumber, 1, 500)), HttpContext.RequestAborted);
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
            if (texts.Capabilities?.CanPlay is true)
            {
                _continue = await continuation.ExecuteAsync<BaseDataList<ContinueItemV1>>(new ReadContinueParametersV1(), HttpContext.RequestAborted);
            }

            _recent = (await library.ExecuteAsync<PageResult<LibraryItemV1>>(new ReadLibraryParametersV1(selected.Slug is null ? null : selected.Type, pageSize: 20), HttpContext.RequestAborted)).Items;
            foreach (var (_, type, typeFeeds) in Types.Where(type => selected.Slug is null || type.Slug == selected.Slug))
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
                        return;
                    }
                }
            }
        }

        private IUiComponent Card(DiscoverItemV1 item)
        {
            var (actionKey, actionHref) = Action(item);
            return new MediaCard(item.Title, DetailsHref(item), item.PosterUrl, item.ReleaseYear?.ToString(CultureInfo.InvariantCulture), "media.state." + item.MediaStateTypeId, actionKey, actionHref);
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

        private static string Address(string slug, DiscoverFeedType feed, int page) =>
            "/?type=" + slug + "&view=" + feed + (page > 1 ? "&p=" + page.ToString(CultureInfo.InvariantCulture) : "");
    }
}
