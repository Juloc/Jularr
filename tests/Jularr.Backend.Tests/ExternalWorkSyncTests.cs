using System.IO.Compression;
using System.Net;
using System.Security;
using System.Text;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Service.System.Media.V1;
using Jularr.Integrations.Logic.Tmdb;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    // Full local discovery for TMDB: the daily id list fills the catalog, entries are read by popularity, changes are read again.
    [TestClass]
    public sealed class ExternalWorkSyncTests
    {
        private const string Token = "eyJ-catalog-token";

        [TestMethod]
        public async Task FullSync_ListsReadsAndRefreshesTheCatalog_OnlyWhileItsModuleIsOn()
        {
            var tmdb = new FakeTmdbCatalog();
            var time = new FixedTime(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
            await using var host = await Host.CreateAsync(library: true, services: services =>
            {
                services.AddHttpClient<TmdbClient>().ConfigurePrimaryHttpMessageHandler(() => tmdb);
                services.AddSingleton<TimeProvider>(time);
            });
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            var provider = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Providers\" WHERE \"Key\" = 'tmdb'"))!;
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/" + provider + "/secret", new SaveProviderSecretDataV1(Token));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());

            // Off by default: nothing is fetched.
            await Assert.ThrowsExactlyAsync<SecurityException>(() => RunAsync(host));
            Assert.AreEqual(0, tmdb.Requests.Count);
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'LocalDiscovery'");

            // The id list adds titles without adult ones and video extras; entries are read and gone ones leave.
            await RunAsync(host);
            Assert.AreEqual("Matrix", await host.ScalarAsync("SELECT \"Title\" FROM \"ExternalWorks\" WHERE \"Namespace\" = 'tmdb_movie' AND \"Value\" = '603'"));
            Assert.AreEqual("tt0133093", await host.ScalarAsync("SELECT \"ImdbId\" FROM \"ExternalWorks\" WHERE \"Value\" = '603'"));
            Assert.AreEqual(8.2m, await host.ScalarAsync("SELECT \"Rating\" FROM \"ExternalWorks\" WHERE \"Value\" = '603'"));
            Assert.AreEqual(1999, await host.ScalarAsync("SELECT \"ReleaseYear\"::integer FROM \"ExternalWorks\" WHERE \"Value\" = '603'"));
            Assert.AreEqual("https://image.tmdb.org/t/p/w500/matrix.jpg", await host.ScalarAsync("SELECT \"ImageUrl\" FROM \"ExternalWorks\" WHERE \"Value\" = '603'"));
            Assert.AreEqual(DBNull.Value, await host.ScalarAsync("SELECT \"Rating\" FROM \"ExternalWorks\" WHERE \"Value\" = '604'"));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"ExternalWorks\" WHERE \"Value\" IN ('9', '10', '999')"));
            Assert.AreEqual("tt0944947", await host.ScalarAsync("SELECT \"ImdbId\" FROM \"ExternalWorks\" WHERE \"Namespace\" = 'tmdb_tv' AND \"Value\" = '1399'"));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"ExternalWorks\" WHERE \"DetailedAt\" IS NULL"));
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"ExternalWorkSyncs\" WHERE \"ListedOn\" = '2026-10-19'"));
            Assert.IsFalse(tmdb.Requests.Any(request => request.AbsolutePath.EndsWith("/changes", StringComparison.Ordinal)), "The first pass reads everything anyway.");

            // The same day the list is not read again; six hours later the changed entries are read again.
            var listed = tmdb.Requests.Count(request => request.Host == "files.tmdb.org");
            time.Now = time.Now.AddHours(7);
            await host.Fixture.RunSqlAsync("UPDATE \"ExternalWorks\" SET \"Title\" = 'Stale' WHERE \"Value\" IN ('603', '604')");
            await RunAsync(host);
            Assert.AreEqual(listed, tmdb.Requests.Count(request => request.Host == "files.tmdb.org"));
            Assert.IsTrue(tmdb.Requests.Any(request => request.AbsolutePath == "/3/movie/changes" && request.Query.Contains("start_date=2026-10-19", StringComparison.Ordinal)));
            Assert.AreEqual("Matrix", await host.ScalarAsync("SELECT \"Title\" FROM \"ExternalWorks\" WHERE \"Value\" = '603'"));
            Assert.AreEqual("Stale", await host.ScalarAsync("SELECT \"Title\" FROM \"ExternalWorks\" WHERE \"Value\" = '604'"));
        }

        private static async Task RunAsync(Host host)
        {
            await using var scope = host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SyncExternalWorksV1>().ExecuteAsync<UpdateResult>(new SyncExternalWorksParametersV1(), default);
        }

        private sealed class FixedTime(DateTimeOffset now) : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = now;
            public override DateTimeOffset GetUtcNow() => Now;
        }

        private sealed class FakeTmdbCatalog : HttpMessageHandler
        {
            public List<Uri> Requests { get; } = [];

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                Requests.Add(uri);
                if (uri.Host == "files.tmdb.org")
                {
                    var lines = uri.AbsolutePath switch
                    {
                        "/p/exports/movie_ids_10_19_2026.json.gz" => new[]
                        {
                            """{"adult":false,"id":603,"original_title":"The Matrix","popularity":50.5,"video":false}""",
                            """{"adult":true,"id":9,"original_title":"Adult","popularity":99,"video":false}""",
                            """{"adult":false,"id":10,"original_title":"Extra","popularity":5,"video":true}""",
                            "not json",
                            """{"adult":false,"id":604,"original_title":"The Matrix Reloaded","popularity":40,"video":false}""",
                            """{"adult":false,"id":999,"original_title":"Gone","popularity":1,"video":false}"""
                        },
                        "/p/exports/tv_series_ids_10_19_2026.json.gz" => ["""{"id":1399,"original_name":"Game of Thrones","popularity":80}"""],
                        _ => null
                    };
                    return Task.FromResult(lines is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Gzip(lines)) });
                }

                if (request.Headers.Authorization?.Parameter != Token) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                var body = uri.AbsolutePath switch
                {
                    "/3/movie/603" => """{"id":603,"title":"Matrix","original_title":"The Matrix","release_date":"1999-03-31","overview":"Ein Hacker.","poster_path":"/matrix.jpg","popularity":60,"vote_average":8.2,"imdb_id":"tt0133093"}""",
                    "/3/movie/604" => """{"id":604,"title":"Matrix Reloaded","original_title":"The Matrix Reloaded","release_date":"2003-05-15","vote_average":0}""",
                    "/3/tv/1399" => """{"id":1399,"name":"Game of Thrones","original_name":"Game of Thrones","first_air_date":"2011-04-17","external_ids":{"imdb_id":"tt0944947"}}""",
                    "/3/movie/changes" => """{"results":[{"id":603,"adult":false}],"page":1,"total_pages":1}""",
                    "/3/tv/changes" => """{"results":[],"page":1,"total_pages":1}""",
                    _ => null
                };
                return Task.FromResult(body is null ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }

            private static byte[] Gzip(IEnumerable<string> lines)
            {
                using var buffer = new MemoryStream();
                using (var zip = new GZipStream(buffer, CompressionLevel.Fastest))
                {
                    var bytes = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
                    zip.Write(bytes);
                }
                return buffer.ToArray();
            }
        }
    }
}
