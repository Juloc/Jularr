using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using Fixture = Jularr.Backend.Tests.ProfileReferenceVerticalTests.ProfileFixture;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class DiscoveryFeedSnapshotTests
    {
        private const string Key = "metadata.feed.tmdb.Movie.Trending.1.de-DE.DE";
        private static readonly TimeSpan FreshFor = TimeSpan.FromHours(6);
        private static readonly TimeSpan RetainFor = TimeSpan.FromDays(90);

        [TestMethod]
        public async Task ColdConcurrentRequests_ShareOneProviderCall_AndRestoreAfterRestart()
        {
            await using var fixture = await Fixture.CreateAsync();
            await InstallAsync(fixture);
            var first = NewCache(fixture);
            var calls = 0;

            Task<IReadOnlyList<int>> Fetch(CancellationToken ct)
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult<IReadOnlyList<int>>([603]);
            }

            var reads = Enumerable.Range(0, 8)
                .Select(_ => first.ReadAsync(Key, "tmdb", FreshFor, RetainFor, Fetch, CancellationToken.None)).ToArray();
            var values = await Task.WhenAll(reads);
            Assert.AreEqual(1, Volatile.Read(ref calls));
            Assert.IsTrue(values.All(value => value.SequenceEqual([603])));

            // Simulate the app restarting: a new cache coordinator serves PostgreSQL
            // without asking the provider again.
            var second = NewCache(fixture);
            var restored = await second.ReadAsync<int>(Key, "tmdb", FreshFor, RetainFor,
                _ => throw new AssertFailedException("Warm reads must not contact TMDB."), CancellationToken.None);
            CollectionAssert.AreEqual(new[] { 603 }, restored.ToArray());
        }

        [TestMethod]
        public async Task StaleSnapshot_IsReturnedWithoutWaitingForBackgroundRefresh()
        {
            await using var fixture = await Fixture.CreateAsync();
            await InstallAsync(fixture);
            var coordinator = NewCache(fixture);
            await coordinator.ReadAsync<int>(Key, "tmdb", FreshFor, RetainFor,
                _ => Task.FromResult<IReadOnlyList<int>>([603]), CancellationToken.None);
            await fixture.RunSqlAsync(
                """
                UPDATE "DiscoverySnapshots"
                SET "FetchedAt" = now() - interval '8 hours',
                    "FreshUntil" = now() - interval '2 hours'
                WHERE "Key" = 'metadata.feed.tmdb.Movie.Trending.1.de-DE.DE';
                """);

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cached = await coordinator.ReadAsync<int>(Key, "tmdb", FreshFor, RetainFor,
                async ct =>
                {
                    started.TrySetResult();
                    await resume.Task.WaitAsync(ct);
                    return [604];
                }, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { 603 }, cached.ToArray());
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            resume.TrySetResult();

            // Allow the async writer to complete, then verify the committed new generation.
            var refreshed = false;
            for (var i = 0; i < 100; i++)
            {
                var response = await NewCache(fixture).ReadAsync<int>(Key, "tmdb", FreshFor, RetainFor,
                    _ => throw new AssertFailedException("A fresh snapshot must not fetch."), CancellationToken.None);
                if (response.SequenceEqual([604]))
                {
                    refreshed = true;
                    break;
                }
                await Task.Delay(50);
            }
            Assert.IsTrue(refreshed, "Background refresh should atomically install the new generation.");
        }

        [TestMethod]
        public async Task MissingMigration_KeepsTheOldProviderPathUsable()
        {
            await using var fixture = await Fixture.CreateAsync();
            var calls = 0;
            var coordinator = NewCache(fixture);
            var response = await coordinator.ReadAsync<int>(Key, "tmdb", FreshFor, RetainFor,
                _ =>
                {
                    Interlocked.Increment(ref calls);
                    return Task.FromResult<IReadOnlyList<int>>([603]);
                }, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { 603 }, response.ToArray());
            Assert.AreEqual(1, calls);
            var reused = await coordinator.ReadAsync<int>(Key, "tmdb", FreshFor, RetainFor,
                _ => throw new AssertFailedException("Short compatibility cache should prevent duplicate source calls."),
                CancellationToken.None);
            CollectionAssert.AreEqual(new[] { 603 }, reused.ToArray());
        }

        [TestMethod]
        public async Task EmptyRefresh_DoesNotEraseAnExistingSnapshot()
        {
            await using var fixture = await Fixture.CreateAsync();
            await InstallAsync(fixture);
            var coordinator = NewCache(fixture);
            await coordinator.ReadAsync<int>(Key, "tmdb", FreshFor, RetainFor,
                _ => Task.FromResult<IReadOnlyList<int>>([603]), CancellationToken.None);
            await fixture.RunSqlAsync(
                """
                UPDATE "DiscoverySnapshots"
                SET "FetchedAt" = now() - interval '8 hours',
                    "FreshUntil" = now() - interval '2 hours'
                WHERE "Key" = 'metadata.feed.tmdb.Movie.Trending.1.de-DE.DE';
                """);

            var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stale = await coordinator.ReadAsync<int>(Key, "tmdb", FreshFor, RetainFor,
                _ =>
                {
                    invoked.TrySetResult();
                    return Task.FromResult<IReadOnlyList<int>>([]);
                }, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { 603 }, stale.ToArray());
            await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var persistent = await NewCache(fixture).ReadAsync<int>(Key, "tmdb", FreshFor, RetainFor,
                _ => Task.FromResult<IReadOnlyList<int>>([]), CancellationToken.None);
            CollectionAssert.AreEqual(new[] { 603 }, persistent.ToArray());
        }

        private static DiscoveryFeedSnapshots NewCache(Fixture fixture) =>
            new(new SqlDatabase(fixture.Database), TimeProvider.System,
                new MemoryCache(new MemoryCacheOptions { SizeLimit = 20_000 }), NullLogger<DiscoveryFeedSnapshots>.Instance);

        private static async Task InstallAsync(Fixture fixture)
        {
            using var stream = typeof(DatabaseBaseline).Assembly.GetManifestResourceStream(
                "Jularr.Database.Migrations.20261018_01_DiscoverySnapshots.up.sql")
                ?? throw new InvalidOperationException("Discovery schema is not embedded.");
            using var reader = new StreamReader(stream);
            await fixture.RunSqlAsync(await reader.ReadToEndAsync());
        }
    }
}
