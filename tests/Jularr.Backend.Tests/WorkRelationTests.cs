using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Logic.Media;
using Jularr.Integrations.Data.AniList;
using Jularr.Integrations.Logic.AniList;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class WorkRelationTests
    {
        [TestMethod]
        [DataRow(WorkRelationType.Sequel, WorkRelationType.Prequel, true)]
        [DataRow(WorkRelationType.Prequel, WorkRelationType.Sequel, false)]
        [DataRow(WorkRelationType.SideStory, WorkRelationType.MainStory, true)]
        [DataRow(WorkRelationType.SpinOff, WorkRelationType.MainStory, true)]
        [DataRow(WorkRelationType.MainStory, WorkRelationType.SideStory, false)]
        [DataRow(WorkRelationType.Adaptation, WorkRelationType.SourceMaterial, true)]
        [DataRow(WorkRelationType.SourceMaterial, WorkRelationType.Adaptation, false)]
        [DataRow(WorkRelationType.Alternative, WorkRelationType.Alternative, true)]
        [DataRow(WorkRelationType.SharedUniverse, WorkRelationType.SharedUniverse, true)]
        [DataRow(WorkRelationType.Summary, WorkRelationType.Summary, true)]
        [DataRow(WorkRelationType.Other, WorkRelationType.Other, true)]
        public void Type_Inverse_NamesTheOtherSideAndStoresOnlyForwardTypes(WorkRelationType type, WorkRelationType inverse, bool isStoredForward)
        {
            Assert.AreEqual(inverse, WorkRelationLogic.Inverse(type));
            Assert.AreEqual(isStoredForward, WorkRelationLogic.IsStoredForward(type));
        }

        [TestMethod]
        public void AniList_Relations_MapToTypesAndKeepOnlyTitlesDiscoverShows()
        {
            Assert.AreEqual(WorkRelationType.SideStory, WorkRelationLogic.FromAniList("SIDE_STORY"));
            Assert.AreEqual(WorkRelationType.MainStory, WorkRelationLogic.FromAniList("PARENT"));
            Assert.AreEqual(WorkRelationType.SourceMaterial, WorkRelationLogic.FromAniList("SOURCE"));
            Assert.AreEqual(WorkRelationType.Summary, WorkRelationLogic.FromAniList("COMPILATION"));
            Assert.IsNull(WorkRelationLogic.FromAniList("CONTAINS"));

            // A music video and a relation of an unknown kind stay out; the same title with the same type is named once.
            var media = new AniListMedia(1, "MANGA", "Example", null, null, [], 2019, null, 3, null, null, [], [], [], null, null)
            {
                Relations =
                [
                    new("ADAPTATION", 2, "ANIME", "TV", "Example Anime", 2021, "https://img.example.test/2.jpg"),
                    new("ADAPTATION", 2, "ANIME", "TV", "Example Anime", 2021, null),
                    new("OTHER", 3, "ANIME", "MUSIC", "Opening", 2021, null),
                    new("CONTAINS", 4, "MANGA", "MANGA", "Collected", 2020, null),
                    new("SEQUEL", 5, "MANGA", "NOVEL", "Example Novel", null, null)
                ]
            };
            var targets = WorkRelationLogic.AniListTargets(media);
            CollectionAssert.AreEqual(new[] { ("2", WorkRelationType.Adaptation, MediaType.Series, MediaSubtype.Anime), ("5", WorkRelationType.Sequel, MediaType.Book, MediaSubtype.LightNovel) },
                targets.Select(target => (target.Value, target.WorkRelationTypeId, target.MediaTypeId, target.MediaSubtypeId!.Value)).ToArray());
        }

        [TestMethod]
        public async Task Relations_ProviderManualInverseAndSuppression_ShowOnBothWorksOnce()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var aniList = new FakeAniList();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true, services: services => services.AddHttpClient<AniListClient>().ConfigurePrimaryHttpMessageHandler(() => aniList));

            // A manga names an anime adaptation and a sequel; neither is a Work yet, so both are catalog titles.
            var manga = await ImportAsync(host, 101);
            var relations = await ReadAsync(host, manga);
            CollectionAssert.AreEquivalent(new[] { (WorkRelationType.Adaptation, "201"), (WorkRelationType.Sequel, "102") }, relations.Select(relation => (relation.WorkRelationTypeId, relation.ExternalId!)).ToArray());
            Assert.IsTrue(relations.All(relation => relation.RelatedWorkId is null && relation.Namespace == "anilist" && relation.SourceKey == "anilist" && relation.WorkRelationId is not null));
            Assert.AreEqual(("Example Anime", MediaType.Series, MediaSubtype.Anime), relations.Where(relation => relation.ExternalId == "201").Select(relation => (relation.Title, relation.MediaTypeId, relation.MediaSubtypeId!.Value)).Single());

            // The sequel becomes a Work: the first sees it as its sequel, the second its prequel, and the relation is shown once although both providers name it.
            var sequel = await ImportAsync(host, 102);
            var fromFirst = await ReadAsync(host, manga);
            var sequelRow = fromFirst.Single(relation => relation.RelatedWorkId == sequel);
            Assert.AreEqual(WorkRelationType.Sequel, sequelRow.WorkRelationTypeId);
            var fromSecond = await ReadAsync(host, sequel);
            Assert.AreEqual((WorkRelationType.Prequel, manga), fromSecond.Select(relation => (relation.WorkRelationTypeId, relation.RelatedWorkId!.Value)).Single());
            Assert.IsNotNull(fromSecond.Single().AvailabilityStateId);

            // The anime is requested later: its Work (found by the season's identity) now answers for the adaptation, and the anime sees its source material.
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "ReleaseYear") VALUES (900, 2, 1, 'Example Anime', 2021);
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (9000, 900, 1, 1);
                INSERT INTO "WorkUnitExternalIdentities" ("WorkUnitId", "Namespace", "Value") VALUES (9000, 'anilist', '201');
                """);
            Assert.AreEqual((WorkRelationType.Adaptation, 900L), (await ReadAsync(host, manga)).Where(relation => relation.RelatedWorkId == 900).Select(relation => (relation.WorkRelationTypeId, 900L)).Single());
            Assert.AreEqual((WorkRelationType.SourceMaterial, manga), (await ReadAsync(host, 900)).Select(relation => (relation.WorkRelationTypeId, relation.RelatedWorkId!.Value)).Single());

            // Removing a provider's relation hides it for good: the next sync does not bring it back, and it leaves the other Work's view too.
            using var hidden = await host.SendAsync(HttpMethod.Delete, "/api/v1/admin/works/" + manga + "/relations/" + sequelRow.WorkRelationId);
            Assert.AreEqual(HttpStatusCode.OK, hidden.StatusCode, await hidden.Content.ReadAsStringAsync());
            Assert.IsFalse((await ReadAsync(host, manga)).Any(relation => relation.RelatedWorkId == sequel));
            await ImportAsync(host, 101);
            Assert.IsFalse((await ReadAsync(host, manga)).Any(relation => relation.RelatedWorkId == sequel || relation.ExternalId == "102"));
            Assert.AreEqual(1L, await host.ScalarAsync($"SELECT count(*) FROM \"WorkRelations\" WHERE \"WorkId\" = {manga} AND \"IsSuppressed\""));

            // A person puts it back: it is theirs now, shown on both Works.
            using var restored = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/" + manga + "/relations", new CreateWorkRelationDataV1(WorkRelationType.Sequel, sequel));
            Assert.AreEqual(HttpStatusCode.OK, restored.StatusCode, await restored.Content.ReadAsStringAsync());
            Assert.AreEqual("manual", (await ReadAsync(host, manga)).Single(relation => relation.RelatedWorkId == sequel).SourceKey);
            Assert.IsTrue((await ReadAsync(host, sequel)).Any(relation => relation.RelatedWorkId == manga && relation.WorkRelationTypeId == WorkRelationType.Prequel));

            // A provider that no longer names a relation takes its rows away, and a person's own stay.
            aniList.Remove(101, "ADAPTATION");
            await ImportAsync(host, 101);
            Assert.IsFalse((await ReadAsync(host, manga)).Any(relation => relation.RelatedWorkId == 900));
            Assert.IsTrue((await ReadAsync(host, manga)).Any(relation => relation.RelatedWorkId == sequel));
        }

        [TestMethod]
        public async Task Relations_ManualAdd_RefusesDuplicatesSelfAndUnknownTitlesAndDeletesOnlyItsOwn()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear", "FranchiseId") VALUES (10, 1, 'First Film', 2001, NULL), (20, 1, 'Second Film', 2003, NULL), (30, 3, 'The Book', 1999, NULL);
                INSERT INTO "ExternalWorks" ("Id", "Namespace", "Value", "MediaTypeId", "Title", "ReleaseYear") VALUES (500, 'tmdb_movie', '77', 1, 'Remake Elsewhere', 2020);
                INSERT INTO "Franchises" ("Id", "Name") VALUES (1, 'The Films');
                UPDATE "Works" SET "FranchiseId" = 1 WHERE "Id" IN (10, 20);
                """);

            // The series alone relates two Works as one universe; a person's relation between them replaces that row instead of adding a second.
            var series = await ReadAsync(host, 10);
            Assert.AreEqual((WorkRelationType.SharedUniverse, 20L, (long?)null, "manual"), series.Select(relation => (relation.WorkRelationTypeId, relation.RelatedWorkId!.Value, relation.WorkRelationId, relation.SourceKey)).Single());
            await AddAsync(host, 20, new CreateWorkRelationDataV1(WorkRelationType.Prequel, 10));
            var viewed = await ReadAsync(host, 10);
            Assert.AreEqual((WorkRelationType.Sequel, "manual"), viewed.Select(relation => (relation.WorkRelationTypeId, relation.SourceKey)).Single());
            Assert.AreEqual(WorkRelationType.Prequel, (await ReadAsync(host, 20)).Single().WorkRelationTypeId);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"WorkRelations\" WHERE \"WorkRelationTypeId\" = 1 AND \"WorkId\" = 10 AND \"RelatedWorkId\" = 20"));

            // The same relation from either side, a Work to itself, nothing, both and an unknown title are refused.
            foreach (var (workId, data) in new (long, CreateWorkRelationDataV1)[]
            {
                (10, new(WorkRelationType.Sequel, 20)),
                (20, new(WorkRelationType.Prequel, 10)),
                (10, new(WorkRelationType.Sequel, 10)),
                (10, new(WorkRelationType.Sequel)),
                (10, new(WorkRelationType.Sequel, 30, "tmdb_movie", "77")),
                (10, new(WorkRelationType.Sequel, 999)),
                (10, new(WorkRelationType.Sequel, null, "tmdb_movie", "404"))
            })
            {
                using var refused = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/" + workId + "/relations", data);
                Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode, workId + " " + data.WorkRelationTypeId + " " + data.RelatedWorkId + await refused.Content.ReadAsStringAsync());
            }

            // A catalog title that is no Work is related as the Work sees it; a second type for the same title is another relation.
            await AddAsync(host, 10, new CreateWorkRelationDataV1(WorkRelationType.Alternative, null, "tmdb_movie", "77"));
            await AddAsync(host, 10, new CreateWorkRelationDataV1(WorkRelationType.Other, null, "tmdb_movie", "77"));
            var external = (await ReadAsync(host, 10)).Where(relation => relation.ExternalId == "77").ToArray();
            CollectionAssert.AreEquivalent(new[] { WorkRelationType.Alternative, WorkRelationType.Other }, external.Select(relation => relation.WorkRelationTypeId).ToArray());
            Assert.AreEqual("Remake Elsewhere", external[0].Title);

            // The relation of a person is removed for good; a Work that does not belong to it refuses.
            using var gone = await host.SendAsync(HttpMethod.Delete, "/api/v1/admin/works/10/relations/" + external[0].WorkRelationId);
            Assert.AreEqual(HttpStatusCode.OK, gone.StatusCode, await gone.Content.ReadAsStringAsync());
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"WorkRelations\" WHERE \"Id\" = " + external[0].WorkRelationId));
            using var foreign = await host.SendAsync(HttpMethod.Delete, "/api/v1/admin/works/30/relations/" + external[1].WorkRelationId);
            Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);

            // Deleting a Work removes its relations on both sides.
            await host.Fixture.RunSqlAsync("DELETE FROM \"Works\" WHERE \"Id\" = 20");
            Assert.IsFalse((await ReadAsync(host, 10)).Any(relation => relation.RelatedWorkId == 20));
        }

        private static async Task<long> ImportAsync(Host host, int id)
        {
            using var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Book, id.ToString(System.Globalization.CultureInfo.InvariantCulture), MediaSubtype.Manga, "anilist"));
            Assert.AreEqual(HttpStatusCode.OK, imported.StatusCode, await imported.Content.ReadAsStringAsync());
            return (await imported.Content.ReadFromJsonAsync<Data.Admin.Media.V1.WorkDetailsV1>())!.Id;
        }

        private static async Task<IReadOnlyList<WorkRelationV1>> ReadAsync(Host host, long workId)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/works/" + workId + "/relations");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<BaseDataList<WorkRelationV1>>())!;
        }

        private static async Task AddAsync(Host host, long workId, CreateWorkRelationDataV1 data)
        {
            using var response = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/" + workId + "/relations", data);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        // Two manga: the first names an anime adaptation, a sequel and a music video; the second names the first as its prequel.
        private sealed class FakeAniList : HttpMessageHandler
        {
            private readonly Dictionary<int, List<string>> _relations = new()
            {
                [101] = [Edge("ADAPTATION", 201, "ANIME", "TV", "Example Anime", 2021), Edge("SEQUEL", 102, "MANGA", "MANGA", "Example Manga 2", 2021), Edge("OTHER", 301, "ANIME", "MUSIC", "Opening", 2021)],
                [102] = [Edge("PREQUEL", 101, "MANGA", "MANGA", "Example Manga", 2019)]
            };

            public void Remove(int id, string relationType) => _relations[id].RemoveAll(edge => edge.Contains("\"" + relationType + "\"", StringComparison.Ordinal));

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var id = body.RootElement.GetProperty("variables").GetProperty("id").GetInt32();
                var title = id == 101 ? "Example Manga" : "Example Manga 2";
                var media = "{\"id\":" + id + ",\"type\":\"MANGA\",\"format\":\"MANGA\",\"title\":{\"romaji\":\"" + title + "\"},\"synonyms\":[],\"startDate\":{\"year\":" + (id == 101 ? 2019 : 2021)
                    + "},\"volumes\":3,\"staff\":{\"edges\":[]},\"relations\":{\"edges\":[" + string.Join(',', _relations[id]) + "]}}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{\"Media\":" + media + "}}", Encoding.UTF8, "application/json") };
            }

            private static string Edge(string relationType, int id, string type, string format, string title, int year) =>
                "{\"relationType\":\"" + relationType + "\",\"node\":{\"id\":" + id + ",\"type\":\"" + type + "\",\"format\":\"" + format + "\",\"title\":{\"romaji\":\"" + title + "\"},\"startDate\":{\"year\":" + year + "}}}";
        }
    }
}
