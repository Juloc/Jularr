using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.History;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Policy;
using Jularr.Web.Features.Library;

namespace Jularr.Tests;

[TestClass]
public sealed class AcquisitionImportPolicyTests
{
    private const string Best = "Frieren.S01E02.1080p.WEB-DL.AAC.H.264-GRP";

    [TestMethod]
    public async Task CopyModeKeepsTheDownloadedFileInPlace()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        await environment.UsePlacementAsync(LibraryPlacementPolicy.Copy);
        await environment.StartAcquisitionAsync([new AnimeEpisodeKey(AnimeAcquisitionEnvironment.AnimeKey, 1, 2, 2)], Best);

        var download = environment.AddCompletedDownload(Best, $"{Best}.mkv");
        var record = await environment.ImportCompletedAsync(await environment.CompleteLatestDownloadAsync(download), download);

        Assert.AreEqual(AnimeImportStatus.Imported, record!.Status, record.Message);
        Assert.IsTrue(File.Exists(Path.Combine(download, $"{Best}.mkv")), "Copy mode never removes the source.");
        Assert.IsNotNull(await environment.MediaFileAsync(1, 2));
    }

    [TestMethod]
    public async Task HardlinkModeLeavesTheSourceInPlaceOnTheSameFilesystem()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        await environment.UsePlacementAsync(LibraryPlacementPolicy.Hardlink);
        await environment.StartAcquisitionAsync([new AnimeEpisodeKey(AnimeAcquisitionEnvironment.AnimeKey, 1, 2, 2)], Best);

        var download = environment.AddCompletedDownload(Best, $"{Best}.mkv");
        var record = await environment.ImportCompletedAsync(await environment.CompleteLatestDownloadAsync(download), download);

        Assert.AreEqual(AnimeImportStatus.Imported, record!.Status, record.Message);
        Assert.IsTrue(File.Exists(Path.Combine(download, $"{Best}.mkv")), "A hardlink keeps the source's own directory entry.");
        var mediaFile = await environment.MediaFileAsync(1, 2);
        Assert.IsNotNull(mediaFile);
        Assert.IsTrue(File.Exists(mediaFile!.Path));
    }

    [TestMethod]
    public async Task PlainHardlinkAcrossFilesystemsFailsWithAClearReasonInstead()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync(new AlwaysCrossDeviceHardLinkCreator());
        await environment.SeedFrierenAsync();
        await environment.UsePlacementAsync(LibraryPlacementPolicy.Hardlink);
        await environment.StartAcquisitionAsync([new AnimeEpisodeKey(AnimeAcquisitionEnvironment.AnimeKey, 1, 2, 2)], Best);

        var download = environment.AddCompletedDownload(Best, $"{Best}.mkv");
        var record = await environment.ImportCompletedAsync(await environment.CompleteLatestDownloadAsync(download), download);

        Assert.AreEqual(AnimeImportStatus.ManualRequired, record!.Status);
        var file = record.Files.Single(item => item.SourcePath.EndsWith(".mkv", StringComparison.Ordinal));
        StringAssert.Contains(file.Error, "different filesystem");
        Assert.IsTrue(File.Exists(Path.Combine(download, $"{Best}.mkv")), "A failed hardlink never touches the source.");
    }

    [TestMethod]
    public async Task HardlinkOrCopyFallsBackToACopyAcrossFilesystems()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync(new AlwaysCrossDeviceHardLinkCreator());
        await environment.SeedFrierenAsync();
        await environment.UsePlacementAsync(LibraryPlacementPolicy.HardlinkOrCopy);
        await environment.StartAcquisitionAsync([new AnimeEpisodeKey(AnimeAcquisitionEnvironment.AnimeKey, 1, 2, 2)], Best);

        var download = environment.AddCompletedDownload(Best, $"{Best}.mkv");
        var record = await environment.ImportCompletedAsync(await environment.CompleteLatestDownloadAsync(download), download);

        Assert.AreEqual(AnimeImportStatus.Imported, record!.Status, record.Message);
        Assert.IsTrue(File.Exists(Path.Combine(download, $"{Best}.mkv")), "The explicit fallback copies instead of moving.");
        Assert.IsNotNull(await environment.MediaFileAsync(1, 2));
    }

    [TestMethod]
    public async Task RemotePathMappingTranslatesTheCompletedDownloadPathBeforeImport()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        await environment.StartAcquisitionAsync([new AnimeEpisodeKey(AnimeAcquisitionEnvironment.AnimeKey, 1, 2, 2)], Best);
        var localFolder = environment.AddCompletedDownload(Best, $"{Best}.mkv");

        // The download client reports the completed job under a mount Jularr does not share.
        var remoteReportedPath = "/downloads/complete/" + Path.GetFileName(localFolder);
        await environment.ImportSettings.UpdateAsync(state => state.WithRemotePathMappings(
            MediaAcquisitionKind.Anime,
            [new RemotePathMapping("/downloads/complete", Path.GetDirectoryName(localFolder)!)]));

        var completed = await environment.CompleteLatestDownloadAsync(remoteReportedPath);
        var record = await environment.ImportCompletedAsync(completed, remoteReportedPath);

        Assert.AreEqual(AnimeImportStatus.Imported, record!.Status, record.Message);
        Assert.IsNotNull(await environment.MediaFileAsync(1, 2));
    }

    [TestMethod]
    public async Task WithoutTheMappingTheUntranslatedRemotePathCannotBeImported()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        await environment.StartAcquisitionAsync([new AnimeEpisodeKey(AnimeAcquisitionEnvironment.AnimeKey, 1, 2, 2)], Best);
        environment.AddCompletedDownload(Best, $"{Best}.mkv");

        var remoteReportedPath = "/downloads/complete/unmapped-job";
        var completed = await environment.CompleteLatestDownloadAsync(remoteReportedPath);
        var record = await environment.ImportCompletedAsync(completed, remoteReportedPath);

        Assert.AreEqual(AnimeImportStatus.Failed, record!.Status);
        StringAssert.Contains(record.Message, "does not exist or is not mounted");
    }

    [TestMethod]
    public async Task NewAnimeWithoutAnExistingFolderUsesItsAssignedTargetRoot()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        var secondRoot = new LibraryRoot { Name = "Anime 2", Path = Path.Combine(environment.TempRoot, "anime2") };
        Directory.CreateDirectory(secondRoot.Path);
        environment.Db.LibraryRoots.Add(secondRoot);
        var freshAnime = new Jularr.Web.Features.Library.Anime { Key = "fresh-anime", Title = "Fresh Anime" };
        environment.Db.Anime.Add(freshAnime);
        await environment.Db.SaveChangesAsync();

        var withoutPreference = await environment.GetLibraryLocationAsync(freshAnime.Id);
        Assert.AreEqual(environment.Root.Id, withoutPreference!.RootId, "Without a preference the first enabled root is used.");

        var withPreference = await environment.GetLibraryLocationAsync(freshAnime.Id, secondRoot.Id);
        Assert.AreEqual(secondRoot.Id, withPreference!.RootId, "The anime's assigned target root is used for its first import.");
    }

    [TestMethod]
    public async Task AnAnimeWithAnExistingFolderIgnoresATargetRootAssignedLater()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        var secondRoot = new LibraryRoot { Name = "Anime 2", Path = Path.Combine(environment.TempRoot, "anime2") };
        Directory.CreateDirectory(secondRoot.Path);
        environment.Db.LibraryRoots.Add(secondRoot);
        await environment.Db.SaveChangesAsync();

        var location = await environment.GetLibraryLocationAsync(environment.AnimeId, secondRoot.Id);

        Assert.AreEqual(environment.Root.Id, location!.RootId, "An anime with existing files keeps importing into its current root.");
    }

    [TestMethod]
    public async Task DelayProfileHoldsBackAReleaseBelowTheUpgradeCutoffUntilAPreferredReleaseAppears()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        await environment.Scheduler.RunExclusiveAsync(
            (pipeline, token) => pipeline.UpdateAnimeSettingsAsync(environment.AnimeId, true, false, null, [], token, tagIds: ["slow"]),
            CancellationToken.None);
        await environment.Policy.UpdateAsync(state => state with
        {
            Tags = [new AcquisitionTag("slow", "Slow")],
            DelayProfiles = [new AnimeDelayProfile("delay-1", "Wait for BluRay", 60, null, ["slow"], false)]
        });

        environment.Prowlarr.Releases.Add(AnimeAcquisitionEnvironment.Release(Best, "g1080"));
        var held = await environment.Scheduler.RunNowAsync(null, AnimeSearchTrigger.PeriodicMissing, CancellationToken.None);

        Assert.AreEqual(0, held.Grabs, "A release below the profile's upgrade cutoff is delayed.");
        Assert.AreEqual(0, environment.Sabnzbd.Grabs.Count);
        var attempts = await environment.MonitoringStateAsync();
        Assert.IsFalse(attempts.Attempts.ContainsKey("frieren:S01E02"), "A delay is not the exponential search-failure backoff.");

        const string preferred = "Frieren.S01E02.1080p.BluRay.AAC.H.264-GRP";
        environment.Prowlarr.Releases.Add(AnimeAcquisitionEnvironment.Release(preferred, "gbd"));
        var grabbed = await environment.Scheduler.RunNowAsync(null, AnimeSearchTrigger.PeriodicMissing, CancellationToken.None);

        Assert.AreEqual(1, grabbed.Grabs, "A release meeting the upgrade cutoff bypasses the delay immediately.");
        Assert.AreEqual(preferred, environment.Sabnzbd.Grabs.Single().NzbName);
    }

    [TestMethod]
    public async Task TagScopedIndexerRestrictionAllowsTheEnabledEntryWhileTheAnimesOwnProwlarrSubIndexerSelectionStaysIndependent()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        await environment.Scheduler.RunExclusiveAsync(
            (pipeline, token) => pipeline.UpdateAnimeSettingsAsync(environment.AnimeId, true, false, null, [1, 2, 3], token, tagIds: ["fast-track"]),
            CancellationToken.None);
        await environment.Policy.UpdateAsync(state => state with
        {
            Tags = [new AcquisitionTag("fast-track", "Fast track")],
            IndexerRestrictions = [new AnimeIndexerRestriction("r1", "Trusted only", ["fast-track"], [environment.ProwlarrIndexerEntryId])]
        });
        environment.Prowlarr.Releases.Add(AnimeAcquisitionEnvironment.Release(Best, "g1080"));

        var run = await environment.Scheduler.RunNowAsync(null, AnimeSearchTrigger.PeriodicMissing, CancellationToken.None);

        Assert.AreEqual(1, run.Grabs, "The restriction allows the anime's only enabled indexer entry, so the search proceeds normally.");
        var connection = environment.Prowlarr.Connections.Last();
        CollectionAssert.AreEquivalent(new[] { 1, 2, 3 }, connection.Settings.IndexerIds, "The anime's own Prowlarr sub-indexer selection is a separate axis, unaffected by the entry-level restriction.");
    }

    [TestMethod]
    public async Task ANonOverlappingIndexerRestrictionSearchesNoIndexersInsteadOfFallingBackToUnrestricted()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        await environment.Scheduler.RunExclusiveAsync(
            (pipeline, token) => pipeline.UpdateAnimeSettingsAsync(environment.AnimeId, true, false, null, [1, 2, 3], token, tagIds: ["fast-track"]),
            CancellationToken.None);
        await environment.Policy.UpdateAsync(state => state with
        {
            Tags = [new AcquisitionTag("fast-track", "Fast track")],
            // References an indexer entry that is not the one enabled entry (the seeded Prowlarr
            // connection): zero overlap with what is actually enabled.
            IndexerRestrictions = [new AnimeIndexerRestriction("r1", "Trusted only", ["fast-track"], [Guid.NewGuid()])]
        });
        // A release exists and would otherwise be grabbed; it must never be found because the
        // restriction leaves no indexer to search at all.
        environment.Prowlarr.Releases.Add(AnimeAcquisitionEnvironment.Release(Best, "g1080"));

        var run = await environment.Scheduler.RunNowAsync(null, AnimeSearchTrigger.PeriodicMissing, CancellationToken.None);

        Assert.AreEqual(0, run.Grabs);
        Assert.AreEqual(0, environment.Prowlarr.Queries.Count, "No indexers were searched at all; Prowlarr is never called with the anime's unrestricted selection.");
        Assert.AreEqual(0, environment.Sabnzbd.Grabs.Count);

        var monitoring = await environment.MonitoringStateAsync();
        Assert.IsFalse(monitoring.Attempts.ContainsKey("frieren:S01E02"), "A restriction leaving no indexer is not the exponential search-failure backoff.");

        var history = await environment.HistoryForAnimeAsync(environment.AnimeId);
        var skipped = history.Single(entry => entry.EventKind == AcquisitionHistoryEventKind.Skipped);
        StringAssert.Contains(skipped.Reason, "Trusted only");
        StringAssert.Contains(skipped.Reason, "no enabled indexer");
        Assert.IsNull(skipped.ReleaseTitle);
    }

    [TestMethod]
    public async Task GrabAndImportAreRecordedInTheAcquisitionHistory()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        environment.Prowlarr.Releases.Add(AnimeAcquisitionEnvironment.Release(Best, "g1080"));
        await environment.Scheduler.RunNowAsync(null, AnimeSearchTrigger.PeriodicMissing, CancellationToken.None);
        var download = environment.AddCompletedDownload(Best, $"{Best}.mkv");
        await environment.ImportCompletedAsync(await environment.CompleteLatestDownloadAsync(download), download);

        var history = await environment.HistoryForAnimeAsync(environment.AnimeId);

        Assert.IsTrue(history.Any(entry => entry.EventKind == AcquisitionHistoryEventKind.Grabbed && entry.ReleaseTitle == Best));
        Assert.IsTrue(history.Any(entry => entry.EventKind == AcquisitionHistoryEventKind.Imported));
        Assert.IsTrue(history.All(entry => entry.SeasonNumber == 1 && entry.EpisodeNumber == 2));
    }

    private sealed class AlwaysCrossDeviceHardLinkCreator : IHardLinkCreator
    {
        public void CreateHardLink(string sourcePath, string destinationPath) => throw new CrossDeviceLinkException(sourcePath);
    }
}
