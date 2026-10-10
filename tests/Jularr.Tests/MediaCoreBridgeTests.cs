using Jularr.Web.Features.Books;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Novels;

namespace Jularr.Tests;

[TestClass]
public sealed class MediaCoreBridgeTests
{
    private static LegacyWorkBridge Bridge(Jularr.Web.Data.AppDbContext db) =>
        new(db, new WorkService(db), new WorkStructureService(db));

    [TestMethod]
    public async Task AnimeBridgeIsIdempotentAndResolvesBackToSource()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var anime = new Anime { Key = "attack-on-titan", Title = "Attack on Titan" };
        db.Anime.Add(anime);
        await db.SaveChangesAsync();

        var bridge = Bridge(db);
        var first = await bridge.EnsureWorkForAnimeAsync(anime, CancellationToken.None);
        var second = await bridge.EnsureWorkForAnimeAsync(anime, CancellationToken.None);

        Assert.AreEqual(first, second, "Bridging the same anime twice must resolve to the same work.");
        Assert.AreEqual(1, db.Works.Count());
        // The legacy anime row is untouched and still present.
        Assert.AreEqual("Attack on Titan", db.Anime.Single().Title);

        var query = new WorkQueryService(db);
        Assert.AreEqual(first, await query.ResolveWorkForSourceAsync(WorkSourceKind.Anime, anime.Id, CancellationToken.None));
        var work = await query.GetWorkAsync(first, CancellationToken.None);
        Assert.AreEqual(WorkMediaType.Series, work!.MediaType);
        Assert.IsTrue(work.IsAnime, "Anime is now classified on a Series work, not a separate media type.");
    }

    [TestMethod]
    public async Task NovelBridgeMirrorsSourceAndMetadataIdentities()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var novel = new NovelWork
        {
            SourceProvider = "ncode",
            SourceKey = "n0000aa",
            SourceUrl = "https://ncode.syosetu.com/n0000aa/",
            Title = "Mushoku Tensei",
            MetadataProvider = "anilist",
            MetadataExternalId = "97852",
            MetadataNativeTitle = "無職転生"
        };
        db.NovelWorks.Add(novel);
        await db.SaveChangesAsync();

        var workId = await Bridge(db).EnsureWorkForNovelAsync(novel, WorkMediaType.LightNovel, CancellationToken.None);
        var query = new WorkQueryService(db);

        var identities = await query.GetIdentitiesAsync(workId, CancellationToken.None);
        Assert.IsTrue(identities.Any(i => i.Provider == "ncode" && i.ExternalId == "n0000aa"));
        Assert.IsTrue(identities.Any(i => i.Provider == "anilist" && i.ExternalId == "97852"));
        var titles = await query.GetTitlesAsync(workId, CancellationToken.None);
        Assert.IsTrue(titles.Any(t => t.TitleType == WorkTitleType.Native && t.Value == "無職転生"));

        // Provider identity resolution works through the bridged work.
        Assert.AreEqual(workId, await query.FindWorkIdByExternalIdentityAsync(WorkMediaType.LightNovel, "anilist", "97852", CancellationToken.None));
    }

    [TestMethod]
    public async Task BookBridgeCreatesEditionAndIsbnIdentity()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var novel = new NovelWork { SourceProvider = "hardcover", SourceKey = "hc-1", Title = "Dune" };
        db.NovelWorks.Add(novel);
        await db.SaveChangesAsync();
        var edition = new BookEdition
        {
            WorkId = novel.Id,
            EditionKey = "ace-1990",
            Language = "en",
            Isbn13 = "9780441172719",
            Publisher = "Ace",
            Title = "Dune",
            IsPrimary = true
        };
        db.BookEditions.Add(edition);
        await db.SaveChangesAsync();

        var workId = await Bridge(db).EnsureWorkForBookEditionAsync(edition, CancellationToken.None);
        var query = new WorkQueryService(db);

        Assert.AreEqual(WorkMediaType.Book, (await query.GetWorkAsync(workId, CancellationToken.None))!.MediaType);
        var editions = await query.GetEditionsAsync(workId, CancellationToken.None);
        Assert.AreEqual(1, editions.Count);
        Assert.AreEqual("9780441172719", editions[0].Isbn13);
        Assert.IsTrue((await query.GetIdentitiesAsync(workId, CancellationToken.None)).Any(i => i.Provider == "isbn"));
        // Both the novel work and the book edition resolve to the same universal work.
        Assert.AreEqual(workId, await query.ResolveWorkForSourceAsync(WorkSourceKind.NovelWork, novel.Id, CancellationToken.None));
        Assert.AreEqual(workId, await query.ResolveWorkForSourceAsync(WorkSourceKind.BookEdition, edition.Id, CancellationToken.None));
    }

    [TestMethod]
    public async Task MangaBridgeLinksSeriesAndAniListId()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var seriesId = Guid.NewGuid();

        var workId = await Bridge(db).EnsureWorkForMangaSeriesAsync(
            seriesId, "Berserk", "ベルセルク", "30002", CancellationToken.None);
        var query = new WorkQueryService(db);

        Assert.AreEqual(WorkMediaType.Manga, (await query.GetWorkAsync(workId, CancellationToken.None))!.MediaType);
        Assert.AreEqual(workId, await query.ResolveWorkForSourceAsync(WorkSourceKind.MangaSeries, seriesId, CancellationToken.None));
        Assert.AreEqual(workId, await query.FindWorkIdByExternalIdentityAsync(WorkMediaType.Manga, "anilist", "30002", CancellationToken.None));
    }
}
