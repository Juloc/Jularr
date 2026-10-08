using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Operations;

namespace Jularr.Tests;

[TestClass]
public sealed class SabnzbdOperationMonitorTests
{
    private static readonly DateTime Now = new(2026, 9, 26, 9, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public async Task BooksSubmissionCreatesTrackedOperationWithBooksCategory()
    {
        await using var environment = await SabnzbdTestSupport.CreateEnvironmentAsync();
        var downloads = environment.NewDownloadService(environment.NewAcquisitionStore());

        var outcome = await downloads.SubmitUrlAsync(
            new SabnzbdSubmission(
                CompletedDownloadImportService.ManualDownloadOperationKind,
                "SABnzbd download",
                "Test Book",
                "owner",
                SabnzbdPurpose.Books,
                JobName: "Test Book"),
            new Uri("https://indexer.example/book.nzb"),
            CancellationToken.None);

        Assert.IsTrue(outcome.Accepted);
        Assert.AreEqual("books", environment.Client.Grabs.Single().Category);
        Assert.AreEqual("Test Book", environment.Client.Grabs.Single().NzbName);

        var operation = await new OperationStore(environment.Db).GetAsync(outcome.OperationId);
        Assert.IsNotNull(operation);
        Assert.AreEqual(CompletedDownloadImportService.ManualDownloadOperationKind, operation.Kind);
        Assert.AreEqual(OperationStatus.Running, operation.Status);
        Assert.AreEqual(SabnzbdClient.ProviderId, operation.ExternalProvider);
        Assert.AreEqual(outcome.NzoId, operation.ExternalId);
        Assert.IsTrue(operation.IsDownload);
        Assert.IsTrue(operation.Retryable);
    }

    [TestMethod]
    public async Task RejectedSubmissionIsRecordedAsFailedOperation()
    {
        await using var environment = await SabnzbdTestSupport.CreateEnvironmentAsync();
        environment.Client.GrabResults.Enqueue(new SabnzbdGrabResult(false, [], "Invalid NZB"));
        var downloads = environment.NewDownloadService(environment.NewAcquisitionStore());

        var outcome = await downloads.SubmitUrlAsync(
            new SabnzbdSubmission(CompletedDownloadImportService.ManualDownloadOperationKind, "SABnzbd download", "Bad", null, SabnzbdPurpose.Books),
            new Uri("https://indexer.example/bad.nzb"),
            CancellationToken.None);

        Assert.IsFalse(outcome.Accepted);
        var operation = await new OperationStore(environment.Db).GetAsync(outcome.OperationId);
        Assert.AreEqual(OperationStatus.Failed, operation!.Status);
        Assert.AreEqual("Invalid NZB", operation.Error);
    }

    [TestMethod]
    public async Task ProjectorUpdatesBooksAndAnimeOperations()
    {
        await using var environment = await SabnzbdTestSupport.CreateEnvironmentAsync();
        var store = new OperationStore(environment.Db);
        var book = await CreateTrackedAsync(store, CompletedDownloadImportService.ManualDownloadOperationKind, "nzo_book");
        var anime = await CreateTrackedAsync(store, AnimeAcquisitionEngine.LegacyOperationKind, "nzo_anime");

        var result = await SabnzbdOperationProjector.ApplyAsync(
            store,
            await store.ListActiveExternalAsync(SabnzbdClient.ProviderId),
            new SabnzbdQueueSnapshot(
                false,
                2048,
                null,
                [
                    new SabnzbdQueueJob(
                        "nzo_book",
                        "Book",
                        "Downloading",
                        "books",
                        75,
                        TimeSpan.FromMinutes(2),
                        100L * 1024 * 1024,
                        25L * 1024 * 1024)
                ]),
            new SabnzbdHistorySnapshot(
            [
                new SabnzbdHistoryJob(
                    "nzo_anime",
                    "Anime - 01",
                    "Failed",
                    "anime",
                    null,
                    "Encrypted archive requires a password",
                    SabnzbdFailureKind.Password,
                    null)
            ]),
            Now,
            CancellationToken.None);

        var bookOperation = await store.GetAsync(book);
        Assert.AreEqual(OperationStatus.Running, bookOperation!.Status);
        Assert.AreEqual(75, bookOperation.ProgressPercent);
        Assert.AreEqual(75L * 1024 * 1024, bookOperation.BytesCompleted);
        Assert.AreEqual(100L * 1024 * 1024, bookOperation.BytesTotal);
        Assert.AreEqual(2048d, bookOperation.BytesPerSecond);
        Assert.AreEqual(Now.AddMinutes(2), bookOperation.EtaUtc);

        var animeOperation = await store.GetAsync(anime);
        Assert.AreEqual(OperationStatus.Failed, animeOperation!.Status);
        StringAssert.Contains(animeOperation.Error, "password-protected");
        StringAssert.Contains(animeOperation.Error, "Encrypted archive requires a password");

        Assert.AreEqual(0, result.Completed.Count);
        var failure = result.Failed.Single();
        Assert.AreEqual(anime, failure.Operation.Id);
        Assert.AreEqual(SabnzbdFailureKind.Password, failure.FailureKind);
    }

    [TestMethod]
    public async Task ProjectorCompletesPostProcessesAndTimesOutMissingJobs()
    {
        await using var environment = await SabnzbdTestSupport.CreateEnvironmentAsync();
        var store = new OperationStore(environment.Db);
        var completed = await CreateTrackedAsync(store, CompletedDownloadImportService.ManualDownloadOperationKind, "nzo_done");
        var processing = await CreateTrackedAsync(store, AnimeAcquisitionEngine.LegacyOperationKind, "nzo_pp");
        var missing = await CreateTrackedAsync(store, AnimeAcquisitionEngine.LegacyOperationKind, "nzo_gone");

        var result = await SabnzbdOperationProjector.ApplyAsync(
            store,
            await store.ListActiveExternalAsync(SabnzbdClient.ProviderId),
            new SabnzbdQueueSnapshot(false, null, null, []),
            new SabnzbdHistorySnapshot(
            [
                new SabnzbdHistoryJob("nzo_done", "Book", "Completed", "books", "/downloads/book", null, SabnzbdFailureKind.None, null, 1024),
                new SabnzbdHistoryJob("nzo_pp", "Anime", "Extracting", "anime", null, null, SabnzbdFailureKind.None, null, 2048)
            ]),
            DateTime.UtcNow + SabnzbdOperationProjector.MissingJobTimeout + TimeSpan.FromMinutes(1),
            CancellationToken.None);

        Assert.AreEqual(completed, result.Completed.Single().Id);
        Assert.AreEqual(OperationStatus.Succeeded, (await store.GetAsync(completed))!.Status);

        var postProcessing = await store.GetAsync(processing);
        Assert.AreEqual(OperationStatus.Running, postProcessing!.Status);
        Assert.AreEqual(99, postProcessing.ProgressPercent);
        StringAssert.Contains(postProcessing.Message, "Extracting");

        Assert.AreEqual(missing, result.Failed.Single().Operation.Id);
        Assert.AreEqual(OperationStatus.Failed, (await store.GetAsync(missing))!.Status);
    }

    private static async Task<Guid> CreateTrackedAsync(
        OperationStore store,
        string kind,
        string nzoId)
    {
        var id = await store.CreateAsync(
            new OperationDescriptor(
                kind,
                SabnzbdDownloadService.OperationCategory,
                "SABnzbd download",
                IsDownload: true,
                ExternalProvider: SabnzbdClient.ProviderId,
                ExternalId: nzoId));
        await store.MarkRunningAsync(id);
        return id;
    }
}
