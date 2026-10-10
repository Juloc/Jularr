using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Wanted;
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
public sealed class MangaRecoveryTests
{
    private const string Job = "/data/downloads/complete/manga/";

    [TestMethod]
    public async Task AnUnusableDownloadContinuesWithTheNextEligibleReleaseAndNeverRepeatsTheBadOne()
    {
        var indexer = new BookIndexer("Frieren Vol 1-3 CBZ", "Frieren v01 CBZ");
        await using var environment = await Lifecycle.StartAsync(indexer, new Lifecycle.FakeAniList { Volumes = 3 });
        var request = await Lifecycle.SubmitAsync(environment);
        StringAssert.Contains(Assert.ContainsSingle(environment.Sabnzbd.Grabs).NzbName, "Vol 1-3");

        File.WriteAllText(Path.Combine(environment.MangaFolder("Frieren Vol 1-3"), "readme.txt"), "not a comic");
        await environment.CompleteDownloadAsync(request, Job + "Frieren Vol 1-3");

        Assert.HasCount(2, environment.Sabnzbd.Grabs, "The next eligible release is taken.");
        StringAssert.Contains(environment.Sabnzbd.Grabs[1].NzbName, "v01");
        var stored = await environment.RequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, stored.Status, stored.StatusMessage);
        var work = await Lifecycle.MangaWorkAsync(environment);
        Assert.IsTrue((await Lifecycle.CoverageAsync(environment, work.Id)).Volumes.All(volume => volume.State == ReadingCoverageState.Missing), "Nothing was installed from the bad download.");
    }

    [TestMethod]
    public async Task AnImportThatWaitsForStorageResumesWithoutDownloadingAgainAndARepeatedImportChangesNothing()
    {
        var indexer = new BookIndexer("Frieren Vol 1-2 CBZ");
        await using var environment = await Lifecycle.StartAsync(indexer, new Lifecycle.FakeAniList { Volumes = 2 });
        var request = await Lifecycle.SubmitAsync(environment);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status);

        // SABnzbd finished, but the completed folder is not mounted yet.
        await environment.CompleteDownloadAsync(request, Job + "Frieren Vol 1-2");
        var waiting = await environment.RequestAsync(request.Id);
        Assert.AreNotEqual(AcquisitionRequestStatus.Completed, waiting.Status);
        Assert.AreNotEqual(AcquisitionRequestStatus.Failed, waiting.Status, waiting.StatusMessage);

        var folder = environment.MangaFolder("Frieren Vol 1-2");
        Lifecycle.WriteVolume(folder, "Frieren v01.cbz");
        Lifecycle.WriteVolume(folder, "Frieren v02.cbz");
        await environment.RecoverAsync(DateTime.UtcNow);

        var stored = await environment.RequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, stored.Status, stored.StatusMessage);
        Assert.HasCount(1, environment.Sabnzbd.Grabs, "Nothing is downloaded again.");
        var work = await Lifecycle.MangaWorkAsync(environment);
        var chapters = await environment.Db.Database.SqlQuery<int>($"""SELECT COUNT(*)::int AS "Value" FROM "MangaChapters" """).SingleAsync();
        var bindings = await environment.Db.WorkUnitBindings.CountAsync();

        for (var pass = 0; pass < 3; pass++)
        {
            await environment.RecoverAsync(DateTime.UtcNow.AddDays(pass + 1));
        }

        Assert.AreEqual(chapters, await environment.Db.Database.SqlQuery<int>($"""SELECT COUNT(*)::int AS "Value" FROM "MangaChapters" """).SingleAsync());
        Assert.AreEqual(bindings, await environment.Db.WorkUnitBindings.CountAsync());
        Assert.AreEqual(1, await environment.Db.Works.CountAsync(item => item.MediaType == WorkMediaType.Manga));
        Assert.HasCount(1, environment.Sabnzbd.Grabs);
        Assert.IsTrue((await Lifecycle.CoverageAsync(environment, work.Id)).Volumes.All(volume => volume.State == ReadingCoverageState.Installed));
    }

    [TestMethod]
    public async Task WhenAniListIsDownTheTitleIsStillSearchedWholeAndTheStructureArrivesWithTheNextRefresh()
    {
        var aniList = new Lifecycle.FakeAniList { Volumes = 2, Down = true };
        var indexer = new BookIndexer("Frieren Complete CBZ");
        await using var environment = await Lifecycle.StartAsync(indexer, aniList);

        var request = await Lifecycle.SubmitAsync(environment);

        StringAssert.Contains(Assert.ContainsSingle(environment.Sabnzbd.Grabs).NzbName, "Complete", "Without a known structure the whole title is searched, as before.");
        var work = await Lifecycle.MangaWorkAsync(environment);
        Assert.IsFalse((await Lifecycle.CoverageAsync(environment, work.Id)).HasStructure);

        aniList.Down = false;
        var refresh = await environment.Services.GetRequiredService<ReadingStructureService>().RefreshAsync(work.Id, CancellationToken.None);
        Assert.AreEqual(2, refresh.Volumes!.Created);
        Assert.IsNull(refresh.Problem);
        var again = await environment.Services.GetRequiredService<ReadingStructureService>().RefreshAsync(work.Id, CancellationToken.None);
        Assert.IsFalse(again.Changed, "A refresh of the same structure changes nothing.");
        Assert.AreEqual(2, await environment.Db.WorkVolumes.CountAsync(volume => volume.WorkId == work.Id));
    }
}
