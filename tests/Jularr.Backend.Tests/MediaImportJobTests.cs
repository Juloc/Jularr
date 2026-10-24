using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.System.Operation.V1;
using Jularr.Backend.Service.System.Operation.V1;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class MediaImportJobTests
    {
        private const string Books = """
            {"version":1,"items":[
              {"type":"movie","ids":{"tmdb":"603"},"title":"The Matrix"},
              {"type":"movie","ids":{"tmdb":"604"},"title":"The Matrix Reloaded"}
            ]}
            """;

        // MediaImportDataV1 supports file upload; the API therefore uses form
        // binding rather than JSON. Each row is explicitly selected by the owner.
        private static Dictionary<string, string> Form(string json, MediaImportActionType action = MediaImportActionType.AddOnly)
        {
            var fields = new Dictionary<string, string>
            {
                ["Json"] = json,
                ["Action"] = ((byte)action).ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            var count = System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("items").GetArrayLength();
            for (var index = 0; index < count; index++)
            {
                fields[$"Choices[{index}].Index"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                fields[$"Choices[{index}].Include"] = "true";
            }
            return fields;
        }

        [TestMethod]
        public async Task DurableJob_IsOwnedByServer_StreamsSavedResultsAndNeverAutoDownloadsAddOnly()
        {
            await using var host = await MediaImportTests.CreateHostAsync(new MediaImportTests.FakeProviders());
            var data = Form(Books);

            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/media-import/jobs", data);
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var job = (await created.Content.ReadFromJsonAsync<MediaImportJobCreatedV1>())!;
            Assert.AreNotEqual(Guid.Empty, job.JobId);

            // A repeated start while active returns exactly the same persisted job,
            // rather than replacing the original list or racing two importers.
            using var duplicate = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/media-import/jobs", data);
            Assert.AreEqual(job.JobId, (await duplicate.Content.ReadFromJsonAsync<MediaImportJobCreatedV1>())!.JobId);

            await using (var scope = host.Services.CreateAsyncScope())
            {
                var claim = await scope.ServiceProvider.GetRequiredService<ClaimOperationV1>()
                    .ExecuteAsync<ClaimOperationResultV1>(new ClaimOperationParametersV1(OperationType.MediaImport), default);
                Assert.IsNotNull(claim.OperationId);
                var completed = await scope.ServiceProvider.GetRequiredService<ExecuteMediaImportJobV1>()
                    .ExecuteAsync<UpdateResult>(new OperationParametersV1(claim.OperationId.Value), default);
                Assert.IsTrue(completed.Success);
            }

            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/media-import/jobs/" + job.JobId);
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
            var status = (await read.Content.ReadFromJsonAsync<MediaImportJobStatusV1>())!;
            Assert.AreEqual(OperationStatusType.Completed, status.StatusTypeId);
            Assert.AreEqual(2, status.TotalCount);
            Assert.AreEqual(2L, status.ProcessedCount);
            Assert.AreEqual(2L, status.Sequence);
            Assert.AreEqual(2, status.Rows.Count);
            Assert.IsTrue(status.Rows.All(item => item.OutcomeTypeId == MediaImportOutcomeType.Added));
            Assert.AreEqual(2L, await host.ScalarAsync("""SELECT count(*) FROM "Works" """));
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "WantedItems" """));

            using var latest = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/media-import/jobs/latest");
            Assert.AreEqual(job.JobId, (await latest.Content.ReadFromJsonAsync<MediaImportLatestJobV1>())!.JobId);
        }

        [TestMethod]
        public async Task DurableJob_ReclaimsInterruptedOperation_WithoutDuplicatingACommittedWork()
        {
            await using var host = await MediaImportTests.CreateHostAsync(new MediaImportTests.FakeProviders());
            using var queued = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/media-import/jobs",
                Form(Books));
            Assert.AreEqual(HttpStatusCode.OK, queued.StatusCode);
            var job = (await queued.Content.ReadFromJsonAsync<MediaImportJobCreatedV1>())!;

            await using var scope = host.Services.CreateAsyncScope();
            var claimant = scope.ServiceProvider.GetRequiredService<ClaimOperationV1>();
            var first = await claimant.ExecuteAsync<ClaimOperationResultV1>(new ClaimOperationParametersV1(OperationType.MediaImport), default);
            Assert.IsNotNull(first.OperationId);

            // The provider import committed but the journal did not: precisely the
            // crash window that cannot be made atomic with external provider I/O.
            const string firstBook = """
                {"version":1,"items":[{"type":"movie","ids":{"tmdb":"603"},"title":"The Matrix"}]}
                """;
            using var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/media-import",
                Form(firstBook));
            Assert.AreEqual(HttpStatusCode.OK, imported.StatusCode);
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "Works" """));

            // Simulates the startup recovery pass of OperationWorker after a restart.
            var recovered = await scope.ServiceProvider.GetRequiredService<RequeueOperationsV1>()
                .ExecuteAsync<UpdateResult>(new OperationQueueParametersV1(), default);
            Assert.IsTrue(recovered.Success);
            var second = await claimant.ExecuteAsync<ClaimOperationResultV1>(new ClaimOperationParametersV1(OperationType.MediaImport), default);
            Assert.AreEqual(first.OperationId, second.OperationId);
            await scope.ServiceProvider.GetRequiredService<ExecuteMediaImportJobV1>()
                .ExecuteAsync<UpdateResult>(new OperationParametersV1(second.OperationId!.Value), default);

            Assert.AreEqual(2L, await host.ScalarAsync("""SELECT count(*) FROM "Works" """));
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "WantedItems" """));
            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/media-import/jobs/" + job.JobId);
            var status = (await read.Content.ReadFromJsonAsync<MediaImportJobStatusV1>())!;
            Assert.AreEqual(OperationStatusType.Completed, status.StatusTypeId);
            Assert.AreEqual(2L, status.Sequence);
            Assert.AreEqual(2, status.Rows.Count);
        }

        [TestMethod]
        public async Task DurableJob_RevokedOwnerCannotImportTheNextWork()
        {
            await using var host = await MediaImportTests.CreateHostAsync(new MediaImportTests.FakeProviders());
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/media-import/jobs",
                Form(Books, MediaImportActionType.MonitorAndSearch));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode);

            // The worker is system-authorized, but the account that queued the
            // job is no longer authorized. It must never create Works or Wanted.
            await host.Fixture.RunSqlAsync("""UPDATE "Accounts" SET "IsEnabled" = false WHERE "Id" = 41;""");
            await using var scope = host.Services.CreateAsyncScope();
            var claim = await scope.ServiceProvider.GetRequiredService<ClaimOperationV1>()
                .ExecuteAsync<ClaimOperationResultV1>(new ClaimOperationParametersV1(OperationType.MediaImport), default);
            Assert.IsNotNull(claim.OperationId);
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
                scope.ServiceProvider.GetRequiredService<ExecuteMediaImportJobV1>()
                    .ExecuteAsync<UpdateResult>(new OperationParametersV1(claim.OperationId.Value), default));
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "Works" """));
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "WantedItems" """));
        }
    }
}
