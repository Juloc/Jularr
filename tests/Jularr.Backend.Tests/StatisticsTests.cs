using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data.Admin.System.V1;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.User.Statistics.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class StatisticsTests
    {
        [TestMethod]
        public async Task Statistics_ProfileHistoryAndSystemHealth_ComeFromStoredState()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (10, 1, 'Long Film'), (20, 1, 'Old Film');
                INSERT INTO "WorkEditions" ("Id", "WorkId") VALUES (1, 10), (2, 20);
                INSERT INTO "WorkVersions" ("Id", "WorkId", "WorkEditionId") VALUES (1, 10, 1), (2, 20, 2);
                INSERT INTO "MediaAssets" ("Id", "WorkId", "WorkVersionId", "MediaAssetTypeId") VALUES (1, 10, 1, 1), (2, 20, 2, 1);
                """);
            var mountId = await LibraryTests.CreateMountAsync(host);
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Movies", MediaType.Movie);
            await host.Fixture.RunSqlAsync($$"""
                INSERT INTO "StoredFiles" ("Id", "LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt", "MediaAssetId") VALUES
                    (1, {{rootId}}, 'Long Film.mkv', 4000000000, now(), now(), 1), (2, {{rootId}}, 'Old Film.mkv', 1000000000, now(), now(), 2);
                INSERT INTO "PlaybackSessions" ("PublicId", "ProfileId", "MediaAssetId", "StoredFileId", "PlaybackModeTypeId", "StartedAt", "LastSeenAt", "EndedAt") VALUES
                    (gen_random_uuid(), 101, 1, 1, 1, now() - interval '2 hours', now(), now() - interval '30 minutes'),
                    (gen_random_uuid(), 101, 2, 2, 1, now() - interval '100 days', now() - interval '100 days', now() - interval '100 days' + interval '1 hour'),
                    (gen_random_uuid(), 202, 1, 1, 1, now() - interval '1 hour', now(), NULL);
                INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "ProgressPositionTypeId", "PositionMs", "IsCompleted", "CompletedAt") VALUES (101, 10, 1, 0, TRUE, now());
                """);

            // Thirty days: one session of 90 minutes, one finished film; all time adds the old hour.
            using var month = await host.SendAsync(HttpMethod.Get, "/api/v1/statistics?days=30");
            Assert.AreEqual(HttpStatusCode.OK, month.StatusCode, await month.Content.ReadAsStringAsync());
            var recent = (await month.Content.ReadFromJsonAsync<ProfileStatisticsV1>())!;
            Assert.AreEqual(1L, recent.Sessions);
            Assert.AreEqual(5_400_000L, recent.WatchedMs, 1000);
            Assert.AreEqual(1L, recent.Completed.Single(item => item.MediaTypeId == MediaType.Movie).Count);
            Assert.AreEqual("Long Film", recent.TopWorks.Single().Title);
            using var all = await host.SendAsync(HttpMethod.Get, "/api/v1/statistics?days=0");
            Assert.AreEqual(9_000_000L, (await all.Content.ReadFromJsonAsync<ProfileStatisticsV1>())!.WatchedMs, 1000);

            // The Owner's system page reads stored state and configured tools.
            using var system = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/system");
            Assert.AreEqual(HttpStatusCode.OK, system.StatusCode, await system.Content.ReadAsStringAsync());
            var health = (await system.Content.ReadFromJsonAsync<SystemHealthV1>())!;
            Assert.IsTrue(health.DatabaseBytes > 0);
            Assert.AreEqual(5_000_000_000L, health.Library.Single(item => item.MediaTypeId == MediaType.Movie).Bytes);
            Assert.IsFalse(health.Tools.Single(tool => tool.Key == "pgDump").IsAvailable);
            foreach (var path in new[] { "/Statistics?days=365", "/Admin/System" })
            {
                using var page = await host.SendAsync(HttpMethod.Get, path);
                Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, path + await page.Content.ReadAsStringAsync());
            }

            // Warnings and errors since the start, without stack traces, for the Owner only.
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Jularr.Test").LogError(new InvalidOperationException("Disk said no."), "Import of {Title} failed.", "Film");
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Jularr.Test").LogInformation("Routine work.");
            using var logs = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/system/logs");
            var entry = (await logs.Content.ReadFromJsonAsync<Jularr.Backend.Data.Common.BaseDataList<RecentLogV1>>())!.First(item => item.Category == "Jularr.Test");
            Assert.AreEqual("Import of Film failed. (InvalidOperationException: Disk said no.)", entry.Message);
            Assert.AreEqual(Jularr.Backend.Data.Enums.Instance.RecentLogLevel.Error, entry.Level);
            using var systemPage = await host.SendAsync(HttpMethod.Get, "/Admin/System");
            var html = await systemPage.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "Import of Film failed.");
            Assert.IsFalse(html.Contains("Routine work.", StringComparison.Ordinal));

            // Every service call of the last hour counts, the slowest in total first.
            using var timings = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/system/timings");
            var timing = (await timings.Content.ReadFromJsonAsync<Jularr.Backend.Data.Common.BaseDataList<ServiceTimingV1>>())!.Single(item => item.Service == "ReadSystemHealthV1");
            Assert.IsTrue(timing.Count >= 3);
            Assert.AreEqual(0L, timing.Errors);
            StringAssert.Contains(html, "ReadSystemHealthV1");
            using var resources = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/system/resources");
            Assert.AreEqual(HttpStatusCode.OK, resources.StatusCode);
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            using var manager = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/system/logs");
            Assert.AreEqual(HttpStatusCode.Forbidden, manager.StatusCode);
        }
    }
}
