using Jularr.Web.Features.Acquisition;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Acquisition.Quality;

namespace Jularr.Tests;

[TestClass]
public sealed class SonarrOwnershipGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly AnimeQualityProfile Profile = AnimeQualityProfiles.CreateDefaultAnime1080p();

    private const string FrierenRoot = "/tv/anime/Frieren";
    private const string MeshiRoot = "/tv/anime/Dungeon Meshi";
    private const string FrierenE01 = "/tv/anime/Frieren/Season 01/Frieren - S01E01.mkv";
    private const string MeshiE01 = "/tv/anime/Dungeon Meshi/Season 01/Dungeon Meshi - S01E01.mkv";

    [TestMethod]
    public void RecognizesSonarrPathsByEpisodeFileSeriesFolderAndQueueOutput()
    {
        var sonarr = Sonarr(frierenMonitored: true);

        var episodeFile = SonarrOwnershipRecognizer.RecognizePath(sonarr, MeshiE01);
        Assert.AreEqual(SonarrPathOwnershipKind.EpisodeFile, episodeFile.Kind);
        Assert.AreEqual(2, episodeFile.Series!.Id);

        var folder = SonarrOwnershipRecognizer.RecognizePath(sonarr, FrierenRoot + "/Season 02/new.mkv");
        Assert.AreEqual(SonarrPathOwnershipKind.SeriesFolder, folder.Kind);
        Assert.AreEqual(1, folder.Series!.Id);

        var download = SonarrOwnershipRecognizer.RecognizePath(sonarr, "/downloads/complete/tv/series2.e05/file.mkv");
        Assert.AreEqual(SonarrPathOwnershipKind.QueueOutput, download.Kind);

        // A sibling folder sharing a name prefix is not inside the Sonarr series folder.
        Assert.IsFalse(SonarrOwnershipRecognizer.RecognizePath(sonarr, "/tv/anime/Frieren 2/e01.mkv").IsSonarrOwned);
        Assert.IsFalse(SonarrOwnershipRecognizer.RecognizePath(sonarr, "/media/other/e01.mkv").IsSonarrOwned);
    }

    [TestMethod]
    public void RecognizesSonarrDownloadsFromQueueAndHistory()
    {
        var sonarr = Sonarr(frierenMonitored: true);

        Assert.IsTrue(SonarrOwnershipRecognizer.IsSonarrDownload(sonarr, "SONARR-NZO-2-5"));
        Assert.IsTrue(SonarrOwnershipRecognizer.IsSonarrDownload(sonarr, "sonarr-grab-meshi-04"));
        Assert.IsFalse(SonarrOwnershipRecognizer.IsSonarrDownload(sonarr, "jularr-nzo-1"));
        Assert.IsFalse(SonarrOwnershipRecognizer.IsSonarrDownload(sonarr, null));
    }

    [TestMethod]
    public void SonarrOwnedAnimeRefusesGrabImportAndRename()
    {
        var ownership = new AcquisitionOwnershipSnapshot(State(), Sonarr(frierenMonitored: false));

        Assert.IsFalse(SonarrParallelSafety.CanGrab(ownership, Grab("meshi", 6), Now).Allowed);
        Assert.IsFalse(SonarrParallelSafety.CanImport(
            ownership,
            new AcquisitionImportRequest("meshi", "job-x", null, "/downloads/jularr/x.mkv")).Allowed);
        Assert.IsFalse(SonarrParallelSafety.CanRename(ownership, "meshi", MeshiE01, MeshiE01 + ".new.mkv", Now).Allowed);
    }

    [TestMethod]
    public void ParallelModeRefusesEpisodeSonarrIsAlreadyDownloading()
    {
        var state = State(meshiMode: AnimeManagementMode.ParallelAcquisition);
        var ownership = new AcquisitionOwnershipSnapshot(state, Sonarr(frierenMonitored: false));

        var sameEpisode = SonarrParallelSafety.CanGrab(ownership, Grab("meshi", 5), Now);
        var otherEpisode = SonarrParallelSafety.CanGrab(ownership, Grab("meshi", 7), Now);

        Assert.IsFalse(sameEpisode.Allowed);
        StringAssert.Contains(sameEpisode.Reason, "already downloading");
        Assert.IsTrue(otherEpisode.Allowed, otherEpisode.Reason);
    }

    [TestMethod]
    public void RecentSonarrGrabBlocksDuplicateUnlessItFailed()
    {
        var state = State(meshiMode: AnimeManagementMode.ParallelAcquisition);
        var sonarr = Sonarr(frierenMonitored: false);

        var blocked = SonarrParallelSafety.CanGrab(new(state, sonarr), Grab("meshi", 4), Now);
        Assert.IsFalse(blocked.Allowed);
        StringAssert.Contains(blocked.Reason, "Sonarr grabbed");

        var failed = sonarr with
        {
            History =
            [
                .. sonarr.History,
                History(9100, 2, SonarrHistoryEventKind.DownloadFailed, Now.AddHours(-1), downloadId: "sonarr-grab-meshi-04")
            ]
        };
        Assert.IsTrue(SonarrParallelSafety.CanGrab(new(state, failed), Grab("meshi", 4), Now).Allowed);
        Assert.IsTrue(SonarrParallelSafety.CanGrab(new(state, sonarr), Grab("meshi", 4), Now.AddDays(2)).Allowed);
    }

    [TestMethod]
    public void ManagedAnimeWithStillMonitoredSonarrSeriesRefusesGrab()
    {
        var ownership = new AcquisitionOwnershipSnapshot(State(), Sonarr(frierenMonitored: true));

        var decision = SonarrParallelSafety.CanGrab(ownership, Grab("frieren", 8), Now);

        Assert.IsFalse(decision.Allowed);
        StringAssert.Contains(decision.Reason, "still monitors");
    }

    [TestMethod]
    public void UnavailableSonarrFailsClosedOnlyForSonarrLinkedOrParallelAnime()
    {
        var state = SonarrParallelSafety.SetMode(State(), "standalone", AnimeManagementMode.JularrManaged, Now);
        state = SonarrParallelSafety.SetMode(state, "trial", AnimeManagementMode.ParallelAcquisition, Now);
        var ownership = new AcquisitionOwnershipSnapshot(state, SonarrObservedState.Unavailable("HTTP 503", Now));

        Assert.IsFalse(SonarrParallelSafety.CanGrab(ownership, Grab("frieren", 8), Now).Allowed);
        Assert.IsFalse(SonarrParallelSafety.CanGrab(ownership, Grab("trial", 1), Now).Allowed);
        Assert.IsTrue(SonarrParallelSafety.CanGrab(ownership, Grab("standalone", 1), Now).Allowed);

        var conflicts = SonarrParallelSafety.DetectConflicts(state, ownership.Sonarr);
        Assert.IsTrue(conflicts.Any(conflict => conflict.Kind == "unverified" && conflict.AnimeKey == "frieren"));
    }

    [TestMethod]
    public void ImportRefusesSonarrDownloadsAndDownloadsBothManagersTrack()
    {
        var state = State();
        state = SonarrParallelSafety.RegisterJob(
            state,
            new AcquisitionOwnership("job-shared", "frieren", AcquisitionOwner.Jularr, "rel", AcquisitionOwnershipStatus.Pending, Now, "shared-nzo"));
        var sonarr = Sonarr(frierenMonitored: false) with
        {
            Queue = [.. Sonarr(frierenMonitored: false).Queue, SonarrMigrationTests.QueueItem(1, 9, "shared-nzo")]
        };
        var ownership = new AcquisitionOwnershipSnapshot(state, sonarr);

        var sonarrDownload = SonarrParallelSafety.CanImport(
            ownership,
            new AcquisitionImportRequest("frieren", "unknown-job", "sonarr-nzo-2-5", "/downloads/x"));
        Assert.IsFalse(sonarrDownload.Allowed);
        StringAssert.Contains(sonarrDownload.Reason, "Sonarr imports it");

        var shared = SonarrParallelSafety.CanImport(
            ownership,
            new AcquisitionImportRequest("frieren", "job-shared", null, "/downloads/jularr/shared"));
        Assert.IsFalse(shared.Allowed);
        StringAssert.Contains(shared.Reason, "also tracking");

        var conflicts = SonarrParallelSafety.DetectConflicts(state, sonarr);
        Assert.IsTrue(conflicts.Any(conflict => conflict.Kind == "download" && conflict.Value == "shared-nzo"));
    }

    [TestMethod]
    public void ImportPlannerIgnoresSonarrOwnedDownloadAndReviewsSonarrOwnedReplacement()
    {
        var state = State(meshiMode: AnimeManagementMode.ParallelAcquisition);
        state = SonarrParallelSafety.RegisterJob(
            state,
            new AcquisitionOwnership("job-meshi", "meshi", AcquisitionOwner.Jularr, "rel", AcquisitionOwnershipStatus.Importing, Now, "jularr-nzo-meshi"));
        var ownership = new AcquisitionOwnershipSnapshot(state, Sonarr(frierenMonitored: false));
        var file = new CompletedDownloadFile(
            "/downloads/jularr/Dungeon Meshi - S01E01 BluRay 1080p HEVC FLAC[JA].mkv",
            2_000_000_000);

        var sonarrDownload = AnimeImportPlanner.Plan(
            MeshiContext("sonarr-job", "sonarr-nzo-2-5"),
            [file],
            null,
            ownership);
        Assert.IsTrue(sonarrDownload.BlockedByOwnership);
        Assert.IsTrue(sonarrDownload.Files.All(item => item.Disposition == AnimeImportDisposition.Ignore));

        var upgrade = AnimeImportPlanner.Plan(
            MeshiContext("job-meshi", "jularr-nzo-meshi"),
            [file],
            [new ExistingAnimeFile(MeshiRoot + "/Season 01/Dungeon Meshi - S01E01 WEB-DL 1080p AVC AAC[JA].mkv", 1, 1, 1_400_000_000)],
            ownership);
        var planned = upgrade.Files.Single();
        Assert.IsFalse(upgrade.BlockedByOwnership);
        Assert.AreEqual(AnimeImportDisposition.ManualReview, planned.Disposition);
        Assert.AreEqual(0, planned.ExistingPathsToReplaceAfterCommit.Count);
        Assert.IsTrue(planned.Reasons.Any(reason => reason.StartsWith("Ownership:", StringComparison.Ordinal)));

        // Without the ownership snapshot the same file would replace the Sonarr file automatically.
        var unguarded = AnimeImportPlanner.Plan(
            MeshiContext("job-meshi", "jularr-nzo-meshi"),
            [file],
            [new ExistingAnimeFile(MeshiRoot + "/Season 01/Dungeon Meshi - S01E01 WEB-DL 1080p AVC AAC[JA].mkv", 1, 1, 1_400_000_000)]);
        Assert.AreEqual(AnimeImportDisposition.AutoImport, unguarded.Files.Single().Disposition);
    }

    [TestMethod]
    public void SonarrRenameOfJularrFileIsAConflictAndBlocksRenamingBack()
    {
        var jularrPath = FrierenRoot + "/Season 01/Frieren - S01E02 [Jularr].mkv";
        var sonarrPath = FrierenRoot + "/Season 01/Frieren - S01E02.mkv";
        var state = SonarrParallelSafety.RegisterPath(
            State(),
            new ManagedMediaPath(jularrPath, "frieren", AcquisitionOwner.Jularr, "job-1", Now));
        var sonarr = Sonarr(frierenMonitored: false) with
        {
            History =
            [
                History(9200, 1, SonarrHistoryEventKind.Renamed, Now.AddMinutes(-5), sourcePath: jularrPath, path: sonarrPath)
            ]
        };

        var conflicts = SonarrParallelSafety.DetectConflicts(state, sonarr);
        Assert.IsTrue(conflicts.Any(conflict => conflict.Kind == "rename-loop"));
        Assert.IsTrue(conflicts.Any(conflict => conflict.Kind == "sonarr-activity"));

        var renameBack = SonarrParallelSafety.CanRename(new(state, sonarr), "frieren", sonarrPath, jularrPath, Now);
        Assert.IsFalse(renameBack.Allowed);
        StringAssert.Contains(renameBack.Reason, "rename loop");
    }

    [TestMethod]
    public void AcceptanceOneAnimeMigratedWhileAnotherStaysSonarrManaged()
    {
        // Both anime start in Sonarr, which monitors both series.
        var state = AcquisitionOwnershipState.Empty();
        var before = Sonarr(frierenMonitored: true);

        // Owner keeps Dungeon Meshi on Sonarr and hands Frieren over, unmonitoring it in Sonarr.
        state = SonarrMigration.Plan(state, before, new("meshi", AnimeMigrationAction.KeepSonarr, 2), Now).State;
        var handOver = SonarrMigration.Plan(
            state,
            before,
            new("frieren", AnimeMigrationAction.HandOverToJularr, 1, ApplySonarrMonitoring: true),
            Now);
        Assert.IsTrue(handOver.Allowed, handOver.Reason);
        Assert.IsFalse(handOver.SetSonarrMonitored);
        state = handOver.State;

        // Sonarr applied the unmonitor request; the next observation reflects it.
        var after = Sonarr(frierenMonitored: false);
        var ownership = new AcquisitionOwnershipSnapshot(state, after);
        var monitoring = MonitoringState("frieren", "meshi");

        // Frieren: Jularr grabs the wanted episode exactly once.
        var frierenWanted = new AnimeWantedEpisode(new AnimeEpisodeKey("frieren", 1, 8), AnimeWantedReason.Missing, Now);
        var frierenRelease = Score("Frieren - S01E08 WEB-DL 1080p AVC AAC[JA]");
        var grab = AnimeMonitoringEngine.EvaluateCandidate(Profile, frierenWanted, frierenRelease, null, monitoring, ownership, Now);
        Assert.IsTrue(grab.Grab, grab.Reason);

        monitoring = AnimeMonitoringEngine.MarkGrabbed(monitoring, frierenWanted.Key, frierenRelease.Candidate.Release.ReleaseKey, Now);
        state = SonarrParallelSafety.RegisterJob(
            state,
            new AcquisitionOwnership(
                "job-frieren-08",
                "frieren",
                AcquisitionOwner.Jularr,
                frierenRelease.Candidate.Release.ReleaseKey,
                AcquisitionOwnershipStatus.Pending,
                Now,
                "jularr-nzo-frieren-08"));
        ownership = new AcquisitionOwnershipSnapshot(state, after);

        var duplicate = AnimeMonitoringEngine.EvaluateCandidate(Profile, frierenWanted, frierenRelease, null, monitoring, ownership, Now);
        Assert.IsFalse(duplicate.Grab);
        var duplicateAfterRestart = AnimeMonitoringEngine.EvaluateCandidate(
            Profile,
            frierenWanted,
            frierenRelease,
            null,
            MonitoringState("frieren", "meshi"),
            ownership,
            Now);
        Assert.IsFalse(duplicateAfterRestart.Grab, "The ownership store alone must prevent a duplicate Jularr grab.");

        // Dungeon Meshi: Jularr never grabs, even for episodes Sonarr is not downloading.
        foreach (var episode in new[] { 5, 6 })
        {
            var wanted = new AnimeWantedEpisode(new AnimeEpisodeKey("meshi", 1, episode), AnimeWantedReason.Missing, Now);
            var decision = AnimeMonitoringEngine.EvaluateCandidate(
                Profile,
                wanted,
                Score($"Dungeon Meshi - S01E{episode:00} WEB-DL 1080p AVC AAC[JA]"),
                null,
                monitoring,
                ownership,
                Now);
            Assert.IsFalse(decision.Grab);
            StringAssert.StartsWith(decision.Reason, "Ownership:");
        }

        // Import: Jularr imports its own Frieren download and leaves Sonarr's Meshi download alone.
        var frierenImport = AnimeImportPlanner.Plan(
            new AnimeImportPlanContext(
                "job-frieren-08",
                "frieren",
                ["Frieren"],
                [new RequestedAnimeEpisode(1, 8)],
                Profile,
                DownloadId: "jularr-nzo-frieren-08"),
            [new CompletedDownloadFile("/downloads/jularr/Frieren - S01E08 WEB-DL 1080p AVC AAC[JA].mkv", 900_000_000)],
            null,
            ownership);
        Assert.AreEqual(AnimeImportDisposition.AutoImport, frierenImport.Files.Single().Disposition);

        var meshiImport = AnimeImportPlanner.Plan(
            MeshiContext("sonarr-queue-101", "sonarr-nzo-2-5"),
            [new CompletedDownloadFile("/downloads/complete/tv/series2.e05/Dungeon Meshi - S01E05 WEB-DL 1080p AVC AAC[JA].mkv", 900_000_000)],
            null,
            ownership);
        Assert.IsTrue(meshiImport.BlockedByOwnership);

        // Rename: the migrated Frieren file may be renamed once; Meshi files are never touched,
        // and Jularr does not rename back what Sonarr renamed (no rename loop).
        var frierenTarget = FrierenRoot + "/Season 01/Frieren - S01E01 - The Journey's End.mkv";
        var rename = SonarrParallelSafety.CanRename(ownership, "frieren", FrierenE01, frierenTarget, Now);
        Assert.IsTrue(rename.Allowed, rename.Reason);
        Assert.IsFalse(SonarrParallelSafety.CanRename(ownership, "meshi", MeshiE01, MeshiE01 + ".renamed.mkv", Now).Allowed);
        Assert.IsFalse(SonarrParallelSafety.CanRename(ownership, "frieren", MeshiE01, frierenTarget, Now).Allowed);

        var sonarrRenamedBack = after with
        {
            History =
            [
                .. after.History,
                History(9300, 1, SonarrHistoryEventKind.Renamed, Now.AddMinutes(1), sourcePath: frierenTarget, path: FrierenE01)
            ]
        };
        Assert.IsFalse(SonarrParallelSafety.CanRename(
            new AcquisitionOwnershipSnapshot(state, sonarrRenamedBack),
            "frieren",
            FrierenE01,
            frierenTarget,
            Now.AddMinutes(2)).Allowed);

        // No conflicts while Sonarr stays out of the migrated anime.
        Assert.AreEqual(0, SonarrParallelSafety.DetectConflicts(state, after).Count);
    }

    private static AcquisitionOwnershipState State(
        AnimeManagementMode meshiMode = AnimeManagementMode.ReadOnlyCoexistence)
    {
        var state = AcquisitionOwnershipState.Empty();
        state.Anime["frieren"] = new AnimeManagementAssignment(
            "frieren",
            AnimeManagementMode.JularrManaged,
            Now.AddDays(-1),
            1,
            "Frieren",
            SonarrUnmonitoredByJularr: true);
        state.Anime["meshi"] = new AnimeManagementAssignment("meshi", meshiMode, Now.AddDays(-1), 2, "Dungeon Meshi");
        return state;
    }

    private static SonarrObservedState Sonarr(bool frierenMonitored) =>
        SonarrObservedState.FromObservation(
            [
                new SonarrObservedSeries(1, "Frieren", FrierenRoot, frierenMonitored),
                new SonarrObservedSeries(2, "Dungeon Meshi", MeshiRoot, true)
            ],
            [
                new SonarrObservedEpisodeFile(21, 1, 1, FrierenE01),
                new SonarrObservedEpisodeFile(11, 2, 1, MeshiE01)
            ],
            [SonarrMigrationTests.QueueItem(2, 5)],
            [
                History(
                    9001,
                    2,
                    SonarrHistoryEventKind.Grabbed,
                    Now.AddHours(-2),
                    downloadId: "sonarr-grab-meshi-04",
                    episode: new SonarrObservedEpisode(1, 4, 4)),
                History(
                    9000,
                    1,
                    SonarrHistoryEventKind.Imported,
                    Now.AddDays(-3),
                    path: FrierenE01)
            ],
            Now);

    private static SonarrObservedHistoryEvent History(
        long id,
        int seriesId,
        SonarrHistoryEventKind kind,
        DateTimeOffset at,
        string? downloadId = null,
        string? sourcePath = null,
        string? path = null,
        SonarrObservedEpisode? episode = null) =>
        new(id, seriesId, kind, kind.ToString(), at, $"history-{id}", null, downloadId, sourcePath, path, episode);

    private static AcquisitionGrabRequest Grab(string animeKey, int episode) =>
        new(animeKey, $"{animeKey}-release-{episode}", 1, episode, episode, episode, episode);

    private static AnimeReleaseScoreResult Score(string title) =>
        AnimeReleaseScorer.Score(Profile, new AnimeReleaseCandidate(AnimeReleaseParser.Parse(title), 900_000_000));

    private static AnimeMonitoringState MonitoringState(params string[] animeKeys)
    {
        var state = AnimeMonitoringState.Empty();
        foreach (var key in animeKeys)
        {
            state.Anime[key] = new AnimeMonitorSettings(
                key,
                Monitored: true,
                SearchOnAdd: true,
                SeasonOverrides: new Dictionary<int, bool>(),
                EpisodeOverrides: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase));
        }

        return state;
    }

    private static AnimeImportPlanContext MeshiContext(string jobId, string downloadId) =>
        new(
            jobId,
            "meshi",
            ["Dungeon Meshi"],
            [new RequestedAnimeEpisode(1, 1), new RequestedAnimeEpisode(1, 5)],
            Profile,
            DownloadId: downloadId);
}
