using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.System.Media.V1;
using Jularr.Integrations.Data.AniList;
using Jularr.Integrations.Logic.AniBridge;
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
        public void FromMappings_SeasonsOffsetsSpecialsAndConflicts()
        {
            static AnimeMappingData Row(int id, int season, int aniListEpisode, int first, int? last, bool isAmbiguous = false) => new(id, season, aniListEpisode, first, last, isAmbiguous);
            static string Seasons(IReadOnlyList<AnimeMappingData> rows) => string.Join(',', AniListMatching.FromMappings(rows).Seasons.Select(entry => entry.Season + ":" + entry.AniListId + ":" + entry.Offset));

            // An exact mapping per season, two cours in one season (the second at its offset) and an OVA in the specials.
            IReadOnlyList<AnimeMappingData> mapped = [Row(16498, 1, 1, 1, 25), Row(20958, 2, 1, 1, 12), Row(99147, 3, 1, 1, 12), Row(104578, 3, 1, 13, 22), Row(18397, 0, 1, 7, 13)];
            Assert.AreEqual("0:18397:6,1:16498:0,2:20958:0,3:99147:0,3:104578:12", Seasons(mapped));
            Assert.AreEqual(0, AniListMatching.FromMappings(mapped).Conflicts.Count);

            // An entry over several seasons sits where its first episode is; one starting before its season's first episode has no place.
            Assert.AreEqual("1:21:0", Seasons([Row(21, 1, 1, 1, 8), Row(21, 2, 9, 1, 22), Row(21, 0, 542, 39, 39)]));
            Assert.AreEqual("", Seasons([Row(9, 2, 36, 1, 48)]));

            // Overlapping ranges of a regular season, or an entry another series names too, wait for a person; overlapping specials are left out.
            CollectionAssert.AreEqual(new[] { 400, 102401 }, AniListMatching.FromMappings([Row(400, 1, 1, 1, 26), Row(102401, 1, 1, 16, 23)]).Conflicts.ToArray());
            CollectionAssert.AreEqual(new[] { 5 }, AniListMatching.FromMappings([Row(5, 1, 1, 1, 12, true), Row(6, 2, 1, 1, null)]).Conflicts.ToArray());
            CollectionAssert.AreEqual(new[] { 1, 2 }, AniListMatching.FromMappings([Row(1, 1, 1, 1, null), Row(2, 1, 1, 30, 40)]).Conflicts.ToArray(), "A running entry reaches every later episode.");
            Assert.AreEqual("1:1:0", Seasons([Row(1, 1, 1, 1, 12), Row(7, 0, 1, 1, 1), Row(8, 0, 1, 1, 1)]));
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
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, services: services => services.AddHttpClient<AniListClient>().ConfigurePrimaryHttpMessageHandler(() => aniList));

            // An anime series with two seasons, a second one with two candidates and a Manga without any, all with their details.
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "ReleaseYear", "DetailedAt") VALUES
                    (10, 2, 1, 'Example Quest', 2019, now()), (20, 2, 1, 'Twin Title', 2020, now()), (30, 3, 2, 'Nowhere Manga', 2001, now());
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (100, 10, 1, 1), (200, 10, 1, 2), (300, 20, 1, 1);
                INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") SELECT 20, 300, 2, n FROM generate_series(1, 12) AS n;
                INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") SELECT 10, 100, 2, n FROM generate_series(1, 12) AS n;
                INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") SELECT 10, 200, 2, n FROM generate_series(1, 24) AS n;
                """);
            await RunAsync(host);

            Assert.AreEqual("100:1:0,200:2:0,200:3:12", await host.ScalarAsync("""SELECT string_agg("WorkUnitId" || ':' || "Value" || ':' || "EpisodeOffset", ',' ORDER BY "Value") FROM "WorkUnitExternalIdentities" """));
            Assert.AreEqual("10:1,20:2,30:3", await host.ScalarAsync("""SELECT string_agg("WorkId" || ':' || "IdentityMatchStatusTypeId", ',' ORDER BY "WorkId") FROM "WorkIdentityMatches" """));

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
        public async Task Matching_SeasonOfAPlaceholder_MovesItsFavoriteRelationsListPlaceAndRequestToTheSeries()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var aniList = new FakeAniList();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, services: services => services.AddHttpClient<AniListClient>().ConfigurePrimaryHttpMessageHandler(() => aniList));

            // The series with two seasons, a favorite; its second season was a Work of its own until now: a favorite too, named by relations, on a list and requested.
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "ReleaseYear", "DetailedAt") VALUES (10, 2, 1, 'Example Quest', 2019, now()), (30, 1, NULL, 'Example Quest Movie', 2021, now());
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (100, 10, 1, 1), (200, 10, 1, 2);
                INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") SELECT 10, 100, 2, n FROM generate_series(1, 12) AS n;
                INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") SELECT 10, 200, 2, n FROM generate_series(1, 24) AS n;
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "ReleaseYear", "CatalogNamespace") VALUES (40, 2, 1, 'Example Quest 2', 2020, 'anilist');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (40, 'anilist', '2');
                INSERT INTO "WatchlistEntries" ("ProfileId", "WorkId")
                SELECT profile."Id", work."Id"
                FROM (SELECT "ActiveProfileId" AS "Id" FROM "AccountSessions" WHERE "ActiveProfileId" IS NOT NULL ORDER BY "CreatedAt" DESC LIMIT 1) AS profile CROSS JOIN (VALUES (10), (40)) AS work ("Id");
                INSERT INTO "WorkRelations" ("WorkId", "RelatedWorkId", "WorkRelationTypeId", "SourceKey") VALUES (30, 40, 3, 'anilist'), (10, 40, 1, 'anilist');
                INSERT INTO "ExternalWorkFeeds" ("Id", "Key", "Namespace", "MediaTypeId", "MediaSubtypeId", "DiscoverFeedTypeId", "Page") VALUES (1, 'anime-trending', 'anilist', 2, 1, 1, 1);
                INSERT INTO "ExternalWorkFeedItems" ("FeedId", "Position", "WorkId") VALUES (1, 1, 40);
                INSERT INTO "AcquisitionRequests" ("WorkId", "RequestedByProfileId", "RequestedByAccountId", "UiLocaleId")
                SELECT 40, "ActiveProfileId", 41, 1 FROM "AccountSessions" WHERE "ActiveProfileId" IS NOT NULL ORDER BY "CreatedAt" DESC LIMIT 1;
                INSERT INTO "Notifications" ("ProfileId", "NotificationTypeId", "WorkId", "Subject")
                SELECT "ActiveProfileId", 3, 40, 'Example Quest 2' FROM "AccountSessions" WHERE "ActiveProfileId" IS NOT NULL ORDER BY "CreatedAt" DESC LIMIT 1;
                INSERT INTO "WantedItems" ("Id", "WorkId") VALUES (700, 10), (701, 40);
                INSERT INTO "AcquisitionReleases" ("WantedItemId", "ReleaseIdentity", "Title", "AcquisitionReleaseStatusTypeId") VALUES (701, 'nzb:quest-two', 'Example Quest 2 S01', 4);
                """);
            await RunAsync(host);

            // The mapping puts the entry on the second season, and everything of the season's Work is the series' now, at that season; the season's Work is gone.
            Assert.AreEqual(200L, await host.ScalarAsync("""SELECT "WorkUnitId" FROM "WorkUnitExternalIdentities" WHERE "Value" = '2'"""));
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "Works" WHERE "Id" = 40"""));
            Assert.AreEqual("10", await host.ScalarAsync("""SELECT string_agg(DISTINCT "WorkId"::text, ',') FROM "WatchlistEntries" """));
            Assert.AreEqual("30:10:3", await host.ScalarAsync("""SELECT string_agg("WorkId" || ':' || "RelatedWorkId" || ':' || "WorkRelationTypeId", ',') FROM "WorkRelations" """));
            Assert.AreEqual("10:2", await host.ScalarAsync("""SELECT "WorkId" || ':' || "SeasonNumber" FROM "ExternalWorkFeedItems" WHERE "FeedId" = 1 AND "Position" = 1"""));
            Assert.AreEqual(10L, await host.ScalarAsync("""SELECT "WorkId" FROM "AcquisitionRequests" """));
            Assert.AreEqual(10L, await host.ScalarAsync("""SELECT "WorkId" FROM "Notifications" """));
            Assert.AreEqual(700L, await host.ScalarAsync("""SELECT "WantedItemId" FROM "AcquisitionReleases" WHERE "ReleaseIdentity" = 'nzb:quest-two'"""),
                "The season's blocked release stays with the series' wanted entry.");
        }

        [TestMethod]
        public async Task Matching_TvdbSeriesTheMappingsName_TakesTheirSeasons_ConflictsWaitForAPerson_AndAPersonsChoiceOutlastsANewRelease()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var aniList = new FakeAniList();
            var release = new AnimeMappingTests.FakeRelease();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, services: services =>
            {
                services.AddHttpClient<AniListClient>().ConfigurePrimaryHttpMessageHandler(() => aniList);
                services.AddHttpClient<AniBridgeClient>().ConfigurePrimaryHttpMessageHandler(() => release);
            });

            // Two TheTVDB series with their seasons: one the mappings name season by season (two cours in its third season, OVAs in its specials),
            // one whose entries overlap in its first season.
            await host.Fixture.RunSqlAsync("""
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" = 'AniList';
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "ReleaseYear", "DetailedAt") VALUES (10, 2, 1, 'Attack Series', 2013, now()), (20, 2, 1, 'Interleaved', 2004, now());
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (10, 'tvdb', '267440'), (20, 'tvdb', '75911');
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (1100, 10, 1, 0), (1101, 10, 1, 1), (1102, 10, 1, 2), (1103, 10, 1, 3), (1200, 20, 1, 1);
                INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") SELECT 10, 1100, 2, n FROM generate_series(1, 20) AS n;
                INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") SELECT 10, 1101, 2, n FROM generate_series(1, 25) AS n;
                INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") SELECT 10, 1102, 2, n FROM generate_series(1, 12) AS n;
                INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") SELECT 10, 1103, 2, n FROM generate_series(1, 22) AS n;
                INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") SELECT 20, 1200, 2, n FROM generate_series(1, 26) AS n;
                """);
            Assert.IsTrue(await AnimeMappingTests.RunAsync(host));
            await RunAsync(host);

            // Every mapped entry is on its season at its offset; AniList was not searched.
            const string Mapped = """SELECT string_agg(season."Number"::integer || ':' || mapped."Value" || ':' || mapped."EpisodeOffset", ',' ORDER BY season."Number", mapped."EpisodeOffset") FROM "WorkUnitExternalIdentities" AS mapped JOIN "WorkUnits" AS season ON season."Id" = mapped."WorkUnitId" WHERE season."WorkId" = 10""";
            Assert.AreEqual("0:18397:6,0:20691:13,1:16498:0,2:20958:0,3:99147:0,3:104578:12", await host.ScalarAsync(Mapped));
            Assert.AreEqual(0, aniList.Searches);

            // The overlapping entries wait for a person, with what AniList says about them.
            Assert.AreEqual("10:1,20:2", await host.ScalarAsync("""SELECT string_agg("WorkId" || ':' || "IdentityMatchStatusTypeId", ',' ORDER BY "WorkId") FROM "WorkIdentityMatches" """));
            using var listed = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/works/matches");
            var reviews = (await listed.Content.ReadFromJsonAsync<PageResult<IdentityReviewV1>>())!;
            CollectionAssert.AreEquivalent(new[] { 400, 102401 }, reviews.Items.Single(review => review.WorkId == 20).CandidateIds);

            // A new release moves the second cour: the automatic entry follows it.
            release.ETag = "\"b\"";
            release.Body = Moved(14);
            Assert.IsTrue(await AnimeMappingTests.RunAsync(host));
            await RunAsync(host);
            StringAssert.Contains((string)(await host.ScalarAsync(Mapped))!, "3:104578:13");

            // A person's entry stays as it is through the next release.
            await host.Fixture.RunSqlAsync("""UPDATE "WorkUnitExternalIdentities" SET "IsManual" = TRUE, "EpisodeOffset" = 11 WHERE "Value" = '104578'""");
            release.ETag = "\"c\"";
            release.Body = Moved(16);
            Assert.IsTrue(await AnimeMappingTests.RunAsync(host));
            await RunAsync(host);
            StringAssert.Contains((string)(await host.ScalarAsync(Mapped))!, "3:104578:11");
            Assert.AreEqual(true, await host.ScalarAsync("""SELECT "IsManual" FROM "WorkUnitExternalIdentities" WHERE "Value" = '104578'"""));

            // The sample with the second cour of the third season starting at another episode.
            static string Moved(int first)
            {
                using var reader = new StreamReader(AnimeMappingTests.Sample());
                return reader.ReadToEnd().Replace("\"1-10\": \"13-22\"", "\"1-10\": \"" + first + "-" + (first + 9) + "\"", StringComparison.Ordinal);
            }
        }

        private static async Task RunAsync(Host host)
        {
            await using var scope = host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<MatchAniListV1>().ExecuteAsync<UpdateResult>(new MediaMatchParametersV1(), default);
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
                [8] = Entry(8, "Twin Title", 2020, 12, []),
                [400] = Entry(400, "Interleaved", 2004, 24, []),
                [102401] = Entry(102401, "Interleaved Extra", 2004, 2, [])
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
