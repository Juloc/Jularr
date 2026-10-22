using System.Net;
using System.Net.Http.Json;
using System.Security;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Service.System.Media.V1;
using Jularr.Integrations.Logic.AniList;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    // The relations AniList names for the entries a matched Work holds become its provider relations, while the AniList module is on.
    [TestClass]
    public sealed class AniListRelationTests
    {
        [TestMethod]
        public async Task Relations_MatchedAnimeSeries_AreSavedFromItsSeasonsAndFollowTheProvider()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var aniList = new FakeAniList();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true, services: services => services.AddHttpClient<AniListClient>().ConfigurePrimaryHttpMessageHandler(() => aniList));
            await host.Fixture.RunSqlAsync("""
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" = 'AniList';
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "ReleaseYear") VALUES (10, 2, 1, 'Example Quest', 2019), (50, 3, 2, 'Example Quest Manga', 2015);
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (50, 'anilist', '501');
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (100, 10, 1, 1), (200, 10, 1, 2);
                INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") SELECT 10, 100, 2, n FROM generate_series(1, 12) AS n;
                INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") SELECT 10, 200, 2, n FROM generate_series(1, 12) AS n;
                """);

            // The matcher finds both seasons; relations wait for their own pass.
            await using (var scope = host.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<MatchAniListV1>().ExecuteAsync<UpdateResult>(new MediaMatchParametersV1(), default);
            }

            Assert.AreEqual("1,2", await host.ScalarAsync("""SELECT string_agg("Value", ',' ORDER BY "Value") FROM "WorkUnitExternalIdentities" WHERE "Namespace" = 'anilist'"""));
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "WorkRelations" """));
            Assert.AreEqual(true, await host.ScalarAsync("""SELECT "RelationsCheckedAt" IS NULL FROM "WorkIdentityMatches" WHERE "WorkId" = 10"""));

            // The pass reads each season's entry: what the seasons say of each other is not a relation of the Work, and the manga in the library answers for its adaptation.
            Assert.IsTrue((await RunRelationsAsync(host)).Success);
            var relations = await ReadAsync(host, 10);
            CollectionAssert.AreEquivalent(new[] { (WorkRelationType.SourceMaterial, "501"), (WorkRelationType.SideStory, "601"), (WorkRelationType.SpinOff, "602") },
                relations.Select(relation => (relation.WorkRelationTypeId, relation.RelatedWorkId is null ? relation.ExternalId! : "501")).ToArray());
            Assert.AreEqual(50L, relations.Single(relation => relation.WorkRelationTypeId == WorkRelationType.SourceMaterial).RelatedWorkId);
            Assert.IsTrue(relations.All(relation => relation.SourceKey == "anilist"));
            Assert.AreEqual(2L, await host.ScalarAsync("""SELECT count(*) FROM "ExternalWorks" WHERE "Namespace" = 'anilist' AND "Value" IN ('601', '602')"""));
            Assert.AreEqual(false, await host.ScalarAsync("""SELECT "RelationsCheckedAt" IS NULL FROM "WorkIdentityMatches" WHERE "WorkId" = 10"""));
            Assert.AreEqual((WorkRelationType.Adaptation, 10L), (await ReadAsync(host, 50)).Select(relation => (relation.WorkRelationTypeId, relation.RelatedWorkId!.Value)).Single());

            // Nothing is read again before the Work is due; a new match makes it due at once.
            var reads = aniList.Reads;
            await RunRelationsAsync(host);
            Assert.AreEqual(reads, aniList.Reads);

            // A relation a person hid stays hidden when the provider names it again; one the provider stops naming is deleted.
            var sideStory = relations.Single(relation => relation.WorkRelationTypeId == WorkRelationType.SideStory);
            using var hidden = await host.SendAsync(HttpMethod.Delete, "/api/v1/admin/works/10/relations/" + sideStory.WorkRelationId);
            Assert.AreEqual(HttpStatusCode.OK, hidden.StatusCode, await hidden.Content.ReadAsStringAsync());
            aniList.Remove(1, "SPIN_OFF");
            await DueAgainAsync(host);
            await RunRelationsAsync(host);
            relations = await ReadAsync(host, 10);
            Assert.IsFalse(relations.Any(relation => relation.ExternalId == "601"));
            Assert.IsFalse(relations.Any(relation => relation.ExternalId == "602"));
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "WorkRelations" WHERE "WorkId" = 10 AND "IsSuppressed" """));
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "WorkRelations" WHERE "WorkId" = 10 AND "SourceKey" = 'anilist' AND NOT "IsSuppressed" """));

            // A Work that waits for a person is no match and has no relations.
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle") VALUES (60, 2, 1, 'Undecided');
                INSERT INTO "WorkIdentityMatches" ("WorkId", "Namespace", "IdentityMatchStatusTypeId") VALUES (60, 'anilist', 2);
                """);
            await RunRelationsAsync(host);
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "WorkRelations" WHERE "WorkId" = 60"""));
        }

        [TestMethod]
        public async Task Relations_AniListModuleOff_NothingIsAskedAndNothingIsSaved()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var aniList = new FakeAniList();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true, services: services => services.AddHttpClient<AniListClient>().ConfigurePrimaryHttpMessageHandler(() => aniList));
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "ReleaseYear") VALUES (10, 2, 1, 'Example Quest', 2019);
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (100, 10, 1, 1);
                INSERT INTO "WorkUnitExternalIdentities" ("WorkUnitId", "Namespace", "Value") VALUES (100, 'anilist', '1');
                INSERT INTO "WorkIdentityMatches" ("WorkId", "Namespace", "IdentityMatchStatusTypeId") VALUES (10, 'anilist', 1);
                """);

            // The module is off by default: the job is skipped and AniList is not called.
            Assert.AreEqual(false, await host.ScalarAsync("""SELECT "IsEnabled" FROM "InstanceModules" WHERE "Key" = 'AniList'"""));
            await Assert.ThrowsExactlyAsync<SecurityException>(() => RunRelationsAsync(host));
            Assert.AreEqual(0, aniList.Reads);
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "WorkRelations" """));

            // Without the series module nobody shows the relations, so none are fetched either.
            await host.Fixture.RunSqlAsync("""UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" = 'AniList'; UPDATE "InstanceModules" SET "IsEnabled" = FALSE WHERE "Key" = 'Franchises'""");
            await Assert.ThrowsExactlyAsync<SecurityException>(() => RunRelationsAsync(host));
            Assert.AreEqual(0, aniList.Reads);

            // On, it works; switched off again, it stops.
            await host.Fixture.RunSqlAsync("""UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" = 'Franchises'""");
            await RunRelationsAsync(host);
            Assert.IsTrue(aniList.Reads > 0);
            Assert.AreEqual(4L, await host.ScalarAsync("""SELECT count(*) FROM "WorkRelations" WHERE "WorkId" = 10"""));
            var reads = aniList.Reads;
            await host.Fixture.RunSqlAsync("""UPDATE "InstanceModules" SET "IsEnabled" = FALSE WHERE "Key" = 'AniList'""");
            await DueAgainAsync(host);
            await Assert.ThrowsExactlyAsync<SecurityException>(() => RunRelationsAsync(host));
            Assert.AreEqual(reads, aniList.Reads);
        }

        [TestMethod]
        public async Task Relations_AniListUnavailable_EndsThePassAndTheWorkStaysDue()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var aniList = new FakeAniList { IsDown = true };
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true, services: services => services.AddHttpClient<AniListClient>().ConfigurePrimaryHttpMessageHandler(() => aniList));
            await host.Fixture.RunSqlAsync("""
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" = 'AniList';
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "ReleaseYear") VALUES (10, 2, 1, 'Example Quest', 2019);
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (100, 10, 1, 1);
                INSERT INTO "WorkUnitExternalIdentities" ("WorkUnitId", "Namespace", "Value") VALUES (100, 'anilist', '1');
                INSERT INTO "WorkIdentityMatches" ("WorkId", "Namespace", "IdentityMatchStatusTypeId") VALUES (10, 'anilist', 1);
                """);

            Assert.IsFalse((await RunRelationsAsync(host)).Success);
            Assert.AreEqual(true, await host.ScalarAsync("""SELECT "RelationsCheckedAt" IS NULL FROM "WorkIdentityMatches" WHERE "WorkId" = 10"""));
            aniList.IsDown = false;
            Assert.IsTrue((await RunRelationsAsync(host)).Success);
            Assert.AreEqual(4L, await host.ScalarAsync("""SELECT count(*) FROM "WorkRelations" WHERE "WorkId" = 10"""));
        }

        // A matched Work whose relations were read a while ago, and the AniList answers read again.
        private static Task DueAgainAsync(Host host)
        {
            ((Microsoft.Extensions.Caching.Memory.MemoryCache)host.Services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()).Clear();
            return host.Fixture.RunSqlAsync("""UPDATE "WorkIdentityMatches" SET "RelationsCheckedAt" = now() - interval '2 days'""");
        }

        private static async Task<UpdateResult> RunRelationsAsync(Host host)
        {
            await using var scope = host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<SyncAniListRelationsV1>().ExecuteAsync<UpdateResult>(new MediaMatchParametersV1(), default);
        }

        private static async Task<IReadOnlyList<WorkRelationV1>> ReadAsync(Host host, long workId)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/works/" + workId + "/relations");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<BaseDataList<WorkRelationV1>>())!;
        }

        // Example Quest: season one (1) and two (2); the first has a manga as its source, a side story and a spin-off, the second names the first as prequel.
        private sealed class FakeAniList : HttpMessageHandler
        {
            private readonly Dictionary<int, List<string>> _relations = new()
            {
                [1] = [Edge("SEQUEL", 2, "ANIME", "TV", "Example Quest 2", 2020), Edge("SOURCE", 501, "MANGA", "MANGA", "Example Quest Manga", 2015), Edge("SIDE_STORY", 601, "ANIME", "TV", "Example Quest Extra", 2019),
                    Edge("SPIN_OFF", 602, "ANIME", "TV", "Example Quest Spin", 2021)],
                [2] = [Edge("PREQUEL", 1, "ANIME", "TV", "Example Quest", 2019)]
            };

            public int Reads { get; private set; }
            public bool IsDown { get; set; }

            public void Remove(int id, string relationType) => _relations[id].RemoveAll(edge => edge.Contains("\"" + relationType + "\"", StringComparison.Ordinal));

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (IsDown)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }

                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var variables = body.RootElement.GetProperty("variables");
                if (variables.TryGetProperty("search", out var search))
                {
                    return Json("{\"data\":{\"Page\":{\"media\":[" + Media(1) + "]}}}");
                }

                Reads++;
                return Json("{\"data\":{\"Media\":" + Media(variables.GetProperty("id").GetInt32()) + "}}");
            }

            private string Media(int id) => "{\"id\":" + id + ",\"type\":\"ANIME\",\"format\":\"TV\",\"title\":{\"romaji\":\"Example Quest" + (id == 2 ? " 2" : "") + "\"},\"synonyms\":[],\"startDate\":{\"year\":" + (id == 1 ? 2019 : 2020)
                + "},\"episodes\":12,\"relations\":{\"edges\":[" + string.Join(',', _relations[id]) + "]}}";

            private static string Edge(string relationType, int id, string type, string format, string title, int year) =>
                "{\"relationType\":\"" + relationType + "\",\"node\":{\"id\":" + id + ",\"type\":\"" + type + "\",\"format\":\"" + format + "\",\"title\":{\"romaji\":\"" + title + "\"},\"startDate\":{\"year\":" + year + "}}}";

            private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
