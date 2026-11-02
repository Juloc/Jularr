using System.Net;
using System.Net.Http.Json;
using System.Text;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Integrations.Logic.Tvdb;
using Microsoft.Extensions.DependencyInjection;
using static Jularr.Backend.Tests.AuthenticationTests;

namespace Jularr.Backend.Tests
{
    // Series of TheTVDB in the library: their subtype, their people, their session and the catalog titles that name them too.
    [TestClass]
    public sealed class SeriesCatalogTests
    {
        private const string TvdbKey = "tvdb-series-catalog-key";

        [TestMethod]
        public async Task Series_NewOneIsClassifiedAnime_ARefreshNeverChangesTheSubtype_AndOneTvdbSessionServesBoth()
        {
            var tvdb = new FakeTvdb();
            await using var host = await CreateHostAsync(tvdb);
            using var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Series, "81189", null, "tvdb"));
            Assert.AreEqual(HttpStatusCode.OK, imported.StatusCode, await imported.Content.ReadAsStringAsync());
            var work = (await imported.Content.ReadFromJsonAsync<WorkDetailsV1>())!;
            Assert.AreEqual(MediaSubtype.Anime, work.MediaSubtypeId, "A new series TheTVDB calls anime is proposed as Anime.");

            // The library's series has no subtype; TheTVDB still calls it anime, and a refresh leaves it as it is.
            await host.Fixture.RunSqlAsync($"""
                UPDATE "Works" SET "MediaSubtypeId" = NULL WHERE "Id" = {work.Id};
                INSERT INTO "WantedItems" ("WorkId") VALUES ({work.Id});
                """);
            using var refreshed = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/" + work.Id + "/metadata");
            Assert.AreEqual(HttpStatusCode.OK, refreshed.StatusCode, await refreshed.Content.ReadAsStringAsync());
            Assert.AreEqual(DBNull.Value, await host.ScalarAsync($"SELECT \"MediaSubtypeId\" FROM \"Works\" WHERE \"Id\" = {work.Id}"));
            Assert.AreEqual(1, tvdb.Logins, "Both imports use one session.");

            // A session TheTVDB no longer accepts is replaced once and the request goes on.
            tvdb.RefusesCurrentSession = true;
            using var renewed = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/" + work.Id + "/metadata");
            Assert.AreEqual(HttpStatusCode.OK, renewed.StatusCode, await renewed.Content.ReadAsStringAsync());
            Assert.AreEqual(2, tvdb.Logins);
        }

        [TestMethod]
        public async Task TvdbPeople_JoinThePersonAnotherCatalogKnowsByTheSameTmdbId_AndAreLookedUpOnce()
        {
            var tvdb = new FakeTvdb();
            await using var host = await CreateHostAsync(tvdb);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "People" ("Id", "Name") VALUES (9900, 'Bryan Example');
                INSERT INTO "PersonExternalIdentities" ("PersonId", "Namespace", "Value") VALUES (9900, 'tmdb', '17419');
                """);
            using var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Series, "81189", null, "tvdb"));
            Assert.AreEqual(HttpStatusCode.OK, imported.StatusCode, await imported.Content.ReadAsStringAsync());
            var work = (await imported.Content.ReadFromJsonAsync<WorkDetailsV1>())!;

            // TheTVDB's person 300 is the TMDB person already known; 301 has no other id and is a person of its own, known by TheTVDB's id from now on.
            Assert.AreEqual(9900L, await host.ScalarAsync("""SELECT "PersonId" FROM "PersonExternalIdentities" WHERE "Namespace" = 'tvdb' AND "Value" = '300'"""));
            Assert.AreEqual("tvdb:301", await host.ScalarAsync("""
                SELECT string_agg(other."Namespace" || ':' || other."Value", ',' ORDER BY other."Namespace")
                FROM "PersonExternalIdentities" AS identity JOIN "PersonExternalIdentities" AS other ON other."PersonId" = identity."PersonId"
                WHERE identity."Namespace" = 'tvdb' AND identity."Value" = '301'
                """));
            Assert.AreEqual(1L, await host.ScalarAsync($"SELECT count(*) FROM \"WorkPeople\" WHERE \"WorkId\" = {work.Id} AND \"PersonId\" = 9900"));
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "People" WHERE "Name" = 'Bryan Example'"""));
            Assert.AreEqual(2, tvdb.PeopleRequests);

            // A refresh asks TheTVDB for no person again.
            using var refreshed = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/" + work.Id + "/metadata");
            Assert.AreEqual(HttpStatusCode.OK, refreshed.StatusCode, await refreshed.Content.ReadAsStringAsync());
            Assert.AreEqual(2, tvdb.PeopleRequests);
        }

        [TestMethod]
        public async Task SeriesSearch_NamesTheLibrarySeriesByItsImdbId_TakesNoTwin_AndKeepsItsTitle()
        {
            var tvdb = new FakeTvdb();
            await using var host = await CreateHostAsync(tvdb);

            // A series a media server brought with its IMDb id only.
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear") VALUES (7001, 2, 'Mein Breaking Example', 2008);
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (7001, 'imdb', 'tt0903747');
                INSERT INTO "WantedItems" ("WorkId") VALUES (7001);
                """);
            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/search", new SearchDiscoverDataV1("Breaking", MediaType.Series));
            Assert.AreEqual(HttpStatusCode.OK, searched.StatusCode, await searched.Content.ReadAsStringAsync());

            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "Works" WHERE "MediaTypeId" = 2"""));
            Assert.AreEqual(7001L, await host.ScalarAsync("""SELECT "WorkId" FROM "WorkExternalIdentities" WHERE "Namespace" = 'tvdb' AND "Value" = '81189'"""));
            Assert.AreEqual("Mein Breaking Example", await host.ScalarAsync("""SELECT "CanonicalTitle" FROM "Works" WHERE "Id" = 7001"""), "A search result does not rename a library series.");
        }

        [TestMethod]
        public async Task Refresh_WhereACatalogTwinHoldsTheTvdbId_MergesTheTwinIntoTheLibrarySeries()
        {
            var tvdb = new FakeTvdb();
            await using var host = await CreateHostAsync(tvdb);

            // The library series holds the IMDb id; a catalog title of TheTVDB, on a Discover list, holds the TheTVDB id.
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear") VALUES (7001, 2, 'Breaking Example', 2008);
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "CatalogNamespace", "TrendingRank") VALUES (7002, 2, 'Breaking Example', 'tvdb', 3);
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (7001, 'imdb', 'tt0903747'), (7002, 'tvdb', '81189');
                INSERT INTO "WantedItems" ("WorkId") VALUES (7001);
                INSERT INTO "ExternalWorkFeeds" ("Id", "Key", "Namespace", "MediaTypeId", "DiscoverFeedTypeId", "Page") VALUES (77, 'series-trending', 'tmdb_tv', 2, 1, 1);
                INSERT INTO "ExternalWorkFeedItems" ("FeedId", "Position", "WorkId") VALUES (77, 1, 7002);
                """);
            using var refreshed = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/7001/metadata");
            Assert.AreEqual(HttpStatusCode.OK, refreshed.StatusCode, await refreshed.Content.ReadAsStringAsync());

            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "Works" WHERE "Id" = 7002"""));
            Assert.AreEqual(7001L, await host.ScalarAsync("""SELECT "WorkId" FROM "WorkExternalIdentities" WHERE "Namespace" = 'tvdb' AND "Value" = '81189'"""));
            Assert.AreEqual("7001:tvdb:3", await host.ScalarAsync("""
                SELECT item."WorkId" || ':' || work."CatalogNamespace" || ':' || work."TrendingRank" FROM "ExternalWorkFeedItems" AS item JOIN "Works" AS work ON work."Id" = item."WorkId" WHERE item."FeedId" = 77
                """));
            Assert.AreEqual(true, await host.ScalarAsync("""SELECT "DetailedAt" IS NOT NULL FROM "Works" WHERE "Id" = 7001"""));
        }

        private static async Task<Host> CreateHostAsync(FakeTvdb tvdb)
        {
            var host = await Host.CreateAsync(library: true, services: services => services.AddHttpClient<TvdbClient>().ConfigurePrimaryHttpMessageHandler(() => tvdb));
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
            var provider = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Providers\" WHERE \"Key\" = 'tvdb'"))!;
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/" + provider + "/secret", new SaveProviderSecretDataV1(TvdbKey));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            return host;
        }

        // Breaking Example: TheTVDB 81189, IMDb tt0903747, TMDB 1396, anime by TheTVDB's genre; two actors, 300 known to TMDB as 17419, 301 without other ids.
        private sealed class FakeTvdb : HttpMessageHandler
        {
            private string? _refused;
            private int _peopleRequests;
            public int Logins { get; private set; }

            // People are asked a few at a time.
            public int PeopleRequests => Volatile.Read(ref _peopleRequests);

            // The session given so far is refused from now on.
            public bool RefusesCurrentSession { get; set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/v4/login")
                {
                    Logins++;
                    return Reply("{\"data\":{\"token\":\"tvdb-session-" + Logins + "\"}}");
                }

                if (RefusesCurrentSession)
                {
                    _refused = "tvdb-session-" + Logins;
                    RefusesCurrentSession = false;
                }

                if (request.Headers.Authorization?.Parameter is not { } token || token == _refused)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                }

                if (path.StartsWith("/v4/people/", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _peopleRequests);
                }

                const string Episodes = """{"data":{"episodes":[{"id":1,"seasonNumber":1,"number":1,"name":"Pilot","aired":"2008-01-20"}]},"links":{"next":null}}""";
                return path switch
                {
                    "/v4/series/81189/extended" => Reply("""
                        {"data":{"id":81189,"name":"Breaking Example","firstAired":"2008-01-20","year":"2008","originalLanguage":"eng","originalCountry":"usa",
                        "genres":[{"name":"Drama"},{"name":"Anime"}],"remoteIds":[{"id":"tt0903747","sourceName":"IMDB"},{"id":"1396","sourceName":"TheMovieDB.com"}],
                        "characters":[{"peopleId":300,"personName":"Bryan Example","peopleType":"Actor","name":"Walt","sort":1},{"peopleId":301,"personName":"New Person","peopleType":"Actor","name":"Jesse","sort":2}],
                        "seasons":[{"number":1,"type":{"type":"official"}}]}}
                        """),
                    "/v4/series/81189/episodes/default" or "/v4/series/81189/episodes/default/eng" => Reply(Episodes),
                    "/v4/people/300/extended" => Reply("""{"data":{"id":300,"name":"Bryan Example","remoteIds":[{"id":"17419","sourceName":"TheMovieDB.com"},{"id":"nm0186505","sourceName":"IMDB"}]}}"""),
                    "/v4/people/301/extended" => Reply("""{"data":{"id":301,"name":"New Person","remoteIds":[]}}"""),
                    "/v4/search/remoteid/tt0903747" => Reply("""{"data":[{"series":{"id":81189}}]}"""),
                    "/v4/search" => Reply("""
                        {"data":[{"tvdb_id":"81189","name":"Breaking Example","year":"2008","type":"series","primary_language":"eng","translations":{"eng":"Breaking Example (TheTVDB)"},
                        "remote_ids":[{"id":"tt0903747","sourceName":"IMDB"},{"id":"1396","sourceName":"TheMovieDB.com"}]}]}
                        """),
                    _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
                };
            }

            private static Task<HttpResponseMessage> Reply(string json) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
