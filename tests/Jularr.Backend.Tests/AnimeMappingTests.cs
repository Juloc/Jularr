using System.Net;
using System.Text;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Service.System.Media.V1;
using Jularr.Integrations.Logic.AniBridge;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    // The AniBridge mappings: the release format and its daily load, from a trimmed excerpt of the real release (Fixtures/AniBridge, MIT).
    [TestClass]
    public sealed class AnimeMappingTests
    {
        [TestMethod]
        public void Parse_Release_TakesAniListEntriesInTheirTvdbSeasons()
        {
            using var sample = Sample();
            var release = AniBridgeClient.Parse(sample, "\"etag\"");
            string Rows(int id) => string.Join(';', release.Mappings.Where(mapping => mapping.AniListId == id).OrderBy(mapping => mapping.SeasonNumber)
                .Select(mapping => mapping.TvdbId + ":s" + mapping.SeasonNumber + ":" + mapping.AniListEpisode + ":" + mapping.FirstEpisode + "-" + mapping.LastEpisode));

            Assert.AreEqual("3.0.3", release.SchemaVersion);
            Assert.AreEqual("\"etag\"", release.ETag);
            Assert.AreEqual(new DateTimeOffset(2026, 10, 28, 7, 48, 11, TimeSpan.Zero), release.GeneratedAt);

            // Two cours of one season, the second starting at the season's thirteenth episode.
            Assert.AreEqual("267440:s3:1:1-12", Rows(99147));
            Assert.AreEqual("267440:s3:1:13-22", Rows(104578));

            // A long entry over many seasons, a special in S00, two ranges in one season and an open end.
            var onePiece = Rows(21).Split(';');
            Assert.AreEqual("81797:s0:542:39-39", onePiece[0]);
            Assert.AreEqual("81797:s1:1:1-8", onePiece[1]);
            CollectionAssert.Contains(onePiece, "81797:s15:517:1-62");
            CollectionAssert.Contains(onePiece, "81797:s23:1156:1-");

            // Several target segments and an episode ratio keep where the entry starts and how far it reaches.
            Assert.AreEqual("267440:s0:1:7-13", Rows(18397));
            Assert.AreEqual("307695:s1:1:1-102", Rows(280));

            // Other providers' keys and AniList's links to other providers are not read.
            Assert.IsFalse(release.Mappings.Any(mapping => mapping.AniListId == 290));
        }

        [TestMethod]
        [DataRow("""{"$meta":{"schema_version":"4.0.0"},"anilist:1":{"tvdb_show:1:s1":{"1":"1"}}}""")]
        [DataRow("""{"anilist:1":{"tvdb_show:1:s1":{"1":"1"}}}""")]
        [DataRow("""{"$meta":{"schema_version":"3.0.3"},"anilist:1":{"tmdb_show:1:s1":{"1":"1"}}}""")]
        [DataRow("""{"$meta":{"schema_version":"3.0.3"},"anilist:1":{"tvdb_show:1:s1":{"1-x":"1"}}}""")]
        public void Parse_UnknownSchemaOrNoTvdbSeason_IsRefused(string json)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            Assert.ThrowsExactly<InvalidDataException>(() => AniBridgeClient.Parse(stream, null));
        }

        [TestMethod]
        public async Task Sync_LoadsTheRelease_KeepsItWhenUnchangedFailedOrOdd_AndReloadsTheSameRows()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var release = new FakeRelease();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, services: services => services.AddHttpClient<AniBridgeClient>().ConfigurePrimaryHttpMessageHandler(() => release));
            using var sample = Sample();
            var expected = (long)AniBridgeClient.Parse(sample, null).Mappings.Count;
            const string Rows = "SELECT count(*) FROM \"AnimeMappings\"";
            const string Source = "SELECT coalesce(\"ETag\", '-') || ':' || coalesce(\"SchemaVersion\", '-') FROM \"AnimeMappingSources\"";

            // Off with the AniList module: nothing is asked.
            await Assert.ThrowsExactlyAsync<System.Security.SecurityException>(() => RunAsync(host));
            Assert.AreEqual(0, release.Requests.Count);
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'AniList'");

            // The first load takes every row of the release and its ETag.
            Assert.IsTrue(await RunAsync(host));
            Assert.AreEqual(expected, await host.ScalarAsync(Rows));
            Assert.AreEqual("\"a\":3.0.3", await host.ScalarAsync(Source));
            Assert.AreEqual("12", await host.ScalarAsync("""SELECT "FirstEpisode" - "AniListEpisode" || '' FROM "AnimeMappings" WHERE "AniListId" = 104578"""));
            Assert.IsNull(release.Requests[0]);

            // Unchanged, the release is not downloaded again; only the check is noted.
            await host.Fixture.RunSqlAsync("UPDATE \"AnimeMappingSources\" SET \"CheckedAt\" = now() - interval '1 day'");
            Assert.IsFalse(await RunAsync(host));
            Assert.AreEqual("\"a\"", release.Requests[1]);
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"CheckedAt\" > now() - interval '1 hour' FROM \"AnimeMappingSources\""));

            // A failed download, a release of another schema and one with far fewer rows leave the loaded rows as they are.
            release.ETag = "\"b\"";
            foreach (var (status, body) in new[]
            {
                (HttpStatusCode.InternalServerError, ""),
                (HttpStatusCode.OK, """{"$meta":{"schema_version":"4.0.0"},"anilist:1":{"tvdb_show:1:s1":{"1":"1"}}}"""),
                (HttpStatusCode.OK, """{"$meta":{"schema_version":"3.0.3"},"anilist:1":{"tvdb_show:1:s1":{"1":"1"}}}""")
            })
            {
                (release.Status, release.Body) = (status, body);
                await Assert.ThrowsExactlyAsync<ServiceUnavailableException>(() => RunAsync(host));
                Assert.AreEqual(expected, await host.ScalarAsync(Rows));
                Assert.AreEqual("\"a\":3.0.3", await host.ScalarAsync(Source));
            }

            // A new release with the same rows loads them once again, no row twice.
            (release.Status, release.Body) = (HttpStatusCode.OK, null);
            Assert.IsTrue(await RunAsync(host));
            Assert.AreEqual(expected, await host.ScalarAsync(Rows));
            Assert.AreEqual("\"b\":3.0.3", await host.ScalarAsync(Source));
        }

        internal static Stream Sample() => typeof(AnimeMappingTests).Assembly.GetManifestResourceStream("AniBridge.mappings.sample.json")
            ?? throw new FileNotFoundException("The AniBridge sample is not embedded.");

        internal static async Task<bool> RunAsync(Host host)
        {
            await using var scope = host.Services.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<SyncAnimeMappingsV1>().ExecuteAsync<UpdateResult>(new MediaMatchParametersV1(), default)).Success;
        }

        // Serves the sample (or a given body) with its ETag, and nothing new to a request that names that ETag; notes the ETag each request named.
        internal sealed class FakeRelease : HttpMessageHandler
        {
            public string ETag { get; set; } = "\"a\"";
            public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
            public string? Body { get; set; }
            public List<string?> Requests { get; } = [];

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var named = request.Headers.IfNoneMatch.FirstOrDefault()?.ToString();
                Requests.Add(named);
                Assert.AreEqual("/anibridge/anibridge-mappings/releases/download/v3/mappings.min.json", request.RequestUri!.AbsolutePath);
                if (Status != HttpStatusCode.OK)
                {
                    return new HttpResponseMessage(Status);
                }

                if (named == ETag)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotModified);
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = Body is null ? new StringContent(await new StreamReader(Sample()).ReadToEndAsync(cancellationToken), Encoding.UTF8, "application/json")
                        : new StringContent(Body, Encoding.UTF8, "application/json")
                };
                response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(ETag);
                return response;
            }
        }
    }
}
