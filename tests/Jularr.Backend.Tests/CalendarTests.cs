using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.User.Calendar.V1;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class CalendarTests
    {
        [TestMethod]
        public async Task Calendar_UpcomingUnits_OfWatchlistedRequestedOrOwnedWorksByDay()
        {
            await using var host = await Host.CreateAsync(razor: true, media: true);
            var today = DateTime.UtcNow.Date;
            await host.Fixture.RunSqlAsync($$"""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (10, 2, 'Watched Show'), (20, 2, 'Unknown Show'), (30, 2, 'Requested Show');
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (100, 10, 1, 1), (200, 20, 1, 1), (300, 30, 1, 2);
                INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number", "Title", "ReleasedOn") VALUES
                    (10, 100, 2, 3, 'Next Week', '{{today.AddDays(7):yyyy-MM-dd}}'),
                    (10, 100, 2, 1, 'Long Ago', '{{today.AddDays(-30):yyyy-MM-dd}}'),
                    (20, 200, 2, 1, 'Not Mine', '{{today.AddDays(2):yyyy-MM-dd}}'),
                    (30, 300, 2, 5, NULL, '{{today.AddDays(1):yyyy-MM-dd}}');
                INSERT INTO "WatchlistEntries" ("ProfileId", "WorkId") VALUES (101, 10);
                INSERT INTO "AcquisitionRequests" ("WorkId", "RequestedByProfileId", "RequestedByAccountId", "UiLocaleId") VALUES (30, 101, 41, 1);
                """);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());

            // Two weeks from today: the requested episode first, then the watchlisted one; nothing of unrelated Works.
            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/calendar?from=" + today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "&days=14");
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
            var entries = (await read.Content.ReadFromJsonAsync<BaseDataList<CalendarEntryV1>>())!;
            CollectionAssert.AreEqual(new[] { 30L, 10L }, entries.Select(entry => entry.WorkId).ToArray());
            Assert.AreEqual(DateOnly.FromDateTime(today.AddDays(1)), entries[0].Day);
            Assert.AreEqual(2m, entries[0].ParentUnitNumber);
            Assert.IsTrue(entries[1].IsWatchlisted);

            using var page = await host.SendAsync(HttpMethod.Get, "/Calendar");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "Next Week");
            Assert.IsFalse(html.Contains("Not Mine", StringComparison.Ordinal));

            using var tooLong = await host.SendAsync(HttpMethod.Get, "/api/v1/calendar?from=" + today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "&days=100");
            Assert.AreEqual(HttpStatusCode.BadRequest, tooLong.StatusCode);
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Calendar'");
            using var off = await host.SendAsync(HttpMethod.Get, "/api/v1/calendar?from=" + today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Assert.AreEqual(HttpStatusCode.Forbidden, off.StatusCode);
        }
    }
}
