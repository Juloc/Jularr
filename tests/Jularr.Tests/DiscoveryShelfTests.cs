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
                "trending-anime", "trending-movie", "trending-series", "trending-manga", "trending-lightnovel", "trending-book",
                "top-anime", "top-movie", "top-series", "top-manga", "top-lightnovel", "top-book",
                "new-movie", "upcoming-movie", "new-series", "upcoming-series", "new-book"
            },
            plans.Select(plan => plan.Id).ToArray());

        Assert.AreEqual(3, plans.Count(plan => plan.Kind == DiscoveryShelfKind.NewlyPublished));
        Assert.AreEqual(2, plans.Count(plan => plan.Kind == DiscoveryShelfKind.Upcoming));
        Assert.IsTrue(plans.Where(plan => plan.Mode == DiscoveryMode.Upcoming)
            .All(plan => plan.Category is DiscoveryCategory.Movie or DiscoveryCategory.Series));
    }

    [TestMethod]
    public void PlanOffersOnlyTheRowsOfTheVisibleTypes()
    {
        var plans = DiscoveryShelfComposer.Plan([WorkMediaType.Movie, WorkMediaType.Series]);

        CollectionAssert.AreEqual(
            new[]
            {
                "trending-movie", "trending-series",
                "top-movie", "top-series",
                "new-movie", "upcoming-movie",
                "new-series", "upcoming-series"
            },
            plans.Select(plan => plan.Id).ToArray());
        Assert.AreEqual(0, DiscoveryShelfComposer.Plan([]).Count);
    }

    [TestMethod]
    public void DeduplicateMergesTheSameWorkAcrossSourcesByIdentity()
    {
        // Two sources return the same AniList anime with different provider casing/ids; the media-core
        // identity normalises them, so the row keeps one (first occurrence wins).
        var sourceA = new[] { Item("anime", "anilist", "111", "Frieren"), Item("anime", "anilist", "222", "Dandadan") };
        var sourceB = new[] { Item("anime", "AniList", "111", "Frieren (dup)"), Item("book", "openlibrary", "111", "A Book") };

        var deduped = DiscoveryShelfComposer.Deduplicate(sourceA.Concat(sourceB));

        Assert.AreEqual(3, deduped.Count);
        Assert.AreEqual("Frieren", deduped[0].Title, "First occurrence of the shared identity must win.");
        // Same external id but a different media type is a different work and is kept.
        Assert.IsTrue(deduped.Any(item => item.Category == "book" && item.ExternalId == "111"));
    }

    [TestMethod]
    public async Task BoardOnlyContainsRowsForVisibleMediaTypes()
    {
        var feed = new FakeFeed();
        var service = new DiscoveryShelfService(feed, Shell(WorkMediaType.Book));

        var board = await service.GetBoardAsync(null, "cap-filter", isOwner: false, DiscoveryCategory.All, DiscoveryWait.None, CancellationToken.None);

        Assert.IsTrue(board.Rows.Count > 0);
        Assert.IsTrue(board.Rows.All(row => row.MediaType == WorkMediaType.Book), "A Books-only profile must see book rows only.");
        Assert.IsTrue(board.Rows.All(row => row.Items.Count > 0), "Empty rows must be dropped.");
    }

    [TestMethod]
    public async Task BoardContainsTmdbRowsForVisibleMovieAndSeriesTypes()
    {
        var feed = new FakeFeed();
        var service = new DiscoveryShelfService(feed, Shell(WorkMediaType.Movie, WorkMediaType.Series));

        var board = await service.GetBoardAsync(null, "tmdb-feed", isOwner: false, DiscoveryCategory.All, DiscoveryWait.None, CancellationToken.None);

        Assert.IsFalse(board.IsEmpty);
        Assert.IsTrue(board.Rows.All(row => row.MediaType is WorkMediaType.Movie or WorkMediaType.Series));
        Assert.AreEqual(1, feed.Loads, "Every row of the board is requested in one load, so the provider calls run side by side.");
    }

    [TestMethod]
    public async Task OnlyTheRowsOfTheScopeTheViewerPickedAreRequestedSoNoOtherSourceIsCalledOrCounted()
    {
        var feed = new FakeFeed();
        var service = new DiscoveryShelfService(feed, Shell(WorkMediaType.Anime, WorkMediaType.Movie, WorkMediaType.Series, WorkMediaType.Book, WorkMediaType.LightNovel));

        var anime = await service.GetBoardAsync(null, "scope", isOwner: false, DiscoveryCategory.Anime, DiscoveryWait.None, CancellationToken.None);
        var novels = await service.GetBoardAsync(null, "scope", isOwner: false, DiscoveryCategory.LightNovel, DiscoveryWait.None, CancellationToken.None);
        var books = await service.GetBoardAsync(null, "scope", isOwner: false, DiscoveryCategory.Book, DiscoveryWait.None, CancellationToken.None);

        Assert.IsTrue(anime.Rows.All(row => row.Category == DiscoveryCategory.Anime));
        Assert.IsTrue(feed.Requested[0].All(request => request.Category == DiscoveryCategory.Anime), "The Anime tab starts no TMDB, books or reading call.");
        Assert.IsTrue(novels.Rows.Count > 0 && novels.Rows.All(row => row.Category == DiscoveryCategory.LightNovel), "The Light Novels tab shows the AniList novels only.");
        Assert.IsTrue(feed.Requested[1].All(request => request.Category == DiscoveryCategory.LightNovel));
        Assert.IsTrue(books.Rows.Count > 0 && books.Rows.All(row => row.Category == DiscoveryCategory.Book), "The Books tab shows the book catalogs only.");
        Assert.IsTrue(feed.Requested[2].All(request => request.Category == DiscoveryCategory.Book));
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
            new[] { "trending-movie", "trending-series", "top-movie", "top-series", "new-movie", "upcoming-movie", "upcoming-series" },
            board.Rows.Select(row => row.Id).ToArray(),
            "The rows keep the order of the plan whatever their state.");
    }

    [TestMethod]
    public void DeepLinkMatchesTheClientDiscoverUrlScheme()
    {
        Assert.AreEqual("/Discover", DiscoveryShelfLinks.ToDiscoverUrl(DiscoveryCategory.All, DiscoveryMode.Trending, ""));
        Assert.AreEqual("/Discover?category=anime", DiscoveryShelfLinks.ToDiscoverUrl(DiscoveryCategory.Anime, DiscoveryMode.Trending, ""));
        Assert.AreEqual("/Discover?category=book&mode=new", DiscoveryShelfLinks.ToDiscoverUrl(DiscoveryCategory.Book, DiscoveryMode.New, ""));
        Assert.AreEqual("/Discover?category=movie&mode=upcoming", DiscoveryShelfLinks.ToDiscoverUrl(DiscoveryCategory.Movie, DiscoveryMode.Upcoming, ""));
        Assert.AreEqual("/Discover?category=tv&mode=top", DiscoveryShelfLinks.ToDiscoverUrl(DiscoveryCategory.Series, DiscoveryMode.Top, ""));
        Assert.AreEqual("/Discover?category=light-novel&mode=top", DiscoveryShelfLinks.ToDiscoverUrl(DiscoveryCategory.LightNovel, DiscoveryMode.Top, ""));
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
        // The stale-request guards must survive; a host such as Home names the page that serves the body.
        StringAssert.Contains(script, "bodyUrl || window.location.pathname");
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
