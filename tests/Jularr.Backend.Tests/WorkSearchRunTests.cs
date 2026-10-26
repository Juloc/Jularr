using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.System.Operation.V1;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Service.System.Acquisition.V1;
using Jularr.Backend.Service.System.Operation.V1;
using Microsoft.Extensions.DependencyInjection;
using Jularr.Integrations.Logic.Newznab;
using Microsoft.Extensions.Logging.Abstractions;
using Jularr.Workers;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class WorkSearchRunTests
    {
        private const string Runs = "/api/v1/admin/works/search-runs";
        public TestContext TestContext { get; set; } = null!;

        private static async Task<Host> CreateAsync(bool razor = false, int count = 65, HttpMessageHandler? source = null)
        {
            var host = await Host.CreateAsync(library: true, razor: razor, services: services =>
            {
                if (source is not null) services.AddHttpClient<NewznabClient>().ConfigurePrimaryHttpMessageHandler(() => source);
            });
            using var auth = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear")
                SELECT id, 1, 'Search target ' || id, 2020 FROM generate_series(601, {600 + count}) AS id;
                INSERT INTO "WantedItems" ("Id", "WorkId", "IsPaused", "NextSearchAt")
                SELECT id, id, id = {600 + count}, now() + interval '3 days' FROM generate_series(601, {600 + count}) AS id;
                """);
            return host;
        }

        [TestMethod]
        public async Task Cancel_InFlightSourceRequest_StopsTheRunWithoutCancellingDownloads()
        {
            var source = new ControlledSource();
            await using var host = await CreateAsync(source: source, count: 3);
            using var connection = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/acquisition/connections",
                new SaveAcquisitionConnectionDataV1("Controlled", AcquisitionConnectionType.Newznab, "https://source.example.test/", "fixture-key", true, 10, [], null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, connection.StatusCode, await connection.Content.ReadAsStringAsync());
            var queued = await QueueAsync(host);
            var running = Task.Run(async () =>
            {
                await using var scope = host.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ClaimOperationV1>().ExecuteAsync<ClaimOperationResultV1>(new ClaimOperationParametersV1(OperationType.AcquisitionSearch), default);
                await scope.ServiceProvider.GetRequiredService<ExecuteWorksSearchV1>().ExecuteAsync<UpdateResult>(new OperationParametersV1(queued.OperationId), default);
            });
            await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await CancelAsync(host, queued.OperationId);
            await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(true, await host.ScalarAsync($"SELECT \"IsCancelled\" FROM \"AcquisitionSearchRuns\" WHERE \"OperationId\" = {queued.OperationId}"));
            Assert.AreEqual(0L, await host.ScalarAsync($"SELECT \"ProcessedCount\" FROM \"Operations\" WHERE \"Id\" = {queued.OperationId}"));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"Operations\" WHERE \"OperationTypeId\" = 2"));
        }

        private sealed class ControlledSource : HttpMessageHandler
        {
            public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        }

        [TestMethod]
        public async Task Worker_RevokedRequesterFailsSafely_AndRetryPreservesAcknowledgedProgress()
        {
            await using var host = await CreateAsync(count: 4);
            var queued = await QueueAsync(host);
            await host.Fixture.RunSqlAsync($"""
                UPDATE "AcquisitionSearchTargets" SET "OutcomeKey" = 'unchanged' WHERE "OperationId" = {queued.OperationId} AND "WantedItemId" = 601;
                UPDATE "Operations" SET "ProcessedCount" = 1 WHERE "Id" = {queued.OperationId};
                UPDATE "Accounts" SET "AccountRoleTypeId" = 2 WHERE "Id" = 41;
                """);
            await using (var scope = host.Services.CreateAsyncScope())
            {
                await OperationWorker.RunNextAsync(scope.ServiceProvider, NullLogger.Instance, OperationType.AcquisitionSearch, CancellationToken.None);
            }

            Assert.AreEqual((short)4, await host.ScalarAsync($"SELECT \"OperationStatusTypeId\" FROM \"Operations\" WHERE \"Id\" = {queued.OperationId}"));
            Assert.AreEqual(1L, await host.ScalarAsync($"SELECT \"ProcessedCount\" FROM \"Operations\" WHERE \"Id\" = {queued.OperationId}"));
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 1 WHERE \"Id\" = 41");
            using var retried = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/activity/" + queued.OperationId + "/retry");
            Assert.AreEqual(HttpStatusCode.OK, retried.StatusCode, await retried.Content.ReadAsStringAsync());
            await using (var scope = host.Services.CreateAsyncScope())
            {
                await OperationWorker.RunNextAsync(scope.ServiceProvider, NullLogger.Instance, OperationType.AcquisitionSearch, CancellationToken.None);
            }

            Assert.AreEqual((short)3, await host.ScalarAsync($"SELECT \"OperationStatusTypeId\" FROM \"Operations\" WHERE \"Id\" = {queued.OperationId}"));
            Assert.AreEqual(3L, await host.ScalarAsync($"SELECT \"ProcessedCount\" FROM \"Operations\" WHERE \"Id\" = {queued.OperationId}"));
            Assert.AreEqual("unchanged", await host.ScalarAsync($"SELECT \"OutcomeKey\" FROM \"AcquisitionSearchTargets\" WHERE \"OperationId\" = {queued.OperationId} AND \"WantedItemId\" = 601"));
        }

        private static async Task<SearchRunQueuedV1> QueueAsync(Host host, QueueWorksSearchDataV1? data = null)
        {
            using var response = await host.SendAsync(HttpMethod.Post, Runs, data ?? new QueueWorksSearchDataV1());
            Assert.IsTrue(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<SearchRunQueuedV1>())!;
        }

        private static async Task CancelAsync(Host host, long id)
        {
            using var response = await host.SendAsync(HttpMethod.Put, Runs + "/" + id + "/cancel");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        [TestMethod]
        public async Task Queue_AllPagesAndFilters_AreSnapshottedAndDuplicateClicksShareTheRun()
        {
            await using var host = await CreateAsync();
            var all = await QueueAsync(host);
            Assert.AreEqual(64, all.TargetCount, "All pages are included; paused targets stay paused.");
            var same = await QueueAsync(host);
            Assert.AreEqual(all.OperationId, same.OperationId);
            Assert.AreEqual(64L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionSearchTargets\""));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"WantedItems\" WHERE \"IsPaused\""));
            using var conflicting = await host.SendAsync(HttpMethod.Post, Runs, new QueueWorksSearchDataV1([601]));
            Assert.AreEqual(HttpStatusCode.BadRequest, conflicting.StatusCode);

            // A durable selection does not grow when the library changes afterwards.
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (900, 1, 'New after queue');
                INSERT INTO "WantedItems" ("Id", "WorkId") VALUES (900, 900);
                """);
            Assert.AreEqual(64L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionSearchTargets\""));
            using var page = await host.SendAsync(HttpMethod.Get, Runs + "/" + all.OperationId + "/results?page=3");
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, await page.Content.ReadAsStringAsync());
            Assert.AreEqual(14, (await page.Content.ReadFromJsonAsync<PageResult<WorkBulkOutcomeV1>>())!.Items.Count);
            await CancelAsync(host, all.OperationId);

            var filtered = await QueueAsync(host, new QueueWorksSearchDataV1(mediaTypeId: MediaType.Movie, query: "Search target 61"));
            Assert.AreEqual(10, filtered.TargetCount);
            await CancelAsync(host, filtered.OperationId);
            var selected = await QueueAsync(host, new QueueWorksSearchDataV1([603, 601, 601]));
            Assert.AreEqual(2, selected.TargetCount);
            await CancelAsync(host, selected.OperationId);
            using var empty = await host.SendAsync(HttpMethod.Post, Runs, new QueueWorksSearchDataV1(isMonitored: false));
            Assert.AreEqual(HttpStatusCode.BadRequest, empty.StatusCode);
            StringAssert.Contains(await empty.Content.ReadAsStringAsync(), "no_search_targets");
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"Operations\" WHERE \"OperationTypeId\" = 6 AND \"OperationStatusTypeId\" IN (1,2)"));
        }

        [TestMethod]
        public async Task Worker_RestartResumesOnlyPendingTargets_AndKeepsExistingDownloads()
        {
            await using var host = await CreateAsync(count: 4);
            var queued = await QueueAsync(host);
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var claimed = await scope.ServiceProvider.GetRequiredService<ClaimOperationV1>().ExecuteAsync<ClaimOperationResultV1>(new ClaimOperationParametersV1(OperationType.AcquisitionSearch), default);
                Assert.AreEqual(queued.OperationId, claimed.OperationId);
            }

            // The first target was acknowledged before a restart; its result must not be run again.
            await host.Fixture.RunSqlAsync($"""
                UPDATE "AcquisitionSearchTargets" SET "OutcomeKey" = 'unchanged' WHERE "OperationId" = {queued.OperationId} AND "WantedItemId" = 601;
                UPDATE "Operations" SET "ProcessedCount" = 1 WHERE "Id" = {queued.OperationId};
                INSERT INTO "AcquisitionReleases" ("Id", "WantedItemId", "ReleaseIdentity", "Title", "AcquisitionReleaseStatusTypeId") VALUES (990, 602, 'running', 'Running download', 1);
                INSERT INTO "Operations" ("Id", "OperationTypeId", "AcquisitionReleaseId", "OperationStatusTypeId") VALUES (990, 2, 990, 2);
                """);
            await using (var scope = host.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<RequeueOperationsV1>().ExecuteAsync<UpdateResult>(new OperationQueueParametersV1(), default);
                var claimed = await scope.ServiceProvider.GetRequiredService<ClaimOperationV1>().ExecuteAsync<ClaimOperationResultV1>(new ClaimOperationParametersV1(OperationType.AcquisitionSearch), default);
                Assert.AreEqual(queued.OperationId, claimed.OperationId);
                await scope.ServiceProvider.GetRequiredService<ExecuteWorksSearchV1>().ExecuteAsync<UpdateResult>(new OperationParametersV1(queued.OperationId), default);
            }

            Assert.AreEqual(3L, await host.ScalarAsync($"SELECT \"ProcessedCount\" FROM \"Operations\" WHERE \"Id\" = {queued.OperationId}"));
            Assert.AreEqual((short)3, await host.ScalarAsync($"SELECT \"OperationStatusTypeId\" FROM \"Operations\" WHERE \"Id\" = {queued.OperationId}"));
            Assert.AreEqual((short)2, await host.ScalarAsync("SELECT \"OperationStatusTypeId\" FROM \"Operations\" WHERE \"Id\" = 990"));
            Assert.AreEqual("unchanged", await host.ScalarAsync($"SELECT \"OutcomeKey\" FROM \"AcquisitionSearchTargets\" WHERE \"OperationId\" = {queued.OperationId} AND \"WantedItemId\" = 601"));
        }

        [TestMethod]
        public async Task CancelledRun_IsFinalAcrossRestart_AndAnotherAccountCannotReadOrCancelIt()
        {
            await using var host = await CreateAsync();
            var queued = await QueueAsync(host);
            await host.Fixture.RunSqlAsync("""
                UPDATE "Accounts" SET "AccountRoleTypeId" = 3 WHERE "Id" = 42;
                INSERT INTO "AccountPasswords" ("AccountId", "PasswordHash") SELECT 42, "PasswordHash" FROM "AccountPasswords" WHERE "AccountId" = 41;
                """);
            var email = (string)(await host.ScalarAsync("SELECT \"Email\" FROM \"Accounts\" WHERE \"Id\" = 42"))!;
            host.Cookies.Clear();
            using var auth = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1(email, "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
            using var status = await host.SendAsync(HttpMethod.Get, Runs + "?operationId=" + queued.OperationId);
            Assert.AreEqual(0, (await status.Content.ReadFromJsonAsync<BaseDataList<SearchRunDetailsV1>>())!.Count);
            using var results = await host.SendAsync(HttpMethod.Get, Runs + "/" + queued.OperationId + "/results");
            Assert.AreEqual(HttpStatusCode.OK, results.StatusCode, await results.Content.ReadAsStringAsync());
            Assert.AreEqual(0, (await results.Content.ReadFromJsonAsync<PageResult<WorkBulkOutcomeV1>>())!.Items.Count);
            using var denied = await host.SendAsync(HttpMethod.Put, Runs + "/" + queued.OperationId + "/cancel");
            Assert.AreEqual(HttpStatusCode.BadRequest, denied.StatusCode);
            Assert.AreEqual(false, await host.ScalarAsync($"SELECT \"IsCancelled\" FROM \"AcquisitionSearchRuns\" WHERE \"OperationId\" = {queued.OperationId}"));

            host.Cookies.Clear();
            using var authAgain = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var owner = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            await CancelAsync(host, queued.OperationId);
            await using var scope = host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<RequeueOperationsV1>().ExecuteAsync<UpdateResult>(new OperationQueueParametersV1(), default);
            var claim = await scope.ServiceProvider.GetRequiredService<ClaimOperationV1>().ExecuteAsync<ClaimOperationResultV1>(new ClaimOperationParametersV1(OperationType.AcquisitionSearch), default);
            Assert.IsNull(claim.OperationId);
            Assert.AreEqual((short)3, await host.ScalarAsync($"SELECT \"OperationStatusTypeId\" FROM \"Operations\" WHERE \"Id\" = {queued.OperationId}"));
        }

        [TestMethod]
        public async Task Razor_QueuesFilteredOrSelected_AndServesAnAuthorizedStatusFragment()
        {
            await using var host = await CreateAsync(razor: true);
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Works?type=1&q=Search%20target%2061");
            var html = await page.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "handler=SearchAll");
            var queued = await QueueAsync(host, new QueueWorksSearchDataV1([601, 602]));
            using var progress = await host.SendAsync(HttpMethod.Get, "/Admin/Works?handler=SearchProgress&searchJob=" + queued.OperationId);
            var fragment = await progress.Content.ReadAsStringAsync();
            StringAssert.Contains(fragment, "data-running=\"true\"");
            StringAssert.Contains(fragment, "0 of 2");
            Assert.IsFalse(fragment.Contains("<html", StringComparison.OrdinalIgnoreCase));
            await CancelAsync(host, queued.OperationId);
            using var cancelled = await host.SendAsync(HttpMethod.Get, "/Admin/Works?handler=SearchProgress&searchJob=" + queued.OperationId);
            StringAssert.Contains(await cancelled.Content.ReadAsStringAsync(), "data-running=\"false\"");
        }

        [TestMethod]
        public async Task Queue_500Targets_RecordsWarmHttpLatencyWithoutCallingSources()
        {
            await using var host = await CreateAsync(count: 501);
            var samples = new List<double>();
            for (var pass = 0; pass < 25; pass++)
            {
                var timer = Stopwatch.StartNew();
                var queued = await QueueAsync(host);
                timer.Stop();
                Assert.AreEqual(500, queued.TargetCount);
                if (pass >= 5) samples.Add(timer.Elapsed.TotalMilliseconds);
                await CancelAsync(host, queued.OperationId);
            }

            samples.Sort();
            var p95 = samples[(int)Math.Ceiling(samples.Count * .95) - 1];
            TestContext.WriteLine($"Search enqueue: 500 targets, {samples.Count} warm HTTP samples, p50={samples[samples.Count / 2]:F1} ms, p95={p95:F1} ms, max={samples[^1]:F1} ms.");
            Assert.IsLessThan(500d, p95, "Enqueue must stay bounded without waiting for source work.");
        }
    }
}
