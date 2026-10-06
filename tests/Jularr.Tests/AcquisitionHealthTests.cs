using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Health;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

[TestClass]
public sealed class AcquisitionHealthTests
{
    [TestMethod]
    public async Task UnknownEntryIsTreatedAsHealthyUntilFirstCheck()
    {
        var directory = SabnzbdTestSupport.CreateTemporaryDirectory();
        try
        {
            var store = new AcquisitionHealthStore(directory);
            var id = Guid.NewGuid();

            Assert.IsTrue(await store.IsHealthyAsync(AcquisitionHealthKind.Indexer, id));

            await store.RecordAsync(new AcquisitionHealthStatus(AcquisitionHealthKind.Indexer, id, "Test", true, false, "auth failed", DateTimeOffset.UtcNow));
            Assert.IsFalse(await store.IsHealthyAsync(AcquisitionHealthKind.Indexer, id));

            await store.RecordAsync(new AcquisitionHealthStatus(AcquisitionHealthKind.Indexer, id, "Test", true, true, null, DateTimeOffset.UtcNow));
            Assert.IsTrue(await store.IsHealthyAsync(AcquisitionHealthKind.Indexer, id));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task CoordinatorSkipsUnhealthyIndexerAndStillSearchesHealthyOnes()
    {
        var directory = SabnzbdTestSupport.CreateTemporaryDirectory();
        try
        {
            var store = new IndexerStore(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), directory);
            var health = new AcquisitionHealthStore(directory);

            var unhealthy = NewEntry("Unhealthy indexer");
            var healthy = NewEntry("Healthy indexer");
            await store.SaveAsync(unhealthy);
            await store.SaveAsync(healthy);
            await health.RecordAsync(new AcquisitionHealthStatus(AcquisitionHealthKind.Indexer, unhealthy.Id, unhealthy.Name, true, false, "auth failed", DateTimeOffset.UtcNow));

            var searched = new List<string>();
            var indexer = new FakeIndexer((entry, _) =>
            {
                searched.Add(entry.Name);
                return [];
            });
            var coordinator = new IndexerSearchCoordinator(
                new Dictionary<IndexerType, IIndexer> { [IndexerType.Newznab] = indexer },
                store,
                health,
                NullLogger<IndexerSearchCoordinator>.Instance);

            var result = await coordinator.SearchAsync(new SearchIntent(MediaAcquisitionKind.Anime, "Anime"), new SearchOptions(), CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "Healthy indexer" }, searched.Distinct().ToArray());
            Assert.IsTrue(result.Warnings.Any(warning => warning.IndexerName == "Unhealthy indexer" && warning.Message.Contains("auth failed")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task HealthCheckServiceRecordsResultsForIndexersAndDownloadClients()
    {
        var directory = SabnzbdTestSupport.CreateTemporaryDirectory();
        try
        {
            var protection = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
            var indexerStore = new IndexerStore(protection, directory);
            var clientStore = new DownloadClientStore(protection, directory);
            var health = new AcquisitionHealthStore(directory);

            var indexerEntry = NewEntry("My Indexer");
            await indexerStore.SaveAsync(indexerEntry);
            var clientEntry = new DownloadClientEntry(
                Guid.NewGuid(), "My Client", DownloadClientType.Sabnzbd, true, 1,
                new DownloadClientSettings("http://client.example", new Dictionary<MediaAcquisitionKind, string?> { [MediaAcquisitionKind.Book] = null, [MediaAcquisitionKind.Anime] = "anime" }), "secret");
            await clientStore.SaveAsync(clientEntry);

            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton(indexerStore);
            services.AddSingleton(clientStore);
            services.AddSingleton(health);
            services.AddSingleton<IReadOnlyDictionary<IndexerType, IIndexer>>(
                new Dictionary<IndexerType, IIndexer> { [IndexerType.Newznab] = new FakeIndexer((_, _) => throw new InvalidOperationException("boom")) });
            services.AddSingleton<IDownloadClient>(new FakeTestOnlyDownloadClient(success: true));
            await using var provider = services.BuildServiceProvider();

            var service = new AcquisitionHealthCheckService(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AcquisitionHealthCheckService>.Instance);
            await service.RunOnceAsync(CancellationToken.None);

            var indexerStatus = await health.GetAsync(AcquisitionHealthKind.Indexer, indexerEntry.Id);
            Assert.IsNotNull(indexerStatus);
            Assert.IsFalse(indexerStatus!.IsHealthy, "The fake indexer's Test call threw, so it must be recorded as unhealthy.");

            var clientStatus = await health.GetAsync(AcquisitionHealthKind.DownloadClient, clientEntry.Id);
            Assert.IsNotNull(clientStatus);
            Assert.IsTrue(clientStatus!.IsHealthy);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static IndexerEntry NewEntry(string name) =>
        new(
            Guid.NewGuid(),
            name,
            IndexerType.Newznab,
            Enabled: true,
            Priority: 1,
            new IndexerSettings("https://indexer.example", [5070], [], 100),
            "indexer-key");

    private sealed class FakeIndexer(
        Func<IndexerEntry, IndexerSearchQuery, IReadOnlyList<ProwlarrReleaseCandidate>> search) : IIndexer
    {
        public IndexerType Type => IndexerType.Newznab;

        public Task<IndexerConnectionTestResult> TestAsync(IndexerEntry entry, CancellationToken cancellationToken)
        {
            // Used only by the health-check test, which does not care about search behavior.
            try
            {
                search(entry, new IndexerSearchQuery(""));
                return Task.FromResult(new IndexerConnectionTestResult(true));
            }
            catch (InvalidOperationException)
            {
                throw;
            }
        }

        public Task<IReadOnlyList<ProwlarrReleaseCandidate>> SearchAsync(
            IndexerEntry entry,
            IndexerSearchQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(search(entry, query));
    }

    private sealed class FakeTestOnlyDownloadClient(bool success) : IDownloadClient
    {
        public string ProviderId => "sabnzbd";

        public Task<DownloadClientTestResult> TestAsync(DownloadClientEntry entry, CancellationToken cancellationToken) =>
            Task.FromResult(new DownloadClientTestResult(success, success ? "1.0" : null, success ? null : "failed"));

        public Task<DownloadClientSubmitResult> SubmitAsync(DownloadClientEntry entry, DownloadClientSubmitRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DownloadClientJobStatus>> GetStatusAsync(DownloadClientEntry entry, IReadOnlyCollection<string> externalIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(DownloadClientEntry entry, string externalId, bool deleteFiles, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
