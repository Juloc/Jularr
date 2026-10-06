using System.Security.Claims;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Shell;

namespace Jularr.Tests;

/// <summary>
/// Covers the provider-driven discovery shelf surface (#595): the row taxonomy, media-core identity
/// de-duplication across sources, capability-filtered rows and the board cache. All pure/faked, so no
/// network is touched.
/// </summary>
[TestClass]
public sealed class DiscoveryShelfTests
{
    [TestMethod]
    public void PlanBuildsProviderBackedRowsForEverySupportedType()
    {
        var plans = DiscoveryShelfComposer.Plan([WorkMediaType.Anime, WorkMediaType.Movie, WorkMediaType.Series, WorkMediaType.Manga, WorkMediaType.LightNovel, WorkMediaType.Book]);

        CollectionAssert.AreEqual(
            new[]
            {
                "trending-anime", "top-anime", "new-anime", "upcoming-anime",
                "trending-series", "top-series", "new-series", "upcoming-series",
                "trending-movie", "top-movie", "new-movie", "upcoming-movie",
                "trending-lightnovel", "top-lightnovel", "new-lightnovel", "upcoming-lightnovel",
                "trending-book", "top-book", "new-book",
                "trending-manga", "top-manga", "new-manga", "upcoming-manga"
            },
            plans.Select(plan => plan.Id).ToArray());

        Assert.AreEqual(6, plans.Count(plan => plan.Kind == DiscoveryShelfKind.NewlyPublished));
        Assert.AreEqual(5, plans.Count(plan => plan.Kind == DiscoveryShelfKind.Upcoming));
        Assert.IsFalse(plans.Any(plan => plan.Mode == DiscoveryMode.Upcoming && plan.Category == DiscoveryCategory.Book), "Open Library announces nothing in advance.");
    }

    [TestMethod]
    public async Task OnlyTheRowsOfTheScopeTheViewerPickedAreRequestedSoNoOtherSourceIsCalledOrCounted()
    {
        var feed = new FakeFeed();
        var service = new DiscoveryShelfService(feed, Shell(WorkMediaType.Anime, WorkMediaType.Movie, WorkMediaType.Series, WorkMediaType.Book, WorkMediaType.LightNovel));

        var anime = await service.GetBoardAsync(null, "scope", isOwner: false, DiscoveryCategory.Anime, DiscoveryWait.None, CancellationToken.None);
        var lightNovels = await service.GetBoardAsync(null, "scope", isOwner: false, DiscoveryCategory.LightNovel, DiscoveryWait.None, CancellationToken.None);

        Assert.IsTrue(anime.Rows.All(row => row.Category == DiscoveryCategory.Anime));
        Assert.IsTrue(feed.Requested[0].All(request => request.Category == DiscoveryCategory.Anime), "The Anime tab starts no TMDB, books or reading call.");
        Assert.IsTrue(lightNovels.Rows.All(row => row.Category == DiscoveryCategory.LightNovel));
        Assert.IsTrue(feed.Requested[1].All(request => request.Category == DiscoveryCategory.LightNovel));
    }

    [TestMethod]
    public async Task ARowThatWaitsKeepsItsPlaceAndAFailedRowStaysVisibleWhileAnEmptyOneIsDropped()
    {
        var feed = new FakeFeed
        {
            StateOf = request => request.Category switch
            {
                DiscoveryCategory.Movie => DiscoverySourceState.Pending,
                DiscoveryCategory.Series when request.Mode == DiscoveryMode.Top => DiscoverySourceState.Unavailable,
                DiscoveryCategory.Series when request.Mode == DiscoveryMode.New => DiscoverySourceState.Ready,
                _ => DiscoverySourceState.Ready
            },
            NoItemsFor = request => request.Category == DiscoveryCategory.Series && request.Mode == DiscoveryMode.New
        };
        var service = new DiscoveryShelfService(feed, Shell(WorkMediaType.Movie, WorkMediaType.Series));

        var board = await service.GetBoardAsync(null, "waiting", isOwner: false, DiscoveryCategory.All, DiscoveryWait.None, CancellationToken.None);

        Assert.AreEqual(DiscoverySectionState.Pending, board.Rows.First(row => row.Id == "trending-movie").State);
        Assert.AreEqual(DiscoverySectionState.Unavailable, board.Rows.Single(row => row.Id == "top-series").State);
        Assert.IsFalse(board.Rows.Any(row => row.Id == "new-series"), "A row that every source answered with nothing is redundant chrome.");
        CollectionAssert.AreEqual(
            new[] { "trending-series", "top-series", "upcoming-series", "trending-movie", "top-movie", "new-movie", "upcoming-movie" },
            board.Rows.Select(row => row.Id).ToArray(),
            "The rows keep the order of the plan whatever their state.");
    }

    [TestMethod]
    public void DeepLinkMatchesTheClientDiscoverUrlScheme()
    {
        Assert.AreEqual("/", DiscoveryShelfLinks.ToDiscoverUrl(DiscoveryCategory.All, DiscoveryMode.Trending, ""));
        Assert.AreEqual("/?category=anime&mode=trending", DiscoveryShelfLinks.ToDiscoverUrl(DiscoveryCategory.Anime, DiscoveryMode.Trending, ""), "A scope of one type opens its overview without a mode, so its Trending view names itself.");
        Assert.AreEqual("/?category=book&mode=new", DiscoveryShelfLinks.ToDiscoverUrl(DiscoveryCategory.Book, DiscoveryMode.New, ""));
        Assert.AreEqual("/?category=movie&mode=upcoming", DiscoveryShelfLinks.ToDiscoverUrl(DiscoveryCategory.Movie, DiscoveryMode.Upcoming, ""));
        Assert.AreEqual("/?category=tv&mode=top", DiscoveryShelfLinks.ToDiscoverUrl(DiscoveryCategory.Series, DiscoveryMode.Top, ""));
        Assert.AreEqual("/?category=light-novel&mode=top", DiscoveryShelfLinks.ToDiscoverUrl(DiscoveryCategory.LightNovel, DiscoveryMode.Top, ""));
    }

    [TestMethod]
    public void DiscoverClientRoutesBetweenShelfLandingAndGrid()
    {
        var script = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "js", "discover.js"));

        // Rows or the result grid are one server-rendered body fetched from the Body handler after
        // first paint; the address decides which of the two the server renders.
        StringAssert.Contains(script, "data-dc-body");
        StringAssert.Contains(script, "params.set(\"handler\", \"Body\")");
        Assert.IsFalse(script.Contains("handler=Results", StringComparison.Ordinal), "The JSON Results handler is gone.");
        // The debounce and stale-request guards must survive.
        StringAssert.Contains(script, "const SEARCH_DELAY = 250");
        StringAssert.Contains(script, "abortController?.abort()");
        StringAssert.Contains(script, "requestVersion");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Jularr repository root.");
    }

    private static IAppShellService Shell(params WorkMediaType[] visible)
    {
        var capabilities = WorkMediaTypes.All.ToDictionary(
            type => type,
            type => visible.Contains(type) ? MediaCapability.Browse : MediaCapability.Hidden);
        return new FakeShell(new ShellMediaAccess(new MediaCapabilityView(false, capabilities)));
    }

    private static DiscoveryItem Item(string category, string provider, string externalId, string title) =>
        new(
            $"{provider}:{category}:{externalId}",
            category,
            provider,
            externalId,
            title,
            null, null, null, null, null, null, null, null, null, null,
            [],
            false,
            null,
            "/details",
            false);

    private sealed class FakeShell(ShellMediaAccess access) : IAppShellService
    {
        public Task<ShellMediaAccess> GetMediaAccessAsync(ClaimsPrincipal? user, CancellationToken cancellationToken = default) =>
            Task.FromResult(access);
    }

    private sealed class FakeFeed : IDiscoveryFeed
    {
        public Task<bool> IsAniListConnectedAsync(CancellationToken cancellationToken) => Task.FromResult(false);

        public int Loads { get; private set; }

        /// <summary>The requests of every load, in the order the loads were made.</summary>
        public List<IReadOnlyList<DiscoveryRequest>> Requested { get; } = [];

        public Func<DiscoveryRequest, DiscoverySourceState> StateOf { get; init; } = _ => DiscoverySourceState.Ready;

        public Func<DiscoveryRequest, bool> NoItemsFor { get; init; } = _ => false;

        public Task<DiscoveryLoad> LoadAsync(
            IReadOnlyList<DiscoveryRequest> requests,
            DiscoveryAudience audience,
            DiscoveryWait wait,
            CancellationToken cancellationToken)
        {
            Loads++;
            Requested.Add(requests);
            var batches = requests.Select(request =>
            {
                var category = request.Category switch
                {
                    DiscoveryCategory.Anime => "anime",
                    DiscoveryCategory.Movie => "movie",
                    DiscoveryCategory.Series => "tv",
                    DiscoveryCategory.Manga => "manga",
                    DiscoveryCategory.LightNovel => "light-novel",
                    DiscoveryCategory.Book => "book",
                    _ => "all"
                };
                var provider = category switch
                {
                    "book" => "openlibrary",
                    "movie" or "tv" => "tmdb",
                    _ => "anilist"
                };
                var state = StateOf(request);
                IReadOnlyList<DiscoveryItem> items = state != DiscoverySourceState.Ready || NoItemsFor(request)
                    ? []
                    : [Item(category, provider, $"{category}-1", $"{category} one"), Item(category, provider, $"{category}-2", $"{category} two")];
                return new DiscoveryBatch(request, false, [new DiscoverySourceResult(DiscoverySources.For(request.Category)[0], state, items)]);
            }).ToArray();
            return Task.FromResult(new DiscoveryLoad(batches, batches.Count(batch => !batch.HasPending), batches.Count(batch => batch.HasPending)));
        }

        public Task<IReadOnlyDictionary<string, DiscoveryItem>> OverlayLocalStateAsync(IEnumerable<DiscoveryItem> items, string profileId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, DiscoveryItem>>(items.DistinctBy(item => item.Id).ToDictionary(item => item.Id));
    }
}
