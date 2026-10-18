using System.Net;
using System.Text;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Integrations.Logic.Tmdb;
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
            var host = await LibraryTests.CreateHostAsync(storagePath,
                services: services => services.AddHttpClient<TmdbClient>().ConfigurePrimaryHttpMessageHandler(() => new FakeTmdb()));
            var provider = (long)(await host.ScalarAsync("""SELECT "Id" FROM "Providers" WHERE "Key" = 'tmdb'"""))!;
            using var secret = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/" + provider + "/secret", new SaveProviderSecretDataV1("eyJ-test-identity-token"));
            Assert.AreEqual(HttpStatusCode.OK, secret.StatusCode, await secret.Content.ReadAsStringAsync());
            return host;
        }

        private sealed class FakeTmdb : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = request.RequestUri!.AbsolutePath switch
                {
                    "/3/tv/1399" => """{"id":1399,"name":"Example Show","first_air_date":"2020-01-01","external_ids":{"imdb_id":"tt39551287"},"seasons":[{"season_number":1,"name":"Season 1","episode_count":1}]}""",
                    "/3/tv/1399/season/1" => """{"episodes":[{"season_number":1,"episode_number":7,"name":"Night"}]}""",
                    _ => null
                };
                return Task.FromResult(response is null ? new HttpResponseMessage(HttpStatusCode.NotFound) :
                    new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") });
            }
        }
    }
}
