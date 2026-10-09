using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Operations;

namespace Jularr.Tests;

[TestClass]
public sealed class SabnzbdDownloadServiceTests
{
    private static async Task<Guid> SubmitAsync(SabnzbdTestEnvironment environment, string release)
    {
        var outcome = await environment.NewSubmissionService().SubmitAsync(
            new DownloadSubmissionSpec(AnimeAcquisitionEngine.OperationKind, "Anime download", $"Show · S01E01 · {release}", null, new Uri($"https://indexer.example/{release}.nzb"), release, MediaAcquisitionKind.Anime),
            CancellationToken.None);
        Assert.IsTrue(outcome.Accepted, outcome.Message);
        return outcome.OperationId;
    }

    private static async Task FailInHistoryAsync(SabnzbdTestEnvironment environment, OperationStore operations, string failureMessage)
    {
        var active = await operations.ListActiveExternalAsync(SabnzbdClient.ProviderId);
        var job = active.Single();
        await SabnzbdOperationProjector.ApplyAsync(
            operations,
            active,
            new SabnzbdQueueSnapshot(false, null, null, []),
            new SabnzbdHistorySnapshot([new SabnzbdHistoryJob(job.ExternalId!, job.Subject ?? "job", "Failed", "anime", null, failureMessage, SabnzbdClient.ClassifyFailure("Failed", failureMessage), DateTimeOffset.UtcNow)]),
            DateTime.UtcNow,
            CancellationToken.None);
    }

    [TestMethod]
    public async Task CancelStopsTheDownloadOnTheClientThatAcceptedItWithoutBlocklistingTheRelease()
    {
        await using var environment = await SabnzbdTestSupport.CreateEnvironmentAsync();
        var store = environment.NewAcquisitionStore();
        var operationId = await SubmitAsync(environment, "a");
        var operations = new OperationStore(environment.Db);
        var nzoId = (await operations.GetAsync(operationId))!.ExternalId;

        // A client with a higher priority is added afterwards; the cancel still goes to the one that accepted the download.
        await environment.NewDownloadClientStore().SaveAsync(
            new DownloadClientEntry(
                Guid.NewGuid(),
                "Higher priority SABnzbd",
                DownloadClientType.Sabnzbd,
                Enabled: true,
                Priority: 0,
                new DownloadClientSettings("http://higher-priority:8080", new Dictionary<MediaAcquisitionKind, string?> { [MediaAcquisitionKind.Book] = "books", [MediaAcquisitionKind.Anime] = "anime" }),
                "secret-key"));
        var cancelled = await environment.NewDownloadService(store).CancelAsync(operationId, CancellationToken.None);

        Assert.IsTrue(cancelled.Success);
        Assert.AreEqual(nzoId, environment.Client.Cancelled.Single());
        Assert.AreEqual("http://sabnzbd:8080", environment.Client.CancelConnections.Single().Settings.BaseUrl);
        Assert.AreEqual(OperationStatus.Cancelled, (await operations.GetAsync(operationId))!.Status);
        Assert.AreEqual(0, (await store.LoadAsync()).Blocklist.Count);
    }

    [TestMethod]
    public async Task RetryRequeuesTheFailedDownloadOfARequestAndReleasesItsBlocklistEntry()
    {
        await using var environment = await SabnzbdTestSupport.CreateEnvironmentAsync();
        var store = environment.NewAcquisitionStore();
        var operations = new OperationStore(environment.Db);
        var operationId = await SubmitAsync(environment, "a");
        await FailInHistoryAsync(environment, operations, "Unpacking failed");
        var previousNzo = (await operations.GetAsync(operationId))!.ExternalId!;
        await store.BlockAsync(new SabnzbdBlockedRelease("release:a", "Show - 01 [a]", "show-key", SabnzbdFailureKind.Download, "Unpacking failed", operationId, DateTimeOffset.UtcNow));
        var requests = new AcquisitionAccessStore(environment.Db);
        var request = await requests.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Anime, "anilist", "1", "Show", null, null), "owner", AcquisitionRequestStatus.Failed, "owner", CancellationToken.None);
        await requests.UpdateStatusAsync(request.Id, AcquisitionRequestStatus.Failed, "The download failed.", operationId, null, null, CancellationToken.None);

        var retried = await environment.NewDownloadService(store).RetryAsync(operationId, CancellationToken.None);

        Assert.IsTrue(retried.Success, retried.Message);
        Assert.AreEqual(previousNzo, environment.Client.Retried.Single());
        Assert.AreEqual("http://sabnzbd:8080", environment.Client.RetryConnections.Single().Settings.BaseUrl);
        var operation = await operations.GetAsync(operationId);
        Assert.AreEqual(OperationStatus.Running, operation!.Status);
        Assert.AreEqual(2, operation.Attempt);
        Assert.AreEqual($"{previousNzo}_retry", operation.ExternalId);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, (await requests.GetAsync(request.Id, CancellationToken.None))!.Status, "The request follows the retried download again.");
        Assert.IsFalse((await store.LoadAsync()).IsBlocked("release:a"), "The owner asked for this release again.");
    }

    [TestMethod]
    public async Task ADownloadTheRequestMovedOnFromIsNotRetried()
    {
        await using var environment = await SabnzbdTestSupport.CreateEnvironmentAsync();
        var operations = new OperationStore(environment.Db);
        var operationId = await SubmitAsync(environment, "a");
        await FailInHistoryAsync(environment, operations, "Unpacking failed");

        var refused = await environment.NewDownloadService(environment.NewAcquisitionStore()).RetryAsync(operationId, CancellationToken.None);

        Assert.IsFalse(refused.Success);
        StringAssert.Contains(refused.Message, "newer release");
        Assert.AreEqual(0, environment.Client.Retried.Count);
    }
}
