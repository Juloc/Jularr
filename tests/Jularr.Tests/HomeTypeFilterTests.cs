using Jularr.Web.Data;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Reading;
using Jularr.Web.Pages;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>
/// #520: the Home All/Anime/Manga/Novels/Books filter chip narrows Continue
/// Watching and Continue Reading by media type via <c>?type=</c>, and every
/// visible row heading links to the matching Discover "My AniList" filter.
/// </summary>
[TestClass]
public sealed class HomeTypeFilterTests
{
    private const string Profile = "home-filter";

    [TestMethod]
    public async Task AllChipIsTheDefaultAndShowsEveryContinueRow()
    {
        await using var fixture = await ContinueReadingQueryTests.ContinueReadingFixture.CreateAsync();
        await SeedAnimeEpisodeWithProgressAsync(fixture.Db);
        var novel = await fixture.SeedNovelAsync("Novel", chapters: 2);
        await fixture.SetNovelProgressAsync(Profile, novel, 0, 200, DateTime.UtcNow);

        var home = await LoadHomeAsync(fixture.Db, type: null);

        Assert.AreEqual(DiscoveryCategory.All, home.ActiveType);
        Assert.AreEqual(1, home.ContinueWatching.Count);
        Assert.AreEqual(1, home.ContinueReading.Count);
        Assert.AreEqual("/Discover?mode=my-list", home.ContinueWatchingDiscoverUrl);
        Assert.AreEqual("/Discover?mode=my-list", home.ContinueReadingDiscoverUrl);
    }

    [TestMethod]
    public async Task AnimeChipKeepsWatchingAndHidesReading()
    {
        await using var fixture = await ContinueReadingQueryTests.ContinueReadingFixture.CreateAsync();
        await SeedAnimeEpisodeWithProgressAsync(fixture.Db);
        var novel = await fixture.SeedNovelAsync("Novel", chapters: 2);
        await fixture.SetNovelProgressAsync(Profile, novel, 0, 200, DateTime.UtcNow);

        var home = await LoadHomeAsync(fixture.Db, type: "anime");

        Assert.AreEqual(DiscoveryCategory.Anime, home.ActiveType);
        Assert.AreEqual(1, home.ContinueWatching.Count);
        Assert.AreEqual(0, home.ContinueReading.Count);
    }

    [TestMethod]
    public async Task MangaChipKeepsOnlyMangaAndHidesWatching()
    {
        await using var fixture = await ContinueReadingQueryTests.ContinueReadingFixture.CreateAsync();
        await SeedAnimeEpisodeWithProgressAsync(fixture.Db);
        var novel = await fixture.SeedNovelAsync("Novel", chapters: 2);
        await fixture.SetNovelProgressAsync(Profile, novel, 0, 200, DateTime.UtcNow.AddMinutes(-1));
        var manga = await fixture.SeedMangaAsync("Manga", (1, 10));
        await fixture.SetMangaProgressAsync(Profile, manga, 0, 2, DateTime.UtcNow);

        var home = await LoadHomeAsync(fixture.Db, type: "manga");

        Assert.AreEqual(DiscoveryCategory.Manga, home.ActiveType);
        Assert.AreEqual(0, home.ContinueWatching.Count);
        var item = Assert.ContainsSingle(home.ContinueReading);
        Assert.AreEqual(ContinueReadingKind.Manga, item.Kind);
        Assert.AreEqual("/Discover?category=manga&mode=my-list", home.ContinueReadingDiscoverUrl);
    }

    [TestMethod]
    public async Task NovelsChipKeepsOnlyLightNovels()
    {
        await using var fixture = await ContinueReadingQueryTests.ContinueReadingFixture.CreateAsync();
        var novel = await fixture.SeedNovelAsync("Novel", chapters: 2);
        await fixture.SetNovelProgressAsync(Profile, novel, 0, 200, DateTime.UtcNow);
        var manga = await fixture.SeedMangaAsync("Manga", (1, 10));
        await fixture.SetMangaProgressAsync(Profile, manga, 0, 2, DateTime.UtcNow.AddMinutes(-1));

        var home = await LoadHomeAsync(fixture.Db, type: "novels");

        Assert.AreEqual(DiscoveryCategory.LightNovel, home.ActiveType);
        var item = Assert.ContainsSingle(home.ContinueReading);
        Assert.AreEqual(ContinueReadingKind.Novel, item.Kind);
        Assert.AreEqual("/Discover?category=light-novel&mode=my-list", home.ContinueReadingDiscoverUrl);
    }

    [TestMethod]
    public async Task BooksChipKeepsOnlyImportedBooks()
    {
        await using var fixture = await ContinueReadingQueryTests.ContinueReadingFixture.CreateAsync();
        var novel = await fixture.SeedNovelAsync("Novel", chapters: 2);
        await fixture.SetNovelProgressAsync(Profile, novel, 0, 200, DateTime.UtcNow.AddMinutes(-1));
        var book = await fixture.SeedBookAsync("Book", chapters: 2);
        await fixture.SetNovelProgressAsync(Profile, book, 0, 200, DateTime.UtcNow);

        var home = await LoadHomeAsync(fixture.Db, type: "books");

        Assert.AreEqual(DiscoveryCategory.Book, home.ActiveType);
        var item = Assert.ContainsSingle(home.ContinueReading);
        Assert.AreEqual(ContinueReadingKind.Book, item.Kind);
        Assert.AreEqual("/Discover?category=book&mode=my-list", home.ContinueReadingDiscoverUrl);
    }

    [TestMethod]
    public void ChipsCoverAllFiveMediaTypesInOrder()
    {
        CollectionAssert.AreEqual(
            new[] { "all", "anime", "manga", "novels", "books" },
            IndexModel.TypeChips.Select(x => x.QueryValue).ToArray());
    }

    [TestMethod]
    public void DefaultChipLinksToThePlainRootUrl()
    {
        Assert.AreEqual("/", IndexModel.ChipHref(IndexModel.TypeChips[0]));
        Assert.AreEqual("/?type=anime", IndexModel.ChipHref(IndexModel.TypeChips[1]));
    }

    private static async Task SeedAnimeEpisodeWithProgressAsync(AppDbContext db)
    {
        var anime = new Anime { Key = "home-filter-anime", Title = "Home Filter Anime" };
        var episode = new Episode
        {
            AnimeId = anime.Id,
            SeasonNumber = 1,
            Number = 1,
            Title = "Episode 1"
        };
        var root = new LibraryRoot { Name = "Test", Path = Path.GetTempPath() };
        db.AddRange(anime, episode, root);
        db.Add(new MediaFile
        {
            LibraryRootId = root.Id,
            EpisodeId = episode.Id,
            Path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mkv"),
            SizeBytes = 1,
            LastWriteTimeUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        await CanonicalProgressSeed.AttachCanonicalVideoAsync(db);

        var progress = EpisodeFlowFixture.ProgressService(db, EpisodeFlowFixture.Account(Profile));
        await progress.UpdateAsync(episode.Id, new EpisodeProgressUpdate(500_000, 1_400_000, false));
    }

    private static async Task<IndexModel> LoadHomeAsync(AppDbContext db, string? type)
    {
        var home = EpisodeFlowFixture.Home(db, EpisodeFlowFixture.Account(Profile));
        await home.OnGetAsync(CancellationToken.None, type);
        return home;
    }
}
