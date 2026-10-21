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
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleasedOn") VALUES (10, 2, 'Watched Show', '{{today.AddDays(4):yyyy-MM-dd}}'), (20, 2, 'Unknown Show', NULL), (30, 2, 'Requested Show', NULL);
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (100, 10, 1, 1), (200, 20, 1, 1), (300, 30, 1, 2);
                INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number", "Title", "ReleasedOn") VALUES
                    (10, 100, 2, 3, 'Next Week', '{{today.AddDays(7):yyyy-MM-dd}}'),
                    (10, 100, 2, 1, 'Long Ago', '{{today.AddDays(-30):yyyy-MM-dd}}'),
                    (20, 200, 2, 1, 'Not Mine', '{{today.AddDays(2):yyyy-MM-dd}}'),
                    (30, 300, 2, 5, NULL, '{{today.AddDays(1):yyyy-MM-dd}}');
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleasedOn") VALUES (40, 1, 'Coming Movie', '{{today.AddDays(3):yyyy-MM-dd}}');
                INSERT INTO "WatchlistEntries" ("ProfileId", "WorkId") VALUES (101, 10), (101, 40);
                INSERT INTO "AcquisitionRequests" ("WorkId", "RequestedByProfileId", "RequestedByAccountId", "UiLocaleId") VALUES (30, 101, 41, 1);
                """);
            await host.Fixture.RunSqlAsync("UPDATE \"Works\" SET \"MediaSubtypeId\" = 1 WHERE \"Id\" = 10");
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());

            // Two weeks from today: the requested episode, the watchlisted movie on its release day, then the watchlisted episode; a Work with units appears by its units only, nothing of unrelated Works.
            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/calendar?from=" + today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "&days=14");
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
            var entries = (await read.Content.ReadFromJsonAsync<BaseDataList<CalendarEntryV1>>())!;
            CollectionAssert.AreEqual(new[] { 30L, 40L, 10L }, entries.Select(entry => entry.WorkId).ToArray());
            Assert.AreEqual(DateOnly.FromDateTime(today.AddDays(1)), entries[0].Day);
            Assert.AreEqual(2m, entries[0].ParentUnitNumber);
            Assert.IsNull(entries[1].WorkUnitTypeId);
            Assert.IsTrue(entries[2].IsWatchlisted);
            Assert.IsTrue(entries[0].IsRequested);
            Assert.IsFalse(entries[1].IsRequested);
            Assert.AreEqual(Jularr.Backend.Data.Enums.Media.MediaSubtype.Anime, entries[2].MediaSubtypeId);
            Assert.IsNull(entries[1].MediaSubtypeId);

            // A week shows the releases of its days; the type, the status and the search narrow it.
            string Week(string query = "") => "/Calendar?view=week&date=" + today.AddDays(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + query;
            async Task<string> Html(string address)
            {
                using var response = await host.SendAsync(HttpMethod.Get, address);
                var html = await response.Content.ReadAsStringAsync();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, html);
                return html;
            }

            var week = await Html(Week());
            StringAssert.Contains(week, "Coming Movie");
            Assert.IsFalse(week.Contains("Not Mine", StringComparison.Ordinal));
            Assert.IsFalse((await Html(Week("&type=series"))).Contains("Coming Movie", StringComparison.Ordinal));
            StringAssert.Contains(await Html(Week("&type=movies")), "Coming Movie");
            StringAssert.Contains(await Html(Week("&status=watchlisted")), "Coming Movie");
            Assert.IsFalse((await Html(Week("&status=available"))).Contains("Coming Movie", StringComparison.Ordinal));
            StringAssert.Contains(await Html(Week("&q=coming")), "Coming Movie");
            Assert.IsFalse((await Html(Week("&q=zzz"))).Contains("Coming Movie", StringComparison.Ordinal));

            // The next week holds the episode of the anime; the day view lists one day.
            var nextWeek = "/Calendar?view=week&date=" + today.AddDays(7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            StringAssert.Contains(await Html(nextWeek + "&type=anime"), "Watched Show");
            Assert.IsFalse((await Html(nextWeek + "&type=series")).Contains("Watched Show", StringComparison.Ordinal));
            var day = await Html("/Calendar?view=day&date=" + today.AddDays(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            StringAssert.Contains(day, "Coming Movie");
            StringAssert.Contains(day, "calendar__strip-dates");

            // The month of the movie shows it in its grid.
            using var grid = await host.SendAsync(HttpMethod.Get, "/Calendar?date=" + today.AddDays(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            var gridHtml = await grid.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, grid.StatusCode, gridHtml);
            StringAssert.Contains(gridHtml, "calendar__grid");
            StringAssert.Contains(gridHtml, "Coming Movie");

            using var tooLong = await host.SendAsync(HttpMethod.Get, "/api/v1/calendar?from=" + today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "&days=100");
            Assert.AreEqual(HttpStatusCode.BadRequest, tooLong.StatusCode);
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Calendar'");
            using var off = await host.SendAsync(HttpMethod.Get, "/api/v1/calendar?from=" + today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "&days=14");
            Assert.AreEqual(HttpStatusCode.Forbidden, off.StatusCode);
        }
    }
}
