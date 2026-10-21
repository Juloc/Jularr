using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Account.V1;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.User.Library.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Service.Admin.Media.V1;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;
using AdminWork = Jularr.Backend.Data.Admin.Media.V1.WorkDetailsV1;
using UserWork = Jularr.Backend.Data.User.Media.V1.WorkDetailsV1;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class MediaTests
    {
        [TestMethod]
        public async Task Work_ManualCreationAndStaleEdit_PreserveIdentityAndTitleProvenance()
        {
            await using var host = await CreateHostAsync();
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works", new CreateWorkDataV1(MediaType.Movie, "Original"));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var work = (await created.Content.ReadFromJsonAsync<AdminWork>())!;
            Assert.AreEqual(1L, work.Version);
            await host.Fixture.RunSqlAsync("INSERT INTO \"WorkTitles\" (\"WorkId\", \"UiLocaleId\", \"Title\", \"IsManual\") SELECT \"Id\", 1, 'Localized manual', TRUE FROM \"Works\"");

            using var changed = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/works/" + work.Id, new UpdateWorkDataV1("Manual edit", work.Version));
            Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode, await changed.Content.ReadAsStringAsync());
            using var stale = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/works/" + work.Id, new UpdateWorkDataV1("Stale", work.Version));
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);
            Assert.AreEqual("Manual edit", await host.ScalarAsync("SELECT \"Title\" FROM \"WorkTitles\" WHERE \"UiLocaleId\" IS NULL"));
            Assert.AreEqual("Localized manual", await host.ScalarAsync("SELECT \"Title\" FROM \"WorkTitles\" WHERE \"UiLocaleId\" = 1"));
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"WorkTitles\""));
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT \"Version\" FROM \"Works\""));

            // Invalid input cannot write.
            using var invalid = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works", new CreateWorkDataV1((MediaType)99, "Invalid"));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Works\""));

            // Manual manager, not User.
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            using var manager = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works", new CreateWorkDataV1(MediaType.Series, "Series"));
            Assert.AreEqual(HttpStatusCode.OK, manager.StatusCode);
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 2 WHERE \"Id\" = 41");
            using var denied = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works", new CreateWorkDataV1(MediaType.Movie, "Denied"));
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"Works\""));
        }

        [TestMethod]
        public async Task Work_Subtype_FitsItsTypeAndBecomesManualOnlyWhenChanged()
        {
            await using var host = await CreateHostAsync();
            using var invalid = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works", new CreateWorkDataV1(MediaType.Movie, "Wrong", null, MediaSubtype.Manga));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works", new CreateWorkDataV1(MediaType.Series, "Show", null, MediaSubtype.Anime));
            var work = (await created.Content.ReadFromJsonAsync<AdminWork>())!;
            Assert.AreEqual(MediaSubtype.Anime, work.MediaSubtypeId);
            Assert.IsTrue(work.IsMediaSubtypeManual);

            // Title edits keep a provider classification; changing it makes it manual.
            await host.Fixture.RunSqlAsync("UPDATE \"Works\" SET \"IsMediaSubtypeManual\" = FALSE");
            using var renamed = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/works/" + work.Id, new UpdateWorkDataV1("Show!", 1, null, MediaSubtype.Anime));
            Assert.AreEqual(HttpStatusCode.OK, renamed.StatusCode, await renamed.Content.ReadAsStringAsync());
            Assert.AreEqual(false, await host.ScalarAsync("SELECT \"IsMediaSubtypeManual\" FROM \"Works\""));
            using var cleared = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/works/" + work.Id, new UpdateWorkDataV1("Show!", 2));
            Assert.AreEqual(HttpStatusCode.OK, cleared.StatusCode);
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"IsMediaSubtypeManual\" AND \"MediaSubtypeId\" IS NULL FROM \"Works\""));
            using var mismatch = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/works/" + work.Id, new UpdateWorkDataV1("Show!", 3, null, MediaSubtype.Manga));
            Assert.AreEqual(HttpStatusCode.BadRequest, mismatch.StatusCode);
        }

        [TestMethod]
        [DataRow("mediaTypeId=2", new long[] { 10 })]
        [DataRow("mediaSubtypeId=1", new long[] { 20, 40 })]
        [DataRow("mediaTypeId=3", new long[] { 30 })]
        [DataRow("mediaSubtypeId=2", new long[] { 50 })]
        [DataRow("", new long[] { 10, 20, 30, 40, 50 })]
        public async Task Library_Subtypes_LeaveTheirParentList(string filter, long[] expected)
        {
            await using var host = await CreateHostAsync();
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle") VALUES (10, 2, NULL, 'Series'), (20, 2, 1, 'Anime'), (30, 3, NULL, 'Book'), (40, 1, 1, 'Anime Movie'), (50, 3, 2, 'Manga');
                INSERT INTO "WantedItems" ("WorkId") SELECT "Id" FROM "Works";
                """);
            using var library = await host.SendAsync(HttpMethod.Get, "/api/v1/library?" + filter);
            var page = (await library.Content.ReadFromJsonAsync<PageResult<Jularr.Backend.Data.User.Library.V1.LibraryItemV1>>())!;
            CollectionAssert.AreEquivalent(expected, page.Items.Select(item => item.Id).ToArray());
        }

        [TestMethod]
        public async Task MediaSwitches_MoveSubtypesBackAndHideTypesWithoutDeleting()
        {
            await using var host = await CreateHostAsync();
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle") VALUES (10, 2, NULL, 'Series'), (20, 2, 1, 'Anime'), (30, 3, NULL, 'Book'), (40, 1, 1, 'Anime Movie'), (50, 3, 2, 'Manga');
                INSERT INTO "WantedItems" ("WorkId") SELECT "Id" FROM "Works";
                """);
            using var listed = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/instance/media");
            var media = (await listed.Content.ReadFromJsonAsync<BaseDataList<InstanceMediaDetailsV1>>())!;
            CollectionAssert.AreEqual(new[] { "Movie", "Series", "Anime", "Book", "Manga", "LightNovel", "Music", "Game" }, media.Select(item => item.Key).ToArray());
            Assert.IsTrue(media.All(item => item.IsEnabled && !item.HasLibrary));

            // Anime off: its Works return to their parent lists.
            await SwitchAsync(host, "Anime", false, 1);
            CollectionAssert.AreEquivalent(new long[] { 10, 20 }, await LibraryAsync(host, "mediaTypeId=2"));
            CollectionAssert.AreEquivalent(Array.Empty<long>(), await LibraryAsync(host, "mediaSubtypeId=1"));

            // Series off: its Works leave the user area, Anime movies come back with Anime.
            await SwitchAsync(host, "Series", false, 1);
            await SwitchAsync(host, "Anime", true, 2);
            CollectionAssert.AreEquivalent(new long[] { 30, 40, 50 }, await LibraryAsync(host, ""));
            using var closed = await host.SendAsync(HttpMethod.Get, "/api/v1/works/20");
            Assert.AreEqual(HttpStatusCode.NotFound, closed.StatusCode);

            // A subtype needs an enabled parent; switching deletes nothing.
            await SwitchAsync(host, "Book", false, 1);
            await SwitchAsync(host, "Manga", false, 1);
            using var orphan = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/media", new UpdateInstanceMediaDataV1("Manga", true, 2));
            Assert.AreEqual(HttpStatusCode.BadRequest, orphan.StatusCode);
            Assert.AreEqual(5L, await host.ScalarAsync("SELECT count(*) FROM \"Works\" JOIN \"WantedItems\" ON \"WantedItems\".\"WorkId\" = \"Works\".\"Id\""));
        }

        [TestMethod]
        public async Task ProfileMedia_BlockedTypesCloseHiddenTypesOnlyLeaveLists()
        {
            await using var host = await CreateHostAsync();
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle") VALUES (10, 2, NULL, 'Series'), (20, 2, 1, 'Anime'), (30, 3, NULL, 'Book');
                INSERT INTO "WantedItems" ("WorkId") SELECT "Id" FROM "Works";
                """);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            var profileId = (await state.Content.ReadFromJsonAsync<AuthenticationResultV1>())!.ProfileId!.Value;

            // The Owner blocks Anime; the profile hides Books.
            using var blocked = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/profiles/" + profileId + "/media", new UpdateProfileMediaAccessDataV1(["Anime"]));
            Assert.AreEqual(HttpStatusCode.OK, blocked.StatusCode, await blocked.Content.ReadAsStringAsync());
            using var hidden = await host.SendAsync(HttpMethod.Put, "/api/v1/profiles/" + profileId + "/media", new UpdateProfileMediaDataV1(["Book"]));
            Assert.AreEqual(HttpStatusCode.OK, hidden.StatusCode, await hidden.Content.ReadAsStringAsync());
            using var unknown = await host.SendAsync(HttpMethod.Put, "/api/v1/profiles/" + profileId + "/media", new UpdateProfileMediaDataV1(["Comics"]));
            Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);

            CollectionAssert.AreEquivalent(new long[] { 10 }, await LibraryAsync(host, ""));
            using var anime = await host.SendAsync(HttpMethod.Get, "/api/v1/works/20");
            Assert.AreEqual(HttpStatusCode.NotFound, anime.StatusCode);
            using var book = await host.SendAsync(HttpMethod.Get, "/api/v1/works/30");
            Assert.AreEqual(HttpStatusCode.OK, book.StatusCode);
            using var request = await host.SendAsync(HttpMethod.Post, "/api/v1/works/20/requests", new Jularr.Backend.Data.User.Request.V1.CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.NotFound, request.StatusCode);

            // A form only replaces what it shows: a block survives a switched-off subtype and the profile's own save.
            await SwitchAsync(host, "Anime", false, 1);
            using var resaved = await host.SendAsync(HttpMethod.Put, "/api/v1/profiles/" + profileId + "/media", new UpdateProfileMediaDataV1(["Book"]));
            Assert.AreEqual(HttpStatusCode.OK, resaved.StatusCode);
            using var ownerSaved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/profiles/" + profileId + "/media", new UpdateProfileMediaAccessDataV1([]));
            Assert.AreEqual(HttpStatusCode.OK, ownerSaved.StatusCode);
            await SwitchAsync(host, "Anime", true, 2);
            using var stillBlocked = await host.SendAsync(HttpMethod.Get, "/api/v1/works/20");
            Assert.AreEqual(HttpStatusCode.NotFound, stillBlocked.StatusCode);

            // The profile sees what it may use; unblocking keeps its own hiding.
            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/profiles/" + profileId + "/media");
            var own = (await read.Content.ReadFromJsonAsync<BaseDataList<ProfileMediaV1>>())!;
            Assert.IsFalse(own.Any(item => item.Key == "Anime"));
            Assert.IsTrue(own.Single(item => item.Key == "Book").IsHidden);
            using var unblocked = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/profiles/" + profileId + "/media", new UpdateProfileMediaAccessDataV1([]));
            Assert.AreEqual(HttpStatusCode.OK, unblocked.StatusCode);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ProfileMediaRules\""));
            CollectionAssert.AreEquivalent(new long[] { 10, 20 }, await LibraryAsync(host, ""));
        }

        private static async Task SwitchAsync(Host host, string key, bool isEnabled, long version)
        {
            using var switched = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/media", new UpdateInstanceMediaDataV1(key, isEnabled, version));
            Assert.AreEqual(HttpStatusCode.OK, switched.StatusCode, await switched.Content.ReadAsStringAsync());
        }

        private static async Task<long[]> LibraryAsync(Host host, string filter)
        {
            using var library = await host.SendAsync(HttpMethod.Get, "/api/v1/library?" + filter);
            return [.. (await library.Content.ReadFromJsonAsync<PageResult<Jularr.Backend.Data.User.Library.V1.LibraryItemV1>>())!.Items.Select(item => item.Id)];
        }

        [TestMethod]
        public async Task Watchlist_RepeatedUpdatesAndPaging_StayProfileScoped()
        {
            await using var host = await CreateHostAsync();
            await host.Fixture.RunSqlAsync("INSERT INTO \"Works\" (\"Id\", \"MediaTypeId\", \"CanonicalTitle\") VALUES (10, 1, 'A'), (20, 2, 'B'); INSERT INTO \"WatchlistEntries\" (\"ProfileId\", \"WorkId\") VALUES (202, 20)");
            using var added = await host.SendAsync(HttpMethod.Put, "/api/v1/watchlist/10", new UpdateWatchlistDataV1(true));
            Assert.AreEqual(HttpStatusCode.OK, added.StatusCode, await added.Content.ReadAsStringAsync());
            using var repeated = await host.SendAsync(HttpMethod.Put, "/api/v1/watchlist/10", new UpdateWatchlistDataV1(true));
            Assert.AreEqual(HttpStatusCode.OK, repeated.StatusCode);

            using var first = await host.SendAsync(HttpMethod.Get, "/api/v1/watchlist?pageSize=1");
            var page = (await first.Content.ReadFromJsonAsync<PageResult<UserWork>>())!;
            Assert.AreEqual(1, page.Items.Count);
            Assert.AreEqual(10L, page.Items[0].Id);
            using var next = await host.SendAsync(HttpMethod.Get, "/api/v1/watchlist?page=2&pageSize=1");
            var nextPage = (await next.Content.ReadFromJsonAsync<PageResult<UserWork>>())!;
            Assert.AreEqual(0, nextPage.Items.Count);

            using var removed = await host.SendAsync(HttpMethod.Put, "/api/v1/watchlist/10", new UpdateWatchlistDataV1(false));
            Assert.AreEqual(HttpStatusCode.OK, removed.StatusCode);
            Assert.AreEqual(202L, await host.ScalarAsync("SELECT \"ProfileId\" FROM \"WatchlistEntries\""));
            using var missing = await host.SendAsync(HttpMethod.Put, "/api/v1/watchlist/999", new UpdateWatchlistDataV1(true));
            Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"WatchlistEntries\""));
        }

        [TestMethod]
        public async Task WorkCard_LibraryAndWatchlist_ShowTheCanonicalAvailabilityOfEveryTitle()
        {
            await using var host = await CreateHostAsync();
            var mountId = await LibraryTests.CreateMountAsync(host);
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Series", MediaType.Series);
            // The profile reads and hears English.
            await host.Fixture.RunSqlAsync($"""UPDATE "Profiles" SET "UiLocaleId" = (SELECT "Id" FROM "UiLocales" WHERE "Locale" = 'en') WHERE "Id" = {host.Fixture.ProfileAId}""");
            await host.Fixture.RunSqlAsync($"""
                WITH work AS (
                    INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES
                        (10, 2, 'One Show'), (20, 1, 'One Film'), (30, 1, 'Other Film'), (40, 1, 'Missing Film')
                    RETURNING "Id"
                ), edition AS (
                    INSERT INTO "WorkEditions" ("WorkId") SELECT "Id" FROM work RETURNING "Id", "WorkId"
                ), version AS (
                    INSERT INTO "WorkVersions" ("WorkId", "WorkEditionId")
                    SELECT "WorkId", "Id" FROM edition RETURNING "Id", "WorkId"
                ), season AS (
                    INSERT INTO "WorkUnits" ("WorkId", "WorkUnitTypeId", "Number")
                    SELECT 10, 1, 1 RETURNING "Id", "WorkId"
                ), episode AS (
                    INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number")
                    SELECT version."WorkId", season."Id", 2, number
                    FROM version
                        JOIN season ON season."WorkId" = version."WorkId"
                    CROSS JOIN generate_series(1, 2) AS number
                    RETURNING "Id", "WorkId", "Number"
                ), asset AS (
                    INSERT INTO "MediaAssets" ("WorkId", "WorkVersionId", "WorkUnitId", "MediaAssetTypeId")
                    SELECT episode."WorkId", version."Id", episode."Id", 1
                    FROM episode
                        JOIN version ON version."WorkId" = episode."WorkId"
                    WHERE episode."Number" = 1
                    UNION ALL
                    SELECT version."WorkId", version."Id", NULL, 1 FROM version WHERE version."WorkId" IN (20, 30, 40)
                    RETURNING "Id", "WorkId"
                ), file AS (
                    INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt", "MediaAssetId")
                    SELECT {rootId}, 'media/' || asset."Id" || '.mkv', 100, now(), now(), asset."Id"
                    FROM asset
                    WHERE asset."WorkId" IN (10, 20, 30)
                    RETURNING "Id"
                )
                SELECT file."Id" FROM file;
                """);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "MediaTracks" ("StoredFileId", "StreamIndex", "MediaTrackTypeId", "Language", "IsDefault", "IsForced")
                SELECT file."Id", track."Index", track."TypeId",
                    CASE WHEN asset."WorkId" = 30 THEN 'ja' ELSE track."Language" END, TRUE, FALSE
                FROM
                    "StoredFiles" AS file
                    JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                    CROSS JOIN (VALUES (1, 2, 'en'), (2, 3, 'de')) AS track("Index", "TypeId", "Language")
                WHERE
                    asset."WorkId" IN (10, 20, 30);
                """);
            foreach (var workId in new long[] { 10, 20, 30, 40 })
            {
                using var added = await host.SendAsync(HttpMethod.Put, "/api/v1/watchlist/" + workId, new UpdateWatchlistDataV1(true));
                Assert.AreEqual(HttpStatusCode.OK, added.StatusCode, await added.Content.ReadAsStringAsync());
            }

            Assert.AreEqual("en", await host.ScalarAsync($"""SELECT lower(split_part(locale."Locale", '-', 1)) FROM "UiLocales" AS locale JOIN "Profiles" AS profile ON profile."UiLocaleId" = locale."Id" WHERE profile."Id" = {host.Fixture.ProfileAId}"""));

            // One of two episodes is here: the card is partial and names the real tracks.
            var show = await ReadCardAsync(host, "/api/v1/watchlist", 10);
            Assert.AreEqual(MediaAvailabilityState.Partial, show.AvailabilityStateId);
            Assert.AreEqual(1, show.AvailableUnits);
            Assert.AreEqual(2, show.MonitoredUnits);
            CollectionAssert.AreEquivalent(new[] { "en" }, show.AudioLanguages);
            CollectionAssert.AreEquivalent(new[] { "de" }, show.SubtitleLanguages);

            // The rating is the one of the catalog entry the title came from; a title without one shows none.
            await host.Fixture.RunSqlAsync("""
                WITH identity AS (
                    INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (20, 'tvdb', '9020') RETURNING "Namespace"
                )
                INSERT INTO "ExternalWorks" ("Namespace", "Value", "MediaTypeId", "Title", "Rating", "Votes") SELECT "Namespace", '9020', 1, 'The film', 7.65, 100 FROM identity;
                """);
            Assert.IsNull(show.Rating);

            // A film has no units; with the profile's audio it is complete.
            var film = await ReadCardAsync(host, "/api/v1/watchlist", 20);
            Assert.AreEqual(7.65m, film.Rating);
            Assert.AreEqual(MediaAvailabilityState.ProfileAudio, film.AvailabilityStateId);
            Assert.AreEqual(1, film.AvailableUnits);
            Assert.AreEqual(1, film.MonitoredUnits);

            // The same film in another audio language is complete, but in the wrong language.
            var other = await ReadCardAsync(host, "/api/v1/watchlist", 30);
            Assert.AreEqual(MediaAvailabilityState.OtherAudio, other.AvailabilityStateId);
            CollectionAssert.AreEquivalent(new[] { "ja" }, other.AudioLanguages);

            // Nothing of it is here.
            var missing = await ReadCardAsync(host, "/api/v1/watchlist", 40);
            Assert.AreEqual(MediaAvailabilityState.Missing, missing.AvailabilityStateId);
            Assert.AreEqual(0, missing.AvailableUnits);
            Assert.AreEqual(1, missing.MonitoredUnits);

            // The library shows the same card as the watchlist.
            var library = await ReadCardAsync(host, "/api/v1/library", 10);
            Assert.AreEqual(show.Id, library.Id);
            Assert.AreEqual(show.AvailabilityStateId, library.AvailabilityStateId);
            Assert.AreEqual(show.AvailableUnits, library.AvailableUnits);
            CollectionAssert.AreEquivalent(library.SubtitleLanguages, show.SubtitleLanguages);

            // A running download of the title replaces the state of the library.
            await host.Fixture.RunSqlAsync("""
                WITH wanted AS (
                    INSERT INTO "WantedItems" ("WorkId") VALUES (40) RETURNING "Id"
                ), release AS (
                    INSERT INTO "AcquisitionReleases" ("WantedItemId", "ReleaseIdentity", "Title", "AcquisitionReleaseStatusTypeId")
                    SELECT wanted."Id", 'release-1', 'Missing Film 1080p', 1 FROM wanted RETURNING "Id"
                )
                INSERT INTO "Operations" ("OperationTypeId", "OperationStatusTypeId", "AcquisitionReleaseId")
                SELECT 2, 2, release."Id" FROM release;
                """);
            Assert.AreEqual(MediaAvailabilityState.Downloading, (await ReadCardAsync(host, "/api/v1/watchlist", 40)).AvailabilityStateId);

            // Without the running download the request of the profile waits.
            await host.Fixture.RunSqlAsync("""DELETE FROM "Operations" WHERE "OperationTypeId" = 2""");
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "AcquisitionRequests" ("WorkId", "RequestedByProfileId", "RequestedByAccountId", "UiLocaleId", "AcquisitionRequestStatusTypeId")
                VALUES (40, 101, 41, 1, 1);
                """);
            Assert.AreEqual(MediaAvailabilityState.Requested, (await ReadCardAsync(host, "/api/v1/watchlist", 40)).AvailabilityStateId);

            // A wanted title without a request searches for itself.
            await host.Fixture.RunSqlAsync("""DELETE FROM "AcquisitionRequests" WHERE "WorkId" = 40""");
            Assert.AreEqual(MediaAvailabilityState.Searching, (await ReadCardAsync(host, "/api/v1/watchlist", 40)).AvailabilityStateId);
        }

        private static async Task<LibraryItemV1> ReadCardAsync(Host host, string route, long workId)
        {
            using var read = await host.SendAsync(HttpMethod.Get, route);
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
            return (await read.Content.ReadFromJsonAsync<PageResult<LibraryItemV1>>())!.Items.Single(item => item.Id == workId);
        }

        [TestMethod]
        public async Task Razor_MediaForms_ValidateCreateEditAndPersonalList()
        {
            await using var host = await CreateHostAsync(razor: true);
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Works");
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, await page.Content.ReadAsStringAsync());
            var html = await page.Content.ReadAsStringAsync();
            var csrf = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);

            using var created = await host.SendAsync(HttpMethod.Post, "/Admin/Works", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = csrf, ["MediaTypeId"] = "Movie", ["CanonicalTitle"] = "<unsafe>"
            });
            Assert.AreEqual(HttpStatusCode.Redirect, created.StatusCode, await created.Content.ReadAsStringAsync());
            var id = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Works\""))!;
            using var editor = await host.SendAsync(HttpMethod.Get, "/Admin/Works/" + id + "?Tab=Metadata");
            Assert.AreEqual(HttpStatusCode.OK, editor.StatusCode, await editor.Content.ReadAsStringAsync());
            StringAssert.Contains(await editor.Content.ReadAsStringAsync(), "&lt;unsafe&gt;");
            StringAssert.Contains(await editor.Content.ReadAsStringAsync(), "name=\"__Invariant\" value=\"Version\"");
            using var edited = await host.SendAsync(HttpMethod.Post, "/Admin/Works/" + id + "?Tab=Metadata", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = csrf, ["CanonicalTitle"] = "<edited>", ["Version"] = "1"
            });
            Assert.AreEqual(HttpStatusCode.Redirect, edited.StatusCode, await edited.Content.ReadAsStringAsync());
            Assert.AreEqual("<edited>", await host.ScalarAsync("SELECT \"CanonicalTitle\" FROM \"Works\""));
            using var rejected = await host.SendAsync(HttpMethod.Post, "/Admin/Works/" + id + "?Tab=Metadata", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = csrf, ["CanonicalTitle"] = "Stale", ["Version"] = "1"
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);
            StringAssert.Contains(await rejected.Content.ReadAsStringAsync(), "class=\"field-error\" role=\"alert\"");
            using var details = await host.SendAsync(HttpMethod.Get, "/Details/" + id);
            Assert.AreEqual(HttpStatusCode.OK, details.StatusCode, await details.Content.ReadAsStringAsync());
            using var saved = await host.SendAsync(HttpMethod.Post, "/Details/" + id, new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = csrf, ["IsWatchlisted"] = "True"
            });
            Assert.AreEqual(HttpStatusCode.Redirect, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            using var watchlist = await host.SendAsync(HttpMethod.Get, "/Favorites");
            Assert.AreEqual(HttpStatusCode.OK, watchlist.StatusCode, await watchlist.Content.ReadAsStringAsync());
            var watchlistHtml = await watchlist.Content.ReadAsStringAsync();
            StringAssert.Contains(watchlistHtml, "&lt;edited&gt;");
            StringAssert.Contains(watchlistHtml, "media-card__bookmark");
            StringAssert.Contains(watchlistHtml, "aria-label=\"Remove from favorites\"");
            Assert.IsFalse(watchlistHtml.Contains("state-chip--notrequested", StringComparison.Ordinal));
            Assert.IsFalse(watchlistHtml.Contains("media-availability-badge--profileaudio", StringComparison.Ordinal));

            // The list narrows to a media type and keeps its order.
            var type = (MediaType)(short)(await host.ScalarAsync("SELECT \"MediaTypeId\" FROM \"Works\""))!;
            var other = type == MediaType.Movie ? MediaType.Series : MediaType.Movie;
            using var own = await host.SendAsync(HttpMethod.Get, "/api/v1/watchlist?mediaTypeIds=" + type + "&librarySortTypeId=Title");
            Assert.AreEqual(1, (await own.Content.ReadFromJsonAsync<PageResult<Jularr.Backend.Data.User.Library.V1.LibraryItemV1>>())!.Items.Count);
            using var none = await host.SendAsync(HttpMethod.Get, "/api/v1/watchlist?mediaTypeIds=" + other);
            Assert.AreEqual(0, (await none.Content.ReadFromJsonAsync<PageResult<Jularr.Backend.Data.User.Library.V1.LibraryItemV1>>())!.Items.Count);
            using var users = await host.SendAsync(HttpMethod.Get, "/Admin/Requests/Users");
            Assert.AreEqual(HttpStatusCode.OK, users.StatusCode, await users.Content.ReadAsStringAsync());
            StringAssert.Contains(await users.Content.ReadAsStringAsync(), "Standard");
        }

        private static async Task<Host> CreateHostAsync(bool razor = false)
        {
            var host = await Host.CreateAsync(razor: razor, media: true);
            try
            {
                using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
                using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
                Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }
    }
}
