using Jularr.Web.Features.Monitoring;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Tests;

/// <summary>
/// The shared cutoff and upgrade policy of Movie and TV: a pure policy over the profile's quality order, and the request-to-import chain
/// that applies it. An installed target below its cutoff stays Wanted for a bounded poll, a better release replaces the file once it is
/// recorded, a final target stops searching, and a profile change makes an installed title upgradable again.
/// </summary>
[TestClass]
public sealed class VideoUpgradeTests
{
    private const string DuneTmdb = "438631";
    private const string SeveranceTmdb = "95396";
    private const string DuneLow = "Dune.2021.720p.WEB-DL.x264-GROUP";
    private const string DuneWeb = "Dune.2021.1080p.WEB-DL.x264-GROUP";
    private const string DuneBluRay = "Dune.2021.1080p.BluRay.x264-GROUP";

    private static readonly QualityProfile s_profile = VideoQualityProfiles.CreateDefaultMovie1080p();

    private static FakeTmdb Tmdb()
    {
        var tmdb = new FakeTmdb();
        tmdb.AddMovie(438631, "Dune", new DateTime(2021, 9, 15));
        var now = DateTime.UtcNow;
        tmdb.AddSeries(95396, "Severance", now.AddDays(-30), (1, [(1, now.AddDays(-30)), (2, now.AddDays(-23))]));
        return tmdb;
    }

    private static byte[] Bytes(int length) => [.. Enumerable.Range(1, length).Select(value => (byte)value)];

    private static async Task<AcquisitionRequest> ImportAsync(VideoRequestToPlayWorld world, AcquisitionRequest request, string release, int size)
    {
        var current = await world.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, current.Status, current.StatusMessage);
        await world.CompleteDownloadAsync(current, release, Bytes(size), $"{release}.mkv");
        await world.WantedPassAsync();
        return await world.GetRequestAsync(request.Id);
    }

    private static async Task<string?> VersionQualityAsync(VideoRequestToPlayWorld world) => Assert.ContainsSingle(await world.Db.WorkVersions.AsNoTracking().Select(version => version.Quality).ToListAsync());

    [TestMethod]
    public void ACutoffMakesTheInstalledQualityFinalAndAnythingBelowItUpgradable()
    {
        Assert.IsTrue(UpgradePolicy.Assess(s_profile, "WEB-720p").IsUpgradable);
        Assert.IsTrue(UpgradePolicy.Assess(s_profile, "HDTV-1080p").IsUpgradable);
        Assert.AreEqual(UpgradeState.Final, UpgradePolicy.Assess(s_profile, "WEB-1080p").State, "The cutoff quality itself is final.");
        Assert.AreEqual(UpgradeState.Final, UpgradePolicy.Assess(s_profile, "BLURAY-1080p").State);
        Assert.AreEqual(UpgradeState.Final, UpgradePolicy.Assess(s_profile, null).State, "An unknown quality is never replaced automatically.");
        Assert.AreEqual(UpgradeState.Final, UpgradePolicy.Assess(s_profile, "WEB-2160p").State, "A quality outside the profile's order cannot be compared.");
        Assert.AreEqual(UpgradeState.Final, UpgradePolicy.Assess(s_profile with { UpgradeAllowed = false }, "WEB-720p").State);
        Assert.IsTrue(UpgradePolicy.Assess(s_profile with { UpgradeCutoffQuality = "BLURAY-1080p" }, "WEB-1080p").IsUpgradable, "Raising the cutoff makes the installed quality upgradable again.");
        Assert.AreEqual(UpgradeState.Final, UpgradePolicy.Assess(s_profile with { UpgradeCutoffQuality = null }, "BLURAY-1080p").State, "Without a cutoff the best quality of the profile is final.");
    }

    [TestMethod]
    public void AnUpgradeNeedsTheProfilesMinimumNumberOfQualityStepsAndNeverMovesDown()
    {
        var steps = s_profile with { UpgradeMinimumQualitySteps = 2, UpgradeCutoffQuality = "BLURAY-1080p" };

        Assert.IsFalse(UpgradePolicy.IsUpgrade(steps, "HDTV-720p", "WEB-720p"), "One step is not enough.");
        Assert.IsTrue(UpgradePolicy.IsUpgrade(steps, "HDTV-720p", "BLURAY-720p"));
        Assert.IsFalse(UpgradePolicy.IsUpgrade(s_profile, "WEB-720p", "HDTV-720p"), "A lower quality is never an upgrade.");
        Assert.IsFalse(UpgradePolicy.IsUpgrade(s_profile, "WEB-720p", "WEB-720p"), "The same quality is never an upgrade.");
        Assert.AreEqual("WEB-1080p", UpgradePolicy.Best(s_profile, ["WEB-720p", null, "WEB-1080p", "WEB-2160p"]));
    }

    [TestMethod]
    public async Task AMovieBelowItsCutoffStaysWantedPollsBoundedAndIsReplacedByTheBetterReleaseThenStops()
    {
        await using var world = await VideoRequestToPlayWorld.CreateAsync(MediaAcquisitionKind.Movie, Tmdb());
        world.Indexer.Publish(DuneLow);
        var request = await world.RequestAsync(DuneTmdb, "Dune");

        var download = (await world.Operations.GetAsync(request.OperationId!.Value))!;
        Assert.IsTrue(DownloadOperationDetails.TryParse(download.Details, out var details), "The download records where its release came from.");
        Assert.AreEqual("Video test indexer", details!.ReleaseSource);
        Assert.AreEqual("GROUP", details.ReleaseGroup, "The outcome of this download can later count for or against its indexer and release group.");

        // The acceptable release is taken at once; the movie stays wanted because 720p is below the cutoff.
        var imported = await ImportAsync(world, request, DuneLow, size: 4);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, imported.Status, imported.StatusMessage);
        StringAssert.Contains(imported.StatusMessage, "better release");
        Assert.AreEqual("WEB-720p", await VersionQualityAsync(world), "The import records the quality the Version has.");
        var placed = Path.Combine(world.LibraryRoot, "Dune (2021)", "Dune (2021).mkv");
        Assert.AreEqual(4, new FileInfo(placed).Length);

        // The wait is bounded and stored: another pass inside it neither searches nor grabs.
        var searches = world.Indexer.Searches;
        await world.WantedPassAsync();
        Assert.AreEqual(searches, world.Indexer.Searches);
        Assert.AreEqual(1, world.Sabnzbd.Grabs.Count);

        // Nothing better exists yet: the request keeps waiting, it does not fail or burn its search budget.
        world.Clock.Advance(UpgradePolicy.SearchInterval + TimeSpan.FromHours(1));
        await world.WantedPassAsync();
        var waiting = await world.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, waiting.Status, waiting.StatusMessage);
        StringAssert.Contains(waiting.StatusMessage, "no better release");
        var waitPayload = VideoRequestPayload.Parse(waiting.PayloadJson)!;
        Assert.AreEqual(0, waitPayload.Searches);
        Assert.IsTrue(waitPayload.NextSearchUtc > world.Clock.UtcNow);
        Assert.AreEqual(1, world.Sabnzbd.Grabs.Count);

        // A 1080p WEB release appears: it is a meaningful upgrade, grabbed once, and replaces the file once it is recorded.
        world.Indexer.Publish(DuneWeb);
        world.Clock.Advance(UpgradePolicy.SearchInterval + TimeSpan.FromHours(1));
        await world.WantedPassAsync();
        var grabbed = await world.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, grabbed.Status, grabbed.StatusMessage);
        Assert.AreEqual(DuneWeb, world.Sabnzbd.Grabs[1].NzbName);
        var upgraded = await ImportAsync(world, request, DuneWeb, size: 9);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, upgraded.Status, upgraded.StatusMessage);
        Assert.AreEqual(9, new FileInfo(placed).Length, "The better file replaced the old one.");
        Assert.AreEqual("WEB-1080p", await VersionQualityAsync(world));
        Assert.AreEqual(1, await world.Db.StoredFiles.CountAsync(), "One canonical file serves the movie after the upgrade.");
        Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(placed)!, "*.replaced-*"), "The replaced file is gone once the new one is recorded.");

        // The cutoff is met: no further search, however long the title waits.
        var finalSearches = world.Indexer.Searches;
        world.Clock.Advance(TimeSpan.FromDays(3));
        await world.WantedPassAsync();
        Assert.AreEqual(finalSearches, world.Indexer.Searches);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, (await world.GetRequestAsync(request.Id)).Status);
    }

    [TestMethod]
    public async Task AnInstalledMovieWaitingForABetterReleaseIsListedInWantedAsAnUpgrade()
    {
        await using var world = await VideoRequestToPlayWorld.CreateAsync(MediaAcquisitionKind.Movie, Tmdb());
        world.Indexer.Publish(DuneLow);
        var request = await world.RequestAsync(DuneTmdb, "Dune");
        await ImportAsync(world, request, DuneLow, size: 4);
        var wanted = new WantedListService(world.Requests, new AnimeMonitoringStore(Path.Combine(Path.GetTempPath(), "jularr-wanted-" + Guid.NewGuid().ToString("N"))), world.Services.GetRequiredService<QualityProfileStore>(), world.Db, new VideoRequestWorkResolver(world.Db), new MonitoringResolver(world.Db), [], world.Clock);

        var row = Assert.ContainsSingle(await wanted.LoadAsync(CancellationToken.None));

        Assert.IsTrue(row.IsUpgrade, "The movie is installed, so the row is an upgrade, not a missing title.");
        Assert.AreEqual(MediaAcquisitionKind.Movie, row.Kind);
    }

    [TestMethod]
    public async Task AnInstalledMovieIsQueuedWhileItsProfileWantsMoreAndLeavesTheQueueWhenTheProfileIsSatisfied()
    {
        await using var world = await VideoRequestToPlayWorld.CreateAsync(MediaAcquisitionKind.Movie, Tmdb());
        world.Indexer.Publish(DuneLow);
        var request = await world.RequestAsync(DuneTmdb, "Dune");
        await ImportAsync(world, request, DuneLow, size: 4);
        var workId = await world.Db.Works.AsNoTracking().Select(work => work.Id).SingleAsync();
        var reconciler = world.Services.GetRequiredService<WantedReconciler>();

        await reconciler.ReconcileAsync(workId, CancellationToken.None);
        var queued = Assert.ContainsSingle(await world.Db.WantedItems.AsNoTracking().ToListAsync());
        Assert.AreEqual(workId, queued.TargetId, "720p is below the cutoff, so the installed movie is queued as an upgrade.");
        Assert.IsEmpty(await reconciler.WorksWithoutOpenRequestAsync(MediaAcquisitionKind.Movie, Guid.Empty, 10, CancellationToken.None), "Nothing is missing, so no new request is opened for it.");

        var store = world.Services.GetRequiredService<QualityProfileStore>();
        await store.UpsertAsync((await store.ResolveAsync(MediaAcquisitionKind.Movie, null)) with { UpgradeCutoffQuality = "WEB-720p" });
        await reconciler.ReconcileAsync(workId, CancellationToken.None);
        Assert.IsEmpty(await world.Db.WantedItems.AsNoTracking().ToListAsync(), "A profile the installed quality satisfies leaves the queue.");

        await store.UpsertAsync((await store.ResolveAsync(MediaAcquisitionKind.Movie, null)) with { UpgradeCutoffQuality = "BLURAY-1080p" });
        var page = await reconciler.ReconcileUpgradesAsync(MediaAcquisitionKind.Movie, Guid.Empty, 10, CancellationToken.None);
        Assert.AreEqual(workId, Assert.ContainsSingle(page.Works));
        Assert.IsTrue(page.ReachedEnd);
        Assert.AreEqual(workId, Assert.ContainsSingle(await world.Db.WantedItems.AsNoTracking().ToListAsync()).TargetId, "The upgrade scan queues what a raised cutoff makes upgradable.");
    }

    [TestMethod]
    public async Task AFinalMovieIsOnlyReplacedWhenTheProfileLaterWantsMoreAndNeverByAWorseOrEqualRelease()
    {
        await using var world = await VideoRequestToPlayWorld.CreateAsync(MediaAcquisitionKind.Movie, Tmdb());
        world.Indexer.Publish(DuneWeb);
        var request = await world.RequestAsync(DuneTmdb, "Dune");
        var imported = await ImportAsync(world, request, DuneWeb, size: 6);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, imported.Status, imported.StatusMessage);

        // An equal release and a worse one do not trigger anything while the cutoff is met.
        world.Indexer.Publish(DuneLow);
        world.Clock.Advance(TimeSpan.FromHours(3));
        await world.WantedPassAsync();
        Assert.AreEqual(1, world.Sabnzbd.Grabs.Count);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, (await world.GetRequestAsync(request.Id)).Status);

        // The owner raises the cutoff: the installed 1080p WEB title becomes wanted again and the BluRay release replaces it.
        var store = world.Services.GetRequiredService<QualityProfileStore>();
        await store.UpsertAsync((await store.ResolveAsync(MediaAcquisitionKind.Movie, null)) with { UpgradeCutoffQuality = "BLURAY-1080p" });
        world.Indexer.Publish(DuneBluRay);
        world.Clock.Advance(TimeSpan.FromHours(3));
        await world.WantedPassAsync();
        var reopened = await world.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, reopened.Status, reopened.StatusMessage);
        Assert.AreEqual(DuneBluRay, world.Sabnzbd.Grabs[1].NzbName);
        var upgraded = await ImportAsync(world, request, DuneBluRay, size: 11);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, upgraded.Status, upgraded.StatusMessage);
        Assert.AreEqual("BLURAY-1080p", await VersionQualityAsync(world));
        Assert.AreEqual(11, new FileInfo(Path.Combine(world.LibraryRoot, "Dune (2021)", "Dune (2021).mkv")).Length);
    }

    [TestMethod]
    public async Task AnIncomingFileIsJudgedAgainstTheInstalledQualityOfItsTarget()
    {
        await using var world = await VideoRequestToPlayWorld.CreateAsync(MediaAcquisitionKind.Movie, Tmdb());
        world.Indexer.Publish(DuneLow);
        var request = await world.RequestAsync(DuneTmdb, "Dune");
        await ImportAsync(world, request, DuneLow, size: 4);
        var installed = world.Services.GetRequiredService<InstalledVideoVersions>();
        var workId = (await world.Db.Works.AsNoTracking().SingleAsync()).Id;

        Assert.AreEqual(IncomingVideoVerdict.Upgrade, (await installed.JudgeIncomingAsync(MediaAcquisitionKind.Movie, workId, null, "WEB-1080p", CancellationToken.None)).Verdict);
        Assert.AreEqual(IncomingVideoVerdict.ExistingPreferred, (await installed.JudgeIncomingAsync(MediaAcquisitionKind.Movie, workId, null, "HDTV-720p", CancellationToken.None)).Verdict);
        Assert.AreEqual(IncomingVideoVerdict.Undecidable, (await installed.JudgeIncomingAsync(MediaAcquisitionKind.Movie, workId, null, null, CancellationToken.None)).Verdict);
        Assert.AreEqual(IncomingVideoVerdict.NothingInstalled, (await installed.JudgeIncomingAsync(MediaAcquisitionKind.Movie, Guid.NewGuid(), null, "WEB-1080p", CancellationToken.None)).Verdict);
    }

    [TestMethod]
    public async Task AnInstalledEpisodeBelowItsCutoffIsUpgradedOnItsOwnWhileTheOtherEpisodesKeepWaiting()
    {
        await using var world = await VideoRequestToPlayWorld.CreateAsync(MediaAcquisitionKind.Tv, Tmdb());
        world.Indexer.Publish("Severance.S01E01.720p.WEB-DL.x264-GROUP");
        world.Indexer.Publish("Severance.S01E02.720p.WEB-DL.x264-GROUP");
        var request = await world.RequestAsync(SeveranceTmdb, "Severance", scope: "all");
        var first = await ImportAsync(world, request, "Severance.S01E01.720p.WEB-DL.x264-GROUP", size: 4);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, first.Status, "The missing second episode is fetched before any upgrade.");
        var second = await ImportAsync(world, request, "Severance.S01E02.720p.WEB-DL.x264-GROUP", size: 4);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, second.Status, second.StatusMessage);
        StringAssert.Contains(second.StatusMessage, "better release");
        Assert.AreEqual(2, world.Sabnzbd.Grabs.Count);

        // Only the first episode has a 1080p release: it is grabbed for that episode and replaces its file; the second one keeps its file.
        world.Indexer.Publish("Severance.S01E01.1080p.WEB-DL.x264-GROUP");
        world.Clock.Advance(UpgradePolicy.SearchInterval + TimeSpan.FromHours(1));
        await world.WantedPassAsync();
        var grabbed = await world.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, grabbed.Status, grabbed.StatusMessage);
        Assert.AreEqual("Severance.S01E01.1080p.WEB-DL.x264-GROUP", world.Sabnzbd.Grabs[2].NzbName);
        await ImportAsync(world, request, "Severance.S01E01.1080p.WEB-DL.x264-GROUP", size: 12);

        var files = (await world.Db.StoredFiles.AsNoTracking().OrderBy(file => file.Path).ToListAsync());
        Assert.HasCount(2, files, "One file per episode after the upgrade.");
        Assert.AreEqual(12, new FileInfo(files[0].Path).Length);
        Assert.AreEqual(4, new FileInfo(files[1].Path).Length);
        var qualities = await world.Db.WorkVersions.AsNoTracking().OrderBy(version => version.UnitKey).Select(version => version.Quality).ToListAsync();
        CollectionAssert.AreEqual(new[] { "WEB-1080p", "WEB-720p" }, qualities);
        Assert.IsEmpty(Directory.GetFiles(world.LibraryRoot, "*.replaced-*", SearchOption.AllDirectories));
    }

    [TestMethod]
    public void AnUpgradeScanContinuesAfterTheTitlesItLookedAtAndStartsOverAtTheEndOfTheLibrary()
    {
        var scans = new UpgradeScanState();
        var last = Guid.NewGuid();

        Assert.AreEqual(Guid.Empty, scans.CursorOf(MediaAcquisitionKind.Movie), "The first scan starts at the beginning.");
        scans.Continue(MediaAcquisitionKind.Movie, last, reachedEnd: false);
        Assert.AreEqual(last, scans.CursorOf(MediaAcquisitionKind.Movie), "A full scan leaves the next one to continue after its last title.");
        Assert.AreEqual(Guid.Empty, scans.CursorOf(MediaAcquisitionKind.Tv), "Every media type walks its own library.");
        scans.Continue(MediaAcquisitionKind.Movie, last, reachedEnd: true);
        Assert.AreEqual(Guid.Empty, scans.CursorOf(MediaAcquisitionKind.Movie), "Reaching the end starts the library over, so a title is never skipped for good.");
    }
}
