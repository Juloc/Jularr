using System.Security;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ExternalFeedTests
    {
        private static readonly ExternalFeedData Trending = new("tmdb_movie", MediaType.Movie, null, DiscoverFeedType.Trending, 1, null, "en");

        [TestMethod]
        public async Task FeedRead_RetainsOrderAndReadsCurrentFactsRegardlessOfRefreshDate()
        {
            await using var host = await Host.CreateAsync(library: true);
            await SaveAsync(host, Trending, [Work("902", "Second"), Work("901", "First")]);
            await host.Fixture.RunSqlAsync("""
                UPDATE "ExternalWorkFeeds" SET "RefreshedAt" = now() - interval '30 days';
                UPDATE "Works" SET "CanonicalTitle" = 'Updated canonical title' WHERE "Id" = (SELECT "WorkId" FROM "WorkExternalIdentities" WHERE "Namespace" = 'tmdb_movie' AND "Value" = '902');
                """);

            await using var read = new SqlContext(host.Fixture.Database);
            await read.BeginAsync(SqlAccessMode.ReadOnly);
            var items = await new LogicContext(read).ReadExternalFeedAsync(Trending, default);
            Assert.IsNotNull(items);
            CollectionAssert.AreEqual(new[] { "902", "901" }, items.Select(item => item.Value).ToArray());
            Assert.AreEqual("Updated canonical title", items[0].Title);
            Assert.AreEqual(8.25m, items[0].Rating);
            Assert.IsNotNull(items[0].WorkId, "Every feed entry is a Work.");
        }

        [TestMethod]
        public async Task FeedSave_FailedReplacementRollsBackProviderFactsAndKeepsPreviousList()
        {
            await using var host = await Host.CreateAsync(library: true);
            await SaveAsync(host, Trending, [Work("901", "Original")]);
            var invalid = new ExternalFeedData("tmdb_movie", MediaType.Movie, null, null, 1, null, "en");
            var error = await Assert.ThrowsExactlyAsync<PostgresException>(() => SaveAsync(host, invalid, [Work("901", "Must roll back"), Work("999", "Must not exist")]));
            Assert.AreEqual("23514", error.SqlState);

            await using var read = new SqlContext(host.Fixture.Database);
            await read.BeginAsync(SqlAccessMode.ReadOnly);
            var items = await new LogicContext(read).ReadExternalFeedAsync(Trending, default);
            Assert.IsNotNull(items);
            Assert.AreEqual("Original", items.Single().Title);
            Assert.AreEqual(1L, await read.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT count(*) FROM \"Works\" WHERE \"CatalogNamespace\" = 'tmdb_movie'" }));
        }

        [TestMethod]
        public async Task FeedRead_EmptyIsPresentAndDisabledCacheRejectsAccess()
        {
            await using var host = await Host.CreateAsync(library: true);
            await SaveAsync(host, Trending, []);
            await using (var read = new SqlContext(host.Fixture.Database))
            {
                await read.BeginAsync(SqlAccessMode.ReadOnly);
                var items = await new LogicContext(read).ReadExternalFeedAsync(Trending, default);
                Assert.IsNotNull(items);
                Assert.AreEqual(0, items.Count);
            }
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'DiscoverCache'");
            await using var disabled = new SqlContext(host.Fixture.Database);
            await disabled.BeginAsync(SqlAccessMode.ReadOnly);
            await Assert.ThrowsExactlyAsync<SecurityException>(() => new LogicContext(disabled).ReadExternalFeedAsync(Trending, default));
            await Assert.ThrowsExactlyAsync<SecurityException>(() => SaveAsync(host, Trending, [Work("999", "Not saved")]));
        }

        [TestMethod]
        public async Task Cache_RestartAndOldList_NeverFetchOnRead()
        {
            await using var host = await Host.CreateAsync(library: true);
            await SaveAsync(host, Trending, [Work("901", "Persisted")]);
            await host.Fixture.RunSqlAsync("UPDATE \"ExternalWorkFeeds\" SET \"RefreshedAt\" = now() - interval '30 days'");
            using var memory = new MemoryCache(new MemoryCacheOptions());
            using var cache = new ExternalFeedCache(memory);
            await using var sql = new SqlContext(host.Fixture.Database);
            var context = BatchContext(sql);
            var items = await cache.LoadAsync(context, Trending, (_, _) => throw new AssertFailedException("A warm read must not call the provider."), default);
            Assert.AreEqual("Persisted", items.Single().Title);
        }

        [TestMethod]
        public async Task Cache_FirstFillIsShared_AndCancelledWaiterDoesNotCancelOtherReaders()
        {
            await using var host = await Host.CreateAsync(library: true);
            using var memory = new MemoryCache(new MemoryCacheOptions());
            using var cache = new ExternalFeedCache(memory);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            async Task<IReadOnlyList<ExternalWorkData>> Fetch(ExternalFeedData _, CancellationToken ct)
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return [Work("901", "Filled once")];
            }
            async Task<IReadOnlyList<ExternalWorkData>> Read(CancellationToken ct)
            {
                await using var sql = new SqlContext(host.Fixture.Database);
                return await cache.LoadAsync(BatchContext(sql), Trending, Fetch, ct);
            }

            var first = Read(default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var cancelled = new CancellationTokenSource();
            var waiter = Read(cancelled.Token);
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => waiter);
            var others = Enumerable.Range(0, 4).Select(_ => Read(default)).ToArray();
            release.SetResult();
            await first;
            foreach (var items in await Task.WhenAll(others))
            {
                Assert.AreEqual("Filled once", items.Single().Title);
            }
            Assert.AreEqual(1, calls);
        }

        [TestMethod]
        public async Task Cache_WarmReadDoesNotWaitForAnOngoingRefresh()
        {
            await using var host = await Host.CreateAsync(library: true);
            await SaveAsync(host, Trending, [Work("901", "Available while refreshing")]);
            using var memory = new MemoryCache(new MemoryCacheOptions());
            using var cache = new ExternalFeedCache(memory);
            await using var refreshSql = new SqlContext(host.Fixture.Database);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var refresh = cache.LoadAsync(BatchContext(refreshSql), Trending, async (_, ct) =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(ct);
                return [Work("902", "New result")];
            }, default, refresh: true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                await using var readSql = new SqlContext(host.Fixture.Database);
                var items = await cache.LoadAsync(BatchContext(readSql), Trending, (_, _) => throw new AssertFailedException("A warm read must not fetch."), default).WaitAsync(TimeSpan.FromSeconds(2));
                Assert.AreEqual("Available while refreshing", items.Single().Title);
            }
            finally
            {
                release.SetResult();
                await refresh;
            }
        }

        [TestMethod]
        public async Task Cache_FailedAndEmptyRefreshRetainLastGoodList()
        {
            await using var host = await Host.CreateAsync(library: true);
            await SaveAsync(host, Trending, [Work("901", "Last good")]);
            using var memory = new MemoryCache(new MemoryCacheOptions());
            using var cache = new ExternalFeedCache(memory);
            await using var sql = new SqlContext(host.Fixture.Database);
            var context = BatchContext(sql);
            await Assert.ThrowsExactlyAsync<ServiceUnavailableException>(() => cache.LoadAsync(context, Trending,
                (_, _) => Task.FromResult<IReadOnlyList<ExternalWorkData>>([]), default, refresh: true));
            await Assert.ThrowsExactlyAsync<ServiceUnavailableException>(() => cache.LoadAsync(context, Trending,
                (_, _) => throw new ServiceUnavailableException("metadata_unavailable"), default, refresh: true));
            var items = await cache.LoadAsync(context, Trending, (_, _) => throw new AssertFailedException("Failure cooldown must not affect warm reads."), default);
            Assert.AreEqual("Last good", items.Single().Title);
        }

        [TestMethod]
        public async Task RefreshSelection_BootstrapsEnabledScopes_RespectsIntervalsAndFailureRetry()
        {
            await using var host = await Host.CreateAsync(library: true);
            await host.Fixture.RunSqlAsync("UPDATE \"Providers\" SET \"ProtectedSecret\" = decode('01', 'hex') WHERE \"Key\" = 'tmdb'");
            var defaults = DiscoverScopes.All.SelectMany(scope => scope.Feeds.Select(feed =>
                new ExternalFeedData(DiscoverScopes.Namespace(scope), scope.MediaTypeId, scope.MediaSubtypeId, feed, 1, null, null))).ToArray();
            var now = DateTime.UtcNow;
            var query = new ExternalFeedRefreshData(now, 100, defaults);
            await using var sql = new SqlContext(host.Fixture.Database);
            var context = BatchContext(sql);
            var missing = await context.ExecuteBatchAsync((ctx, ct) => ctx.ReadMissingExternalFeedsAsync(query, ct), mode: SqlAccessMode.ReadOnly);
            Assert.AreEqual(17, missing.Count);
            Assert.IsTrue(missing.Where(feed => feed.Namespace.StartsWith("tmdb", StringComparison.Ordinal)).All(feed => feed.Language is not null));
            var top = new ExternalFeedData("tmdb_movie", MediaType.Movie, null, DiscoverFeedType.Top, 1, null, "en");
            var recommendations = new ExternalFeedData("tmdb_movie", MediaType.Movie, null, null, 1, "603", "en");
            await SaveAsync(host, Trending, [Work("901", "Trending")]);
            await SaveAsync(host, top, [Work("902", "Top")]);
            await SaveAsync(host, recommendations, [Work("903", "Recommended")]);
            await host.Fixture.RunSqlAsync("UPDATE \"ExternalWorkFeeds\" SET \"RefreshedAt\" = now() - interval '1 hour', \"RetryAfter\" = now() - interval '1 minute'");
            var due = await context.ExecuteBatchAsync((ctx, ct) => ctx.ReadDueExternalFeedsAsync(query, ct), mode: SqlAccessMode.ReadOnly);
            CollectionAssert.AreEqual(new[] { top.Key }, due.Select(feed => feed.Key).ToArray());
            await context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.PostponeExternalFeedAsync(new ExternalFeedRetryData(top, now.AddMinutes(1)), ct);
                return true;
            });
            due = await context.ExecuteBatchAsync((ctx, ct) => ctx.ReadDueExternalFeedsAsync(query, ct), mode: SqlAccessMode.ReadOnly);
            Assert.AreEqual(0, due.Count);
            await host.Fixture.RunSqlAsync("UPDATE \"ExternalWorkFeeds\" SET \"RefreshedAt\" = now() - interval '13 hours', \"RetryAfter\" = now() - interval '1 minute'");
            due = await context.ExecuteBatchAsync((ctx, ct) => ctx.ReadDueExternalFeedsAsync(query, ct), mode: SqlAccessMode.ReadOnly);
            Assert.AreEqual(3, due.Count);
            var localCatalog = new ExternalFeedRefreshData(now, 100, defaults, new HashSet<string>(["tmdb_movie"], StringComparer.Ordinal));
            due = await context.ExecuteBatchAsync((ctx, ct) => ctx.ReadDueExternalFeedsAsync(localCatalog, ct), mode: SqlAccessMode.ReadOnly);
            Assert.AreEqual(recommendations.Key, due.Single().Key, "A whole local catalog needs no remote feed lists, but still needs recommendations.");
            await host.Fixture.RunSqlAsync("UPDATE \"MediaTypes\" SET \"IsEnabled\" = FALSE WHERE \"Id\" = 1");
            due = await context.ExecuteBatchAsync((ctx, ct) => ctx.ReadDueExternalFeedsAsync(query, ct), mode: SqlAccessMode.ReadOnly);
            Assert.AreEqual(0, due.Count);
        }

        private static LogicContext BatchContext(SqlContext sql)
        {
            var context = new LogicContext(sql);
            context.SetTransactionalAccessCheck((_, _, _) => Task.CompletedTask);
            return context;
        }

        private static ExternalWorkData Work(string value, string title) => new("tmdb_movie", value, title, null, 2026, null, null, null, null, null, 8.25m);

        private static async Task SaveAsync(Host host, ExternalFeedData feed, IReadOnlyList<ExternalWorkData> works)
        {
            await using var write = new SqlContext(host.Fixture.Database);
            await write.BeginAsync(SqlAccessMode.ReadWrite);
            await new LogicContext(write).SaveExternalFeedAsync(feed, works, default);
            await write.CommitAsync();
        }
    }
}
