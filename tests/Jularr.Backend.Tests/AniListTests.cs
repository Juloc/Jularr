using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.System.Media.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.AniList;
using Jularr.Integrations.Logic.AniList;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class AniListTests
    {
        [TestMethod]
        public void Align_SplitSeasonsAndRunningEntries_MapInOrder()
        {
            // Two entries share a TMDB season; a running entry takes the rest of its season.
            CollectionAssert.AreEqual(new[] { (1, 10, 0), (2, 20, 0), (2, 30, 12) },
                AniListMatching.Align([(1, 12), (2, 24)], [(10, 12), (20, 12), (30, 12)]).ToArray());
            CollectionAssert.AreEqual(new[] { (1, 10, 0), (2, 20, 0) }, AniListMatching.Align([(1, 12), (2, 3)], [(10, 12), (20, null)]).ToArray());

            // An entry longer than its season stops the alignment.
            Assert.AreEqual(0, AniListMatching.Align([(1, 12), (2, 12)], [(10, 24)]).Count);
        }

        [TestMethod]
        public void Choose_OneFittingTitleAndYear_IsTheMatch()
        {
            AniListMedia Media(int id, string title, int year, string format) => new(id, format, title, null, null, [], year, 12, null, null, null, [], [], [], null, null);
            var results = new[] { Media(1, "Example Quest", 2019, "TV"), Media(2, "Example Quest", 2019, "MOVIE"), Media(3, "Example Quest 2", 2021, "TV") };
            var formats = AniListMatching.Formats(Data.Enums.Media.MediaType.Series, Data.Enums.Media.MediaSubtype.Anime);
            Assert.AreEqual(1, AniListMatching.Choose(results, ["Example Quest!"], 2020, formats).Match?.Id);
            Assert.IsNull(AniListMatching.Choose(results, ["Example Quest"], 2015, formats).Match);
            Assert.IsNull(AniListMatching.Choose([.. results, Media(4, "example quest", 2019, "ONA")], ["Example Quest"], 2019, formats).Match);
            Assert.IsTrue(AniListMatching.IsAnime([16, 10759], "ja", []));
            Assert.IsFalse(AniListMatching.IsAnime([16], "en", ["US"]));
            Assert.AreNotEqual(AniListMatching.Normalize("ガンダム"), AniListMatching.Normalize("カンタム"));
            Assert.AreEqual("pokemon", AniListMatching.Normalize("Pokémon"));
        }

        [TestMethod]
        public async Task Matching_SeriesSeasonsAndReviews_FollowTheSequelChain()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var aniList = new FakeAniList();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, services: services =>
            {
                services.AddHttpClient<AniListClient>().ConfigurePrimaryHttpMessageHandler(() => aniList);
                services.AddHttpClient<AniBridgeMappings>().ConfigurePrimaryHttpMessageHandler(() => new FakeAniBridge());
            });

            // An anime series with two seasons, a second one with two candidates and a Manga without any.
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "ReleaseYear") VALUES (10, 2, 1, 'Example Quest', 2019), (20, 2, 1, 'Twin Title', 2020), (30, 3, 2, 'Nowhere Manga', 2001);
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (100, 10, 1, 1), (200, 10, 1, 2), (300, 20, 1, 1);
                INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") SELECT 20, 300, 2, n FROM generate_series(1, 12) AS n;
                INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") SELECT 10, 100, 2, n FROM generate_series(1, 12) AS n;
                INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") SELECT 10, 200, 2, n FROM generate_series(1, 24) AS n;
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (20, 'tvdb', '88');
                """);
            await RunAsync(host);

            Assert.AreEqual("100:1:0,200:2:0,200:3:12", await host.ScalarAsync("""SELECT string_agg("WorkUnitId" || ':' || "Value" || ':' || "EpisodeOffset", ',' ORDER BY "Value") FROM "WorkUnitExternalIdentities" """));
            Assert.AreEqual("10:1,20:2,30:3", await host.ScalarAsync("""SELECT string_agg("WorkId" || ':' || "IdentityMatchStatusTypeId", ',' ORDER BY "WorkId") FROM "WorkIdentityMatches" """));

            // Discover opens the existing TVDB-structured series at the season mapped to this AniList entry.
            using var details = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/entry",
                new ReadDiscoverCatalogEntryDataV1(Data.Enums.Media.MediaType.Series, "anilist", "2", Data.Enums.Media.MediaSubtype.Anime));
            Assert.AreEqual(HttpStatusCode.OK, details.StatusCode, await details.Content.ReadAsStringAsync());
            var catalog = await details.Content.ReadFromJsonAsync<DiscoverCatalogEntryV1>();
            Assert.AreEqual(10L, catalog?.WorkId);
            Assert.AreEqual(2, catalog?.SeasonNumber);

            // AniBridge resolves a TVDB identity even when title matching is still awaiting manual review.
            using var bridged = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/entry",
                new ReadDiscoverCatalogEntryDataV1(Data.Enums.Media.MediaType.Series, "anilist", "7", Data.Enums.Media.MediaSubtype.Anime));
            Assert.AreEqual(HttpStatusCode.OK, bridged.StatusCode, await bridged.Content.ReadAsStringAsync());
            var bridgedEntry = await bridged.Content.ReadFromJsonAsync<DiscoverCatalogEntryV1>();
            Assert.AreEqual(20L, bridgedEntry?.WorkId);
            Assert.AreEqual(1, bridgedEntry?.SeasonNumber);

            // The person picks one candidate; the mapping is manual. Neither a missing entry nor a decided Work is accepted.
            using var listed = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/works/matches");
            var reviews = (await listed.Content.ReadFromJsonAsync<PageResult<IdentityReviewV1>>())!;
            CollectionAssert.AreEquivalent(new[] { 7, 8 }, reviews.Items.Single(review => review.WorkId == 20).CandidateIds);
            using var empty = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/20/matches", new ResolveIdentityReviewDataV1(null));
            Assert.AreEqual(HttpStatusCode.BadRequest, empty.StatusCode);
            using var decided = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/10/matches", new ResolveIdentityReviewDataV1(null, true));
            Assert.AreEqual(HttpStatusCode.NotFound, decided.StatusCode);
            using var resolved = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/20/matches", new ResolveIdentityReviewDataV1(8));
            Assert.AreEqual(HttpStatusCode.OK, resolved.StatusCode, await resolved.Content.ReadAsStringAsync());
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "WorkIdentityMatches" WHERE "WorkId" = 20 AND "IdentityMatchStatusTypeId" = 1"""));
            Assert.AreEqual(true, await host.ScalarAsync("""SELECT "IsManual" FROM "WorkUnitExternalIdentities" WHERE "Value" = '8'"""));

            // A match without any entry is not tried again for a while.
            await RunAsync(host);
            Assert.AreEqual(3, aniList.Searches);
        }

        [TestMethod]
        public async Task Matching_VerifiedTvdbMappingsSkipTitleGuessing()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var aniList = new FakeAniList();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, services: services =>
            {
                services.AddHttpClient<AniListClient>().ConfigurePrimaryHttpMessageHandler(() => aniList);
                services.AddHttpClient<AniBridgeMappings>().ConfigurePrimaryHttpMessageHandler(() => new FakeAniBridge());
            });

            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle") VALUES (40, 2, 1, 'Unknown Anime');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (40, 'tvdb', '99');
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (400, 40, 1, 1);
                INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number")
                    SELECT 40, 400, 2, n FROM generate_series(1, 12) AS n;
                """);

            await RunAsync(host);
            Assert.AreEqual("41", await host.ScalarAsync("""SELECT "Value" FROM "WorkUnitExternalIdentities" WHERE "WorkUnitId" = 400"""));
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "WorkIdentityMatches" WHERE "WorkId" = 40 AND "IdentityMatchStatusTypeId" = 1"""));
            Assert.AreEqual(0, aniList.Searches);
        }

        [TestMethod]
        public async Task Matching_ConflictingTvdbRangesDoNotPartiallyWrite()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, services: services =>
            {
                services.AddHttpClient<AniListClient>().ConfigurePrimaryHttpMessageHandler(() => new FakeAniList());
                services.AddHttpClient<AniBridgeMappings>().ConfigurePrimaryHttpMessageHandler(() => new FakeAniBridge());
            });

            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle") VALUES (50, 2, 1, 'Other Anime');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (50, 'tvdb', '100');
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (500, 50, 1, 1), (600, 50, 1, 2);
                INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number")
                    SELECT 50, 500, 2, n FROM generate_series(1, 12) AS n;
                INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number")
                    SELECT 50, 600, 2, n FROM generate_series(1, 12) AS n;
                INSERT INTO "WorkUnitExternalIdentities" ("WorkUnitId", "Namespace", "Value", "EpisodeOffset")
                    VALUES (500, 'anilist', '66', 0);
                """);

            await RunAsync(host);
            Assert.AreEqual(2L, await host.ScalarAsync("""SELECT "IdentityMatchStatusTypeId"::bigint FROM "WorkIdentityMatches" WHERE "WorkId" = 50"""));
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "WorkUnitExternalIdentities" WHERE "Value" = '67'"""));
            Assert.AreEqual(500L, await host.ScalarAsync("""SELECT "WorkUnitId" FROM "WorkUnitExternalIdentities" WHERE "Value" = '66'"""));
        }

        private static async Task RunAsync(Host host)
        {
            await using var scope = host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<MatchAniListV1>().ExecuteAsync<UpdateResult>(new MediaMatchParametersV1(), default);
        }

        private sealed class FakeAniBridge : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {
                            "$meta":{"schema_version":"3.0.3"},
                            "anilist:7":{"tvdb_show:88:s1":{"1-12":"1-12"}},
                            "tvdb_show:99:s1":{"anilist:41":{"1-12":"1-12"}},
                            "tvdb_show:100:s1":{"anilist:67":{"1-12":"1-12"}},
                            "tvdb_show:100:s2":{"anilist:66":{"1-12":"1-12"}}
                        }
                        """, Encoding.UTF8, "application/json")
                });
        }

        private sealed class FakeAniList : HttpMessageHandler
        {
            // Example Quest: 1 -> 2 -> 3 (12 episodes each); Twin Title has two equal candidates.
            private static readonly Dictionary<int, string> Media = new()
            {
                [1] = Entry(1, "Example Quest", 2019, 12, [2]),
                [2] = Entry(2, "Example Quest 2", 2020, 12, [3]),
                [3] = Entry(3, "Example Quest 2 Part 2", 2021, 12, []),
                [7] = Entry(7, "Twin Title", 2020, 12, []),
                [8] = Entry(8, "Twin Title", 2020, 12, [])
            };

            public int Searches { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var variables = body.RootElement.GetProperty("variables");
                if (variables.TryGetProperty("search", out var search))
                {
                    Searches++;
                    var found = search.GetString() switch
                    {
                        "Example Quest" => new[] { Media[1], Media[2] },
                        "Twin Title" => [Media[7], Media[8]],
                        _ => []
                    };
                    return Json("{\"data\":{\"Page\":{\"media\":[" + string.Join(',', found) + "]}}}");
                }

                return Json("{\"data\":{\"Media\":" + Media[variables.GetProperty("id").GetInt32()] + "}}");
            }

            private static string Entry(int id, string title, int year, int episodes, int[] sequels)
            {
                var relations = string.Join(',', sequels.Select(sequel => "{\"relationType\":\"SEQUEL\",\"node\":{\"id\":" + sequel + ",\"type\":\"ANIME\"}}"));
                return "{\"id\":" + id + ",\"type\":\"ANIME\",\"format\":\"TV\",\"title\":{\"romaji\":\"" + title + "\"},\"synonyms\":[],\"startDate\":{\"year\":" + year
                    + "},\"episodes\":" + episodes + ",\"relations\":{\"edges\":[" + relations + "]}}";
            }

            private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
