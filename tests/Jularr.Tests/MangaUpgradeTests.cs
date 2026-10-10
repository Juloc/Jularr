using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Manga;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.ReadingAcquisition;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using BookIndexer = Jularr.Tests.BooksLifecycleTests.BookIndexer;
using Env = Jularr.Tests.BookPdfAcquisitionTests.BookAcquisitionEnvironment;
using Lifecycle = Jularr.Tests.MangaLifecycleTests;

namespace Jularr.Tests;

[TestClass]
public sealed class MangaUpgradeTests
{
    private const string Job = "/data/downloads/complete/manga/";

    internal static async Task<(Env Environment, BookIndexer Indexer, long WorkId, Guid SeriesId)> ZipLibraryAsync()
    {
        var indexer = new BookIndexer("Frieren Vol 1-2 ZIP");
        var environment = await Lifecycle.StartAsync(indexer, new Lifecycle.FakeAniList { Volumes = 2 });
        var request = await Lifecycle.SubmitAsync(environment);
        StringAssert.Contains(Assert.ContainsSingle(environment.Sabnzbd.Grabs).NzbName, "ZIP");
        var folder = environment.MangaFolder("Frieren Vol 1-2 ZIP");
        Lifecycle.WriteVolume(folder, "Frieren v01.zip");
        Lifecycle.WriteVolume(folder, "Frieren v02.zip");
        await environment.CompleteDownloadAsync(request, Job + "Frieren Vol 1-2 ZIP");
        var workId = (await environment.RequestAsync(request.Id)).WorkId!.Value;
        var seriesId = (await environment.Db.WorkSourceLinks.AsNoTracking().SingleAsync(link => link.WorkId == workId && link.SourceKind == WorkSourceKind.MangaSeries)).SourceId;
        return (environment, indexer, workId, seriesId);
    }

    internal static async Task<AcquisitionRequest> LatestRequestAsync(Env environment, long workId)
    {
        var id = await environment.Db.Database.SqlQuery<string>(
            $"""SELECT request."Id" AS "Value" FROM "AcquisitionRequests" request WHERE request."WorkId" = {workId} ORDER BY request."CreatedAt" DESC LIMIT 1""").SingleAsync();
        return await environment.RequestAsync(Guid.Parse(id));
    }

    internal static async Task<int> VersionsOfAsync(Env environment, Guid seriesId) =>
        await environment.Db.Database.SqlQuery<int>($"""SELECT COUNT(*)::int AS "Value" FROM "MangaChapters" WHERE "SeriesId" = {seriesId.ToString()}""").SingleAsync();

    [TestMethod]
    public async Task AZipVolumeIsUpgradedToTheProfilesCbzWithoutDuplicatesAndTheReaderKeepsItsPlace()
    {
        var (environment, indexer, workId, seriesId) = await ZipLibraryAsync();
        await using var scope = environment;
        var repository = new MangaRepository(environment.Db);
        var coverage = await Lifecycle.CoverageAsync(environment, workId);
        Assert.IsTrue(coverage.Volumes.All(volume => volume.InstalledQuality == "ZIP" && volume.UpgradeWanted), "A ZIP below the profile's CBZ cutoff is an upgrade candidate.");
        var zipChapter = (await repository.GetChaptersAsync(seriesId, CancellationToken.None)).First(chapter => chapter.VolumeNumber == 1);
        await repository.SaveProgressAsync("owner", (await repository.GetChapterAsync(zipChapter.Id, CancellationToken.None))!, 2, CancellationToken.None);

        await environment.RecoverAsync(DateTime.UtcNow);

        Assert.HasCount(1, environment.Sabnzbd.Grabs, "Nothing better than ZIP exists yet, and the same ZIP release is never downloaded again.");
        Assert.IsTrue(await environment.Db.WantedItems.CountAsync(item => item.WorkId == workId && item.TargetKind == Jularr.Web.Features.Acquisition.Wanted.WantedTargetKind.Volume) == 2, "Both installed volumes are wanted for an upgrade.");

        indexer.Titles.AddRange(["Frieren Vol 1-2 CBZ", "Frieren Vol 1-2 ZIP"]);
        await environment.RecoverAsync(DateTime.UtcNow.AddDays(2));

        Assert.HasCount(2, environment.Sabnzbd.Grabs);
        StringAssert.Contains(environment.Sabnzbd.Grabs[1].NzbName, "CBZ");
        await environment.RecoverAsync(DateTime.UtcNow.AddDays(2));
        Assert.HasCount(2, environment.Sabnzbd.Grabs, "The running upgrade is not started twice.");

        var folder = environment.MangaFolder("Frieren Vol 1-2 CBZ");
        Lifecycle.WriteVolume(folder, "Frieren v01.cbz");
        Lifecycle.WriteVolume(folder, "Frieren v02.cbz");
        await environment.CompleteDownloadAsync(await LatestRequestAsync(environment, workId), Job + "Frieren Vol 1-2 CBZ");

        Assert.AreEqual(4, await VersionsOfAsync(environment, seriesId), "Both versions of both volumes are kept.");
        var shown = await repository.GetChaptersAsync(seriesId, CancellationToken.None);
        Assert.HasCount(2, shown, "The reader lists each volume once.");
        Assert.IsTrue(shown.All(chapter => environment.Db.Database.SqlQuery<string>($"""SELECT "SourcePath" AS "Value" FROM "MangaChapters" WHERE "Id" = {chapter.Id.ToString()}""").Single().EndsWith(".cbz", StringComparison.OrdinalIgnoreCase)), "The CBZ is the version shown.");
        var progress = (await repository.GetProgressAsync("owner", seriesId, CancellationToken.None))!;
        Assert.AreNotEqual(zipChapter.Id, progress.ChapterId, "Progress moved to the shown version.");
        Assert.AreEqual(2, progress.PageIndex);
        Assert.AreEqual(zipChapter.VolumeNumber, shown.Single(chapter => chapter.Id == progress.ChapterId).VolumeNumber);
        Assert.IsTrue(Directory.EnumerateFiles(Path.Combine(environment.Root, "library-manga"), "*.zip", SearchOption.AllDirectories).Count() == 2, "The older versions stay on disk.");

        var after = await Lifecycle.CoverageAsync(environment, workId);
        Assert.IsTrue(after.Volumes.All(volume => volume.InstalledQuality == "CBZ" && !volume.UpgradeWanted));
        Assert.AreEqual(0, await environment.Db.WantedItems.CountAsync(item => item.WorkId == workId));
        for (var pass = 1; pass <= 3; pass++)
        {
            await environment.RecoverAsync(DateTime.UtcNow.AddDays(3 + pass));
        }

        Assert.HasCount(2, environment.Sabnzbd.Grabs, "A library at the cutoff is not searched again, also after restarts.");
        Assert.AreEqual(4, await VersionsOfAsync(environment, seriesId));
    }

    [TestMethod]
    public async Task ADamagedUpgradeIsRejectedItsCopyRemovedAndTheOlderVersionStaysReadable()
    {
        var (environment, indexer, workId, seriesId) = await ZipLibraryAsync();
        await using var scope = environment;
        indexer.Titles.Add("Frieren Vol 1-2 CBZ");
        await environment.RecoverAsync(DateTime.UtcNow.AddDays(2));
        Assert.HasCount(2, environment.Sabnzbd.Grabs);
        var folder = environment.MangaFolder("Frieren Vol 1-2 CBZ");
        File.WriteAllBytes(Path.Combine(folder, "Frieren v01.cbz"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(folder, "Frieren v02.cbz"), [5, 6, 7, 8]);

        await environment.CompleteDownloadAsync(await LatestRequestAsync(environment, workId), Job + "Frieren Vol 1-2 CBZ");

        Assert.AreEqual(2, await VersionsOfAsync(environment, seriesId), "The damaged files left no library entries.");
        Assert.HasCount(2, await new MangaRepository(environment.Db).GetChaptersAsync(seriesId, CancellationToken.None));
        Assert.IsFalse(Directory.EnumerateFiles(Path.Combine(environment.Root, "library-manga"), "*.cbz", SearchOption.AllDirectories).Any(), "The damaged copies were removed.");
        Assert.AreEqual(2, Directory.EnumerateFiles(Path.Combine(environment.Root, "library-manga"), "*.zip", SearchOption.AllDirectories).Count());
        var coverage = await Lifecycle.CoverageAsync(environment, workId);
        Assert.IsTrue(coverage.Volumes.All(volume => volume.State == ReadingCoverageState.Installed && volume.InstalledQuality == "ZIP"));

        await environment.RecoverAsync(DateTime.UtcNow.AddDays(4));
        Assert.HasCount(2, environment.Sabnzbd.Grabs, "The damaged release is not tried again.");
    }
}
