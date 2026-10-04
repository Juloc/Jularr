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
        var plans = DiscoveryShelfComposer.Plan(
            [WorkMediaType.Anime, WorkMediaType.Movie, WorkMediaType.Series, WorkMediaType.Manga, WorkMediaType.LightNovel, WorkMediaType.Book],
            includeAniList: true,
            includeBooks: true);

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
    public void BooksAndLightNovelsBecomeOneInterleavedDiscoverShelfPerMode()
    {
        DiscoveryShelfRow Row(
            string id,
            DiscoveryShelfKind kind,
            WorkMediaType type,
            DiscoveryCategory category,
            DiscoveryMode mode,
            params DiscoveryItem[] items) =>
            new(
                id,
                kind,
                type,
                category,
                mode,
                "",
                mode == DiscoveryMode.Top ? "discover.tabs.top" : mode == DiscoveryMode.New ? "discover.tabs.new" : "discover.tabs.trending",
                type == WorkMediaType.Book ? "nav.books" : "discover.categories.lightNovel",
                items);

        var rows = new[]
        {
            Row("trending-anime", DiscoveryShelfKind.Trending, WorkMediaType.Anime, DiscoveryCategory.Anime, DiscoveryMode.Trending,
                Item("anime", "anilist", "a1", "Anime")),
            Row("trending-lightnovel", DiscoveryShelfKind.Trending, WorkMediaType.LightNovel, DiscoveryCategory.LightNovel, DiscoveryMode.Trending,
                Item("light-novel", "anilist", "ln1", "LN 1"),
                Item("light-novel", "anilist", "ln2", "LN 2")),
            Row("trending-book", DiscoveryShelfKind.Trending, WorkMediaType.Book, DiscoveryCategory.Book, DiscoveryMode.Trending,
                Item("book", "openlibrary", "b1", "Book 1"),
                Item("book", "openlibrary", "b2", "Book 2")),
            Row("top-lightnovel", DiscoveryShelfKind.Top, WorkMediaType.LightNovel, DiscoveryCategory.LightNovel, DiscoveryMode.Top,
                Item("light-novel", "anilist", "ln3", "LN 3")),
            Row("top-book", DiscoveryShelfKind.Top, WorkMediaType.Book, DiscoveryCategory.Book, DiscoveryMode.Top,
                Item("book", "openlibrary", "b3", "Book 3")),
            Row("new-book", DiscoveryShelfKind.NewlyPublished, WorkMediaType.Book, DiscoveryCategory.Book, DiscoveryMode.New,
                Item("book", "openlibrary", "b4", "Book 4"))
        };

        var combined = DiscoveryShelfComposer.CombineBooksAndLightNovels(rows);

        CollectionAssert.AreEqual(
            new[]
            {
                "trending-anime",
                "trending-books-light-novels",
                "top-books-light-novels",
                "new-book"
            },
            combined.Select(row => row.Id).ToArray());

        var trending = combined.Single(row => row.Id == "trending-books-light-novels");
        Assert.IsNull(trending.MediaType);
        Assert.AreEqual(DiscoveryCategory.BooksAndLightNovels, trending.Category);
        CollectionAssert.AreEqual(
            new[] { "LN 1", "Book 1", "LN 2", "Book 2" },
            trending.Items.Select(item => item.Title).ToArray());
        Assert.AreEqual(
            "/Discover?category=books-light-novels",
            trending.DeepLinkUrl);

        var newBooks = combined.Single(row => row.Id == "new-book");
        Assert.AreEqual(DiscoveryCategory.Book, newBooks.Category);
    }

    [TestMethod]
    public void PlanUsesTmdbForMovieAndSeriesIndependentlyOfAniList()
    {
        var plans = DiscoveryShelfComposer.Plan(
            [WorkMediaType.Movie, WorkMediaType.Series, WorkMediaType.Anime],
            includeAniList: false,
            includeBooks: false);

        CollectionAssert.AreEqual(
            new[]
            {
                "trending-movie", "trending-series",
                "top-movie", "top-series",
                "new-movie", "upcoming-movie",
                "new-series", "upcoming-series"
            },
            plans.Select(plan => plan.Id).ToArray());
        Assert.IsTrue(plans.All(plan => !plan.UsesAniList && !plan.UsesBooks));
    }

    [TestMethod]
    public void PlanRespectsDisabledSources()
    {
        var booksOnly = DiscoveryShelfComposer.Plan(
            [WorkMediaType.Anime, WorkMediaType.Book],
            includeAniList: false,
            includeBooks: true);
        Assert.IsTrue(booksOnly.All(plan => plan.Category == DiscoveryCategory.Book));

        var aniListOnly = DiscoveryShelfComposer.Plan(
            [WorkMediaType.Anime, WorkMediaType.Book],
            includeAniList: true,
            includeBooks: false);
        Assert.IsFalse(aniListOnly.Any(plan => plan.Category == DiscoveryCategory.Book));
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
        DiscoveryShelfService.InvalidateCache();
        var feed = new FakeFeed();
        var service = new DiscoveryShelfService(feed, Shell(WorkMediaType.Book));

        var board = await service.GetBoardAsync(
            null, "cap-filter", isOwner: false, includeAniList: true, includeBooks: true, CancellationToken.None);

        Assert.IsTrue(board.Rows.Count > 0);
        Assert.IsTrue(
            board.Rows.All(row => row.MediaType == WorkMediaType.Book),
            "A Books-only profile must see book rows only.");
        Assert.IsTrue(board.Rows.All(row => row.Items.Count > 0), "Empty rows must be dropped.");
    }

    [TestMethod]
    public async Task BoardContainsTmdbRowsForVisibleMovieAndSeriesTypes()
    {
        DiscoveryShelfService.InvalidateCache();
        var feed = new FakeFeed();
        var service = new DiscoveryShelfService(feed, Shell(WorkMediaType.Movie, WorkMediaType.Series));

        var board = await service.GetBoardAsync(
            null, "tmdb-feed", isOwner: false, includeAniList: true, includeBooks: true, CancellationToken.None);

        Assert.IsFalse(board.IsEmpty);
        Assert.IsTrue(board.Rows.All(row => row.MediaType is WorkMediaType.Movie or WorkMediaType.Series));
        Assert.IsTrue(feed.Calls > 0);
    }

    [TestMethod]
    public async Task BoardIsCachedPerProfileWithinTtl()
    {
        DiscoveryShelfService.InvalidateCache();
        var feed = new FakeFeed();
        var service = new DiscoveryShelfService(feed, Shell(WorkMediaType.Book));

        await service.GetBoardAsync(null, "cache-a", false, true, true, CancellationToken.None);
        var afterFirst = feed.Calls;
        Assert.IsTrue(afterFirst > 0);

        await service.GetBoardAsync(null, "cache-a", false, true, true, CancellationToken.None);
        Assert.AreEqual(afterFirst, feed.Calls, "A repeat board request within TTL must be served from cache.");

        await service.GetBoardAsync(null, "cache-b", false, true, true, CancellationToken.None);
        Assert.AreEqual(afterFirst * 2, feed.Calls, "A different profile must not hit another profile's cached board.");
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
        public int Calls { get; private set; }

        public Task<DiscoveryResponse> GetAsync(
            DiscoveryRequest request,
            string profileId,
            bool isOwner,
            bool includeAniList,
            bool includeBooks,
            CancellationToken cancellationToken)
        {
            Calls++;
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
            IReadOnlyList<DiscoveryItem> items =
            [
                Item(category, provider, $"{category}-1", $"{category} one"),
                Item(category, provider, $"{category}-2", $"{category} two")
            ];

            return Task.FromResult(new DiscoveryResponse(
                request.Query,
                category,
                request.Mode.ToString().ToLowerInvariant(),
                request.Genre,
                AniListConnected: false,
                items,
                []));
        }
    }
}
