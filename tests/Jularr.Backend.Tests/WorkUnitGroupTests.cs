using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.User.Media.V1;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class WorkUnitGroupTests
    {
        private const string Route = "/api/v1/admin/works/10/unit-groups";

        [TestMethod]
        public async Task Sections_NameUnitRangesWithoutOverlapAndSplitTheDetails()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (10, 2, 'Show');
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (100, 10, 1, 1);
                INSERT INTO "WorkUnits" ("Id", "WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") VALUES (101, 10, 100, 2, 1), (102, 10, 100, 2, 2), (103, 10, 100, 2, 3);
                INSERT INTO "WorkEditions" ("Id", "WorkId") VALUES (1, 10);
                INSERT INTO "WorkVersions" ("Id", "WorkId", "WorkEditionId") VALUES (1, 10, 1);
                INSERT INTO "MediaAssets" ("Id", "WorkId", "WorkVersionId", "WorkUnitId", "MediaAssetTypeId") VALUES (1, 10, 1, 101, 1), (2, 10, 1, 102, 1), (3, 10, 1, 103, 1);
                """);
            var mountId = await LibraryTests.CreateMountAsync(host);
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Series", MediaType.Series);
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt", "MediaAssetId")
                SELECT {rootId}, 'Show/e' || n || '.mkv', 1, now(), now(), n FROM generate_series(1, 3) AS n;
                """);

            // Sections cover forward ranges that never overlap.
            using var part1 = await host.SendAsync(HttpMethod.Post, Route, new CreateWorkUnitGroupDataV1("Part 1", 1m, 1m, 1m, 2m));
            Assert.AreEqual(HttpStatusCode.OK, part1.StatusCode, await part1.Content.ReadAsStringAsync());
            using var overlap = await host.SendAsync(HttpMethod.Post, Route, new CreateWorkUnitGroupDataV1("Part 2", 1m, 2m, 1m, 3m));
            StringAssert.Contains(await overlap.Content.ReadAsStringAsync(), "overlapping_group");
            using var backwards = await host.SendAsync(HttpMethod.Post, Route, new CreateWorkUnitGroupDataV1("Part 2", 1m, 3m, 1m, 2m));
            Assert.AreEqual(HttpStatusCode.BadRequest, backwards.StatusCode);
            using var part2 = await host.SendAsync(HttpMethod.Post, Route, new CreateWorkUnitGroupDataV1("Part 2", 1m, 3m, 1m, 3m));
            Assert.AreEqual(HttpStatusCode.OK, part2.StatusCode, await part2.Content.ReadAsStringAsync());

            // Each available unit names its section; the details page shows them.
            using var availability = await host.SendAsync(HttpMethod.Get, "/api/v1/works/10/availability");
            var items = (await availability.Content.ReadFromJsonAsync<PageResult<WorkAvailabilityV1>>())!.Items;
            CollectionAssert.AreEqual(new[] { "Part 1", "Part 1", "Part 2" }, items.Select(item => item.GroupName).ToArray());
            using var details = await host.SendAsync(HttpMethod.Get, "/Details/10");
            var html = await details.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, details.StatusCode, html);
            StringAssert.Contains(html, "<h3>Part 2</h3>");
            using var editor = await host.SendAsync(HttpMethod.Get, "/Admin/Works/10");
            StringAssert.Contains(await editor.Content.ReadAsStringAsync(), "Add section");

            // Removing a section and switching the module off leave the plain list.
            using var list = await host.SendAsync(HttpMethod.Get, Route);
            var groups = (await list.Content.ReadFromJsonAsync<BaseDataList<WorkUnitGroupV1>>())!;
            using var removed = await host.SendAsync(HttpMethod.Delete, Route + "/" + groups[1].Id);
            Assert.AreEqual(HttpStatusCode.OK, removed.StatusCode, await removed.Content.ReadAsStringAsync());
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'UnitGroups'");
            using var plain = await host.SendAsync(HttpMethod.Get, "/api/v1/works/10/availability");
            Assert.IsTrue((await plain.Content.ReadFromJsonAsync<PageResult<WorkAvailabilityV1>>())!.Items.All(item => item.GroupName is null));
            using var off = await host.SendAsync(HttpMethod.Get, Route);
            Assert.AreEqual(HttpStatusCode.Forbidden, off.StatusCode);
        }
    }
}
