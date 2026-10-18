using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Integrations.Logic.Tvdb;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class TvdbSeriesIdentityTests
    {
        private const string Route = "/api/v1/admin/works/100/tvdb-identity";

        [TestMethod]
        public async Task Link_VerifiedSeries_PreservesProgressAndIsIdempotent()
        {
            using var directory = new LibraryTests.TemporaryDirectory();
            await using var host = await HostAsync(directory.Path, "tt1234567");
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (100, 2, 'Show');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (100, 'imdb', 'tt1234567');
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (501, 100, 1, 1);
                INSERT INTO "WorkUnits" ("Id", "WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") VALUES (502, 100, 501, 2, 7);
                INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "WorkUnitId", "ProgressPositionTypeId", "PositionMs") VALUES (202, 100, 502, 1, 52000);
                """);
            using var linked = await host.SendAsync(HttpMethod.Post, Route, new LinkTvdbSeriesIdentityDataV1(123, 1));
            Assert.AreEqual(HttpStatusCode.OK, linked.StatusCode, await linked.Content.ReadAsStringAsync());
            Assert.AreEqual(2L, (await linked.Content.ReadFromJsonAsync<WorkDetailsV1>())!.Version);
            Assert.AreEqual(100L, await host.ScalarAsync("""SELECT "WorkId" FROM "WorkExternalIdentities" WHERE "Namespace" = 'tvdb' AND "Value" = '123'"""));
            Assert.AreEqual(true, await host.ScalarAsync("""SELECT "IsManual" FROM "WorkExternalIdentities" WHERE "Namespace" = 'tvdb'"""));
            Assert.AreEqual(502L, await host.ScalarAsync("""SELECT "Id" FROM "WorkUnits" WHERE "WorkId" = 100 AND "Number" = 7"""));
            Assert.AreEqual(52000L, await host.ScalarAsync("""SELECT "PositionMs" FROM "MediaProgress" WHERE "WorkUnitId" = 502"""));
            using var repeated = await host.SendAsync(HttpMethod.Post, Route, new LinkTvdbSeriesIdentityDataV1(123, 2));
            Assert.AreEqual(HttpStatusCode.OK, repeated.StatusCode, await repeated.Content.ReadAsStringAsync());
            Assert.AreEqual(2L, await host.ScalarAsync("""SELECT "Version" FROM "Works" WHERE "Id" = 100"""));
        }

        [TestMethod]
        public async Task Link_TvdbIdAlreadyMappedToAnotherWork_IsRejected()
        {
            using var directory = new LibraryTests.TemporaryDirectory();
            await using var host = await HostAsync(directory.Path, "tt1234567");
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (100, 2, 'Show'), (200, 2, 'Other');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value")
                VALUES (100, 'imdb', 'tt1234567'), (200, 'tvdb', '123');
                """);
            using var denied = await host.SendAsync(HttpMethod.Post, Route, new LinkTvdbSeriesIdentityDataV1(123, 1));
            Assert.AreEqual(HttpStatusCode.BadRequest, denied.StatusCode);
            Assert.AreEqual(200L, await host.ScalarAsync("""SELECT "WorkId" FROM "WorkExternalIdentities" WHERE "Namespace" = 'tvdb'"""));
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT "Version" FROM "Works" WHERE "Id" = 100"""));
        }

        [TestMethod]
        public async Task Link_MismatchedRemoteImdb_IsRejected()
        {
            using var directory = new LibraryTests.TemporaryDirectory();
            await using var host = await HostAsync(directory.Path, "tt9999999");
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (100, 2, 'Show');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (100, 'imdb', 'tt1234567');
                """);
            using var response = await host.SendAsync(HttpMethod.Post, Route, new LinkTvdbSeriesIdentityDataV1(123, 1));
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "WorkExternalIdentities" WHERE "Namespace" = 'tvdb'"""));
        }

        [TestMethod]
        public async Task Link_ExistingAutomaticMapping_BecomesManual()
        {
            using var directory = new LibraryTests.TemporaryDirectory();
            await using var host = await HostAsync(directory.Path, "tt1234567");
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (100, 2, 'Show');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value")
                VALUES (100, 'imdb', 'tt1234567'), (100, 'tvdb', '123');
                """);
            using var response = await host.SendAsync(HttpMethod.Post, Route, new LinkTvdbSeriesIdentityDataV1(123, 1));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual(true, await host.ScalarAsync("""SELECT "IsManual" FROM "WorkExternalIdentities" WHERE "Namespace" = 'tvdb'"""));
            Assert.AreEqual(2L, await host.ScalarAsync("""SELECT "Version" FROM "Works" WHERE "Id" = 100"""));
        }

        [TestMethod]
        public async Task Link_StaleVersionOrMovie_IsRejected()
        {
            using var directory = new LibraryTests.TemporaryDirectory();
            await using var host = await HostAsync(directory.Path, "tt1234567");
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (100, 1, 'Movie');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (100, 'imdb', 'tt1234567');
                """);
            using var movie = await host.SendAsync(HttpMethod.Post, Route, new LinkTvdbSeriesIdentityDataV1(123, 1));
            Assert.AreEqual(HttpStatusCode.BadRequest, movie.StatusCode);
            await host.Fixture.RunSqlAsync("""UPDATE "Works" SET "MediaTypeId" = 2, "Version" = 3 WHERE "Id" = 100""");
            using var stale = await host.SendAsync(HttpMethod.Post, Route, new LinkTvdbSeriesIdentityDataV1(123, 1));
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "WorkExternalIdentities" WHERE "Namespace" = 'tvdb'"""));
        }

        private static async Task<Host> HostAsync(string folder, string remoteImdb)
        {
            var host = await LibraryTests.CreateHostAsync(folder, services: services =>
                services.AddHttpClient<TvdbClient>().ConfigurePrimaryHttpMessageHandler(() => new FakeTvdb(remoteImdb)));
            var provider = (long)(await host.ScalarAsync("""SELECT "Id" FROM "Providers" WHERE "Key" = 'tvdb'"""))!;
            using var secret = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/" + provider + "/secret",
                new SaveProviderSecretDataV1("test-project-key"));
            Assert.AreEqual(HttpStatusCode.OK, secret.StatusCode, await secret.Content.ReadAsStringAsync());
            return host;
        }

        private sealed class FakeTvdb(string imdb) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var path = request.RequestUri!.AbsolutePath;
                string response;
                if (path == "/v4/login")
                {
                    StringAssert.Contains(await request.Content!.ReadAsStringAsync(ct), "test-project-key");
                    response = """{"data":{"token":"tvdb-token"}}""";
                }
                else if (path == "/v4/series/123/extended")
                {
                    Assert.AreEqual("tvdb-token", request.Headers.Authorization?.Parameter);
                    response = JsonSerializer.Serialize(new { data = new { id = 123, name = "Series",
                        remoteIds = new[] { new { sourceName = "IMDb", id = imdb } } } });
                }
                else
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(response, Encoding.UTF8, "application/json") };
            }
        }
    }
}
