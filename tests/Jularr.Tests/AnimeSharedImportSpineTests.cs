using Jularr.Web.Features.Library;
using Jularr.Web.Features.Acquisition;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Tests;

/// <summary>
/// Anime runs on the same completed-download spine as every other media type (#389): the shared
/// import step resolves the download's location with the Anime remote path mappings, hands the
/// files to the Anime adapter behind the dispatcher and records the outcome on the download
/// Operation. The Anime adapter keeps the episode mapping, ownership and naming.
/// </summary>
[TestClass]
public sealed class AnimeSharedImportSpineTests
{
    private const string Best = "Frieren.S01E02.1080p.WEB-DL.AAC.H.264-GRP";

    private static readonly AnimeEpisodeKey[] EpisodeTwo = [new(AnimeAcquisitionEnvironment.AnimeKey, 1, 2, 2)];

    [TestMethod]
    public async Task AnimeIsRegisteredBehindTheSharedDispatcher()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await using var scope = environment.CreateScope();

        var dispatcher = scope.ServiceProvider.GetRequiredService<CompletedDownloadDispatcher>();

        Assert.IsTrue(dispatcher.Supports(MediaAcquisitionKind.Anime));
        var adapters = scope.ServiceProvider.GetServices<ICompletedDownloadImportAdapter>().ToArray();
        Assert.AreEqual(1, adapters.Count(adapter => adapter.Kind == MediaAcquisitionKind.Anime));
    }

    [TestMethod]
    public async Task CompletedAnimeDownloadIsImportedByTheSharedStepAndRecordedOnTheDownload()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        await environment.StartAcquisitionAsync(EpisodeTwo, Best);
        var download = environment.AddCompletedDownload(Best, $"{Best}.mkv");
        var completed = await environment.CompleteLatestDownloadAsync(download);

        var (result, details) = await environment.ImportThroughSharedSpineAsync(completed);

        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, result.Disposition, result.Message);
        var record = await environment.Imports.FindByDownloadAsync(completed.Id);
        Assert.AreEqual(AnimeImportStatus.Imported, record!.Status, record.Message);
        Assert.IsTrue(File.Exists(Path.Combine(environment.SeriesFolder, "Season 01", "Frieren - S01E02 - Episode 2.mkv")));

        Assert.IsNotNull(details);
        Assert.AreEqual(DownloadImportState.Completed, details.State);
        Assert.AreEqual(download, details.ReportedPath);
        Assert.AreEqual(download, details.LocalPath);
        Assert.AreEqual(Path.Combine(environment.SeriesFolder, "Season 01"), details.Destination);
        Assert.AreEqual(ImportMode.Move, details.Mode);
        Assert.AreEqual(record.Message, details.Result);
        Assert.AreEqual(result.Placement!.Destination, details.Destination);
    }

    [TestMethod]
    public async Task ImportModeOfTheLibraryRootIsReportedAsThePlacement()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        await environment.UsePlacementAsync(LibraryPlacementPolicy.Copy);
        await environment.StartAcquisitionAsync(EpisodeTwo, Best);
        var download = environment.AddCompletedDownload(Best, $"{Best}.mkv");
        var completed = await environment.CompleteLatestDownloadAsync(download);

        var (result, details) = await environment.ImportThroughSharedSpineAsync(completed);

        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, result.Disposition, result.Message);
        Assert.AreEqual(ImportMode.Copy, result.Placement!.Mode);
        Assert.AreEqual(ImportMode.Copy, details!.Mode);
        Assert.IsTrue(File.Exists(Path.Combine(download, $"{Best}.mkv")), "Copy keeps the download.");
    }

    [TestMethod]
    public async Task AnimeUsesItsOwnRemotePathMappingAndTheDownloadShowsBothPaths()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        await environment.StartAcquisitionAsync(EpisodeTwo, Best);
        var localFolder = environment.AddCompletedDownload(Best, $"{Best}.mkv");
        var reported = "/downloads/complete/" + Path.GetFileName(localFolder);
        await environment.ImportSettings.UpdateAsync(state => state.WithRemotePathMappings(
            MediaAcquisitionKind.Anime,
            [new RemotePathMapping("/downloads/complete", Path.GetDirectoryName(localFolder)!)]));
        var completed = await environment.CompleteLatestDownloadAsync(reported);

        var (result, details) = await environment.ImportThroughSharedSpineAsync(completed);

        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, result.Disposition, result.Message);
        Assert.AreEqual(reported, details!.ReportedPath);
        Assert.AreEqual(
            Path.GetFullPath(localFolder),
            Path.GetFullPath(details.LocalPath!),
            "The Anime mapping translated the reported path.");
    }

    [TestMethod]
    public async Task AnimeIgnoresRemotePathMappingsOfOtherMediaTypes()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        await environment.StartAcquisitionAsync(EpisodeTwo, Best);
        var localFolder = environment.AddCompletedDownload(Best, $"{Best}.mkv");
        var reported = "/downloads/complete/" + Path.GetFileName(localFolder);
        await environment.ImportSettings.UpdateAsync(state => state
            .WithRemotePathMappings(MediaAcquisitionKind.Manga, [new RemotePathMapping("/downloads/complete", Path.GetDirectoryName(localFolder)!)])
            .WithRemotePathMappings(MediaAcquisitionKind.Book, [new RemotePathMapping("/downloads", Path.GetDirectoryName(Path.GetDirectoryName(localFolder))!)]));
        var completed = await environment.CompleteLatestDownloadAsync(reported);

        var (result, details) = await environment.ImportThroughSharedSpineAsync(completed);

        Assert.AreEqual(CompletedDownloadImportDisposition.Failed, result.Disposition);
        StringAssert.Contains(result.Message, "does not exist or is not mounted");
        Assert.AreEqual(DownloadImportState.Failed, details!.State);
        Assert.AreEqual(reported, details.LocalPath, "No Anime mapping applied, so the reported path was read as it is.");
        Assert.IsTrue(File.Exists(Path.Combine(localFolder, $"{Best}.mkv")));
    }

    [TestMethod]
    public async Task FilesThatNeedADecisionAreReportedAsNeedsReviewOnTheDownload()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        await environment.StartAcquisitionAsync(EpisodeTwo, Best);
        // The package holds another episode than the one requested.
        var download = environment.AddCompletedDownload(Best, "Frieren.S01E05.1080p.WEB-DL.AAC.H.264-GRP.mkv");
        var completed = await environment.CompleteLatestDownloadAsync(download);

        var (result, details) = await environment.ImportThroughSharedSpineAsync(completed);

        Assert.AreEqual(CompletedDownloadImportDisposition.NeedsReview, result.Disposition, result.Message);
        Assert.AreEqual(DownloadImportState.ManualReview, details!.State);
        var record = await environment.Imports.FindByDownloadAsync(completed.Id);
        Assert.AreEqual(AnimeImportStatus.ManualRequired, record!.Status);
        Assert.IsTrue(record.NeedsAttention);
        Assert.IsNull(result.Placement, "Nothing was placed in the library.");
    }

    [TestMethod]
    public async Task OwnerDecisionsOnAnAnimeImportAreRecordedOnTheDownloadToo()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        await environment.StartAcquisitionAsync(EpisodeTwo, Best);
        var download = environment.AddCompletedDownload(Best, "Frieren.S01E05.1080p.WEB-DL.AAC.H.264-GRP.mkv");
        var completed = await environment.CompleteLatestDownloadAsync(download);
        await environment.ImportThroughSharedSpineAsync(completed);
        var record = (await environment.Imports.FindByDownloadAsync(completed.Id))!;
        var source = record.Files.Single(file => file.SourcePath.EndsWith(".mkv", StringComparison.Ordinal)).SourcePath;

        var manual = await environment.ImportManuallyAsync(record.Id, source, 1, 2);

        Assert.IsTrue(manual.Success, manual.Message);
        var details = await environment.DownloadImportAsync(completed.Id);
        Assert.AreEqual(DownloadImportState.Completed, details!.State);
        Assert.AreEqual(Path.Combine(environment.SeriesFolder, "Season 01"), details.Destination);
        Assert.AreEqual(ImportMode.Move, details.Mode);
        StringAssert.Contains(details.Result, "manually");
    }

    [TestMethod]
    public async Task DeferredImportWaitsAndResumesFromItsRecordedPathAfterTheClientForgotTheJob()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        await environment.StartAcquisitionAsync(EpisodeTwo, Best);
        var scan = await environment.Operations.CreateAsync(new OperationDescriptor("library-scan", "Library", "Library scan"));
        await environment.Operations.MarkRunningAsync(scan);
        var download = environment.AddCompletedDownload(Best, $"{Best}.mkv");
        var completed = await environment.CompleteLatestDownloadAsync(download);

        var (deferred, details) = await environment.ImportThroughSharedSpineAsync(completed);

        Assert.AreEqual(CompletedDownloadImportDisposition.RetryLater, deferred.Disposition);
        StringAssert.Contains(deferred.Message, "Waiting for 'Library scan'");
        Assert.AreEqual(DownloadImportState.Waiting, details!.State);
        Assert.AreEqual(AnimeImportStatus.Importing, (await environment.Imports.FindByDownloadAsync(completed.Id))!.Status);

        // SABnzbd purged the job from its history; the recorded mapped path is still readable.
        environment.Sabnzbd.History = new SabnzbdHistorySnapshot([]);
        await environment.Operations.MarkSucceededAsync(scan);
        var recovered = await environment.RecoverImportsAsync(DateTime.UtcNow);

        Assert.AreEqual(1, recovered);
        Assert.AreEqual(AnimeImportStatus.Imported, (await environment.Imports.FindByDownloadAsync(completed.Id))!.Status);
        Assert.AreEqual(DownloadImportState.Completed, (await environment.DownloadImportAsync(completed.Id))!.State);
        Assert.IsNotNull(await environment.MediaFileAsync(1, 2));
        Assert.AreEqual(0, await environment.RecoverImportsAsync(DateTime.UtcNow), "A finished import is not resumed again.");
    }

    [TestMethod]
    public async Task DownloadTheClientDoesNotReportWaitsThenFailsVisiblyAfterTheImportTimeout()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        await environment.StartAcquisitionAsync(EpisodeTwo, Best);
        var download = environment.AddCompletedDownload(Best, $"{Best}.mkv");
        var completed = await environment.CompleteLatestDownloadAsync(download);
        environment.Sabnzbd.History = new SabnzbdHistorySnapshot([]);

        var now = DateTime.UtcNow;
        Assert.AreEqual(1, await environment.RecoverImportsAsync(now));

        Assert.IsNull(await environment.Imports.FindByDownloadAsync(completed.Id), "Nothing failed yet: the files may still show up.");
        var waiting = await environment.DownloadImportAsync(completed.Id);
        Assert.AreEqual(DownloadImportState.Waiting, waiting!.State);
        StringAssert.Contains(waiting.Result, "has not exposed the completed storage path");

        var late = now + CompletedDownloadImportService.CompletedImportTimeout + TimeSpan.FromMinutes(1);
        Assert.AreEqual(1, await environment.RecoverImportsAsync(late));

        var record = await environment.Imports.FindByDownloadAsync(completed.Id);
        Assert.AreEqual(AnimeImportStatus.Failed, record!.Status);
        Assert.IsTrue(record.NeedsAttention, "The owner sees it on the acquisition overview.");
        StringAssert.Contains(record.Message, "Gave up importing 24 hours");
        var failed = await environment.DownloadImportAsync(completed.Id);
        Assert.AreEqual(DownloadImportState.Failed, failed!.State);
        Assert.AreEqual(0, await environment.RecoverImportsAsync(late), "A failed import is not retried.");
    }
}
