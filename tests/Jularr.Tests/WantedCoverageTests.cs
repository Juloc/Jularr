using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Monitoring;
using Jularr.Web.Features.Novels;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class WantedCoverageTests
{
    private static async Task<Work> MonitoredWorkAsync(AppDbContext db, WorkMediaType type)
    {
        var work = await new WorkService(db).CreateWorkAsync(type, "Frieren", 2020, CancellationToken.None);
        await MonitoringTestSupport.Commands(db).SetAsync(MonitoringTargetKind.Work, work.Id, true, CancellationToken.None);
        return work;
    }

    private static async Task<bool> IsWantedAsync(AppDbContext db, Work work)
    {
        await new WantedReconciler(db, TimeProvider.System).ReconcileAsync(work.Id, CancellationToken.None);
        return await db.WantedItems.AnyAsync(item => item.WorkId == work.Id);
    }

    private static async Task LinkAsync(AppDbContext db, Work work, WorkSourceKind kind, Guid sourceId)
    {
        db.WorkSourceLinks.Add(new WorkSourceLink { WorkId = work.Id, SourceKind = kind, SourceId = sourceId });
        await db.SaveChangesAsync();
    }

    [TestMethod]
    public async Task Manga_WantedUntilAChapterIsInTheLibrary()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var work = await MonitoredWorkAsync(db, WorkMediaType.Manga);
        var series = Guid.NewGuid();
        await LinkAsync(db, work, WorkSourceKind.MangaSeries, series);
        Assert.IsTrue(await IsWantedAsync(db, work));

        await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "MangaSeries" ("Id", "Title", "SourcePath", "Direction", "CreatedAt", "UpdatedAt") VALUES ({series.ToString()}, 'Frieren', '/manga/frieren', 'rtl', 'now', 'now')""");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "MangaChapters" ("Id", "SeriesId", "Number", "Title", "SourcePath", "SourceKind", "PageCount", "SourceUpdatedAt", "CreatedAt", "UpdatedAt") VALUES ({Guid.NewGuid().ToString()}, {series.ToString()}, 1, 'c1', '/manga/frieren/c1.cbz', 'cbz', 10, 'now', 'now', 'now')""");

        Assert.IsFalse(await IsWantedAsync(db, work));
    }

    [TestMethod]
    public async Task Book_WantedUntilAnEditionHasAFile()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var work = await MonitoredWorkAsync(db, WorkMediaType.Book);
        var novel = new NovelWork { SourceProvider = "upload", SourceKey = "dune", SourceUrl = "u", Title = "Dune" };
        db.NovelWorks.Add(novel);
        await db.SaveChangesAsync();
        var edition = new BookEdition { WorkId = novel.Id, EditionKey = "e1" };
        db.BookEditions.Add(edition);
        await db.SaveChangesAsync();
        await LinkAsync(db, work, WorkSourceKind.BookEdition, edition.Id);
        Assert.IsTrue(await IsWantedAsync(db, work));

        db.BookFiles.Add(new BookFile { EditionId = edition.Id, FileKey = "f1", FileName = "frieren.epub" });
        await db.SaveChangesAsync();

        Assert.IsFalse(await IsWantedAsync(db, work));
    }

    [TestMethod]
    public async Task Audiobook_TheAudioEditionIsWantedWhileARequestNamesItAndUntilTheLibraryHoldsOne()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var work = await new WorkService(db).CreateWorkAsync(WorkMediaType.Book, "Frieren", 2020, CancellationToken.None);
        var request = await new Jularr.Web.Features.Acquisition.Access.AcquisitionAccessStore(db).CreateAsync(
            new Jularr.Web.Features.Acquisition.Access.AcquisitionRequestDraft(Jularr.Web.Features.Acquisition.Access.MediaAcquisitionKind.Audiobook, "catalog", "c1", "Frieren", null, null) { WorkId = work.Id },
            "owner",
            Jularr.Web.Features.Acquisition.Access.AcquisitionRequestStatus.Approved,
            "owner",
            CancellationToken.None);
        await new RequestIntent(db, TimeProvider.System).RecordAsync(request, CancellationToken.None);
        var reconciler = new WantedReconciler(db, TimeProvider.System);

        await reconciler.ReconcileAsync(work.Id, CancellationToken.None);
        var item = await db.WantedItems.AsNoTracking().SingleAsync(row => row.WorkId == work.Id);
        Assert.AreEqual(WantedTargetKind.Edition, item.TargetKind, "The request names the audio edition, not the book.");
        Assert.IsEmpty(await reconciler.WorksWithoutOpenRequestAsync(Jularr.Web.Features.Acquisition.Access.MediaAcquisitionKind.Book, Guid.Empty, 10, CancellationToken.None), "Book requests do not carry an audio edition.");

        var audiobook = new Jularr.Web.Features.Audiobooks.Audiobook { Key = "frieren-2020", Title = "Frieren" };
        db.Add(audiobook);
        db.Add(new Jularr.Web.Features.Audiobooks.AudiobookFile { AudiobookId = audiobook.Id, FileKey = "a", FileName = "a.m4b", Format = "M4B" });
        await db.SaveChangesAsync();
        await LinkAsync(db, work, WorkSourceKind.Audiobook, audiobook.Id);
        await reconciler.ReconcileAsync(work.Id, CancellationToken.None);

        Assert.IsFalse(await db.WantedItems.AnyAsync(row => row.WorkId == work.Id), "An audiobook in the library fulfils the request.");
    }

    [TestMethod]
    public async Task Book_AnInstalledPdfIsQueuedAsAnUpgradeUntilTheProfileIsSatisfied()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var work = await MonitoredWorkAsync(db, WorkMediaType.Book);
        var novel = new NovelWork { SourceProvider = "upload", SourceKey = "dune", SourceUrl = "u", Title = "Dune" };
        db.NovelWorks.Add(novel);
        await db.SaveChangesAsync();
        var edition = new BookEdition { WorkId = novel.Id, EditionKey = "e1" };
        db.BookEditions.Add(edition);
        await db.SaveChangesAsync();
        await LinkAsync(db, work, WorkSourceKind.BookEdition, edition.Id);
        db.BookFiles.Add(new BookFile { EditionId = edition.Id, FileKey = "f1", FileName = "dune.pdf", Format = BookFileFormats.Pdf });
        await db.SaveChangesAsync();
        var profiles = new QualityProfileStore(new DirectoryInfo(Path.Combine(Path.GetTempPath(), "jularr-book-upgrade-" + Guid.NewGuid().ToString("N"))), new MediaAcquisitionRegistry([new BookAcquisitionRegistration()]));
        var reconciler = new WantedReconciler(db, TimeProvider.System, null, new UpgradeAssessors([new BookUpgradeAssessor(db, profiles)]));

        await reconciler.ReconcileAsync(work.Id, CancellationToken.None);
        Assert.IsTrue(await db.WantedItems.AnyAsync(item => item.WorkId == work.Id), "A PDF is below the EPUB cutoff of the default Book profile.");

        db.BookFiles.Add(new BookFile { EditionId = edition.Id, FileKey = "f2", FileName = "dune.epub", Format = BookFileFormats.Epub });
        await db.SaveChangesAsync();
        await reconciler.ReconcileAsync(work.Id, CancellationToken.None);
        Assert.IsFalse(await db.WantedItems.AnyAsync(item => item.WorkId == work.Id), "With an EPUB next to it the Book is final.");
    }

    [TestMethod]
    public async Task LightNovel_WantedUntilAVolumeIsInTheLibrary()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var work = await MonitoredWorkAsync(db, WorkMediaType.LightNovel);
        var novel = new NovelWork { SourceProvider = "upload", SourceKey = "frieren", SourceUrl = "u", Title = "Frieren" };
        db.NovelWorks.Add(novel);
        await db.SaveChangesAsync();
        await LinkAsync(db, work, WorkSourceKind.NovelWork, novel.Id);
        Assert.IsTrue(await IsWantedAsync(db, work));

        db.NovelVolumes.Add(new NovelVolume { WorkId = novel.Id, Number = 1, SourceKey = "v1" });
        await db.SaveChangesAsync();

        Assert.IsFalse(await IsWantedAsync(db, work));
    }

    [TestMethod]
    public async Task Music_UnreleasedAlbumsAreNotWanted()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var work = await MonitoredWorkAsync(db, WorkMediaType.Music);
        var artist = new Jularr.Web.Features.Music.MusicArtist { Name = "Daft Punk", SortName = "Daft Punk", MusicBrainzId = Guid.NewGuid().ToString() };
        db.MusicArtists.Add(artist);
        var album = new Jularr.Web.Features.Music.MusicAlbum { WorkId = work.Id, ArtistId = artist.Id, MusicBrainzReleaseGroupId = "rg-1", ReleaseDate = DateTime.UtcNow.AddDays(30) };
        db.MusicAlbums.Add(album);
        await db.SaveChangesAsync();
        Assert.IsFalse(await IsWantedAsync(db, work));

        album.ReleaseDate = DateTime.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();

        Assert.IsTrue(await IsWantedAsync(db, work));
    }
}
