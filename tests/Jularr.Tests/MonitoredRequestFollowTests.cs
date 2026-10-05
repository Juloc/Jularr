using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Events;
using Jularr.Web.Features.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>
/// How the shared Wanted pass follows the requests of a media type that its own pipeline downloads for: every request in bounded batches,
/// the pipeline state loaded once per pass, one broken request never stopping the others or the manual imports, and a request that
/// somebody else moved on meanwhile never overwritten. Uses a fake pipeline observation so only the pass itself is under test.
/// </summary>
[TestClass]
public sealed class MonitoredRequestFollowTests
{
    private sealed class FakeMonitoredExecutor(Func<AcquisitionRequest, AcquisitionExecution> observe) : IMonitoredAcquisitionExecutor
    {
        public MediaAcquisitionKind Kind => MediaAcquisitionKind.Anime;
        public int Begins { get; private set; }
        public bool FailBegin { get; set; }
        public List<string> Observed { get; } = [];

        public Task<AcquisitionExecution> ExecuteAsync(AcquisitionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IRequestObservation> BeginObservationAsync(DateTime nowUtc, CancellationToken cancellationToken)
        {
            Begins++;
            return FailBegin
                ? throw new InvalidDataException("The monitoring state is unreadable.")
                : Task.FromResult<IRequestObservation>(new Observation(this, observe));
        }

        private sealed class Observation(FakeMonitoredExecutor owner, Func<AcquisitionRequest, AcquisitionExecution> observe) : IRequestObservation
        {
            public Task<AcquisitionExecution> ObserveAsync(AcquisitionRequest request, CancellationToken cancellationToken)
            {
                owner.Observed.Add(request.Title);
                return Task.FromResult(observe(request));
            }
        }
    }

    private sealed class CapturingLogger : ILogger<WantedAcquisitionService>
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception, formatter(state, exception)));
    }

    private sealed class RecordingImportAdapter : ICompletedDownloadImportAdapter
    {
        public MediaAcquisitionKind Kind => MediaAcquisitionKind.Manga;
        public int Imports { get; private set; }

        public Task<CompletedDownloadImportResult> ImportAsync(CompletedDownloadImportRequest request, CancellationToken cancellationToken)
        {
            Imports++;
            return Task.FromResult(CompletedDownloadImportResult.Completed("Imported.", null));
        }
    }

    private sealed class FixedLocationResolver : ICompletedDownloadLocationResolver
    {
        public Task<CompletedDownloadLocation> ResolveAsync(OperationSnapshot operation, MediaAcquisitionKind kind, CancellationToken cancellationToken) =>
            Task.FromResult(new CompletedDownloadLocation(true, "/mapped/manga", "Resolved."));
    }

    private sealed class World(AcquisitionAccessFixture fixture, FakeMonitoredExecutor executor) : IAsyncDisposable
    {
        public FakeMonitoredExecutor Executor => executor;
        public RecordingImportAdapter ManualAdapter { get; } = new();
        public CapturingLogger Log { get; } = new();
        public AcquisitionAccessStore Store => fixture.Store;
        public AcquisitionRequestService Requests { get; } = fixture.Service("owner", isOwner: true, executor);

        public IServiceProvider Services =>
            new ServiceCollection()
                .AddSingleton(Store)
                .AddSingleton(Requests)
                .AddSingleton<IMonitoredAcquisitionExecutor>(executor)
                .AddSingleton<ILogger<WantedAcquisitionService>>(Log)
                .AddSingleton(new CompletedDownloadImportService(new FixedLocationResolver(), new CompletedDownloadDispatcher([ManualAdapter]), fixture.Db, Store, NullLogger<CompletedDownloadImportService>.Instance))
                .BuildServiceProvider();

        public Task<int> PassAsync() => WantedAcquisitionService.ProcessOnceAsync(Services, DateTime.UtcNow, CancellationToken.None);

        public async Task<AcquisitionRequest> RequestAsync(string title, AcquisitionRequestStatus status = AcquisitionRequestStatus.Approved) =>
            await Store.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Anime, "anilist", title, title, null, null), "owner", status, "owner", CancellationToken.None);

        /// <summary>A finished download without a request that the shared importer handles in the same pass.</summary>
        public async Task AddManualDownloadAsync()
        {
            var operations = new OperationStore(fixture.Db);
            var id = await operations.CreateAsync(
                new OperationDescriptor(
                    CompletedDownloadImportService.ManualDownloadOperationKind,
                    "External downloads",
                    "SABnzbd download",
                    IsDownload: true,
                    ExternalProvider: "sabnzbd",
                    ExternalId: "job-manual",
                    Details: new DownloadOperationDetails(Guid.NewGuid(), MediaAcquisitionKind.Manga, "manga").Serialize()),
                CancellationToken.None);
            await operations.MarkRunningAsync(id, CancellationToken.None);
            await operations.MarkSucceededAsync(id, "Downloaded.", CancellationToken.None);
        }

        public ValueTask DisposeAsync() => fixture.DisposeAsync();
    }

    private static async Task<World> CreateAsync(Func<AcquisitionRequest, AcquisitionExecution> observe) =>
        new(await AcquisitionAccessFixture.CreateAsync(), new FakeMonitoredExecutor(observe));

    private static AcquisitionExecution Downloading(AcquisitionRequest request) =>
        new(AcquisitionRequestStatus.Downloading, "Download is in progress.", request.Id);

    [TestMethod]
    public async Task EveryOpenRequestIsFollowedInBatchesAndThePipelineStateIsLoadedOncePerPass()
    {
        await using var world = await CreateAsync(Downloading);
        var total = WantedAcquisitionService.FollowBatchSize * 4 + 5;
        for (var index = 0; index < total; index++)
        {
            await world.RequestAsync($"title-{index}");
        }

        var advanced = await world.PassAsync();

        Assert.AreEqual(total, advanced, "Every request is followed, not only the oldest ones.");
        Assert.AreEqual(total, (await world.Store.ListByStatusAsync(MediaAcquisitionKind.Anime, AcquisitionRequestStatus.Downloading, CancellationToken.None)).Count);
        Assert.AreEqual(1, world.Executor.Begins);
        Assert.AreEqual(0, await world.PassAsync(), "A request that is where its pipeline is stays untouched.");
    }

    [TestMethod]
    public async Task ARequestWhoseObservationFailsIsLoggedWithItsCauseAndTheOthersAndTheManualImportsStillRun()
    {
        await using var world = await CreateAsync(request => request.Title == "broken" ? throw new InvalidOperationException("the inventory is inconsistent") : Downloading(request));
        var first = await world.RequestAsync("first");
        var broken = await world.RequestAsync("broken");
        var last = await world.RequestAsync("last");
        await world.AddManualDownloadAsync();

        var advanced = await world.PassAsync();

        Assert.AreEqual(3, advanced, "Two requests followed and one manual download imported.");
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, (await world.Store.GetAsync(first.Id, CancellationToken.None))!.Status);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, (await world.Store.GetAsync(broken.Id, CancellationToken.None))!.Status);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, (await world.Store.GetAsync(last.Id, CancellationToken.None))!.Status);
        Assert.AreEqual(1, world.ManualAdapter.Imports, "The manual downloads of the same pass are still imported.");
        var entry = Assert.ContainsSingle(world.Log.Entries);
        Assert.AreEqual(LogLevel.Warning, entry.Level);
        StringAssert.Contains(entry.Message, broken.Id.ToString());
        Assert.AreEqual("the inventory is inconsistent", entry.Exception!.Message, "The cause is preserved.");
    }

    [TestMethod]
    public async Task AnUnreadablePipelineStateIsLoggedAndDoesNotStopTheManualImports()
    {
        await using var world = await CreateAsync(Downloading);
        world.Executor.FailBegin = true;
        await world.RequestAsync("waiting");
        await world.AddManualDownloadAsync();

        Assert.AreEqual(1, await world.PassAsync(), "Only the manual download was handled.");

        Assert.AreEqual(1, world.ManualAdapter.Imports);
        Assert.IsInstanceOfType<InvalidDataException>(Assert.ContainsSingle(world.Log.Entries).Exception);
    }

    [TestMethod]
    public async Task ARequestTheOwnerMarkedDoneWhileItWasObservedIsNotOverwritten()
    {
        await using var world = await CreateAsync(Downloading);
        var request = await world.RequestAsync("title");
        var observation = new MarksDoneWhileObserving(world.Store, request.Id);

        var changed = await world.Requests.FollowMonitoredAsync(request, observation, CancellationToken.None);

        Assert.AreEqual(MonitoredFollowOutcome.Unchanged, changed, "The conditional change lost to the owner's decision.");
        Assert.AreEqual(AcquisitionRequestStatus.Completed, (await world.Store.GetAsync(request.Id, CancellationToken.None))!.Status);
    }

    private sealed class MarksDoneWhileObserving(AcquisitionAccessStore store, Guid id) : IRequestObservation
    {
        public async Task<AcquisitionExecution> ObserveAsync(AcquisitionRequest request, CancellationToken cancellationToken)
        {
            await store.UpdateStatusAsync(id, AcquisitionRequestStatus.Completed, null, null, null, "owner", cancellationToken);
            return new AcquisitionExecution(AcquisitionRequestStatus.Downloading, "Download is in progress.", id);
        }
    }

    [TestMethod]
    public async Task OnlyAFailedRequestThatKeepsItsDownloadIsReadBackFromThePipeline()
    {
        await using var world = await CreateAsync(request => new AcquisitionExecution(AcquisitionRequestStatus.Failed, "Needs a decision.", request.Id));
        await world.RequestAsync("failed-setup", AcquisitionRequestStatus.Failed);
        var kept = await world.RequestAsync("failed-import", AcquisitionRequestStatus.Failed);
        await world.Store.UpdateStatusAsync(kept.Id, AcquisitionRequestStatus.Failed, "The import needs a decision.", Guid.NewGuid(), null, null, CancellationToken.None);

        await world.PassAsync();

        CollectionAssert.AreEqual(new[] { "failed-import" }, world.Executor.Observed);
    }

    [TestMethod]
    public async Task GoingBackToWaitingUnlinksTheDownloadTheRequestHad()
    {
        await using var world = await CreateAsync(_ => new AcquisitionExecution(AcquisitionRequestStatus.Approved, "Looking for the requested episodes."));
        var request = await world.RequestAsync("title", AcquisitionRequestStatus.Downloading);
        await world.Store.UpdateStatusAsync(request.Id, AcquisitionRequestStatus.Downloading, "Download is in progress.", Guid.NewGuid(), null, null, CancellationToken.None);

        await world.PassAsync();

        var after = (await world.Store.GetAsync(request.Id, CancellationToken.None))!;
        Assert.AreEqual(AcquisitionRequestStatus.Approved, after.Status);
        Assert.IsNull(after.OperationId, "A stale download id would send a recovered request back to Downloading with nothing downloading.");
    }
}
