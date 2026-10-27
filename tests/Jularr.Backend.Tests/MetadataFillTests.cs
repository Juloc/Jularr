using System.Net;
using System.Net.Http.Json;
using System.Text;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Data.User.Library.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Media.V1;
using Jularr.Integrations.Logic.Tmdb;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class MetadataFillTests
    {
        [TestMethod]
        public async Task Fill_WorkWithTmdbIdAndNoFacts_GetsItsDescription_AndIsNotAskedAgainToday()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var catalog = new FakeCatalog();
            await using var host = await CreateHostAsync(storage.Path, catalog);
            await host.Fixture.RunSqlAsync("""
                WITH work AS (
                    INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle", "ReleaseYear") VALUES (1, 'The Matrix', 1999) RETURNING "Id"
                )
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") SELECT "Id", namespace, value FROM work, (VALUES ('tmdb_movie', '603'), ('plex', '11')) AS id (namespace, value);
                """);

            var more = await FillAsync(host);
            Assert.IsFalse(more.Success);
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "WorkMetadataFacts" WHERE "FactKey" = 'Overview' """));
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "Works" """));

            // A title that has its facts is not read again.
            var requests = catalog.Requests;
            await FillAsync(host);
            Assert.AreEqual(requests, catalog.Requests);
        }

        [TestMethod]
        public async Task Fill_FilmKnownOnlyByItsImdbId_IsFoundThroughTmdbAndFilled()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var catalog = new FakeCatalog();
            await using var host = await CreateHostAsync(storage.Path, catalog);
            await host.Fixture.RunSqlAsync("""
                WITH work AS (
                    INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle", "ReleaseYear") VALUES (1, 'The Matrix', 1999) RETURNING "Id"
                )
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") SELECT "Id", namespace, value FROM work, (VALUES ('imdb', 'tt0133093'), ('plex', '11')) AS id (namespace, value);
                """);

            await FillAsync(host);
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "Works" """));
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "WorkMetadataFacts" WHERE "FactKey" = 'Overview' """));
            Assert.AreEqual("603", await host.ScalarAsync("""SELECT "Value" FROM "WorkExternalIdentities" WHERE "Namespace" = 'tmdb_movie' """));
        }

        [TestMethod]
        public async Task Fill_WorkWithoutAnyCatalogIdentity_IsLeftAlone()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var catalog = new FakeCatalog();
            await using var host = await CreateHostAsync(storage.Path, catalog);
            await host.Fixture.RunSqlAsync("""INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (1, 'Home Video');""");

            Assert.IsFalse((await FillAsync(host)).Success);
            Assert.AreEqual(0, catalog.Requests);
        }

        [TestMethod]
        public async Task CatalogTitle_NobodyOpened_StaysOutOfTheLibraryAndIsNotFilled()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var catalog = new FakeCatalog();
            await using var host = await CreateHostAsync(storage.Path, catalog);
            await host.Fixture.RunSqlAsync("""
                WITH work AS (
                    INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle", "ReleaseYear", "Overview", "ImageUrl", "CatalogNamespace")
                    VALUES (1, 'The Matrix', 1999, 'A hacker learns the truth.', 'https://image.tmdb.org/t/p/w500/poster.jpg', 'tmdb_movie') RETURNING "Id"
                )
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") SELECT "Id", 'tmdb_movie', '603' FROM work;
                """);

            // The library lists what the profile has or asked for; a catalog title without either is none of it.
            foreach (var filter in new[] { "", "?availabilityTypeIds=MyRequests", "?availabilityTypeIds=Available&availabilityTypeIds=MyRequests&availabilityTypeIds=OtherRequests" })
            {
                using var library = await host.SendAsync(HttpMethod.Get, "/api/v1/library" + filter);
                Assert.AreEqual(HttpStatusCode.OK, library.StatusCode, await library.Content.ReadAsStringAsync());
                Assert.AreEqual(0, (await library.Content.ReadFromJsonAsync<PageResult<LibraryItemV1>>())!.Items.Count, filter);
            }

            // The fill job asks no provider for it.
            Assert.IsFalse((await FillAsync(host)).Success);
            Assert.AreEqual(0, catalog.Requests);
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "WorkMetadataFacts" """));
        }

        [TestMethod]
        public void Fill_IsAJobOfTheMediaWorker()
        {
            var job = WorkerJobs.Definition("metadataFill");
            Assert.IsTrue(job.CanRunNow && job.CanPauseWhileUsed);
        }

        private static async Task<UpdateResult> FillAsync(AuthenticationTests.Host host)
        {
            await using var scope = host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<FillWorkMetadataV1>().ExecuteAsync<UpdateResult>(new CoverParametersV1(), default);
        }

        private static async Task<AuthenticationTests.Host> CreateHostAsync(string storage, FakeCatalog catalog)
        {
            var host = await LibraryTests.CreateHostAsync(storage, services: services =>
                services.AddHttpClient<TmdbClient>().ConfigurePrimaryHttpMessageHandler(() => catalog));
            var tmdb = (long)(await host.ScalarAsync("""SELECT "Id" FROM "Providers" WHERE "Key" = 'tmdb'"""))!;
            using var secret = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/" + tmdb + "/secret", new SaveProviderSecretDataV1("eyJ-fill-token"));
            Assert.AreEqual(HttpStatusCode.OK, secret.StatusCode, await secret.Content.ReadAsStringAsync());
            return host;
        }

        // One film: The Matrix, TMDB 603, IMDb tt0133093.
        private sealed class FakeCatalog : HttpMessageHandler
        {
            public int Requests { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests++;
                var body = request.RequestUri!.AbsolutePath switch
                {
                    "/3/movie/603" => """{"id":603,"title":"The Matrix","original_title":"The Matrix","release_date":"1999-03-31","overview":"A hacker learns the truth.","external_ids":{"imdb_id":"tt0133093"}}""",
                    "/3/find/tt0133093" => """{"movie_results":[{"id":603}],"tv_results":[]}""",
                    _ => null
                };
                return Task.FromResult(body is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }
        }
    }
}
