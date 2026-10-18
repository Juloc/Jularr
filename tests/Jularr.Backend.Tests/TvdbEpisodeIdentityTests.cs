using System.Net;
using System.Net.Http.Json;
using System.Text;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Integrations.Logic.Tvdb;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class TvdbEpisodeIdentityTests
    {
        private const string Route = "/api/v1/admin/works/100/tvdb-episodes";

        [TestMethod]
        public async Task Link_VerifiedRemoteEpisode_OnlyAddsManualMappingAndPreservesProgress()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);
            await SeedAsync(host);
            using var first = await host.SendAsync(HttpMethod.Post, Route, new LinkTvdbEpisodeIdentityDataV1(501, 43, 1));
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, await first.Content.ReadAsStringAsync());
            Assert.AreEqual(2L, (await first.Content.ReadFromJsonAsync<WorkDetailsV1>())!.Version);
            Assert.AreEqual("43", await host.ScalarAsync("""SELECT "Value" FROM "WorkUnitExternalIdentities" WHERE "WorkUnitId" = 501 AND "Namespace" = 'tvdb'"""));
            Assert.AreEqual(true, await host.ScalarAsync("""SELECT "IsManual" FROM "WorkUnitExternalIdentities" WHERE "WorkUnitId" = 501 AND "Namespace" = 'tvdb'"""));
            Assert.AreEqual(0, await host.ScalarAsync("""SELECT "EpisodeOffset" FROM "WorkUnitExternalIdentities" WHERE "WorkUnitId" = 501 AND "Namespace" = 'tvdb'"""));
            Assert.AreEqual(52000L, await host.ScalarAsync("""SELECT "PositionMs" FROM "MediaProgress" WHERE "WorkUnitId" = 501"""));
            Assert.AreEqual(7m, await host.ScalarAsync("""SELECT "Number" FROM "WorkUnits" WHERE "Id" = 501"""));

            using var repeated = await host.SendAsync(HttpMethod.Post, Route, new LinkTvdbEpisodeIdentityDataV1(501, 43, 2));
            Assert.AreEqual(HttpStatusCode.OK, repeated.StatusCode, await repeated.Content.ReadAsStringAsync());
            Assert.AreEqual(2L, await host.ScalarAsync("""SELECT "Version" FROM "Works" WHERE "Id" = 100"""));
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "WorkUnitExternalIdentities" WHERE "Namespace" = 'tvdb'"""));
        }

        [TestMethod]
        public async Task Link_UnknownTvdbEpisode_RejectsWithoutDatabaseChange()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);
            await SeedAsync(host);
            using var missing = await host.SendAsync(HttpMethod.Post, Route, new LinkTvdbEpisodeIdentityDataV1(501, 99, 1));
            Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "WorkUnitExternalIdentities" WHERE "Namespace" = 'tvdb'"""));
        }

        [TestMethod]
        public async Task Link_SameLocalUnitToSecondRemoteEpisode_Rejects()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);
            await SeedAsync(host);
            using var first = await host.SendAsync(HttpMethod.Post, Route, new LinkTvdbEpisodeIdentityDataV1(501, 43, 1));
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
            using var changed = await host.SendAsync(HttpMethod.Post, Route, new LinkTvdbEpisodeIdentityDataV1(501, 44, 2));
            Assert.AreEqual(HttpStatusCode.BadRequest, changed.StatusCode);
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "WorkUnitExternalIdentities" WHERE "Namespace" = 'tvdb'"""));
            Assert.AreEqual("43", await host.ScalarAsync("""SELECT "Value" FROM "WorkUnitExternalIdentities" WHERE "WorkUnitId" = 501 AND "Namespace" = 'tvdb'"""));
        }

        [TestMethod]
        public async Task Link_RemoteEpisodeUsedByAnotherLocalUnit_Rejects()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);
            await SeedAsync(host);
            using var first = await host.SendAsync(HttpMethod.Post, Route, new LinkTvdbEpisodeIdentityDataV1(501, 43, 1));
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
            using var second = await host.SendAsync(HttpMethod.Post, Route, new LinkTvdbEpisodeIdentityDataV1(502, 43, 2));
            Assert.AreEqual(HttpStatusCode.BadRequest, second.StatusCode);
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "WorkUnitExternalIdentities" WHERE "Namespace" = 'tvdb'"""));
        }

        [TestMethod]
        public async Task Link_UnitFromAnotherSeriesOrStaleVersion_Rejects()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);
            await SeedAsync(host);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (200, 2, 'Another Show');
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (700, 200, 1, 1);
                INSERT INTO "WorkUnits" ("Id", "WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") VALUES (701, 200, 700, 2, 1);
                """);
            using var foreign = await host.SendAsync(HttpMethod.Post, Route, new LinkTvdbEpisodeIdentityDataV1(701, 43, 1));
            Assert.AreEqual(HttpStatusCode.BadRequest, foreign.StatusCode);
            using var stale = await host.SendAsync(HttpMethod.Post, Route, new LinkTvdbEpisodeIdentityDataV1(501, 43, 9));
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "WorkUnitExternalIdentities" WHERE "Namespace" = 'tvdb'"""));
        }

        private static async Task SeedAsync(Host host)
        {
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (100, 2, 'Series');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (100, 'tvdb', '123');
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (500, 100, 1, 1);
                INSERT INTO "WorkUnits" ("Id", "WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number")
                VALUES (501, 100, 500, 2, 7), (502, 100, 500, 2, 8);
                INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "WorkUnitId", "ProgressPositionTypeId", "PositionMs")
                VALUES (202, 100, 501, 1, 52000);
                """);
        }

        private static async Task<Host> CreateHostAsync(string folder)
        {
            var host = await LibraryTests.CreateHostAsync(folder, services: services =>
                services.AddHttpClient<TvdbClient>().ConfigurePrimaryHttpMessageHandler(() => new FakeTvdb()));
            var provider = (long)(await host.ScalarAsync("""SELECT "Id" FROM "Providers" WHERE "Key" = 'tvdb'"""))!;
            using var secret = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/" + provider + "/secret", new SaveProviderSecretDataV1("project-test-key"));
            Assert.AreEqual(HttpStatusCode.OK, secret.StatusCode, await secret.Content.ReadAsStringAsync());
            return host;
        }

        private sealed class FakeTvdb : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var path = request.RequestUri!.AbsolutePath;
                string? json = null;
                if (path == "/v4/login")
                {
                    StringAssert.Contains(await request.Content!.ReadAsStringAsync(ct), "project-test-key");
                    json = """{"data":{"token":"ep-token"}}""";
                }
                else if (path == "/v4/series/123/episodes/default")
                {
                    Assert.AreEqual("ep-token", request.Headers.Authorization?.Parameter);
                    json = """{"data":{"episodes":[{"id":43,"seasonNumber":1,"number":1,"name":"Remote A"},{"id":44,"seasonNumber":1,"number":2,"name":"Remote B"}]},"links":{"next":null}}""";
                }

                return Task.FromResult(json is null ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }
        }
    }
}
