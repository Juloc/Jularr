using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.User.Media.V1;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class WorkUnitGroupTests
    {
        private const string Route = "/api/v1/admin/works/10/unit-groups";

        private const string Episodes = """
            INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (100, 10, 1, 1);
            INSERT INTO "WorkUnits" ("Id", "WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") VALUES (101, 10, 100, 2, 1), (102, 10, 100, 2, 2), (103, 10, 100, 2, 3);
            """;

        [TestMethod]
        public async Task Sections_SeriesRanges_NameEpisodesAndSplitTheDetails()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await CreateAsync(storage, MediaType.Series, Episodes, [101, 102, 103]);
            await CreateGroupAsync(host, new CreateWorkUnitGroupDataV1("Part 1", 1m, 1m, 1m, 2m));
            await CreateGroupAsync(host, new CreateWorkUnitGroupDataV1("Part 2", 1m, 3m, 1m, 3m));

            // Each available episode names its section; the details page heads them.
            CollectionAssert.AreEqual(new[] { "Part 1", "Part 1", "Part 2" }, await GroupNamesAsync(host));
            using var details = await host.SendAsync(HttpMethod.Get, "/Details/10?tab=episodes");
            var html = await details.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, details.StatusCode, html);
            StringAssert.Contains(html, "<h3>Part 2</h3>");
            using var editor = await host.SendAsync(HttpMethod.Get, "/Admin/Works/10?Tab=Metadata");
            StringAssert.Contains(await editor.Content.ReadAsStringAsync(), "Add section");

            // Removed sections and a switched-off module leave the plain list.
            using var list = await host.SendAsync(HttpMethod.Get, Route);
            var part2 = (await list.Content.ReadFromJsonAsync<BaseDataList<WorkUnitGroupV1>>())!.Single(group => group.Name == "Part 2");
            using var removed = await host.SendAsync(HttpMethod.Delete, Route + "/" + part2.Id);
            Assert.AreEqual(HttpStatusCode.OK, removed.StatusCode, await removed.Content.ReadAsStringAsync());
            CollectionAssert.AreEqual(new[] { "Part 1", "Part 1", null }, await GroupNamesAsync(host));
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'UnitGroups'");
            CollectionAssert.AreEqual(new string?[] { null, null, null }, await GroupNamesAsync(host));
            using var off = await host.SendAsync(HttpMethod.Get, Route);
            Assert.AreEqual(HttpStatusCode.Forbidden, off.StatusCode);
        }

        [TestMethod]
        public async Task Sections_BookVolumes_TakeTheirChaptersAlong()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await CreateAsync(storage, MediaType.Book, """
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (101, 10, 3, 1), (102, 10, 3, 2), (103, 10, 4, 7);
                INSERT INTO "WorkUnits" ("Id", "WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") VALUES (104, 10, 102, 4, 5);
                """, [101, 104, 103]);
            await CreateGroupAsync(host, new CreateWorkUnitGroupDataV1("First arc", null, 1m, null, 2m));

            // Volume 1 and chapter 5 of volume 2 belong to the arc; a chapter without volume does not.
            CollectionAssert.AreEquivalent(new[] { "First arc", "First arc", null }, await GroupNamesAsync(host));
        }

        [TestMethod]
        public async Task Sections_InvalidRangesAndUsers_AreRefused()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await CreateAsync(storage, MediaType.Series, Episodes, [101, 102, 103]);
            await host.Fixture.RunSqlAsync("INSERT INTO \"Works\" (\"Id\", \"MediaTypeId\", \"CanonicalTitle\") VALUES (20, 1, 'Film')");
            await CreateGroupAsync(host, new CreateWorkUnitGroupDataV1("Part 1", 1m, 1m, 1m, 2m));
            foreach (var (route, data, error) in new[]
            {
                (Route, new CreateWorkUnitGroupDataV1("Overlap", 1m, 2m, 1m, 3m), "overlapping_group"),
                (Route, new CreateWorkUnitGroupDataV1("Backwards", 1m, 3m, 1m, 2m), "out_of_range"),
                (Route, new CreateWorkUnitGroupDataV1("No season", null, 3m, null, 3m), "required"),
                ("/api/v1/admin/works/20/unit-groups", new CreateWorkUnitGroupDataV1("Film", null, 1m, null, 1m), "invalid_transition")
            })
            {
                using var refused = await host.SendAsync(HttpMethod.Post, route, data);
                Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode, data.Name);
                StringAssert.Contains(await refused.Content.ReadAsStringAsync(), error, data.Name);
            }

            // Media managers may name sections, users may not.
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            await CreateGroupAsync(host, new CreateWorkUnitGroupDataV1("Part 2", 1m, 3m, 1m, 3m));
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 2 WHERE \"Id\" = 41");
            using var user = await host.SendAsync(HttpMethod.Post, Route, new CreateWorkUnitGroupDataV1("Part 3", 2m, 1m, 2m, 1m));
            Assert.AreEqual(HttpStatusCode.Unauthorized, user.StatusCode);
        }

        // Work 10 with one asset and one stored file per given unit.
        private static async Task<Host> CreateAsync(LibraryTests.TemporaryDirectory storage, MediaType mediaType, string units, long[] assetUnits)
        {
            var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (10, {(byte)mediaType}, 'Title');
                INSERT INTO "WorkEditions" ("Id", "WorkId") VALUES (1, 10);
                INSERT INTO "WorkVersions" ("Id", "WorkId", "WorkEditionId") VALUES (1, 10, 1);
                """);
            await host.Fixture.RunSqlAsync(units);
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "MediaAssets" ("Id", "WorkId", "WorkVersionId", "WorkUnitId", "MediaAssetTypeId")
                SELECT n, 10, 1, (ARRAY[{string.Join(", ", assetUnits)}])[n], 1 FROM generate_series(1, {assetUnits.Length}) AS n;
                """);
            var mountId = await LibraryTests.CreateMountAsync(host);
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Media", mediaType);
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt", "MediaAssetId")
                SELECT {rootId}, 'Title/' || n || '.mkv', 1, now(), now(), n FROM generate_series(1, {assetUnits.Length}) AS n;
                """);
            return host;
        }

        private static async Task CreateGroupAsync(Host host, CreateWorkUnitGroupDataV1 data)
        {
            using var created = await host.SendAsync(HttpMethod.Post, Route, data);
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
        }

        private static async Task<string?[]> GroupNamesAsync(Host host)
        {
            using var availability = await host.SendAsync(HttpMethod.Get, "/api/v1/works/10/availability");
            return [.. (await availability.Content.ReadFromJsonAsync<PageResult<WorkAvailabilityV1>>())!.Items.Select(item => item.GroupName)];
        }
    }
}
