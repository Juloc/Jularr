using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Enums.Provider;
using Jularr.Backend.Data.Logic.AniList;
using Jularr.Backend.Data.System.Provider.V1;
using Jularr.Backend.Data.User.AniList.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Logic.AniList;
using Jularr.Backend.Service.System.Provider.V1;
using Jularr.Integrations.Logic.AniList;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class AniListSyncTests
    {
        private const string Route = "/api/v1/anilist";

        [TestMethod]
        public void FinishedInARow_StopsAtTheFirstGap()
        {
            Assert.AreEqual(3, AniListSyncRules.FinishedInARow([Unit(1, true), Unit(2, true), Unit(3, true), Unit(5, true)]));
            Assert.AreEqual(0, AniListSyncRules.FinishedInARow([Unit(2, true), Unit(3, true)]));
            Assert.AreEqual(1, AniListSyncRules.FinishedInARow([Unit(1, true), Unit(2, false), Unit(3, true)]));
        }

        [TestMethod]
        public void PlanImport_TakesOverWhatAniListCountsAsDone_AndNeverMore()
        {
            var units = new[] { Unit(1, true), Unit(2, false), Unit(3, false), Unit(4, false) };
            int[] Planned(AniListEntryData entry) => [.. AniListSyncRules.PlanImport(entry, units).Select(unit => unit.Number)];

            // Progress covers the units up to it, a completed entry all of them, a planned one none; a finished unit is not touched again.
            CollectionAssert.AreEqual(new[] { 2 }, Planned(Entry("CURRENT", 2)));
            CollectionAssert.AreEqual(new[] { 2, 3, 4 }, Planned(Entry("COMPLETED", 0)));
            CollectionAssert.AreEqual(Array.Empty<int>(), Planned(Entry("PLANNING", 0)));
            CollectionAssert.AreEqual(new[] { 2, 3 }, Planned(Entry("DROPPED", 3, manga: true)));
        }

        [TestMethod]
        public void PlanPush_SendsOnlyFurtherProgressMadeSinceSwitchedOn_AndNeverTheLastOne()
        {
            var enabled = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
            var after = enabled.AddMinutes(5);
            var finished = new[] { Unit(1, true, enabled.AddDays(-1)), Unit(2, true, after), Unit(3, true, after) };

            // Further than AniList, made after sending was switched on.
            var push = AniListSyncRules.PlanPush(Entry("CURRENT", 1), finished, enabled)!;
            Assert.AreEqual((3, false, false), (push.Progress, push.StartsTracking, push.IsVolume));

            // A planned entry starts with its first progress.
            Assert.IsTrue(AniListSyncRules.PlanPush(Entry("PLANNING", 0), finished, enabled)!.StartsTracking);

            // AniList is level or ahead; the progress is older than the switch; the entry is no tracked one; the last episode closes the entry itself.
            Assert.IsNull(AniListSyncRules.PlanPush(Entry("CURRENT", 3), finished, enabled));
            Assert.IsNull(AniListSyncRules.PlanPush(Entry("CURRENT", 5), finished, enabled));
            Assert.IsNull(AniListSyncRules.PlanPush(Entry("CURRENT", 1), [Unit(1, true, enabled.AddDays(-1)), Unit(2, true, enabled.AddDays(-1))], enabled));
            Assert.IsNull(AniListSyncRules.PlanPush(Entry("DROPPED", 1), finished, enabled));
            Assert.IsNull(AniListSyncRules.PlanPush(Entry("CURRENT", 1, total: 3), finished, enabled));
            Assert.IsNotNull(AniListSyncRules.PlanPush(Entry("CURRENT", 1, total: 4), finished, enabled));

            // Volumes are counted by their own progress and total.
            var volumes = AniListSyncRules.PlanPush(Entry("CURRENT", 0, manga: true, volumesProgress: 0), [Unit(1, true, after, volume: true)], enabled)!;
            Assert.IsTrue(volumes.IsVolume);
        }

        [TestMethod]
        public void RetryDelay_DoublesFromAMinuteUpToSixHours()
        {
            CollectionAssert.AreEqual(new[] { 1, 2, 4, 8 }, new[] { 1, 2, 3, 4 }.Select(attempt => (int)AniListSyncRules.RetryDelay(attempt).TotalMinutes).ToArray());
            Assert.AreEqual(TimeSpan.FromHours(6), AniListSyncRules.RetryDelay(40));
        }

        [TestMethod]
        public void StateOf_ComparesWhatIsFinishedHereWithWhatAniListCounts()
        {
            Assert.AreEqual(AniListSyncStateType.Synchronized, AniListSyncRules.StateOf("CURRENT", 4, 12, 4));
            Assert.AreEqual(AniListSyncStateType.LocalAhead, AniListSyncRules.StateOf("CURRENT", 4, 12, 6));
            Assert.AreEqual(AniListSyncStateType.AniListAhead, AniListSyncRules.StateOf("CURRENT", 8, 12, 4));

            // A completed entry names everything, whatever its progress says; without a total it counts what it has.
            Assert.AreEqual(AniListSyncStateType.AniListAhead, AniListSyncRules.StateOf("COMPLETED", 0, 12, 4));
            Assert.AreEqual(AniListSyncStateType.Synchronized, AniListSyncRules.StateOf("COMPLETED", 0, 12, 12));
            Assert.AreEqual(AniListSyncStateType.Synchronized, AniListSyncRules.StateOf("COMPLETED", 7, null, 7));
        }

        [TestMethod]
        public void ProblemOf_NamesOnlyWhatTheNextPassDoesNotSettle()
        {
            const AniListSyncStateType ahead = AniListSyncStateType.LocalAhead;
            Assert.IsNull(AniListSyncRules.ProblemOf(AniListSyncStateType.Synchronized, false, true, "anilist_unavailable", "x"));
            Assert.AreEqual(AniListSyncProblemType.SendingOff, AniListSyncRules.ProblemOf(ahead, false, false, null, null));
            Assert.IsNull(AniListSyncRules.ProblemOf(ahead, true, false, null, null));
            Assert.AreEqual(AniListSyncProblemType.SendFailed, AniListSyncRules.ProblemOf(ahead, true, false, null, "anilist_unavailable"));
            Assert.AreEqual(AniListSyncProblemType.SignInNeeded, AniListSyncRules.ProblemOf(ahead, true, true, null, null));
            Assert.AreEqual(AniListSyncProblemType.SignInNeeded, AniListSyncRules.ProblemOf(AniListSyncStateType.AniListAhead, true, false, "anilist_authentication_failed", null));
            Assert.AreEqual(AniListSyncProblemType.Unreachable, AniListSyncRules.ProblemOf(AniListSyncStateType.AniListAhead, true, false, "anilist_unavailable", null));
            Assert.IsNull(AniListSyncRules.ProblemOf(AniListSyncStateType.AniListAhead, true, false, null, null));
        }

        [TestMethod]
        public async Task Lists_ShowTheProfilesEntriesWithTheirTitles_AndTheWorkStandsAgainstItsEntry()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var anilist = new FakeAniList();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                services: services => services.AddHttpClient<AniListClient>().ConfigurePrimaryHttpMessageHandler(() => anilist));
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle") VALUES (20, 2, 1, 'Example Anime'), (30, 1, 1, 'Example Movie'), (40, 3, 2, 'Example Manga'), (50, 2, 1, 'Unlisted Anime');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (30, 'anilist', '5002'), (40, 'anilist', '6001');
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (21, 20, 1, 1), (41, 40, 3, 1), (42, 40, 3, 2), (43, 40, 3, 3);
                INSERT INTO "WorkUnits" ("Id", "WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number")
                    VALUES (22, 20, 21, 2, 1), (23, 20, 21, 2, 2), (24, 20, 21, 2, 3), (25, 20, 21, 2, 4), (26, 20, 21, 2, 5), (27, 20, 21, 2, 6);
                INSERT INTO "WorkUnitExternalIdentities" ("WorkUnitId", "Namespace", "Value", "EpisodeOffset") VALUES (21, 'anilist', '5001', 0);
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" IN ('ProgressSync', 'AniList', 'Library');
                """);

            // Nothing is connected: no figures, no lists.
            var providerId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Providers\" WHERE \"Key\" = 'anilist'"))!;
            using var configured = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/" + providerId + "/secret", new SaveProviderSecretDataV1(FakeAniList.Secret, FakeAniList.ClientId));
            Assert.AreEqual(HttpStatusCode.OK, configured.StatusCode, await configured.Content.ReadAsStringAsync());
            var before = await ReadWorkAsync(host, 20);
            Assert.AreEqual((false, false), (before.IsConnected, before.IsOnList));
            Assert.AreEqual(0, (await ReadListsAsync(host)).Count);

            using var connected = await host.SendAsync(HttpMethod.Post, Route, new ConnectAniListDataV1(FakeAniList.Code));
            Assert.AreEqual(HttpStatusCode.OK, connected.StatusCode, await connected.Content.ReadAsStringAsync());
            await using var scope = host.Services.CreateAsyncScope();
            var sync = scope.ServiceProvider.GetRequiredService<SyncAniListV1>();
            Assert.IsTrue((await sync.ExecuteAsync<UpdateResult>(new AniListSyncParametersV1(), default)).Success);

            // The lists count by status; an entry of a library title carries its Work, the others their AniList cover.
            var counts = (await ReadListsAsync(host)).ToDictionary(item => item.Status, item => item.Count);
            Assert.AreEqual((3, 1), (counts["CURRENT"], counts["COMPLETED"]));
            var current = await ReadEntriesAsync(host, "CURRENT");
            CollectionAssert.AreEquivalent(new[] { true, true, false }, current.Select(entry => entry.IsInLibrary).ToArray());
            CollectionAssert.IsSubsetOf(new long?[] { 20, 40 }, current.Select(entry => entry.WorkId).ToArray());

            // An entry of no library title is a catalog Work all the same, so it has a page of its own.
            var pending = current.Single(entry => !entry.IsInLibrary);
            Assert.AreEqual((7777, "Title 7777", "https://s4.anilist.co/cover-7777.jpg", 3, 24), (pending.MediaId, pending.Title, pending.CoverUrl, pending.Progress, pending.Total));
            Assert.IsNotNull(pending.WorkId);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"WorkExternalIdentities\" AS identity JOIN \"Works\" AS work ON work.\"Id\" = identity.\"WorkId\" WHERE identity.\"Namespace\" = 'anilist' AND identity.\"Value\" = '7777' AND work.\"MediaTypeId\" = 2 AND work.\"MediaSubtypeId\" = 1"));
            var catalogOnly = await ReadWorkAsync(host, pending.WorkId!.Value);
            Assert.AreEqual((true, 3, null, null), (catalogOnly.IsOnList, catalogOnly.Progress, catalogOnly.SyncStateId, catalogOnly.SyncProblemId));
            Assert.AreEqual(0, (await ReadEntriesAsync(host, "DROPPED")).Count);
            Assert.AreEqual(0, (await ReadEntriesAsync(host, "NOT-A-STATUS")).Count);

            // Taken over from AniList, the title agrees with it; finished further here it is ahead, further there it is behind.
            var agreed = await ReadWorkAsync(host, 20);
            Assert.AreEqual((true, true, "CURRENT", false, 2, 12, 2, AniListSyncStateType.Synchronized), (agreed.IsConnected, agreed.IsOnList, agreed.Status, agreed.IsVolume, agreed.Progress, agreed.Total, agreed.LocalProgress, agreed.SyncStateId));
            // The pages: a card per status with entries in Favorites, the entries of one, and the line under the title's progress.
            using var strip = await host.SendAsync(HttpMethod.Get, "/Favorites");
            var stripHtml = await strip.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, strip.StatusCode, stripHtml);
            StringAssert.Contains(stripHtml, "AniList: Current");
            StringAssert.Contains(stripHtml, "AniList: Completed");
            Assert.IsFalse(stripHtml.Contains("AniList: Dropped", StringComparison.Ordinal));
            using var shown = await host.SendAsync(HttpMethod.Get, "/Favorites?anilist=current");
            var shownHtml = await shown.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, shown.StatusCode, shownHtml);
            StringAssert.Contains(shownHtml, "Title 7777");
            StringAssert.Contains(shownHtml, "Not in library");
            StringAssert.Contains(shownHtml, "/Details/20");
            StringAssert.Contains(shownHtml, "/Details/" + pending.WorkId);
            using var catalogPage = await host.SendAsync(HttpMethod.Get, "/Details/" + pending.WorkId);
            var catalogHtml = await catalogPage.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, catalogPage.StatusCode, catalogHtml);
            StringAssert.Contains(catalogHtml, "AniList 3 / 24");
            Assert.IsFalse(catalogHtml.Contains("In sync", StringComparison.Ordinal));
            using var details = await host.SendAsync(HttpMethod.Get, "/Details/20");
            var detailsHtml = await details.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, details.StatusCode, detailsHtml);
            StringAssert.Contains(detailsHtml, "AniList 2 / 12");
            StringAssert.Contains(detailsHtml, "In sync");
            using var unlistedPage = await host.SendAsync(HttpMethod.Get, "/Details/50");
            StringAssert.Contains(await unlistedPage.Content.ReadAsStringAsync(), "Not on your AniList");

            await WatchAsync(host, 20, 24, 25);
            var ahead = await ReadWorkAsync(host, 20);
            Assert.AreEqual((2, 4, AniListSyncStateType.LocalAhead), (ahead.Progress, ahead.LocalProgress, ahead.SyncStateId));

            // With sending off the difference stays, and the page says why; a failed write or an account that fails says so too.
            Assert.AreEqual(AniListSyncProblemType.SendingOff, ahead.SyncProblemId);
            using var reason = await host.SendAsync(HttpMethod.Get, "/Details/20");
            StringAssert.Contains(await reason.Content.ReadAsStringAsync(), "Sending is off");
            await host.Fixture.RunSqlAsync("UPDATE \"AniListConnections\" SET \"PushEnabledAt\" = now()");
            Assert.IsNull((await ReadWorkAsync(host, 20)).SyncProblemId, "The next pass sends it.");
            await host.Fixture.RunSqlAsync("UPDATE \"AniListEntries\" SET \"LastError\" = 'anilist_unavailable' WHERE \"MediaId\" = 5001");
            Assert.AreEqual(AniListSyncProblemType.SendFailed, (await ReadWorkAsync(host, 20)).SyncProblemId);
            await host.Fixture.RunSqlAsync("UPDATE \"AniListConnections\" SET \"FailureCode\" = 'anilist_unavailable'");
            Assert.AreEqual(AniListSyncProblemType.Unreachable, (await ReadWorkAsync(host, 20)).SyncProblemId);
            await host.Fixture.RunSqlAsync("UPDATE \"AniListConnections\" SET \"FailureCode\" = NULL");
            await host.Fixture.RunSqlAsync("UPDATE \"AniListEntries\" SET \"Progress\" = 8 WHERE \"MediaId\" = 5001");
            var behind = await ReadWorkAsync(host, 20);
            Assert.AreEqual((AniListSyncStateType.AniListAhead, (AniListSyncProblemType?)null), (behind.SyncStateId, behind.SyncProblemId), "The next pass takes it over.");

            // A film that AniList completed and a book counted in volumes; a title that is not on the list says so.
            var film = await ReadWorkAsync(host, 30);
            Assert.AreEqual(("COMPLETED", AniListSyncStateType.Synchronized), (film.Status, film.SyncStateId));
            var manga = await ReadWorkAsync(host, 40);
            Assert.AreEqual((true, 1, 1), (manga.IsVolume, manga.Progress, manga.LocalProgress));
            var unlisted = await ReadWorkAsync(host, 50);
            Assert.AreEqual((true, false, null), (unlisted.IsConnected, unlisted.IsOnList, unlisted.SyncStateId));

            // Both are the profile's to see only while the module is on.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'AniList'");
            using var hidden = await host.SendAsync(HttpMethod.Get, Route + "/lists");
            Assert.AreEqual(HttpStatusCode.Forbidden, hidden.StatusCode);
            using var hiddenWork = await host.SendAsync(HttpMethod.Get, "/api/v1/works/20/anilist");
            Assert.AreEqual(HttpStatusCode.Forbidden, hiddenWork.StatusCode);
        }

        [TestMethod]
        public async Task Account_ConnectImportPushRetryAndDisconnect_FollowTheOwnersSetupAndNeverShowTheToken()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var anilist = new FakeAniList();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                services: services => services.AddHttpClient<AniListClient>().ConfigurePrimaryHttpMessageHandler(() => anilist));
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle") VALUES (20, 2, 1, 'Example Anime'), (30, 1, 1, 'Example Movie'), (40, 3, 2, 'Example Manga');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (30, 'anilist', '5002'), (40, 'anilist', '6001');
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (21, 20, 1, 1), (41, 40, 3, 1), (42, 40, 3, 2), (43, 40, 3, 3);
                INSERT INTO "WorkUnits" ("Id", "WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number")
                    VALUES (22, 20, 21, 2, 1), (23, 20, 21, 2, 2), (24, 20, 21, 2, 3), (25, 20, 21, 2, 4), (26, 20, 21, 2, 5), (27, 20, 21, 2, 6);
                INSERT INTO "WorkUnitExternalIdentities" ("WorkUnitId", "Namespace", "Value", "EpisodeOffset") VALUES (21, 'anilist', '5001', 0);
                """);

            // Off by default, and a profile needs the Owner's application.
            using var off = await host.SendAsync(HttpMethod.Get, Route);
            Assert.AreEqual(HttpStatusCode.Forbidden, off.StatusCode);

            // The account part also needs the progress sync: AniList alone offers no account.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'AniList'");
            using var withoutSync = await host.SendAsync(HttpMethod.Get, Route);
            Assert.AreEqual(HttpStatusCode.Forbidden, withoutSync.StatusCode);
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" IN ('ProgressSync', 'AniList', 'Library')");
            var unset = await ReadAsync(host);
            Assert.IsFalse(unset.IsConfigured);
            Assert.IsNull(unset.AuthorizationUrl);
            using var early = await host.SendAsync(HttpMethod.Post, Route, new ConnectAniListDataV1("good-code"));
            StringAssert.Contains(await early.Content.ReadAsStringAsync(), "anilist_not_configured");

            // The Owner enters client id and secret on the providers page; neither comes back.
            var providerId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Providers\" WHERE \"Key\" = 'anilist'"))!;
            using var configured = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/" + providerId + "/secret", new SaveProviderSecretDataV1(FakeAniList.Secret, FakeAniList.ClientId));
            Assert.AreEqual(HttpStatusCode.OK, configured.StatusCode, await configured.Content.ReadAsStringAsync());
            var ready = await ReadAsync(host);
            Assert.IsTrue(ready.IsConfigured);
            StringAssert.Contains(ready.AuthorizationUrl, "client_id=" + FakeAniList.ClientId);
            StringAssert.Contains(ready.AuthorizationUrl, "response_type=code");
            Assert.IsFalse(ready.AuthorizationUrl!.Contains(FakeAniList.Secret, StringComparison.Ordinal));

            // The profile page offers the sign-in; the providers page never shows the secret.
            using var offered = await host.SendAsync(HttpMethod.Get, "/Settings");
            var offeredHtml = await offered.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, offered.StatusCode, offeredHtml);
            StringAssert.Contains(offeredHtml, "Open AniList");
            StringAssert.Contains(offeredHtml, "Connect AniList");
            using var providers = await host.SendAsync(HttpMethod.Get, "/Admin/Providers");
            Assert.IsFalse((await providers.Content.ReadAsStringAsync()).Contains(FakeAniList.Secret, StringComparison.Ordinal));

            // A code AniList refuses connects nothing; the right one connects the profile's own account.
            using var refused = await host.SendAsync(HttpMethod.Post, Route, new ConnectAniListDataV1("wrong-code"));
            StringAssert.Contains(await refused.Content.ReadAsStringAsync(), "anilist_authentication_failed");
            using var connected = await host.SendAsync(HttpMethod.Post, Route, new ConnectAniListDataV1(FakeAniList.Code));
            Assert.AreEqual(HttpStatusCode.OK, connected.StatusCode, await connected.Content.ReadAsStringAsync());
            var account = await ReadAsync(host);
            Assert.AreEqual((true, "Fan", false), (account.IsConnected, account.ViewerName, account.IsPushEnabled));
            using var raw = await host.SendAsync(HttpMethod.Get, Route);
            var body = await raw.Content.ReadAsStringAsync();
            Assert.IsFalse(body.Contains(FakeAniList.Token, StringComparison.Ordinal) || body.Contains(FakeAniList.Secret, StringComparison.Ordinal));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AniListConnections\" WHERE \"ProfileId\" = 101 AND \"ProtectedToken\" <> convert_to('" + FakeAniList.Token + "', 'UTF8')"));
            using var shown = await host.SendAsync(HttpMethod.Get, "/Settings");
            var shownHtml = await shown.Content.ReadAsStringAsync();
            StringAssert.Contains(shownHtml, "Connected as Fan");
            Assert.IsFalse(shownHtml.Contains(FakeAniList.Token, StringComparison.Ordinal) || shownHtml.Contains(FakeAniList.Secret, StringComparison.Ordinal));

            await using var scope = host.Services.CreateAsyncScope();
            var sync = scope.ServiceProvider.GetRequiredService<SyncAniListV1>();
            try
            {
                // The first pass reads the list and takes over what is watched and read there; unknown titles wait as pending matches and nothing is sent yet.
                Assert.IsTrue((await sync.ExecuteAsync<UpdateResult>(new AniListSyncParametersV1(), default)).Success);
                Assert.AreEqual(4L, await host.ScalarAsync("SELECT count(*) FROM \"AniListEntries\" WHERE \"ProfileId\" = 101"));
                Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"MediaProgress\" WHERE \"WorkId\" = 20 AND \"IsCompleted\""));
                Assert.AreEqual(true, await host.ScalarAsync("SELECT \"IsCompleted\" FROM \"MediaProgress\" WHERE \"WorkId\" = 30 AND \"WorkUnitId\" IS NULL"));
                Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"MediaProgress\" WHERE \"WorkId\" = 40 AND \"WorkUnitId\" = 41 AND \"IsCompleted\""));
                Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"MediaProgress\" WHERE \"WorkId\" NOT IN (20, 30, 40)"));
                Assert.AreEqual(0, anilist.Mutations.Count);
                var imported = await ReadAsync(host);
                Assert.AreEqual((4, 3), (imported.EntryCount, imported.MatchedCount));
                Assert.IsNotNull(imported.SyncedAt);

                // What a person took back is not imported again while AniList did not change.
                await host.Fixture.RunSqlAsync("DELETE FROM \"MediaProgress\" WHERE \"WorkUnitId\" = 23");
                Assert.IsTrue((await sync.ExecuteAsync<UpdateResult>(new AniListSyncParametersV1(), default)).Success);
                Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"MediaProgress\" WHERE \"WorkUnitId\" = 23"));

                // Sending is the profile's choice and covers progress made after it.
                using var enabled = await host.SendAsync(HttpMethod.Put, Route + "/push", new UpdateAniListPushDataV1(true));
                Assert.AreEqual(HttpStatusCode.OK, enabled.StatusCode, await enabled.Content.ReadAsStringAsync());
                await WatchAsync(host, 20, 23, 24, 25, 27);
                await host.Fixture.RunSqlAsync("INSERT INTO \"MediaProgress\" (\"ProfileId\", \"WorkId\", \"WorkUnitId\", \"ProgressPositionTypeId\", \"Locator\", \"Progression\", \"IsCompleted\", \"CompletedAt\") VALUES (101, 40, 42, 2, 's0/b9', 1, TRUE, now())");
                Assert.IsTrue((await sync.ExecuteAsync<UpdateResult>(new AniListSyncParametersV1(), default)).Success);
                var episodes = anilist.Mutations.Single(mutation => mutation.Id == 900);
                Assert.AreEqual(4, episodes.Progress, "Episode 6 sits behind a gap in the row; the finished ones before it count.");
                // What is written is the arguments of the mutation; the selection after it only names what AniList answers with.
                var written = episodes.Query[(episodes.Query.IndexOf("SaveMediaListEntry(", StringComparison.Ordinal))..].Split(')')[0];
                Assert.IsFalse(written.Contains("progressVolumes", StringComparison.Ordinal) || written.Contains("score", StringComparison.Ordinal) || written.Contains("status", StringComparison.Ordinal), written);
                var volumes = anilist.Mutations.Single(mutation => mutation.Id == 903);
                Assert.IsTrue(volumes.Query.Contains("progressVolumes: $progress", StringComparison.Ordinal));
                Assert.AreEqual(2, volumes.Progress);
                Assert.AreEqual(2, anilist.Mutations.Count);
                Assert.AreEqual(4, (int)(await host.ScalarAsync("SELECT \"Progress\" FROM \"AniListEntries\" WHERE \"MediaId\" = 5001"))!);

                // Nothing is sent twice; a limit ends the pass quietly and holds every call until it is over.
                Assert.IsTrue((await sync.ExecuteAsync<UpdateResult>(new AniListSyncParametersV1(), default)).Success);
                Assert.AreEqual(2, anilist.Mutations.Count);
                await WatchAsync(host, 20, 26);
                anilist.Mode = FakeAniListMode.RateLimited;
                Assert.IsFalse((await sync.ExecuteAsync<UpdateResult>(new AniListSyncParametersV1(), default)).Success);
                Assert.IsNotNull(AniListClient.BlockedUntil);
                var held = anilist.Calls;
                Assert.IsFalse((await sync.ExecuteAsync<UpdateResult>(new AniListSyncParametersV1(), default)).Success);
                Assert.AreEqual(held, anilist.Calls);
                ResetRateLimit();

                // A write AniList refuses waits and is tried again after a growing wait; nothing is sent while it waits, the rest of the pass goes on.
                anilist.Mode = FakeAniListMode.WritesFail;
                Assert.IsTrue((await sync.ExecuteAsync<UpdateResult>(new AniListSyncParametersV1(), default)).Success);
                Assert.AreEqual(1, anilist.FailedWrites);
                Assert.AreEqual((short)1, await host.ScalarAsync("SELECT \"AttemptCount\" FROM \"AniListEntries\" WHERE \"MediaId\" = 5001"));
                Assert.AreEqual("anilist_unavailable", await host.ScalarAsync("SELECT \"LastError\" FROM \"AniListEntries\" WHERE \"MediaId\" = 5001"));
                Assert.AreEqual(true, await host.ScalarAsync("SELECT \"NextAttemptAt\" > now() FROM \"AniListEntries\" WHERE \"MediaId\" = 5001"));
                anilist.Mode = FakeAniListMode.Working;
                Assert.IsTrue((await sync.ExecuteAsync<UpdateResult>(new AniListSyncParametersV1(), default)).Success);
                Assert.AreEqual(1, anilist.FailedWrites);
                Assert.AreEqual(2, anilist.Mutations.Count);
                await host.Fixture.RunSqlAsync("UPDATE \"AniListEntries\" SET \"NextAttemptAt\" = now() - interval '1 minute' WHERE \"MediaId\" = 5001");
                Assert.IsTrue((await sync.ExecuteAsync<UpdateResult>(new AniListSyncParametersV1(), default)).Success);
                Assert.AreEqual(6, anilist.Mutations.Last().Progress);
                Assert.AreEqual((short)0, await host.ScalarAsync("SELECT \"AttemptCount\" FROM \"AniListEntries\" WHERE \"MediaId\" = 5001"));

                // An unreachable AniList fails the pass after the other profiles were served, and says so on the profile.
                anilist.Mode = FakeAniListMode.Down;
                var failure = await Assert.ThrowsExactlyAsync<ServiceUnavailableException>(() => sync.ExecuteAsync<UpdateResult>(new AniListSyncParametersV1(), default));
                Assert.AreEqual("anilist_unavailable", failure.Code);
                Assert.AreEqual("anilist_unavailable", (await ReadAsync(host)).FailureCode);
                anilist.Mode = FakeAniListMode.Working;

                // A token AniList no longer accepts waits for the profile to connect again.
                anilist.Mode = FakeAniListMode.Rejected;
                Assert.IsTrue((await sync.ExecuteAsync<UpdateResult>(new AniListSyncParametersV1(), default)).Success);
                Assert.AreEqual("anilist_authentication_failed", (await ReadAsync(host)).FailureCode);
                anilist.Mode = FakeAniListMode.Working;
                using var again = await host.SendAsync(HttpMethod.Post, Route, new ConnectAniListDataV1(FakeAniList.Code));
                Assert.AreEqual(HttpStatusCode.OK, again.StatusCode, await again.Content.ReadAsStringAsync());
                var reconnected = await ReadAsync(host);
                Assert.IsNull(reconnected.FailureCode);
                Assert.IsTrue(reconnected.IsPushEnabled, "The same AniList user keeps its choice.");

                // Another AniList user starts over without sending; the list of the first is gone.
                anilist.ViewerId = 77;
                using var other = await host.SendAsync(HttpMethod.Post, Route, new ConnectAniListDataV1(FakeAniList.Code));
                Assert.AreEqual(HttpStatusCode.OK, other.StatusCode, await other.Content.ReadAsStringAsync());
                Assert.IsFalse((await ReadAsync(host)).IsPushEnabled);
                Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"AniListEntries\""));

                // Disconnecting removes the connection and the list; progress in Jularr stays.
                using var disconnected = await host.SendAsync(HttpMethod.Delete, Route);
                Assert.AreEqual(HttpStatusCode.OK, disconnected.StatusCode, await disconnected.Content.ReadAsStringAsync());
                Assert.IsFalse((await ReadAsync(host)).IsConnected);
                Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"AniListConnections\""));
                Assert.IsTrue((long)(await host.ScalarAsync("SELECT count(*) FROM \"MediaProgress\" WHERE \"WorkId\" = 20 AND \"IsCompleted\""))! >= 5);
                Assert.IsFalse((await sync.ExecuteAsync<UpdateResult>(new AniListSyncParametersV1(), default)).Success, "Nothing is connected, nothing to do.");

                // Off, nothing is called and nothing is shown.
                await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'AniList'");
                var calls = anilist.Calls;
                await Assert.ThrowsExactlyAsync<SecurityException>(() => sync.ExecuteAsync<UpdateResult>(new AniListSyncParametersV1(), default));
                Assert.AreEqual(calls, anilist.Calls);
                using var hidden = await host.SendAsync(HttpMethod.Get, Route);
                Assert.AreEqual(HttpStatusCode.Forbidden, hidden.StatusCode);
            }
            finally
            {
                ResetRateLimit();
            }
        }

        private static async Task<AniListConnectionV1> ReadAsync(Host host)
        {
            using var response = await host.SendAsync(HttpMethod.Get, Route);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<AniListConnectionV1>())!;
        }

        private static async Task<WorkAniListV1> ReadWorkAsync(Host host, long workId)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/works/" + workId + "/anilist");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<WorkAniListV1>())!;
        }

        private static async Task<BaseDataList<AniListStatusCountV1>> ReadListsAsync(Host host)
        {
            using var response = await host.SendAsync(HttpMethod.Get, Route + "/lists");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<BaseDataList<AniListStatusCountV1>>())!;
        }

        private static async Task<IReadOnlyList<AniListEntryV1>> ReadEntriesAsync(Host host, string status)
        {
            using var response = await host.SendAsync(HttpMethod.Get, Route + "/lists/" + status);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<PageResult<AniListEntryV1>>())!.Items;
        }

        private static async Task WatchAsync(Host host, long workId, params long[] unitIds)
        {
            foreach (var unitId in unitIds)
            {
                using var response = await host.SendAsync(HttpMethod.Put, "/api/v1/works/" + workId + "/progress", new UpdateWorkProgressDataV1(ProgressAction.Watched, unitId));
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            }
        }

        private static void ResetRateLimit() =>
            typeof(AniListClient).GetField("_blockedUntilTicks", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, 0L);

        private static AniListUnitData Unit(int number, bool completed, DateTime? at = null, bool volume = false) => new(1, 1, number, number, completed, completed ? at ?? DateTime.UtcNow : null, volume);

        private static AniListEntryData Entry(string status, int progress, bool manga = false, int? total = null, int? volumesProgress = null) =>
            new(1, 1, manga, status, manga ? 0 : progress, manga ? volumesProgress ?? progress : 0, manga ? null : total, manga ? total : null);

        private enum FakeAniListMode
        {
            Working,
            Rejected,
            Down,
            RateLimited,
            WritesFail
        }

        // AniList's sign-in and GraphQL for one person: an anime list, a manga list and the progress writes it receives.
        private sealed class FakeAniList : HttpMessageHandler
        {
            public const string ClientId = "4242";
            public const string Secret = "fake-application-secret";
            public const string Code = "good-code";
            public const string Token = "fake-access-token-0123456789";

            private long _stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600;
            private readonly Dictionary<int, (int Progress, int Volumes)> _state = new() { [900] = (2, 0), [901] = (1, 0), [902] = (3, 0), [903] = (10, 1) };

            public FakeAniListMode Mode { get; set; }
            public int ViewerId { get; set; } = 31;
            public int Calls { get; private set; }
            public int FailedWrites { get; private set; }
            public List<(int Id, int Progress, string Query)> Mutations { get; } = [];

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var root = body.RootElement;
                if (request.RequestUri!.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal))
                {
                    var valid = root.GetProperty("client_id").GetString() == ClientId && root.GetProperty("client_secret").GetString() == Secret && root.GetProperty("code").GetString() == Code
                        && root.GetProperty("grant_type").GetString() == "authorization_code" && root.GetProperty("redirect_uri").GetString()!.EndsWith("/oauth/pin", StringComparison.Ordinal);
                    return valid ? Json("""{"token_type":"Bearer","expires_in":31536000,"access_token":"fake-access-token-0123456789"}""") : new HttpResponseMessage(HttpStatusCode.BadRequest);
                }

                if (Mode == FakeAniListMode.Down)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }

                if (Mode == FakeAniListMode.RateLimited)
                {
                    return new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30)) } };
                }

                if (Mode == FakeAniListMode.Rejected || request.Headers.Authorization?.Parameter != Token)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"errors":[{"message":"Invalid token","status":400}]}""", Encoding.UTF8, "application/json") };
                }

                var query = root.GetProperty("query").GetString()!;
                var variables = root.GetProperty("variables");
                if (query.Contains("Viewer", StringComparison.Ordinal))
                {
                    return Json("{\"data\":{\"Viewer\":{\"id\":" + ViewerId + ",\"name\":\"Fan\"}}}");
                }

                if (query.Contains("MediaListCollection", StringComparison.Ordinal))
                {
                    var anime = variables.GetProperty("type").GetString() == "ANIME";
                    var entries = anime
                        ? new[] { Entry(900, 5001, "CURRENT", false, 12, null), Entry(901, 5002, "COMPLETED", false, 1, null), Entry(902, 7777, "CURRENT", false, 24, null) }
                        : [Entry(903, 6001, "CURRENT", true, null, null)];
                    return Json("{\"data\":{\"MediaListCollection\":{\"hasNextChunk\":false,\"lists\":[{\"entries\":[" + string.Join(',', entries) + "]},{\"entries\":[" + entries[0] + "]}]}}}");
                }

                if (query.Contains("SaveMediaListEntry", StringComparison.Ordinal))
                {
                    if (Mode == FakeAniListMode.WritesFail)
                    {
                        FailedWrites++;
                        return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                    }

                    var id = variables.GetProperty("id").GetInt32();
                    var progress = variables.GetProperty("progress").GetInt32();
                    var current = _state[id];
                    _state[id] = query.Contains("progressVolumes:", StringComparison.Ordinal) ? (current.Progress, progress) : (progress, current.Volumes);
                    _stamp++;
                    Mutations.Add((id, progress, query));
                    return Json("{\"data\":{\"SaveMediaListEntry\":{\"id\":" + id + ",\"progress\":" + _state[id].Progress + ",\"progressVolumes\":" + _state[id].Volumes + ",\"updatedAt\":" + _stamp + "}}}");
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            private string Entry(int id, int mediaId, string status, bool manga, int? episodes, int? volumes)
            {
                var state = _state[id];
                var total = manga ? "\"chapters\":null,\"volumes\":null" : "\"episodes\":" + episodes + ",\"chapters\":null,\"volumes\":null";
                return "{\"id\":" + id + ",\"mediaId\":" + mediaId + ",\"status\":\"" + status + "\",\"progress\":" + state.Progress + ",\"progressVolumes\":" + state.Volumes + ",\"score\":80,\"updatedAt\":" + _stamp
                    + ",\"media\":{" + total + ",\"title\":{\"romaji\":\"Title " + mediaId + "\",\"english\":null},\"coverImage\":{\"large\":\"https://s4.anilist.co/cover-" + mediaId + ".jpg\"}}}";
            }

            private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
