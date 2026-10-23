using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Authentication.V1;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class WorkListTests
    {
        private const string Works = "/api/v1/admin/works";
        private const string Bulk = "/api/v1/admin/works/bulk/";

        // 901 Alpha: film, monitored, filed, library 801 and its own profile. 902 Beta: film, paused. 903 Gamma: series, monitored, nothing filed.
        // 904 Delta: film without a target. 905 Epsilon: film, monitored, a download runs. 906 Zeta: a game with two platform targets.
        private static async Task SeedAsync(Host host)
        {
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "StorageMounts" ("Id", "Name", "StorageProtocolTypeId", "SharePath", "IsReadOnly") OVERRIDING SYSTEM VALUE VALUES (801, 'Listed', 3, '/media/listed', FALSE);
                INSERT INTO "LibraryRoots" ("Id", "Name", "MediaTypeId", "StorageMountId", "RelativePath") VALUES (801, 'Movies A', 1, 801, 'movies-a'), (802, 'Movies B', 1, 801, 'movies-b'), (803, 'Shows', 2, 801, 'shows');
                INSERT INTO "AcquisitionProfiles" ("Id", "Name", "MediaTypeId", "IsDefault", "Definition") OVERRIDING SYSTEM VALUE
                    SELECT 801, 'Film listed', 1, FALSE, "Definition" FROM "AcquisitionProfiles" WHERE "MediaTypeId" = 1 AND "IsDefault" LIMIT 1;
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear", "CreatedAt") VALUES
                    (901, 1, 'Alpha', 2020, '2026-01-01'), (902, 1, 'Beta', 2018, '2026-01-02'), (903, 2, 'Gamma', 2022, '2026-01-03'),
                    (904, 1, 'Delta', 2021, '2026-01-04'), (905, 1, 'Epsilon', 2019, '2026-01-05'), (906, 7, 'Zeta', 2017, '2026-01-06');
                INSERT INTO "GamePlatforms" ("Id", "Name") OVERRIDING SYSTEM VALUE VALUES (801, 'Platform one'), (802, 'Platform two');
                INSERT INTO "WantedItems" ("Id", "WorkId", "AcquisitionProfileId", "LibraryRootId", "IsPaused") VALUES (901, 901, 801, 801, FALSE), (902, 902, NULL, NULL, TRUE), (903, 903, NULL, NULL, FALSE), (905, 905, NULL, NULL, FALSE);
                INSERT INTO "WantedItems" ("Id", "WorkId", "GamePlatformId", "IsPaused") VALUES (906, 906, 801, FALSE), (907, 906, 802, TRUE);
                INSERT INTO "WorkEditions" ("Id", "WorkId") VALUES (901, 901);
                INSERT INTO "WorkVersions" ("Id", "WorkId", "WorkEditionId") VALUES (901, 901, 901);
                INSERT INTO "MediaAssets" ("Id", "WorkId", "WorkVersionId", "MediaAssetTypeId") VALUES (901, 901, 901, 1);
                INSERT INTO "StoredFiles" ("Id", "LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt", "MediaAssetId") VALUES (901, 801, 'Alpha (2020)/Alpha.mkv', 3000000000, now(), now(), 901);
                INSERT INTO "AcquisitionReleases" ("Id", "WantedItemId", "ReleaseIdentity", "Title", "AcquisitionReleaseStatusTypeId") VALUES (905, 905, 'epsilon', 'Epsilon.2019.1080p', 1);
                INSERT INTO "Operations" ("Id", "OperationTypeId", "OperationStatusTypeId", "AcquisitionReleaseId") VALUES (905, 2, 2, 905);
                INSERT INTO "WorkMetadataFacts" ("WorkId", "FactKey", "Value", "IsManual") VALUES (901, 'Poster', '/api/v1/covers/901/poster-1.webp', TRUE);
                """);
        }

        [TestMethod]
        public async Task Read_FiltersSortsAndPages_WithWhatTheRowShows()
        {
            await using var host = await CreateAsync();
            await SeedAsync(host);

            var all = await ReadAsync(host, "pageSize=50");
            CollectionAssert.AreEqual(new long[] { 901, 902, 904, 905, 903, 906 }.Order().ToArray(), all.Select(item => item.Id).Order().ToArray());
            CollectionAssert.AreEqual(new[] { "Alpha", "Beta", "Delta", "Epsilon", "Gamma", "Zeta" }, all.Select(item => item.CanonicalTitle).ToArray());

            // The row
            var alpha = all.Single(item => item.Id == 901);
            Assert.AreEqual((true, WorkListStatus.Available, 1, 3000000000L, "Film listed", "Movies A", 901L), (alpha.IsMonitored, alpha.WorkListStatusId, alpha.FileCount, alpha.SizeBytes, alpha.ProfileName, alpha.LibraryName, alpha.WantedItemId));
            Assert.AreEqual("/api/v1/covers/901/poster-1.webp", alpha.PosterUrl);
            Assert.AreEqual(WorkListStatus.Unmonitored, all.Single(item => item.Id == 902).WorkListStatusId);
            Assert.AreEqual(WorkListStatus.Missing, all.Single(item => item.Id == 903).WorkListStatusId);
            Assert.AreEqual((false, 0, (long?)null), (all.Single(item => item.Id == 904).IsMonitored, all.Single(item => item.Id == 904).TargetCount, all.Single(item => item.Id == 904).WantedItemId));
            Assert.AreEqual(WorkListStatus.Downloading, all.Single(item => item.Id == 905).WorkListStatusId);
            Assert.AreEqual((2, true), (all.Single(item => item.Id == 906).TargetCount, all.Single(item => item.Id == 906).IsMonitored));

            // Filters
            CollectionAssert.AreEqual(new long[] { 903 }, (await ReadAsync(host, "mediaTypeId=2")).Select(item => item.Id).ToArray());
            CollectionAssert.AreEquivalent(new long[] { 901, 903, 905, 906 }, (await ReadAsync(host, "isMonitored=true")).Select(item => item.Id).ToArray());
            CollectionAssert.AreEquivalent(new long[] { 902, 904 }, (await ReadAsync(host, "isMonitored=false")).Select(item => item.Id).ToArray());
            CollectionAssert.AreEqual(new long[] { 903, 906 }, (await ReadAsync(host, "workListStatusId=" + (int)WorkListStatus.Missing)).Select(item => item.Id).Order().ToArray());
            CollectionAssert.AreEqual(new long[] { 905 }, (await ReadAsync(host, "workListStatusId=" + (int)WorkListStatus.Downloading)).Select(item => item.Id).ToArray());
            CollectionAssert.AreEqual(new long[] { 901 }, (await ReadAsync(host, "libraryRootId=801")).Select(item => item.Id).ToArray());
            Assert.AreEqual(0, (await ReadAsync(host, "libraryRootId=802")).Count);
            CollectionAssert.AreEqual(new long[] { 901 }, (await ReadAsync(host, "acquisitionProfileId=801")).Select(item => item.Id).ToArray());
            CollectionAssert.AreEqual(new[] { "Beta", "Delta", "Zeta" }, (await ReadAsync(host, "query=%20ta%20")).Select(item => item.CanonicalTitle).ToArray());
            CollectionAssert.AreEqual(new long[] { 905 }, (await ReadAsync(host, "mediaTypeId=1&isMonitored=true&workListStatusId=3")).Select(item => item.Id).ToArray());

            // Sort
            CollectionAssert.AreEqual(new long[] { 901 }, (await ReadAsync(host, "workSortTypeId=" + (int)WorkSortType.Size + "&isDescending=true&pageSize=1")).Select(item => item.Id).ToArray());
            CollectionAssert.AreEqual(new[] { "Zeta", "Gamma", "Epsilon", "Delta", "Beta", "Alpha" }, (await ReadAsync(host, "isDescending=true&pageSize=50")).Select(item => item.CanonicalTitle).ToArray());
            CollectionAssert.AreEqual(new long[] { 906, 905, 904, 903, 902, 901 }, (await ReadAsync(host, "workSortTypeId=" + (int)WorkSortType.Added + "&isDescending=true&pageSize=50")).Select(item => item.Id).ToArray());
            CollectionAssert.AreEqual(new long[] { 902, 905, 901, 904 }, (await ReadAsync(host, "workSortTypeId=" + (int)WorkSortType.Year + "&mediaTypeId=1&pageSize=50")).Select(item => item.Id).ToArray());

            // Paging is by the order, with the same filters.
            CollectionAssert.AreEqual(new[] { "Alpha", "Beta" }, (await ReadAsync(host, "pageSize=2&page=1")).Select(item => item.CanonicalTitle).ToArray());
            CollectionAssert.AreEqual(new[] { "Delta", "Epsilon" }, (await ReadAsync(host, "pageSize=2&page=2")).Select(item => item.CanonicalTitle).ToArray());
            CollectionAssert.AreEqual(new[] { "Epsilon" }, (await ReadAsync(host, "pageSize=1&page=2&mediaTypeId=1&isMonitored=true")).Select(item => item.CanonicalTitle).ToArray());
        }

        [TestMethod]
        public async Task Monitoring_SameRuleAsTheTitlePage_AndAReportPerTitle()
        {
            await using var host = await CreateAsync();
            await SeedAsync(host);

            // Beta, Gamma (already monitored), Delta (no target), Zeta (a game), and a title that does not exist.
            var report = await PostAsync(host, "monitoring", new UpdateWorksMonitoringDataV1([902, 903, 904, 906, 999], true));
            CollectionAssert.AreEqual(new[] { "done", "unchanged", "not_wanted", "per_platform", "not_found" }, report.Select(item => item.OutcomeKey).ToArray());
            CollectionAssert.AreEqual(new[] { "Beta", "Gamma", "Delta", "Zeta", "#999" }, report.Select(item => item.CanonicalTitle).ToArray());

            // The title page reads the same target: monitored is a target that is not paused, and a resumed one is searched at the next cycle.
            using var target = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/wanted/902");
            var wanted = (await target.Content.ReadFromJsonAsync<WantedDetailsV1>())!;
            Assert.IsFalse(wanted.IsPaused);
            Assert.AreEqual(2L, wanted.Version);
            Assert.IsTrue((await ReadAsync(host, "isMonitored=true&pageSize=50")).Any(item => item.Id == 902));
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT "Version" FROM "WantedItems" WHERE "Id" = 903"""), "A target that already had the state is not touched.");
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "WantedItems" WHERE "WorkId" = 906 AND "IsPaused" """), "A game stays as it is per platform.");

            // And back, then the title page's own switch agrees with the list.
            await PostAsync(host, "monitoring", new UpdateWorksMonitoringDataV1([901, 902], false));
            Assert.IsTrue((await ReadAsync(host, "isMonitored=false&pageSize=50")).Select(item => item.Id).Intersect([901L, 902L]).Count() == 2);
            using var page = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/wanted/901");
            var current = (await page.Content.ReadFromJsonAsync<WantedDetailsV1>())!;
            Assert.IsTrue(current.IsPaused);
            using var resumed = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/wanted/901", new UpdateWantedDataV1(false, current.Version, current.AcquisitionProfileId, current.LibraryRootId));
            Assert.AreEqual(HttpStatusCode.OK, resumed.StatusCode, await resumed.Content.ReadAsStringAsync());
            Assert.AreEqual(true, (await ReadAsync(host, "pageSize=50")).Single(item => item.Id == 901).IsMonitored);
        }

        [TestMethod]
        public async Task ProfileAndLibrary_ApplyToTargetsThatFit_AndSayWhyOthersStay()
        {
            await using var host = await CreateAsync();
            await SeedAsync(host);
            var seriesProfile = (long)(await host.ScalarAsync("""SELECT "Id" FROM "AcquisitionProfiles" WHERE "MediaTypeId" = 2 AND "IsDefault" LIMIT 1"""))!;

            // The film profile fits films only; Alpha already has it, Delta has no target.
            var profiles = await PostAsync(host, "profile", new UpdateWorksProfileDataV1([901, 902, 903, 904], 801));
            CollectionAssert.AreEqual(new[] { "unchanged", "done", "profile_mismatch", "not_wanted" }, profiles.Select(item => item.OutcomeKey).ToArray());
            Assert.AreEqual(801L, await host.ScalarAsync("""SELECT "AcquisitionProfileId" FROM "WantedItems" WHERE "Id" = 902"""));
            Assert.AreEqual(DBNull.Value, await host.ScalarAsync("""SELECT "AcquisitionProfileId" FROM "WantedItems" WHERE "Id" = 903"""));
            var series = await PostAsync(host, "profile", new UpdateWorksProfileDataV1([903], seriesProfile));
            Assert.AreEqual("done", series.Single().OutcomeKey);

            // Without a profile the default of the type applies again.
            Assert.AreEqual("done", (await PostAsync(host, "profile", new UpdateWorksProfileDataV1([902], null))).Single().OutcomeKey);
            Assert.AreEqual(DBNull.Value, await host.ScalarAsync("""SELECT "AcquisitionProfileId" FROM "WantedItems" WHERE "Id" = 902"""));

            // The library takes new files only; the filed file stays where it is.
            var libraries = await PostAsync(host, "library", new UpdateWorksLibraryDataV1([901, 902, 903], 802));
            CollectionAssert.AreEqual(new[] { "done", "done", "library_mismatch" }, libraries.Select(item => item.OutcomeKey).ToArray());
            Assert.AreEqual(802L, await host.ScalarAsync("""SELECT "LibraryRootId" FROM "WantedItems" WHERE "Id" = 901"""));
            Assert.AreEqual(801L, await host.ScalarAsync("""SELECT "LibraryRootId" FROM "StoredFiles" WHERE "Id" = 901"""));
            Assert.AreEqual("unchanged", (await PostAsync(host, "library", new UpdateWorksLibraryDataV1([901], 802))).Single().OutcomeKey);
            var rows = await ReadAsync(host, "libraryRootId=802");
            CollectionAssert.AreEqual(new long[] { 901, 902 }, rows.Select(item => item.Id).ToArray());
            Assert.AreEqual("Movies B", rows[0].LibraryName);
        }

        [TestMethod]
        public async Task Delete_LikeTheTitleDialog_RefusesARunningDownloadAndKeepsTheOthersGoing()
        {
            await using var host = await CreateAsync();
            await SeedAsync(host);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value", "IsManual") VALUES (902, 'tmdb_movie', '7002', FALSE);
                """);

            // Beta goes with an exclusion, Epsilon downloads and stays, 999 does not exist.
            var report = await PostAsync(host, "deletion", new DeleteWorksDataV1([902, 905, 999], false, true));
            CollectionAssert.AreEqual(new[] { "done", "download_active", "not_found" }, report.Select(item => item.OutcomeKey).ToArray());
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "Works" WHERE "Id" = 902"""));
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "Works" WHERE "Id" = 905"""));
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "WorkExclusions" WHERE "Title" = 'Beta'"""));
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "WorkExclusionIdentities" WHERE "Value" = '7002'"""));
        }

        [TestMethod]
        public async Task RefreshMetadata_TitleWithoutProviderIdentity_StaysAndSaysWhy()
        {
            await using var host = await CreateAsync();
            await SeedAsync(host);

            var report = await PostAsync(host, "metadata", new HeavyWorksDataV1([901, 999]));
            CollectionAssert.AreEqual(new[] { "unknown_reference", "not_found" }, report.Select(item => item.OutcomeKey).ToArray());
            Assert.AreEqual("Alpha", report[0].CanonicalTitle);
        }

        [TestMethod]
        public async Task Search_ReportsEveryTitle_AndTheBatchIsBounded()
        {
            await using var host = await CreateAsync();
            await SeedAsync(host);

            // Without indexers nothing is found, but each title is answered for.
            var report = await PostAsync(host, "search", new HeavyWorksDataV1([904, 906, 999, 901]));
            CollectionAssert.AreEqual(new long[] { 901, 904, 906, 999 }, report.Select(item => item.Id).ToArray());
            CollectionAssert.AreEqual(new[] { "not_wanted", "per_platform", "not_found" }, report.Skip(1).Select(item => item.OutcomeKey).ToArray());
            Assert.AreNotEqual("not_wanted", report[0].OutcomeKey);

            // More than the batch is refused before anything runs.
            using var tooMany = await host.SendAsync(HttpMethod.Post, Bulk + "search", new HeavyWorksDataV1([.. Enumerable.Range(1, 26).Select(id => (long)id)]));
            Assert.AreEqual(HttpStatusCode.BadRequest, tooMany.StatusCode);
            using var tooManyToMonitor = await host.SendAsync(HttpMethod.Post, Bulk + "monitoring", new UpdateWorksMonitoringDataV1([.. Enumerable.Range(1, 101).Select(id => (long)id)], true));
            Assert.AreEqual(HttpStatusCode.BadRequest, tooManyToMonitor.StatusCode);
            using var none = await host.SendAsync(HttpMethod.Post, Bulk + "monitoring", new UpdateWorksMonitoringDataV1([], true));
            Assert.AreEqual(HttpStatusCode.OK, none.StatusCode);
        }

        [TestMethod]
        [DataRow("monitoring")]
        [DataRow("profile")]
        [DataRow("library")]
        [DataRow("search")]
        [DataRow("metadata")]
        [DataRow("deletion")]
        public async Task Bulk_AnAccountWithoutTheManagerRole_IsRefused(string action)
        {
            await using var host = await CreateAsync();
            await SeedAsync(host);
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 2 WHERE \"Id\" = 41");
            object data = action switch
            {
                "monitoring" => new UpdateWorksMonitoringDataV1([901], false),
                "profile" => new UpdateWorksProfileDataV1([901], null),
                "library" => new UpdateWorksLibraryDataV1([901], null),
                "deletion" => new DeleteWorksDataV1([901], true, true),
                _ => new HeavyWorksDataV1([901])
            };
            using var denied = await host.SendAsync(HttpMethod.Post, Bulk + action, data);
            Assert.IsTrue(denied.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, denied.StatusCode.ToString());
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "Works" WHERE "Id" = 901"""));
            Assert.AreEqual(false, await host.ScalarAsync("""SELECT "IsPaused" FROM "WantedItems" WHERE "Id" = 901"""));
            using var read = await host.SendAsync(HttpMethod.Get, Works);
            Assert.IsTrue(read.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, read.StatusCode.ToString());

            // A media manager may do what the mass editor offers.
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            using var allowed = await host.SendAsync(HttpMethod.Post, Bulk + "profile", new UpdateWorksProfileDataV1([901], null));
            Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode, await allowed.Content.ReadAsStringAsync());
        }

        private static async Task<IReadOnlyList<WorkListItemV1>> ReadAsync(Host host, string query)
        {
            using var response = await host.SendAsync(HttpMethod.Get, Works + "?" + query);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<PageResult<WorkListItemV1>>())!.Items;
        }

        private static async Task<IReadOnlyList<WorkBulkOutcomeV1>> PostAsync(Host host, string action, object data)
        {
            using var response = await host.SendAsync(HttpMethod.Post, Bulk + action, data);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<BaseDataList<WorkBulkOutcomeV1>>())!.ToArray();
        }

        [TestMethod]
        public async Task Razor_List_ShowsRowsFiltersAndTakesTheRowAndBulkActions()
        {
            await using var host = await CreateAsync(razor: true);
            await SeedAsync(host);

            // The list, its filters and the mass editor
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Works");
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, await page.Content.ReadAsStringAsync());
            var html = await page.Content.ReadAsStringAsync();
            foreach (var expected in new[] { "data-work-table=\"works\"", "name=\"type\"", "name=\"library\"", "name=\"profile\"", "id=\"works-bulk\"", "data-dock-select-all", "view-switch", ">Alpha<", ">Zeta<", "href=\"/Admin/Works/901\"" })
            {
                StringAssert.Contains(html, expected);
            }

            Assert.IsFalse(html.Contains("style=\"", StringComparison.Ordinal));
            var csrf = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);

            // A filter leads to the titles that fit
            using var films = await host.SendAsync(HttpMethod.Get, "/Admin/Works?type=1&monitored=false");
            var filtered = await films.Content.ReadAsStringAsync();
            StringAssert.Contains(filtered, ">Beta<");
            Assert.IsFalse(filtered.Contains(">Alpha<", StringComparison.Ordinal));
            Assert.IsFalse(filtered.Contains(">Gamma<", StringComparison.Ordinal));

            // The switch of one row is the title page's rule
            using var monitored = await host.SendAsync(HttpMethod.Post, "/Admin/Works?handler=Monitor", new Dictionary<string, string> { ["__RequestVerificationToken"] = csrf, ["workId"] = "902", ["isMonitored"] = "true" });
            Assert.AreEqual(HttpStatusCode.Redirect, monitored.StatusCode, await monitored.Content.ReadAsStringAsync());
            Assert.AreEqual(false, await host.ScalarAsync("""SELECT "IsPaused" FROM "WantedItems" WHERE "Id" = 902"""));

            // The mass editor sets what was chosen; a series keeps its profile because a film profile does not fit it
            using var applied = await host.SendAsync(HttpMethod.Post, "/Admin/Works?handler=Bulk", new FormUrlEncodedContent([new("__RequestVerificationToken", csrf), new("action", "apply"),
                new("workIds", "901"), new("workIds", "903"), new("monitoring", "off"), new("profileId", "801")]));
            Assert.AreEqual(HttpStatusCode.Redirect, applied.StatusCode, await applied.Content.ReadAsStringAsync());
            Assert.AreEqual(true, await host.ScalarAsync("""SELECT "IsPaused" FROM "WantedItems" WHERE "Id" = 903"""));
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "WantedItems" WHERE "Id" = 901 AND "AcquisitionProfileId" = 801"""));
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "WantedItems" WHERE "Id" = 903 AND "AcquisitionProfileId" IS NULL"""));
        }

        private static async Task<Host> CreateAsync(bool razor = false)
        {
            var host = await Host.CreateAsync(library: true, razor: razor);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
            return host;
        }
    }
}
