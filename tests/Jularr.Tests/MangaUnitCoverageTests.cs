using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Monitoring;
using Jularr.Web.Features.ReadingAcquisition;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using BookIndexer = Jularr.Tests.BooksLifecycleTests.BookIndexer;
using Env = Jularr.Tests.BookPdfAcquisitionTests.BookAcquisitionEnvironment;
using Lifecycle = Jularr.Tests.MangaLifecycleTests;

namespace Jularr.Tests;

/// <summary>Volumes, chapters and ranges of a Manga Work through the real pipeline: what is wanted, what a download covers and what the library then holds.</summary>
[TestClass]
public sealed class MangaUnitCoverageTests
{
    private const string Job = "/data/downloads/complete/manga/";

    /// <summary>The Wanted queue and the coverage calculation say the same: the rows of the Work are exactly the units the coverage view calls wanted.</summary>
    private static async Task AssertWantedMatchesCoverageAsync(Env environment, long workId)
    {
        await environment.Services.GetRequiredService<WantedReconciler>().ReconcileAsync(workId, CancellationToken.None);
        var open = await environment.Db.Database.SqlQuery<bool>(
            $"""SELECT EXISTS (SELECT 1 FROM "AcquisitionRequests" request WHERE request."WorkId" = {workId} AND request."Status" IN ('approved', 'searching', 'downloading', 'importing')) AS "Value" """).SingleAsync();
        var view = await environment.Services.GetRequiredService<ReadingCoverageService>().LoadAsync(workId, CancellationToken.None, wholeTitleAsked: open);
        var expected = view.Volumes.Where(volume => volume.Wanted).Select(volume => volume.Id)
            .Concat(view.Volumes.SelectMany(volume => volume.Chapters).Concat(view.LooseChapters).Where(chapter => chapter.Wanted).Select(chapter => chapter.Id))
            .Order()
            .ToArray();
        var rows = (await environment.Db.WantedItems.AsNoTracking().Where(item => item.WorkId == workId && item.TargetId != null).Select(item => item.TargetId!.Value).ToListAsync()).Order().ToArray();
        CollectionAssert.AreEqual(expected, rows, "Wanted rows and the coverage view disagree.");
    }

    private static async Task<AcquisitionRequest> DownloadAsync(Env environment, AcquisitionRequest request, string job, params string[] files)
    {
        var folder = environment.MangaFolder(job);
        foreach (var file in files)
        {
            Lifecycle.WriteVolume(folder, file);
        }

        await environment.CompleteDownloadAsync(request, Job + job);
        return await environment.RequestAsync(request.Id);
    }

    private static async Task<AcquisitionRequest> LatestRequestAsync(Env environment, long workId)
    {
        var id = await environment.Db.Database.SqlQuery<string>(
            $"""SELECT request."Id" AS "Value" FROM "AcquisitionRequests" request WHERE request."WorkId" = {workId} ORDER BY request."CreatedAt" DESC LIMIT 1""").SingleAsync();
        return await environment.RequestAsync(Guid.Parse(id));
    }

    private static async Task<long> StructuredWorkAsync(Env environment)
    {
        var workId = (await environment.Services.GetRequiredService<RequestWorkBinder>().ResolveAsync(MediaAcquisitionKind.Manga, "anilist", Lifecycle.AniListId, "Frieren", CancellationToken.None))!.Value;
        await environment.Services.GetRequiredService<ReadingStructureService>().RefreshAsync(workId, CancellationToken.None);
        return workId;
    }

    [TestMethod]
    public async Task OnlyTheVolumesStillMissingAreSearchedForAfterAPartialImportAndNoVolumeIsDownloadedTwice()
    {
        var indexer = new BookIndexer("Frieren Vol 1-2 CBZ", "Frieren v03 CBZ", "Frieren v02 CBZ");
        await using var environment = await Lifecycle.StartAsync(indexer, new Lifecycle.FakeAniList { Volumes = 3 });

        var request = await Lifecycle.SubmitAsync(environment);
        StringAssert.Contains(Assert.ContainsSingle(environment.Sabnzbd.Grabs).NzbName, "Vol 1-2", "The set that covers two missing volumes beats the single ones.");
        var stored = await DownloadAsync(environment, request, "Frieren Vol 1-2", "Frieren v01.cbz", "Frieren v02.cbz");
        Assert.AreEqual(AcquisitionRequestStatus.Completed, stored.Status, stored.StatusMessage);

        var work = await Lifecycle.MangaWorkAsync(environment);
        var coverage = await Lifecycle.CoverageAsync(environment, work.Id);
        CollectionAssert.AreEqual(new[] { ReadingCoverageState.Installed, ReadingCoverageState.Installed, ReadingCoverageState.Missing }, coverage.Volumes.Select(volume => volume.State).ToArray());
        CollectionAssert.AreEqual(new[] { 3 }, coverage.Want.Volumes.ToArray());
        await AssertWantedMatchesCoverageAsync(environment, work.Id);
        Assert.AreEqual(1, await environment.Db.WantedItems.CountAsync(item => item.WorkId == work.Id), "Only the missing volume is wanted.");

        await environment.RecoverAsync(DateTime.UtcNow);

        Assert.HasCount(2, environment.Sabnzbd.Grabs);
        StringAssert.Contains(environment.Sabnzbd.Grabs[1].NzbName, "v03", "Held volumes are never fetched again.");
        var done = await DownloadAsync(environment, await LatestRequestAsync(environment, work.Id), "Frieren v03", "Frieren v03.cbz");
        Assert.AreEqual(AcquisitionRequestStatus.Completed, done.Status, done.StatusMessage);
        Assert.IsTrue((await Lifecycle.CoverageAsync(environment, work.Id)).Volumes.All(volume => volume.State == ReadingCoverageState.Installed));
        await AssertWantedMatchesCoverageAsync(environment, work.Id);
        Assert.AreEqual(0, await environment.Db.WantedItems.CountAsync(item => item.WorkId == work.Id));
        await environment.RecoverAsync(DateTime.UtcNow);
        Assert.HasCount(2, environment.Sabnzbd.Grabs, "A complete Manga is not searched again.");
    }

    [TestMethod]
    public async Task ChaptersOfATitleWithoutVolumesAreCoveredByRangesAndEachChapterIsFetchedOnce()
    {
        var indexer = new BookIndexer("Frieren c001-002 CBZ", "Frieren c003 CBZ");
        await using var environment = await Lifecycle.StartAsync(indexer, new Lifecycle.FakeAniList { Chapters = 4 });

        var request = await Lifecycle.SubmitAsync(environment);
        StringAssert.Contains(Assert.ContainsSingle(environment.Sabnzbd.Grabs).NzbName, "c001-002");
        var stored = await DownloadAsync(environment, request, "Frieren c001-002", "Frieren c001-002.cbz");
        Assert.AreEqual(AcquisitionRequestStatus.Completed, stored.Status, stored.StatusMessage);

        var work = await Lifecycle.MangaWorkAsync(environment);
        var coverage = await Lifecycle.CoverageAsync(environment, work.Id);
        Assert.IsEmpty(coverage.Volumes);
        CollectionAssert.AreEqual(new[] { true, true, false, false }, coverage.LooseChapters.Select(chapter => chapter.Installed).ToArray(), "One package of two chapters ties to both.");
        CollectionAssert.AreEqual(new[] { 3d, 4d }, coverage.Want.Chapters.ToArray());
        await AssertWantedMatchesCoverageAsync(environment, work.Id);

        await environment.RecoverAsync(DateTime.UtcNow);

        Assert.HasCount(2, environment.Sabnzbd.Grabs);
        StringAssert.Contains(environment.Sabnzbd.Grabs[1].NzbName, "c003", "The package that overlaps the installed chapters is not taken again.");
        await DownloadAsync(environment, await LatestRequestAsync(environment, work.Id), "Frieren c003", "Frieren c003.cbz");
        coverage = await Lifecycle.CoverageAsync(environment, work.Id);
        CollectionAssert.AreEqual(new[] { 4d }, coverage.Want.Chapters.ToArray());
        await AssertWantedMatchesCoverageAsync(environment, work.Id);
    }

    [TestMethod]
    public async Task AnInstalledVolumeCoversItsChaptersAndASingleChapterNeverCompletesAVolume()
    {
        var indexer = new BookIndexer("Frieren v01 CBZ", "Frieren c001-004 CBZ", "Frieren c005 CBZ");
        await using var environment = await Lifecycle.StartAsync(indexer, new Lifecycle.FakeAniList { Volumes = 2 });
        var workId = await StructuredWorkAsync(environment);
        var volumes = await environment.Db.WorkVolumes.Where(volume => volume.WorkId == workId).OrderBy(volume => volume.Number).ToListAsync();
        await environment.Services.GetRequiredService<ReadingUnits>().EnrichChaptersAsync(workId, "anilist", [.. Enumerable.Range(1, 8).Select(number => new ProviderUnit($"{Lifecycle.AniListId}:c{number}", number, null))], CancellationToken.None);
        foreach (var chapter in await environment.Db.WorkChapters.Where(chapter => chapter.WorkId == workId).ToListAsync())
        {
            chapter.VolumeId = volumes[chapter.Number <= 4 ? 0 : 1].Id;
        }

        await environment.Db.SaveChangesAsync();
        environment.Db.ChangeTracker.Clear();

        var request = await Lifecycle.SubmitAsync(environment);
        StringAssert.Contains(Assert.ContainsSingle(environment.Sabnzbd.Grabs).NzbName, "v01", "The wanted units are the volumes; a chapter inside a monitored volume is not wanted on its own.");
        await DownloadAsync(environment, request, "Frieren v01", "Frieren v01.cbz", "Frieren c005.cbz");

        var coverage = await Lifecycle.CoverageAsync(environment, workId);
        Assert.AreEqual(ReadingCoverageState.Installed, coverage.Volumes[0].State);
        Assert.IsTrue(coverage.Volumes[0].Chapters.All(chapter => chapter.Installed), "The volume covers its four chapters.");
        CollectionAssert.AreEqual(new[] { 2 }, coverage.Want.Volumes.ToArray());
        Assert.IsEmpty(coverage.Want.Chapters);
        await AssertWantedMatchesCoverageAsync(environment, workId);
        Assert.AreEqual(ReadingCoverageState.Partial, coverage.Volumes[1].State, "One chapter of the second volume is not the volume.");
    }

    [TestMethod]
    public async Task ALibraryThatExistedBeforeIsMatchedToItsVolumesByFileNameAndOnlyTheMissingOnesAreFetched()
    {
        var indexer = new BookIndexer("Frieren Vol 1-3 CBZ", "Frieren v03 CBZ");
        await using var environment = await Lifecycle.StartAsync(indexer, new Lifecycle.FakeAniList { Volumes = 3 });
        var folder = Directory.CreateDirectory(Path.Combine(environment.Root, "existing", "Frieren")).FullName;
        Lifecycle.WriteVolume(folder, "Frieren v01.cbz");
        Lifecycle.WriteVolume(folder, "Frieren v02.cbz");
        var repository = new Jularr.Web.Features.Manga.MangaRepository(environment.Db);
        var imported = await new Jularr.Web.Features.Manga.MangaImportService(repository, Path.Combine(environment.Root, "cache")).ImportAsync(folder, CancellationToken.None);
        var existingWork = await environment.Services.GetRequiredService<LegacyWorkBridge>().EnsureWorkForMangaSeriesAsync(imported.SeriesId, "Frieren", null, Lifecycle.AniListId, CancellationToken.None);

        var request = await Lifecycle.SubmitAsync(environment);

        Assert.AreEqual(existingWork, request.WorkId, "The request uses the Work of the library series, never a second one.");
        Assert.AreEqual(1, await environment.Db.Works.CountAsync(item => item.MediaType == WorkMediaType.Manga));
        var coverage = await Lifecycle.CoverageAsync(environment, existingWork);
        CollectionAssert.AreEqual(new[] { ReadingCoverageState.Installed, ReadingCoverageState.Installed, ReadingCoverageState.Missing }, coverage.Volumes.Select(volume => volume.State).ToArray(), "The two volumes in the library are matched by their file names.");
        StringAssert.Contains(Assert.ContainsSingle(environment.Sabnzbd.Grabs).NzbName, "v03", "The set that repeats two held volumes loses against the missing one.");
    }

    [TestMethod]
    public async Task AVolumeTheOwnerSwitchedOffIsNeitherWantedNorTakenAndStaysOffAfterTheRequest()
    {
        var indexer = new BookIndexer("Frieren v02 CBZ", "Frieren v01 CBZ");
        await using var environment = await Lifecycle.StartAsync(indexer, new Lifecycle.FakeAniList { Volumes = 3 });
        var workId = await StructuredWorkAsync(environment);
        var second = await environment.Db.WorkVolumes.AsNoTracking().SingleAsync(volume => volume.WorkId == workId && volume.Number == 2);
        await environment.Services.GetRequiredService<MonitoringCommands>().SetAsync(MonitoringTargetKind.Volume, second.Id, false, CancellationToken.None);

        var request = await Lifecycle.SubmitAsync(environment);

        StringAssert.Contains(Assert.ContainsSingle(environment.Sabnzbd.Grabs).NzbName, "v01", "Volume 2 is switched off, so only volume 1 is wanted.");
        var view = await Lifecycle.CoverageAsync(environment, workId);
        Assert.IsFalse(view.Volumes[1].Monitored);
        CollectionAssert.AreEqual(new[] { 1, 3 }, view.Want.Volumes.ToArray());
        await AssertWantedMatchesCoverageAsync(environment, workId);
        Assert.IsFalse(await environment.Db.WantedItems.AnyAsync(item => item.TargetId == second.Id), "The unit that is off is not in the Wanted queue, even while a request names the whole title.");
        await DownloadAsync(environment, request, "Frieren v01", "Frieren v01.cbz");

        view = await Lifecycle.CoverageAsync(environment, workId);
        Assert.AreEqual(ReadingCoverageState.Missing, view.Volumes[1].State);
        CollectionAssert.AreEqual(new[] { 3 }, view.Want.Volumes.ToArray());
    }
}
