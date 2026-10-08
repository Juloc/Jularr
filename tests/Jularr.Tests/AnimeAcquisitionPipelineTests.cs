using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Media.Optimization;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Sonarr;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Tests;

[TestClass]
public sealed class AnimeAcquisitionPipelineTests
{
    private const string Best = "Frieren.S01E02.1080p.WEB-DL.AAC.H.264-GRP";
    private const string Lower = "Frieren.S01E02.720p.WEB-DL.AAC.H.264-GRP";
    private const string Rejected = "Frieren.S01E02.480p.WEB-DL.AAC.H.264-GRP";
    private const string OtherSeries = "Dungeon.Meshi.S01E02.1080p.WEB-DL.AAC.H.264-GRP";

    [TestMethod]
    public async Task ScheduledRunGrabsBestAcceptedReleaseOnceAndRecordsEveryDecision()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        AddStandardReleases(environment);

        var request = await environment.SearchNowAsync();

        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request!.Status, request.StatusMessage);
        var grab = environment.Sabnzbd.Grabs.Single();
        Assert.AreEqual(Best, grab.NzbName);
        Assert.AreEqual("anime", grab.Category);

        var payload = AnimeRequestPayload.Of(request);
        Assert.AreEqual(new AnimeEpisodeKey(AnimeAcquisitionEnvironment.AnimeKey, 1, 2, 2), payload.Episodes!.Single());
        var job = (await environment.Ownership.LoadAsync()).Jobs[request.OperationId!.Value.ToString()];
        Assert.AreEqual(AcquisitionOwner.Jularr, job.Owner);
        Assert.AreEqual(AcquisitionOwnershipStatus.Pending, job.Status);

        var decisions = (await environment.LogsAsync(AnimeAcquisitionPipeline.LogModule)).Select(entry => entry.Message).ToArray();
        Assert.IsTrue(decisions.Any(message => message.StartsWith($"Accepted: {Best}", StringComparison.Ordinal)), string.Join(Environment.NewLine, decisions));
        Assert.IsTrue(decisions.Any(message => message.StartsWith($"Rejected: {Rejected}", StringComparison.Ordinal) && message.Contains("quality profile", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(decisions.Any(message => message.StartsWith($"Rejected: {OtherSeries}", StringComparison.Ordinal) && message.Contains("does not match this anime", StringComparison.Ordinal)));
        Assert.IsTrue(decisions.Any(message => message.StartsWith("Sent to SABnzbd", StringComparison.Ordinal)));

        await environment.SearchNowAsync();
        Assert.AreEqual(1, environment.Sabnzbd.Grabs.Count, "A grabbed episode is not searched again.");
    }

    [TestMethod]
    public async Task SonarrOwnedAnimeAndReleasesAreNeverGrabbed()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(mode: null);
        AddStandardReleases(environment);

        // Default mode: Sonarr keeps the anime (read-only coexistence).
        var readOnly = await environment.SearchNowAsync();
        Assert.AreEqual(0, environment.Prowlarr.Queries.Count, "No indexer search for an anime Sonarr manages.");
        StringAssert.Contains(readOnly!.StatusMessage, "Sonarr manages this series");

        // Parallel mode, but Sonarr owns both acceptable releases.
        var now = DateTimeOffset.UtcNow;
        await environment.Ownership.UpdateAsync(state =>
        {
            var parallel = SonarrParallelSafety.SetMode(state, AnimeAcquisitionEnvironment.AnimeKey, AnimeManagementMode.ParallelAcquisition, now);
            parallel = SonarrParallelSafety.RegisterJob(parallel, new AcquisitionOwnership("sonarr-1", AnimeAcquisitionEnvironment.AnimeKey, AcquisitionOwner.Sonarr, AnimeReleaseParser.Parse(Best).ReleaseKey, AcquisitionOwnershipStatus.Pending, now));
            return SonarrParallelSafety.RegisterJob(parallel, new AcquisitionOwnership("sonarr-2", AnimeAcquisitionEnvironment.AnimeKey, AcquisitionOwner.Sonarr, AnimeReleaseParser.Parse(Lower).ReleaseKey, AcquisitionOwnershipStatus.Pending, now));
        });

        var parallelRun = await environment.SearchNowAsync();

        Assert.AreEqual(1, environment.Prowlarr.Queries.Count > 0 ? 1 : 0, "The anime is searched in parallel mode.");
        Assert.AreEqual(0, environment.Sabnzbd.Grabs.Count);
        var decisions = (await environment.LogsAsync(AnimeAcquisitionPipeline.LogModule)).Select(entry => entry.Message).ToArray();
        Assert.IsTrue(decisions.Any(message => message.StartsWith($"Rejected: {Best}", StringComparison.Ordinal) && message.Contains("Ownership:", StringComparison.Ordinal)), string.Join(Environment.NewLine, decisions));
        Assert.IsNotNull(AnimeRequestPayload.Of(parallelRun!).NextSearchUtc, "The request backs off instead of being searched every pass.");
    }

    [TestMethod]
    public async Task RestartNeverGrabsTheSameEpisodeTwice()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        AddStandardReleases(environment);
        var request = await environment.SearchNowAsync();
        Assert.AreEqual(1, environment.Sabnzbd.Grabs.Count);

        // The grab is the request's own state, so a restart finds it again and never downloads the episode twice.
        await environment.RestartAsync();
        await environment.Scheduler.RecoverAsync(CancellationToken.None);
        var afterRestart = await environment.SearchNowAsync();

        Assert.AreEqual(request!.OperationId, afterRestart!.OperationId);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, afterRestart.Status);
        Assert.AreEqual(1, environment.Sabnzbd.Grabs.Count);
        Assert.AreEqual(1, (await environment.Operations.ListAsync(new OperationListFilter(Kind: AnimeAcquisitionEngine.OperationKind))).Count);
    }

    [TestMethod]
    [DataRow(LosslessPlaybackOptimizationMode.Off, 0)]
    [DataRow(LosslessPlaybackOptimizationMode.SafeOnly, 1)]
    public async Task ImportQueuesPlaybackOptimizationOnlyWhenEnabled(LosslessPlaybackOptimizationMode mode, int expectedOperations)
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        await environment.ImportSettings.UpdateAsync(state => state with { PlaybackOptimization = mode });
        AddStandardReleases(environment);
        await environment.SearchNowAsync();

        var download = environment.AddCompletedDownload(Best, $"{Best}.mkv");
        var completed = await environment.CompleteLatestDownloadAsync(download);
        var record = await environment.ImportCompletedAsync(completed, download);

        Assert.AreEqual(AnimeImportStatus.Imported, record!.Status, record.Message);
        var optimizations = await environment.Operations.ListAsync(
            new OperationListFilter(Kind: MediaContainerOptimizer.OperationKind));
        Assert.AreEqual(expectedOperations, optimizations.Count);
        if (expectedOperations > 0)
        {
            Assert.AreEqual(OperationStatus.Queued, optimizations[0].Status, "The import does not wait for the optimization.");
            Assert.AreEqual(OperationLane.Maintenance, optimizations[0].Lane);
            var importLog = (await environment.LogsAsync(AnimeImportExecutor.LogModule)).Select(entry => entry.Message);
            CollectionAssert.Contains(importLog.ToList(), "Queued lossless playback optimization for 1 file(s).");
        }
    }

    [TestMethod]
    public async Task OptimizedReplacementWaitsForImportsAndMovesAcquisitionPathRecords()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        AddStandardReleases(environment);
        await environment.SearchNowAsync();
        var download = environment.AddCompletedDownload(Best, $"{Best}.mkv");
        var record = await environment.ImportCompletedAsync(await environment.CompleteLatestDownloadAsync(download), download);
        Assert.AreEqual(AnimeImportStatus.Imported, record!.Status, record.Message);
        var media = (await environment.MediaFileAsync(1, 2))!;
        var replacement = new MediaFileReplacement(media.Id, media.Path, Path.ChangeExtension(media.Path, ".mp4"));

        await environment.WithScopeAsync(async services =>
        {
            var participant = new AcquisitionMediaReplacementParticipant(
                environment.Db,
                environment.Ownership,
                services.GetRequiredService<SonarrObservationService>(),
                environment.Imports);

            using (AnimeImportExecutor.TryEnterExecution())
            {
                var busy = await participant.CheckAsync(replacement, CancellationToken.None);
                Assert.AreEqual(MediaReplacementVerdict.Wait, busy.Verdict, "A moving import keeps the file in place.");
            }

            var allowed = await participant.CheckAsync(replacement, CancellationToken.None);
            Assert.AreEqual(MediaReplacementVerdict.Allowed, allowed.Verdict, allowed.Reason);
            Assert.IsNull(AnimeImportExecutor.TryEnterExecution(), "The lease excludes imports until the path is recorded.");
            allowed.Lease!.Dispose();

            File.Move(replacement.SourcePath, replacement.TargetPath);
            await participant.OnReplacedAsync(replacement, CancellationToken.None);
            await participant.OnReplacedAsync(replacement, CancellationToken.None);
            return true;
        });

        var paths = (await environment.Ownership.LoadAsync()).Paths.Values.Select(path => path.Path).ToArray();
        CollectionAssert.Contains(paths, SonarrParallelSafety.NormalizePath(replacement.TargetPath));
        CollectionAssert.DoesNotContain(paths, SonarrParallelSafety.NormalizePath(replacement.SourcePath));
        var imported = (await environment.Imports.LoadAsync()).Imports.Single().Files.Single(file => file.Status == AnimeImportFileStatus.Imported);
        Assert.AreEqual(replacement.TargetPath, imported.ImportedPath);
    }

    [TestMethod]
    public async Task CompletedDownloadIsImportedWithNamingAndReconciledIntoTheLibrary()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        AddStandardReleases(environment);
        await environment.SearchNowAsync();

        var download = environment.AddCompletedDownload(Best, $"{Best}.mkv");
        var completed = await environment.CompleteLatestDownloadAsync(download);
        var record = await environment.ImportCompletedAsync(completed, download);

        Assert.AreEqual(AnimeImportStatus.Imported, record!.Status, record.Message);
        var expected = Path.Combine(environment.SeriesFolder, "Season 01", "Frieren - S01E02 - Episode 2.mkv");
        Assert.IsTrue(File.Exists(expected), "The file is named by the naming profile.");
        Assert.IsFalse(File.Exists(Path.Combine(download, $"{Best}.mkv")));
        var mediaFile = await environment.MediaFileAsync(1, 2);
        Assert.IsNotNull(mediaFile, "The folder reconciliation added the episode.");
        Assert.AreEqual(Path.GetFullPath(expected), mediaFile.Path);
        StringAssert.Contains(record.Message, "Library reconciled");

        var downloadImportLog = await environment.Operations.ListLogsAsync(
            new OperationLogFilter(OperationId: completed.Id, Module: "Import"));
        CollectionAssert.AreEqual(
            new[]
            {
                "Verifying completed files before import.",
                "Importing completed files into the library.",
                "Matching imported Anime metadata.",
                record.Message
            },
            downloadImportLog.Reverse().Select(entry => entry.Message).ToArray());

        Assert.AreEqual(AcquisitionOwnershipStatus.Completed, (await environment.Ownership.LoadAsync()).Jobs[completed.Id.ToString()].Status);
        var importLog = (await environment.LogsAsync(AnimeImportExecutor.LogModule)).Select(entry => entry.Message).ToArray();
        Assert.IsTrue(importLog.Any(message => message.StartsWith($"Imported {Best}.mkv as S01E02", StringComparison.Ordinal)), string.Join(Environment.NewLine, importLog));

        // Running the import again changes nothing.
        Assert.AreEqual(record.Id, (await environment.ImportCompletedAsync(completed, download))!.Id);

        var searches = environment.Prowlarr.Queries.Count;
        var next = await environment.SearchNowAsync();
        Assert.AreEqual(searches, environment.Prowlarr.Queries.Count, "The imported episode is not searched again.");
        Assert.AreNotEqual(AcquisitionRequestStatus.Downloading, next!.Status);
    }

    [TestMethod]
    public async Task AbsoluteNumberedDownloadIsImportedAsTheMappedLocalEpisode()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(episodeCount: 1);
        // AniList numbers the second season on from the first: its episode 13 is local S02E01.
        await environment.AniListAccounts.TryAddEpisodeMappingAsync(new AnimeEpisodeMetadataMapping(Guid.NewGuid(), environment.AnimeId, 2, 1, 12, 13, "anilist", "154587", "Frieren", 12, DateTimeOffset.UtcNow), CancellationToken.None);
        const string title = "[Group] Frieren - 13 WEB-DL 1080p HEVC AAC[JA]";
        var started = await environment.StartAcquisitionAsync([new AnimeEpisodeKey(AnimeAcquisitionEnvironment.AnimeKey, 2, 1, 13)], title);
        Assert.IsTrue(started.Success, started.Message);

        var download = environment.AddCompletedDownload(title, $"{title}.mkv");
        var record = await environment.ImportCompletedAsync(await environment.CompleteLatestDownloadAsync(download), download);

        Assert.AreEqual(AnimeImportStatus.Imported, record!.Status, record.Message);
        var expected = Path.Combine(environment.SeriesFolder, "Season 02", "Frieren - S02E01 - Episode 1.mkv");
        Assert.IsTrue(File.Exists(expected));
        Assert.AreEqual(Path.GetFullPath(expected), (await environment.MediaFileAsync(2, 1))?.Path, "AniList episode 13 is local S02E01.");
        Assert.IsNull(await environment.MediaFileAsync(1, 13));
    }

    [TestMethod]
    public async Task UnwritableLibraryFolderBecomesManualImportThatCanBeCompleted()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(seasonFolders: false);
        AddStandardReleases(environment);
        await environment.SearchNowAsync();

        // A file where the season folder must be created makes the library folder unwritable for
        // the import, like a read-only mount.
        var blocker = Path.Combine(environment.SeriesFolder, "Season 01");
        await File.WriteAllTextAsync(blocker, "not a folder");
        var download = environment.AddCompletedDownload(Best, $"{Best}.mkv");
        var record = await environment.ImportCompletedAsync(await environment.CompleteLatestDownloadAsync(download), download);

        Assert.AreEqual(AnimeImportStatus.ManualRequired, record!.Status);
        var file = record.Files.Single(item => item.SourcePath.EndsWith(".mkv", StringComparison.Ordinal));
        Assert.AreEqual(AnimeImportFileStatus.Failed, file.Status);
        StringAssert.Contains(file.Error, "not writable");
        Assert.IsTrue(File.Exists(Path.Combine(download, $"{Best}.mkv")), "The download is left untouched.");
        Assert.IsTrue(record.NeedsAttention);
        var operation = await environment.Operations.GetAsync(record.ImportOperationId!.Value);
        Assert.IsFalse(operation!.IsActive, "A manual-import state does not keep the operation running.");

        File.Delete(blocker);
        var manual = await environment.ImportManuallyAsync(record.Id, file.SourcePath, 1, 2);

        Assert.IsTrue(manual.Success, manual.Message);
        Assert.IsTrue(File.Exists(Path.Combine(environment.SeriesFolder, "Season 01", "Frieren - S01E02 - Episode 2.mkv")));
        Assert.IsNotNull(await environment.MediaFileAsync(1, 2));
        Assert.AreEqual(AnimeImportStatus.Imported, (await environment.Imports.GetAsync(record.Id))!.Status);
    }

    [TestMethod]
    public async Task ExistingDestinationAndSonarrOwnedPathsAreNeverOverwritten()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        AddStandardReleases(environment);
        await environment.SearchNowAsync();

        var destination = Path.Combine(environment.SeriesFolder, "Season 01", "Frieren - S01E02 - Episode 2.mkv");
        await File.WriteAllTextAsync(destination, "someone else's file");
        var download = environment.AddCompletedDownload(Best, $"{Best}.mkv");
        var record = await environment.ImportCompletedAsync(await environment.CompleteLatestDownloadAsync(download), download);

        Assert.AreEqual(AnimeImportStatus.ManualRequired, record!.Status);
        StringAssert.Contains(record.Files.Single(item => item.SourcePath.EndsWith(".mkv", StringComparison.Ordinal)).Error, "already exists");
        Assert.AreEqual("someone else's file", await File.ReadAllTextAsync(destination));

        // Sonarr owns the destination: a manual import is refused as well.
        File.Delete(destination);
        await environment.Ownership.UpdateAsync(state => SonarrParallelSafety.RegisterPath(
            state,
            new ManagedMediaPath(destination, AnimeAcquisitionEnvironment.AnimeKey, AcquisitionOwner.Sonarr, null, DateTimeOffset.UtcNow)));
        var source = record.Files.Single(item => item.SourcePath.EndsWith(".mkv", StringComparison.Ordinal)).SourcePath;
        var manual = await environment.ImportManuallyAsync(record.Id, source, 1, 2);

        Assert.IsFalse(manual.Success);
        Assert.IsFalse(File.Exists(destination));
        Assert.IsTrue(File.Exists(source));
        var updated = await environment.Imports.GetAsync(record.Id);
        StringAssert.Contains(updated!.Files.Single(item => item.SourcePath == source).Error, "owned by Sonarr");
    }

    [TestMethod]
    public async Task DownloadCompletedWhileStoppedIsImportedByRestartRecovery()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        AddStandardReleases(environment);
        await environment.SearchNowAsync();

        // SABnzbd finished and the monitor recorded it, but Jularr stopped before importing.
        var download = environment.AddCompletedDownload(Best, $"{Best}.mkv");
        await environment.CompleteLatestDownloadAsync(download);
        await environment.RestartAsync();

        Assert.AreEqual(1, await environment.Scheduler.RecoverAsync(CancellationToken.None));

        Assert.IsNotNull(await environment.MediaFileAsync(1, 2));
        var record = (await environment.Imports.LoadAsync()).Imports.Single();
        Assert.AreEqual(AnimeImportStatus.Imported, record.Status, record.Message);
        Assert.AreEqual(0, await environment.Scheduler.RecoverAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task ImportWaitsForRunningLibraryScanAndResumesOnTheNextRun()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        AddStandardReleases(environment);
        await environment.SearchNowAsync();
        var scan = await environment.Operations.CreateAsync(new OperationDescriptor("library-scan", "Library", "Library scan"));
        await environment.Operations.MarkRunningAsync(scan);

        var download = environment.AddCompletedDownload(Best, $"{Best}.mkv");
        var record = await environment.ImportCompletedAsync(await environment.CompleteLatestDownloadAsync(download), download);

        Assert.AreEqual(AnimeImportStatus.Importing, record!.Status);
        StringAssert.Contains(record.Message, "Waiting for 'Library scan'");
        Assert.IsTrue(File.Exists(Path.Combine(download, $"{Best}.mkv")));

        await environment.Operations.MarkSucceededAsync(scan);
        await environment.SearchNowAsync();

        Assert.AreEqual(AnimeImportStatus.Imported, (await environment.Imports.GetAsync(record.Id))!.Status);
        Assert.IsNotNull(await environment.MediaFileAsync(1, 2));
    }

    private static void AddStandardReleases(AnimeAcquisitionEnvironment environment)
    {
        environment.Prowlarr.Releases.AddRange(
        [
            AnimeAcquisitionEnvironment.Release(Lower, "g720"),
            AnimeAcquisitionEnvironment.Release(Rejected, "g480"),
            AnimeAcquisitionEnvironment.Release(Best, "g1080"),
            AnimeAcquisitionEnvironment.Release(OtherSeries, "other")
        ]);
    }
}
