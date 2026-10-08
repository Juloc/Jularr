using Jularr.Web.Features.Acquisition.Api;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Operations;

namespace Jularr.Tests;

/// <summary>
/// <see cref="AcquisitionApiService"/> is a thin façade over the same services the owner Razor
/// Pages call (<see cref="AnimeAcquisitionTestSupport"/> wires it identically), so these tests
/// reuse the shared <see cref="AnimeAcquisitionEnvironment"/> fixture rather than fakes of their
/// own — a happy-path assertion here is only meaningful if it also proves the write landed in the
/// one canonical store the UI reads from.
/// </summary>
[TestClass]
public sealed class AcquisitionApiServiceTests
{
    private const string Best = "Frieren.S01E02.1080p.WEB-DL.AAC.H.264-GRP";

    [TestMethod]
    public async Task GetMonitoredAndWantedReflectPipelineState()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        await environment.SearchNowAsync();

        var monitored = await environment.WithAcquisitionApiAsync(api => api.GetMonitoredAsync(CancellationToken.None));
        var row = monitored.Single();
        Assert.AreEqual(environment.AnimeId, row.AnimeId);
        Assert.AreEqual(AnimeManagementMode.JularrManaged.ToString(), row.Mode);
        Assert.AreEqual(1, row.WantedEpisodes, "Frieren S01E02 is missing and monitored.");

        var wanted = await environment.WithAcquisitionApiAsync(api => api.GetWantedAsync(null, CancellationToken.None));
        var episode = wanted.Single();
        Assert.AreEqual(environment.AnimeId, episode.AnimeId);
        Assert.AreEqual(2, episode.EpisodeNumber);
        Assert.AreEqual(AnimeWantedReason.Missing.ToString(), episode.Reason);

        var scopedToAnime = await environment.WithAcquisitionApiAsync(api => api.GetWantedAsync(environment.AnimeId, CancellationToken.None));
        Assert.AreEqual(1, scopedToAnime.Count);
        var scopedToOther = await environment.WithAcquisitionApiAsync(api => api.GetWantedAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.AreEqual(0, scopedToOther.Count);
    }

    [TestMethod]
    public async Task GetAnimeMonitoringReturnsNotFoundForAnUnknownAnime()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();

        var exception = await Assert.ThrowsExactlyAsync<AcquisitionApiException>(() =>
            environment.WithAcquisitionApiAsync(api => api.GetAnimeMonitoringAsync(Guid.NewGuid(), CancellationToken.None)));

        Assert.AreEqual(404, exception.StatusCode);
    }

    [TestMethod]
    public async Task SetAnimeMonitoringWritesThroughTheSameStoreTheOwnerPageUses()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();

        var response = await environment.WithAcquisitionApiAsync(api => api.SetAnimeMonitoringAsync(
            environment.AnimeId,
            new SetAnimeMonitoringRequest(
                Monitored: true,
                SearchOnAdd: false,
                QualityProfileId: null,
                IndexerIds: [],
                TargetRootId: environment.Root.Id),
            CancellationToken.None));

        Assert.AreEqual(AnimeQualityProfiles.DefaultAnime1080pId, response.QualityProfileId);
        Assert.AreEqual(environment.Root.Id, response.TargetRootId);

        var persisted = await environment.MonitoringStateAsync();
        var settings = persisted.Anime[AnimeAcquisitionEnvironment.AnimeKey];
        Assert.AreEqual(environment.Root.Id, settings.TargetRootId);
    }

    [TestMethod]
    public async Task SetAnimeMonitoringRefusesASonarrOwnedAnimeWithProblemDetails()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        // Default management mode with no explicit assignment is read-only Sonarr coexistence.
        await environment.SeedFrierenAsync(mode: null);

        var exception = await Assert.ThrowsExactlyAsync<AcquisitionApiException>(() =>
            environment.WithAcquisitionApiAsync(api => api.SetAnimeMonitoringAsync(
                environment.AnimeId,
                new SetAnimeMonitoringRequest(true, false, null, [], null),
                CancellationToken.None)));

        Assert.AreEqual(409, exception.StatusCode);
        StringAssert.Contains(exception.Detail, "Sonarr");
    }

    [TestMethod]
    public async Task SearchAnimeRefusesASonarrOwnedAnimeWithProblemDetails()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(mode: null);

        var exception = await Assert.ThrowsExactlyAsync<AcquisitionApiException>(() =>
            environment.WithAcquisitionApiAsync(api => api.SearchAnimeAsync(environment.AnimeId, CancellationToken.None)));

        Assert.AreEqual(409, exception.StatusCode);
    }

    [TestMethod]
    public async Task SearchAnimeQueuesAndReturnsAnInspectableOperation()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();

        var queued = await environment.WithAcquisitionApiAsync(api => api.SearchAnimeAsync(environment.AnimeId, CancellationToken.None));

        Assert.AreNotEqual(Guid.Empty, queued.OperationId);
        var detail = await environment.WithAcquisitionApiAsync(api => api.GetOperationAsync(queued.OperationId, CancellationToken.None));
        Assert.AreEqual(OperationStatus.Succeeded.ToString(), detail.Operation.Status);
        Assert.AreEqual(AnimeAcquisitionEnvironment.AnimeKey, detail.Operation.Subject);

        var listed = await environment.WithAcquisitionApiAsync(api => api.ListOperationsAsync(null, null, 50, CancellationToken.None));
        Assert.IsTrue(listed.Any(operation => operation.Id == queued.OperationId));
    }

    [TestMethod]
    public async Task SearchAllMonitoredRespectsTheSchedulerQueueGuard()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();

        // Fill the scheduler's own request queue directly (the same guard "Search now" always
        // goes through); nothing drains it because the scheduler's background loop never started
        // in this test environment.
        for (var i = 0; i < 50; i++)
        {
            Assert.IsTrue(environment.Scheduler.RequestRun(), $"Request {i} should still fit in the queue.");
        }

        var exception = await Assert.ThrowsExactlyAsync<AcquisitionApiException>(() =>
            environment.WithAcquisitionApiAsync(api => api.SearchAllMonitoredAsync(CancellationToken.None)));

        Assert.AreEqual(409, exception.StatusCode);
    }

    [TestMethod]
    public async Task ManualImportCanBeListedAndResolvedThroughTheSameExecutor()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync(hardLinkCreator: null);
        await environment.SeedFrierenAsync(seasonFolders: false);
        environment.Prowlarr.Releases.Add(AnimeAcquisitionEnvironment.Release(Best, "g1080"));
        await environment.SearchNowAsync();

        // A file where the season folder must be created makes the library folder unwritable for
        // the import, like a read-only mount — the same recipe AnimeAcquisitionPipelineTests uses.
        var blocker = Path.Combine(environment.SeriesFolder, "Season 01");
        await File.WriteAllTextAsync(blocker, "not a folder");
        var download = environment.AddCompletedDownload(Best, $"{Best}.mkv");
        var record = await environment.ImportCompletedAsync(await environment.CompleteLatestDownloadAsync(download), download);
        Assert.AreEqual(AnimeImportStatus.ManualRequired, record!.Status);

        var needingAttention = await environment.WithAcquisitionApiAsync(api => api.GetManualImportsAsync(true, CancellationToken.None));
        var item = needingAttention.Single(entry => entry.Id == record.Id);
        Assert.IsTrue(item.NeedsAttention);
        var file = item.Files.Single(entry => entry.SourcePath.EndsWith(".mkv", StringComparison.Ordinal));

        File.Delete(blocker);
        var resolved = await environment.WithAcquisitionApiAsync(api => api.ResolveManualImportAsync(
            record.Id,
            new ResolveManualImportRequest(file.SourcePath, 1, 2),
            CancellationToken.None));

        Assert.IsTrue(resolved.Success, resolved.Message);
        Assert.AreEqual(AnimeImportStatus.Imported, (await environment.Imports.GetAsync(record.Id))!.Status);

        var stillAttention = await environment.WithAcquisitionApiAsync(api => api.GetManualImportsAsync(true, CancellationToken.None));
        Assert.IsFalse(stillAttention.Any(entry => entry.Id == record.Id));
    }

    [TestMethod]
    public async Task ResolveManualImportReturnsNotFoundForAnUnknownRecord()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();

        var exception = await Assert.ThrowsExactlyAsync<AcquisitionApiException>(() =>
            environment.WithAcquisitionApiAsync(api => api.ResolveManualImportAsync(
                Guid.NewGuid(),
                new ResolveManualImportRequest("does-not-matter.mkv", 1, 1),
                CancellationToken.None)));

        Assert.AreEqual(404, exception.StatusCode);
    }

    [TestMethod]
    public async Task DismissManualImportLeavesDownloadedFilesUntouched()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        environment.Prowlarr.Releases.Add(AnimeAcquisitionEnvironment.Release(Best, "g1080"));
        await environment.SearchNowAsync();

        var destination = Path.Combine(environment.SeriesFolder, "Season 01", "Frieren - S01E02 - Episode 2.mkv");
        await File.WriteAllTextAsync(destination, "someone else's file");
        var download = environment.AddCompletedDownload(Best, $"{Best}.mkv");
        var record = await environment.ImportCompletedAsync(await environment.CompleteLatestDownloadAsync(download), download);
        Assert.AreEqual(AnimeImportStatus.ManualRequired, record!.Status);

        var dismissed = await environment.WithAcquisitionApiAsync(api => api.DismissManualImportAsync(record.Id, CancellationToken.None));

        Assert.IsTrue(dismissed.Success, dismissed.Message);
        Assert.AreEqual(AnimeImportStatus.Dismissed, (await environment.Imports.GetAsync(record.Id))!.Status);
        Assert.AreEqual("someone else's file", await File.ReadAllTextAsync(destination));
    }

    [TestMethod]
    public async Task HealthSummaryListsConfiguredIndexersAndDownloadClientsAsUncheckedByDefault()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();

        var summary = await environment.WithAcquisitionApiAsync(api => api.GetHealthSummaryAsync(CancellationToken.None));

        var indexer = summary.Indexers.Single();
        Assert.AreEqual(environment.ProwlarrIndexerEntryId, indexer.Id);
        Assert.IsTrue(indexer.Enabled);
        Assert.IsNull(indexer.Healthy, "Never checked yet.");

        Assert.AreEqual(1, summary.DownloadClients.Count);
        Assert.IsTrue(summary.DownloadClients[0].Enabled);
    }
}
