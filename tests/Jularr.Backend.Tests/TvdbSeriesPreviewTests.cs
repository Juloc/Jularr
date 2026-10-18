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
    public sealed class TvdbSeriesPreviewTests
    {
        [TestMethod]
        public async Task Preview_KnownTvdbSeries_ComparesNumbersWithoutChangingLocalUnits()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var fake = new FakeTvdb();
            await using var host = await CreateHostAsync(storage.Path, fake);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (100, 2, 'Local Show');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value")
                VALUES (100, 'tvdb', '123'), (100, 'imdb', 'tt1234567');
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (500, 100, 1, 1);
                INSERT INTO "WorkUnits" ("Id", "WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number", "Title")
                VALUES (501, 100, 500, 2, 1, 'Original Episode'), (503, 100, 500, 2, 3, 'Local Only');
                INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "WorkUnitId", "ProgressPositionTypeId", "PositionMs")
                VALUES (202, 100, 501, 1, 5000);
                """);

            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/works/100/tvdb-preview");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var preview = (await response.Content.ReadFromJsonAsync<TvdbSeriesPreviewV1>())!;
            Assert.AreEqual(100L, preview.WorkId);
            Assert.AreEqual(123, preview.TvdbId);
            Assert.AreEqual("review", preview.Episodes.Single(item => item.SeasonNumber == 1 && item.EpisodeNumber == 1).State);
            Assert.AreEqual(501L, preview.Episodes.Single(item => item.SeasonNumber == 1 && item.EpisodeNumber == 1).LocalWorkUnitId);
            Assert.AreEqual("new", preview.Episodes.Single(item => item.SeasonNumber == 1 && item.EpisodeNumber == 2).State);
            Assert.AreEqual("local_only", preview.Episodes.Single(item => item.SeasonNumber == 1 && item.EpisodeNumber == 3).State);
            Assert.AreEqual("new", preview.Episodes.Single(item => item.SeasonNumber == 0 && item.EpisodeNumber == 1).State);
            Assert.AreEqual(2L, await host.ScalarAsync("""SELECT count(*) FROM "WorkUnits" WHERE "WorkUnitTypeId" = 2"""));
            Assert.AreEqual(5000L, await host.ScalarAsync("""SELECT "PositionMs" FROM "MediaProgress" WHERE "WorkUnitId" = 501"""));
            Assert.AreEqual(2L, await host.ScalarAsync("""SELECT count(*) FROM "WorkExternalIdentities" WHERE "WorkId" = 100"""));
            Assert.AreEqual(0, fake.ImdbLookups);
        }

        [TestMethod]
        public async Task Preview_ImdbOnly_ResolvesTvdbWithoutSavingExternalIdentity()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var fake = new FakeTvdb();
            await using var host = await CreateHostAsync(storage.Path, fake);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (100, 2, 'Show');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (100, 'imdb', 'tt1234567');
                """);

            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/works/100/tvdb-preview");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var preview = (await response.Content.ReadFromJsonAsync<TvdbSeriesPreviewV1>())!;
            Assert.AreEqual(123, preview.TvdbId);
            Assert.AreEqual(1, fake.ImdbLookups);
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "WorkExternalIdentities" WHERE "Namespace" = 'tvdb'"""));
        }

        [TestMethod]
        public async Task Preview_ConflictingImdbIdentity_IsRejectedWithoutChanges()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var fake = new FakeTvdb { SeriesImdbId = "tt9999999" };
            await using var host = await CreateHostAsync(storage.Path, fake);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (100, 2, 'Show');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value")
                VALUES (100, 'imdb', 'tt1234567'), (100, 'tvdb', '123');
                """);
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/works/100/tvdb-preview");
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.AreEqual(2L, await host.ScalarAsync("""SELECT count(*) FROM "WorkExternalIdentities" WHERE "WorkId" = 100"""));
        }

        [TestMethod]
        public async Task Preview_NoCredential_DoesNotContactProvider()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var fake = new FakeTvdb();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path,
                services: services => services.AddHttpClient<TvdbClient>().ConfigurePrimaryHttpMessageHandler(() => fake));
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (100, 2, 'Show');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (100, 'tvdb', '123');
                """);
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/works/100/tvdb-preview");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.AreEqual(0, fake.Calls);
        }

        private static async Task<Host> CreateHostAsync(string storage, FakeTvdb fake)
        {
            var host = await LibraryTests.CreateHostAsync(storage,
                services: services => services.AddHttpClient<TvdbClient>().ConfigurePrimaryHttpMessageHandler(() => fake));
            var providerId = (long)(await host.ScalarAsync("""SELECT "Id" FROM "Providers" WHERE "Key" = 'tvdb'"""))!;
            using var secret = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/" + providerId + "/secret", new SaveProviderSecretDataV1("project-test-key"));
            Assert.AreEqual(HttpStatusCode.OK, secret.StatusCode, await secret.Content.ReadAsStringAsync());
            return host;
        }

        private sealed class FakeTvdb : HttpMessageHandler
        {
            public int ImdbLookups { get; private set; }
            public int Calls { get; private set; }
            public string SeriesImdbId { get; set; } = "tt1234567";

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Calls++;
                string? json = null;
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/v4/login")
                {
                    var body = await request.Content!.ReadAsStringAsync(ct);
                    StringAssert.Contains(body, "project-test-key");
                    json = """{"data":{"token":"tvdb-test-token"}}""";
                }
                else
                {
                    Assert.AreEqual("tvdb-test-token", request.Headers.Authorization?.Parameter);
                    if (path == "/v4/search/remoteid/tt1234567")
                    {
                        ImdbLookups++;
                        json = """{"data":[{"series":{"id":123}}]}""";
                    }
                    else if (path == "/v4/series/123/extended")
                    {
                        json = """{"data":{"id":123,"name":"Remote Show","remoteIds":[{"sourceName":"IMDb","id":""" + "\"" + SeriesImdbId + "\"" + "}]}}";
                    }
                    else if (path == "/v4/series/123/episodes/default")
                    {
                        json = """{"data":{"episodes":[{"id":10,"seasonNumber":0,"number":1,"name":"Special"},{"id":11,"seasonNumber":1,"number":1,"name":"Pilot"},{"id":12,"seasonNumber":1,"number":2,"name":"Next"}]},"links":{"next":null}}""";
                    }
                }

                return json is null ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
        }
    }
}
