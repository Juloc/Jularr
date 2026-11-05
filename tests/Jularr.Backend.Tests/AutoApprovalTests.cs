using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data.User.Request.V1;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Service.Core;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    // A request an administrator makes (or a rule approves) is approved at once, becomes a wanted target that is due now, and wakes the search: for every kind of title.
    // A target that waits for the feed is due again when another request for it is approved.
    [TestClass]
    public sealed class AutoApprovalTests
    {
        [TestMethod]
        public async Task OwnerRequest_IsApprovedAtOnce_ForEveryType_AndTheTargetIsDueNow()
        {
            await using var host = await Host.CreateAsync(media: true);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle") VALUES
                    (301, 1, NULL, 'A film'), (302, 2, NULL, 'A series'), (303, 2, 1, 'An anime'), (304, 3, NULL, 'A book'), (305, 3, 2, 'A manga'),
                    (306, 3, 3, 'A light novel'), (307, 6, NULL, 'An album'), (308, 7, NULL, 'A game');
                INSERT INTO "GamePlatforms" ("Id", "Name") OVERRIDING SYSTEM VALUE VALUES (901, 'PC');
                INSERT INTO "WorkPlatforms" ("WorkId", "GamePlatformId") VALUES (308, 901);
                """);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
            var signal = host.Services.GetRequiredService<AcquisitionSearchSignal>();

            foreach (var workId in new[] { 301, 302, 303, 304, 305, 306, 307, 308 })
            {
                signal.TakeRequest();
                using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/works/" + workId + "/requests", new CreateRequestDataV1(1, workId == 308 ? 901 : null));
                Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, "Work " + workId + ": " + await created.Content.ReadAsStringAsync());

                // Approved, one shared target that is not paused and has no search time (due now), and the search was asked for.
                Assert.AreEqual("2:true:true", await host.ScalarAsync($"""
                    SELECT request."AcquisitionRequestStatusTypeId" || ':' || (wanted."NextSearchAt" IS NULL) || ':' || NOT wanted."IsPaused"
                    FROM "AcquisitionRequests" AS request
                        JOIN "RequestTargets" AS target ON target."AcquisitionRequestId" = request."Id"
                        JOIN "WantedItems" AS wanted ON wanted."Id" = target."WantedItemId"
                    WHERE request."WorkId" = {workId}
                    """), "Work " + workId);
                Assert.IsTrue(signal.TakeRequest(), "Work " + workId + " did not wake the search.");
            }
        }

        [TestMethod]
        public async Task ApprovedRequest_WakesATargetThatWaitsForTheFeed()
        {
            await using var host = await Host.CreateAsync(media: true);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (301, 1, 'A film');
                INSERT INTO "WantedItems" ("Id", "WorkId", "NextSearchAt", "PeriodicSearchAt") VALUES (401, 301, 'infinity', now() + interval '1 day');
                """);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());

            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/works/301/requests", new CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());

            Assert.AreEqual(true, await host.ScalarAsync("""SELECT "NextSearchAt" IS NULL FROM "WantedItems" WHERE "Id" = 401"""));
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "RequestTargets" WHERE "WantedItemId" = 401"""));
        }
    }
}
