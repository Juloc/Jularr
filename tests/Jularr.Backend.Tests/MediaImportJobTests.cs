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

        [TestMethod]
        public async Task DurableJob_IsOwnedByServer_StreamsSavedResultsAndNeverAutoDownloadsAddOnly()
        {
            await using var host = await MediaImportTests.CreateHostAsync(new MediaImportTests.FakeProviders());
            var data = new MediaImportDataV1(null, Books, MediaImportActionType.AddOnly);

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
            Assert.AreEqual(job.JobId, (await latest.Content.ReadFromJsonAsync<MediaImportJobCreatedV1>())!.JobId);
        }

        [TestMethod]
        public async Task DurableJob_RevokedOwnerCannotImportTheNextWork()
        {
            await using var host = await MediaImportTests.CreateHostAsync(new MediaImportTests.FakeProviders());
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/media-import/jobs",
                new MediaImportDataV1(null, Books, MediaImportActionType.MonitorAndSearch));
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
