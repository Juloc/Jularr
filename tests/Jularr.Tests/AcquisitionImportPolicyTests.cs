using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.History;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Monitoring;
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
    public async Task GrabAndImportAreRecordedInTheAcquisitionHistory()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        environment.Prowlarr.Releases.Add(AnimeAcquisitionEnvironment.Release(Best, "g1080"));
        await environment.SearchNowAsync();
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
