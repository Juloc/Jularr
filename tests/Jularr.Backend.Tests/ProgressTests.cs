using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.User.Media.V1;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ProgressTests
    {
        [TestMethod]
        public async Task Progress_EpisodesSeasonsAndSeries_AreMarkedAndClearedForTheProfileOnly()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            var (show, unit) = await CreateSeriesAsync(host);

            // An episode alone.
            await ChangeAsync(host, show, ProgressAction.Watched, unit["1x2"]);
            Assert.AreEqual(1L, await CountAsync(host, "\"IsCompleted\" AND \"PositionMs\" = 0 AND \"ProgressPositionTypeId\" = 1 AND \"WorkUnitId\" = " + unit["1x2"]));

            // A season marks its aired episodes, keeps a begun one's start, leaves the unaired and the other seasons alone, and creates no row for the season itself.
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "WorkUnitId", "ProgressPositionTypeId", "PositionMs", "DurationMs")
                SELECT {ProfileSql}, {show}, {unit["1x1"]}, 1, 5000, 60000;
                INSERT INTO "Profiles" ("Id", "OwnerAccountId", "DisplayName", "UiLocaleId") SELECT 77, "OwnerAccountId", 'Other', "UiLocaleId" FROM "Profiles" LIMIT 1;
                INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") SELECT "OwnerAccountId", 77 FROM "Profiles" WHERE "Id" = 77;
                INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "WorkUnitId", "ProgressPositionTypeId", "PositionMs") VALUES (77, {show}, {unit["1x1"]}, 1, 7000);
                """);
            var completedAt = await host.ScalarAsync($"SELECT \"CompletedAt\" FROM \"MediaProgress\" WHERE \"WorkUnitId\" = {unit["1x2"]} AND \"ProfileId\" = {ProfileSql}");
            await ChangeAsync(host, show, ProgressAction.Watched, unit["season1"]);
            Assert.AreEqual(2L, await CountAsync(host, $"\"IsCompleted\" AND \"ProfileId\" = {ProfileSql}"));
            Assert.AreEqual(0L, await host.ScalarAsync($"SELECT \"PositionMs\" FROM \"MediaProgress\" WHERE \"WorkUnitId\" = {unit["1x1"]} AND \"ProfileId\" = {ProfileSql}"));
            Assert.AreEqual(completedAt, await host.ScalarAsync($"SELECT \"CompletedAt\" FROM \"MediaProgress\" WHERE \"WorkUnitId\" = {unit["1x2"]} AND \"ProfileId\" = {ProfileSql}"));
            Assert.AreEqual(0L, await CountAsync(host, $"\"WorkUnitId\" IN ({unit["1x3"]}, {unit["season1"]}, {unit["2x1"]})"));

            // The whole series reaches every aired episode, specials too, but no row for the title itself.
            await ChangeAsync(host, show, ProgressAction.Watched, null);
            Assert.AreEqual(4L, await CountAsync(host, $"\"IsCompleted\" AND \"ProfileId\" = {ProfileSql}"));
            Assert.AreEqual(0L, await CountAsync(host, "\"WorkUnitId\" IS NULL"));
            Assert.AreEqual(0L, await CountAsync(host, $"\"WorkUnitId\" = {unit["1x3"]}"));

            // Unwatched takes a season's marks away; everything of the series with the series.
            await ChangeAsync(host, show, ProgressAction.Unwatched, unit["season1"]);
            Assert.AreEqual(2L, await CountAsync(host, $"\"ProfileId\" = {ProfileSql}"));
            await ChangeAsync(host, show, ProgressAction.Unwatched, null);
            Assert.AreEqual(0L, await CountAsync(host, $"\"ProfileId\" = {ProfileSql}"));

            // Another profile's progress is never touched, and a title or unit it does not belong to is not found.
            Assert.AreEqual(1L, await CountAsync(host, "\"ProfileId\" = 77"));
            await ChangeAsync(host, show, ProgressAction.Watched, null);
            Assert.AreEqual(7000L, await host.ScalarAsync("SELECT \"PositionMs\" FROM \"MediaProgress\" WHERE \"ProfileId\" = 77"));
            using var foreignUnit = await host.SendAsync(HttpMethod.Put, "/api/v1/works/" + show + "/progress", new UpdateWorkProgressDataV1(ProgressAction.Watched, (long)(await host.ScalarAsync("SELECT max(\"Id\") + 1000 FROM \"WorkUnits\""))!));
            Assert.AreEqual(HttpStatusCode.NotFound, foreignUnit.StatusCode);
            using var unknown = await host.SendAsync(HttpMethod.Put, "/api/v1/works/987654/progress", new UpdateWorkProgressDataV1(ProgressAction.Watched));
            Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        [TestMethod]
        public async Task Progress_FilmsBooksAndGames_FollowTheirOwnRules()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);

            // A film has its own progress: it completes without a file, once, and clears.
            var film = await CreateWorkAsync(host, MediaType.Movie, "Film");
            await ChangeAsync(host, film, ProgressAction.Watched, null);
            await ChangeAsync(host, film, ProgressAction.Watched, null);
            Assert.AreEqual(1L, await CountAsync(host, "\"WorkUnitId\" IS NULL AND \"IsCompleted\" AND \"PositionMs\" = 0"));
            await ChangeAsync(host, film, ProgressAction.Unwatched, null);
            Assert.AreEqual(0L, await CountAsync(host, "TRUE"));

            // A book's volumes complete only where the profile has progress; a volume without one stays without, and unread removes it.
            var book = await CreateWorkAsync(host, MediaType.Book, "Book");
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "WorkUnits" ("WorkId", "WorkUnitTypeId", "Number") VALUES ({book}, 3, 1), ({book}, 3, 2);
                INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "WorkUnitId", "ProgressPositionTypeId", "Locator", "Progression")
                SELECT {ProfileSql}, {book}, unit."Id", 2, 'p12', 0.4 FROM "WorkUnits" AS unit WHERE unit."WorkId" = {book} AND unit."Number" = 1;
                """);
            await ChangeAsync(host, book, ProgressAction.Watched, null);
            Assert.AreEqual(1L, await CountAsync(host, "\"IsCompleted\" AND \"Progression\" = 1 AND \"Locator\" = 'p12'"));
            Assert.AreEqual(1L, await CountAsync(host, "TRUE"));
            await ChangeAsync(host, book, ProgressAction.Unwatched, null);
            Assert.AreEqual(0L, await CountAsync(host, "TRUE"));

            // The time played in a game is no watching.
            var game = await CreateWorkAsync(host, MediaType.Game, "Game");
            await host.Fixture.RunSqlAsync($"INSERT INTO \"MediaProgress\" (\"ProfileId\", \"WorkId\", \"ProgressPositionTypeId\", \"PositionMs\") VALUES ({ProfileSql}, {game}, 3, 90000)");
            await ChangeAsync(host, game, ProgressAction.Watched, null);
            await ChangeAsync(host, game, ProgressAction.Unwatched, null);
            Assert.AreEqual(90000L, await host.ScalarAsync("SELECT \"PositionMs\" FROM \"MediaProgress\""));
            Assert.AreEqual(false, await host.ScalarAsync("SELECT \"IsCompleted\" FROM \"MediaProgress\""));
        }

        [TestMethod]
        public async Task Details_ShowsWatchedStateAndChangesItFromItsRows()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            var (show, unit) = await CreateSeriesAsync(host);

            // Nothing watched: every episode offers to mark it; the season and the series offer their buttons.
            var html = await GetAsync(host, "/Details/" + show + "?season=1");
            StringAssert.Contains(html, "handler=Progress");
            StringAssert.Contains(html, "Mark season watched");
            StringAssert.Contains(html, "Mark series watched");
            Assert.IsFalse(html.Contains("Start season over", StringComparison.Ordinal));
            Assert.IsFalse(html.Contains("state-chip--watched", StringComparison.Ordinal));

            // One episode: its row shows Watched and offers the opposite and starting over; the season counts it.
            using var marked = await PostAsync(host, html, show, ProgressAction.Watched, unit["1x1"]);
            Assert.AreEqual(HttpStatusCode.Redirect, marked.StatusCode, await marked.Content.ReadAsStringAsync());
            html = await GetAsync(host, "/Details/" + show + "?season=1");
            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(html, "state-chip--watched").Count);
            StringAssert.Contains(html, "Mark unwatched");
            StringAssert.Contains(html, "1 watched");

            // Marking the season, then unmarking the series.
            using var season = await PostAsync(host, html, show, ProgressAction.Watched, unit["season1"]);
            Assert.AreEqual(HttpStatusCode.Redirect, season.StatusCode);
            html = await GetAsync(host, "/Details/" + show + "?season=1");
            Assert.AreEqual(2, System.Text.RegularExpressions.Regex.Matches(html, "state-chip--watched").Count);
            StringAssert.Contains(html, "2 watched");
            using var cleared = await PostAsync(host, html, show, ProgressAction.Unwatched, null);
            Assert.AreEqual(HttpStatusCode.Redirect, cleared.StatusCode);
            Assert.AreEqual(0L, await CountAsync(host, "TRUE"));

            // Starting over opens the player at the start; an address that is not the player's own is ignored.
            using var restarted = await PostAsync(host, html, show, ProgressAction.Unwatched, unit["1x1"], "/Player/5");
            Assert.AreEqual("/Player/5", restarted.Headers.Location!.OriginalString);
            using var foreign = await PostAsync(host, html, show, ProgressAction.Unwatched, unit["1x1"], "https://other.example/Player/5");
            Assert.AreEqual("/Details/" + show, foreign.Headers.Location!.OriginalString);
            using var elsewhere = await PostAsync(host, html, show, ProgressAction.Unwatched, unit["1x1"], "/Admin/Works");
            Assert.AreEqual("/Details/" + show, elsewhere.Headers.Location!.OriginalString);

            // The season list reads the same state.
            using var seasons = await host.SendAsync(HttpMethod.Get, "/api/v1/works/" + show + "/seasons");
            var list = (await seasons.Content.ReadFromJsonAsync<BaseDataList<WorkSeasonV1>>())!;
            Assert.AreEqual(unit["season1"], list.Single(item => item.SeasonNumber == 1).Id);
            Assert.AreEqual(0, list.Single(item => item.SeasonNumber == 1).WatchedCount);
        }

        private static string ProfileSql => "(SELECT \"ActiveProfileId\" FROM \"AccountSessions\" WHERE \"ActiveProfileId\" IS NOT NULL ORDER BY \"CreatedAt\" DESC LIMIT 1)";

        private static async Task<long> CreateWorkAsync(Host host, MediaType type, string title)
        {
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works", new CreateWorkDataV1(type, title));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            return (await created.Content.ReadFromJsonAsync<Jularr.Backend.Data.Admin.Media.V1.WorkDetailsV1>())!.Id;
        }

        // A special, season one with two aired episodes and one to come, and a second season.
        private static async Task<(long WorkId, Dictionary<string, long> Units)> CreateSeriesAsync(Host host)
        {
            var show = await CreateWorkAsync(host, MediaType.Series, "Show");
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "WorkUnits" ("WorkId", "WorkUnitTypeId", "Number") VALUES ({show}, 1, 0), ({show}, 1, 1), ({show}, 1, 2);
                INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number", "ReleasedOn")
                SELECT {show}, season."Id", 2, episode."Number", episode."ReleasedOn"
                FROM (VALUES (0, 1, current_date - 100), (1, 1, current_date - 30), (1, 2, current_date - 20), (1, 3, current_date + 20), (2, 1, current_date - 5))
                    AS episode ("Season", "Number", "ReleasedOn")
                    JOIN "WorkUnits" AS season ON season."WorkId" = {show} AND season."WorkUnitTypeId" = 1 AND season."Number" = episode."Season";
                """);
            var units = new Dictionary<string, long>();
            foreach (var season in new[] { 0, 1, 2 })
            {
                units["season" + season] = (long)(await host.ScalarAsync($"SELECT \"Id\" FROM \"WorkUnits\" WHERE \"WorkId\" = {show} AND \"WorkUnitTypeId\" = 1 AND \"Number\" = {season}"))!;
            }

            foreach (var (season, number) in new[] { (0, 1), (1, 1), (1, 2), (1, 3), (2, 1) })
            {
                units[season + "x" + number] = (long)(await host.ScalarAsync($"""
                    SELECT episode."Id" FROM "WorkUnits" AS episode JOIN "WorkUnits" AS season ON season."Id" = episode."ParentWorkUnitId"
                    WHERE episode."WorkId" = {show} AND season."Number" = {season} AND episode."Number" = {number}
                    """))!;
            }

            return (show, units);
        }

        private static async Task ChangeAsync(Host host, long workId, ProgressAction action, long? unitId)
        {
            using var changed = await host.SendAsync(HttpMethod.Put, "/api/v1/works/" + workId + "/progress", new UpdateWorkProgressDataV1(action, unitId));
            Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode, await changed.Content.ReadAsStringAsync());
        }

        private static async Task<long> CountAsync(Host host, string where) => (long)(await host.ScalarAsync("SELECT count(*) FROM \"MediaProgress\" WHERE " + where))!;

        private static async Task<string> GetAsync(Host host, string path)
        {
            using var page = await host.SendAsync(HttpMethod.Get, path);
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            return html;
        }

        private static Task<HttpResponseMessage> PostAsync(Host host, string html, long workId, ProgressAction action, long? unitId, string? playHref = null)
        {
            var form = new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = LibraryTests.Token(html),
                ["action"] = ((byte)action).ToString(),
                ["returnUrl"] = "/Details/" + workId
            };
            if (unitId is { } unit) form["unitId"] = unit.ToString();
            if (playHref is not null) form["playHref"] = playHref;
            return host.SendAsync(HttpMethod.Post, "/Details/" + workId + "?handler=Progress", form);
        }
    }
}
