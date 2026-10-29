using System.Net;
using System.Text;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Integrations.Logic.Tmdb;
using Jularr.Integrations.Logic.Tvdb;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class VideoIdentityImportTests
    {
        [TestMethod]
        public async Task Import_KnownImdbWork_ReusesItsUnitsAndProgress()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (100, 2, 'Example Show');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (100, 'imdb', 'tt39551287');
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (501, 100, 1, 1);
                INSERT INTO "WorkUnits" ("Id", "WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") VALUES (502, 100, 501, 2, 7);
                INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "WorkUnitId", "ProgressPositionTypeId", "PositionMs") VALUES (202, 100, 502, 1, 4000);
                """);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var result = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Series, "1399"));
                Assert.AreEqual(HttpStatusCode.OK, result.StatusCode, await result.Content.ReadAsStringAsync());
            }

            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "Works" WHERE "MediaTypeId" = 2"""));
            Assert.AreEqual(100L, await host.ScalarAsync("""SELECT "WorkId" FROM "WorkExternalIdentities" WHERE "Namespace" = 'tmdb_tv' AND "Value" = '1399'"""));
            Assert.AreEqual(502L, await host.ScalarAsync("""SELECT "Id" FROM "WorkUnits" WHERE "WorkId" = 100 AND "ParentWorkUnitId" = 501 AND "Number" = 7"""));
            Assert.AreEqual(4000L, await host.ScalarAsync("""SELECT "PositionMs" FROM "MediaProgress" WHERE "WorkId" = 100 AND "WorkUnitId" = 502"""));
        }

        // A series found by its TMDB id comes from TheTVDB: titles and summaries in every language, artwork, people, genres, networks, trailer, seasons and episodes;
        // TMDB's earlier facts go and only its rating stays TMDB's.
        [TestMethod]
        public async Task Import_Series_TakesEveryFactFromTvdb_AndOnlyTheRatingFromTmdb()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "UiLocales" ("Locale", "Name") VALUES ('de-DE', 'Deutsch') ON CONFLICT ("Locale") DO NOTHING;
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (100, 2, 'Example Show');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (100, 'tmdb_tv', '1399');
                INSERT INTO "WorkMetadataFacts" ("WorkId", "FactKey", "Value", "ProviderId") SELECT 100, 'Overview', 'Old TMDB summary.', "Id" FROM "Providers" WHERE "Key" = 'tmdb';
                INSERT INTO "WorkTitles" ("WorkId", "Title", "ProviderId", "IsManual") SELECT 100, 'Old TMDB title', "Id", FALSE FROM "Providers" WHERE "Key" = 'tmdb';
                """);
            using var result = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Series, "1399"));
            Assert.AreEqual(HttpStatusCode.OK, result.StatusCode, await result.Content.ReadAsStringAsync());

            Assert.AreEqual("imdb:tt39551287,tmdb_tv:1399,tvdb:81189", await host.ScalarAsync(
                """SELECT string_agg("Namespace" || ':' || "Value", ',' ORDER BY "Namespace") FROM "WorkExternalIdentities" WHERE "WorkId" = 100"""));
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "WorkMetadataFacts" AS fact JOIN "Providers" AS provider ON provider."Id" = fact."ProviderId" WHERE provider."Key" = 'tmdb'"""));
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "WorkTitles" WHERE "Title" = 'Old TMDB title'"""));
            Assert.AreEqual("Backdrop=https://artworks.thetvdb.com/banners/fanart/1.jpg | Genres=Drama, Crime | Overview=A teacher cooks. | "
                + "Poster=https://artworks.thetvdb.com/banners/posters/de.jpg | Studio=AMC, Sony Pictures Television | Trailer=HhesaQXLuRY", await host.ScalarAsync("""
                SELECT string_agg(fact."FactKey" || '=' || fact."Value", ' | ' ORDER BY fact."FactKey")
                FROM
                    "WorkMetadataFacts" AS fact
                    JOIN "Providers" AS provider ON provider."Id" = fact."ProviderId"
                    CROSS JOIN "InstanceSettings" AS settings
                WHERE
                    fact."WorkId" = 100
                    AND provider."Key" = 'tvdb'
                    AND (fact."UiLocaleId" IS NULL OR fact."UiLocaleId" = settings."UiLocaleId")
                """));
            Assert.AreEqual("Breaking Bad (DE)|Ein Lehrer kocht.", await host.ScalarAsync("""
                SELECT title."Title" || '|' || fact."Value"
                FROM
                    "WorkTitles" AS title
                    JOIN "UiLocales" AS locale ON locale."Id" = title."UiLocaleId" AND locale."Locale" = 'de-DE'
                    JOIN "WorkMetadataFacts" AS fact ON fact."WorkId" = title."WorkId" AND fact."UiLocaleId" = locale."Id" AND fact."FactKey" = 'Overview'
                WHERE
                    title."WorkId" = 100
                """));
            Assert.AreEqual("Bryan Cranston:Walter White:1,Vince Gilligan::6", await host.ScalarAsync("""
                SELECT string_agg(person."Name" || ':' || coalesce(credit."Credit", '') || ':' || credit."PersonRoleId", ',' ORDER BY credit."PersonRoleId")
                FROM "WorkPeople" AS credit JOIN "People" AS person ON person."Id" = credit."PersonId"
                WHERE credit."WorkId" = 100
                """));
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "PersonExternalIdentities" WHERE "Namespace" = 'tvdb' AND "Value" = '300'"""));
            Assert.AreEqual("https://artworks.thetvdb.com/banners/seasons/1.jpg", await host.ScalarAsync("""SELECT "ImageUrl" FROM "WorkUnits" WHERE "WorkId" = 100 AND "WorkUnitTypeId" = 1 AND "Number" = 1"""));
            Assert.AreEqual("Night|Walter starts.|https://artworks.thetvdb.com/banners/episodes/81189/7.jpg", await host.ScalarAsync("""
                SELECT "Title" || '|' || "Overview" || '|' || "ImageUrl" FROM "WorkUnits" WHERE "WorkId" = 100 AND "WorkUnitTypeId" = 2 AND "Number" = 7
                """), "Episodes take the instance language's names over the original ones.");
            Assert.AreEqual("8.90:15000:es:2008-01-20", await host.ScalarAsync("""
                SELECT "Rating"::text || ':' || "Votes" || ':' || "OriginalLanguage" || ':' || "ReleasedOn"::text FROM "Works" WHERE "Id" = 100
                """));
        }

        [TestMethod]
        public async Task Import_SeriesTheTvdbDoesNotKnow_CreatesNoWork()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);
            using var result = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Series, "1400"));
            Assert.AreNotEqual(HttpStatusCode.OK, result.StatusCode);
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "Works" """));
        }

        [TestMethod]
        public async Task Import_TwoConflictingProviderWorks_BlocksBeforeAnyWrite()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (100, 2, 'First'), (101, 2, 'Second');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value")
                VALUES (100, 'imdb', 'tt39551287'), (101, 'tmdb_tv', '1399');
                """);
            using var result = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Series, "1399"));
            Assert.AreEqual(HttpStatusCode.BadRequest, result.StatusCode, await result.Content.ReadAsStringAsync());
            Assert.AreEqual(2L, await host.ScalarAsync("""SELECT count(*) FROM "Works" """));
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "WorkExternalIdentities" WHERE "WorkId" = 100"""));
        }

        [TestMethod]
        public async Task Import_DifferentMediaTypeWithSameImdbId_DoesNotMerge()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (100, 1, 'Movie');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (100, 'imdb', 'tt39551287');
                """);
            using var result = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Series, "1399"));
            Assert.AreEqual(HttpStatusCode.BadRequest, result.StatusCode, await result.Content.ReadAsStringAsync());
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "Works" """));
        }

        [TestMethod]
        public async Task Import_ConflictingExistingTmdbId_LeavesManualIdentityIntact()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (100, 2, 'Show');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value", "IsManual")
                VALUES (100, 'imdb', 'tt39551287', TRUE), (100, 'tmdb_tv', '987', TRUE);
                """);
            using var result = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Series, "1399"));
            Assert.AreEqual(HttpStatusCode.BadRequest, result.StatusCode, await result.Content.ReadAsStringAsync());
            Assert.AreEqual("987", await host.ScalarAsync("""SELECT "Value" FROM "WorkExternalIdentities" WHERE "WorkId" = 100 AND "Namespace" = 'tmdb_tv'"""));
        }

        private static async Task<Host> CreateHostAsync(string storagePath)
        {
            var host = await LibraryTests.CreateHostAsync(storagePath, services: services =>
            {
                services.AddHttpClient<TmdbClient>().ConfigurePrimaryHttpMessageHandler(() => new FakeTmdb());
                services.AddHttpClient<TvdbClient>().ConfigurePrimaryHttpMessageHandler(() => new FakeTvdb());
            });
            foreach (var key in new[] { "tmdb", "tvdb" })
            {
                var provider = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Providers\" WHERE \"Key\" = '" + key + "'"))!;
                using var secret = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/" + provider + "/secret", new SaveProviderSecretDataV1("eyJ-test-identity-token"));
                Assert.AreEqual(HttpStatusCode.OK, secret.StatusCode, await secret.Content.ReadAsStringAsync());
            }

            return host;
        }

        // TheTVDB knows the series as 81189: the full record of TvdbTests, its episode seven in the original order and in English.
        private sealed class FakeTvdb : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = request.RequestUri!.AbsolutePath switch
                {
                    "/v4/login" => """{"data":{"token":"tvdb-test-token"}}""",
                    "/v4/series/81189/extended" => TvdbTests.FullSeries.Replace("tt0903747", "tt39551287", StringComparison.Ordinal).Replace("\"1396\"", "\"1399\"", StringComparison.Ordinal)
                        .Replace("\"originalLanguage\":\"eng\"", "\"originalLanguage\":\"spa\"", StringComparison.Ordinal),
                    "/v4/series/81189/episodes/default" => """{"data":{"episodes":[{"id":7,"seasonNumber":1,"number":7,"name":"Noche","aired":"2008-03-02","image":"/banners/episodes/81189/7.jpg"}]},"links":{"next":null}}""",
                    "/v4/series/81189/episodes/default/eng" => """{"data":{"episodes":[{"id":7,"seasonNumber":1,"number":7,"name":"Night","overview":"Walter starts."}]},"links":{"next":null}}""",
                    _ => null
                };
                return Task.FromResult(response is null ? new HttpResponseMessage(HttpStatusCode.NotFound) :
                    new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") });
            }
        }

        private sealed class FakeTmdb : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = request.RequestUri!.AbsolutePath switch
                {
                    "/3/tv/1399" => """{"id":1399,"name":"Example Show","first_air_date":"2020-01-01","vote_average":8.9,"vote_count":15000,"external_ids":{"imdb_id":"tt39551287","tvdb_id":81189},"seasons":[{"season_number":1,"name":"Season 1","episode_count":1}]}""",
                    "/3/tv/1400" => """{"id":1400,"name":"Unknown Show","external_ids":{}}""",
                    "/3/tv/1399/season/1" => """{"episodes":[{"season_number":1,"episode_number":7,"name":"Night"}]}""",
                    _ => null
                };
                return Task.FromResult(response is null ? new HttpResponseMessage(HttpStatusCode.NotFound) :
                    new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") });
            }
        }
    }
}
