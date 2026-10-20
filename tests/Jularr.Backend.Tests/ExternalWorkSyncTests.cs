using System.IO.Compression;
using System.Net;
using System.Security;
using System.Text;
using System.Net.Http.Json;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Service.System.Media.V1;
using System.Text.Json;
using Jularr.Integrations.Logic.AniList;
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

        [TestMethod]
        public async Task AniList_WalksItsTitlesByPage_KeepsTheFormatsDiscoverShows_AndWaitsWhileUsed()
        {
            var aniList = new FakeAniListCatalog();
            var time = new FixedTime(new DateTimeOffset(2026, 10, 19, 3, 0, 0, TimeSpan.Zero));
            await using var host = await Host.CreateAsync(library: true, services: services =>
            {
                services.AddHttpClient<AniListClient>().ConfigurePrimaryHttpMessageHandler(() => aniList);
                services.AddSingleton<TimeProvider>(time);
            });
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'LocalDiscovery'");

            // While someone uses Jularr the walk waits.
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            using var used = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/catalog/syncs");
            await RunAsync(host);
            Assert.AreEqual(0, aniList.Pages.Count);

            await host.Fixture.RunSqlAsync("UPDATE \"ExternalWorkSyncs\" SET \"PausesWhileUsed\" = FALSE WHERE \"Namespace\" = 'anilist'");
            await RunAsync(host);
            CollectionAssert.AreEqual(new[] { 0, 3 }, aniList.Pages.ToArray());
            Assert.AreEqual("2:1", await host.ScalarAsync("SELECT \"MediaTypeId\" || ':' || \"MediaSubtypeId\" FROM \"ExternalWorks\" WHERE \"Namespace\" = 'anilist' AND \"Value\" = '1'"));
            Assert.AreEqual(8.4m, await host.ScalarAsync("SELECT \"Rating\" FROM \"ExternalWorks\" WHERE \"Value\" = '1'"));
            Assert.AreEqual("Frieren", await host.ScalarAsync("SELECT \"Title\" FROM \"ExternalWorks\" WHERE \"Value\" = '1'"));
            Assert.AreEqual("2023-09-29", await host.ScalarAsync("SELECT \"ReleasedOn\"::text FROM \"ExternalWorks\" WHERE \"Value\" = '1'"));
            Assert.AreEqual("3:3", await host.ScalarAsync("SELECT \"MediaTypeId\" || ':' || \"MediaSubtypeId\" FROM \"ExternalWorks\" WHERE \"Value\" = '3'"));
            Assert.AreEqual("3:2", await host.ScalarAsync("SELECT \"MediaTypeId\" || ':' || \"MediaSubtypeId\" FROM \"ExternalWorks\" WHERE \"Value\" = '5'"));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"ExternalWorks\" WHERE \"Value\" = '2'"), "A music video is no Discover title.");
            Assert.AreEqual("2026-10-19", await host.ScalarAsync("SELECT \"ListedOn\"::text FROM \"ExternalWorkSyncs\" WHERE \"Namespace\" = 'anilist' AND \"Cursor\" IS NULL"));

            // A finished walk rests for a week.
            await RunAsync(host);
            Assert.AreEqual(2, aniList.Pages.Count);
        }

        [TestMethod]
        public void Windows_AndUse_DecideWhetherACatalogSyncs()
        {
            var sync = new ExternalWorkSyncData("anilist", true, 20, true, null, null, null);
            IReadOnlyList<ExternalWorkSyncWindowData> night = [new("anilist", new TimeOnly(22, 0), new TimeOnly(6, 0)), new("tmdb_movie", new TimeOnly(12, 0), new TimeOnly(13, 0))];
            Assert.IsTrue(ExternalWorkLogic.MaySync(sync, [], new TimeOnly(15, 0), false), "Without windows a catalog syncs any time.");
            Assert.IsTrue(ExternalWorkLogic.MaySync(sync, night, new TimeOnly(23, 30), false));
            Assert.IsTrue(ExternalWorkLogic.MaySync(sync, night, new TimeOnly(5, 59), false), "A window past midnight ends the next day.");
            Assert.IsFalse(ExternalWorkLogic.MaySync(sync, night, new TimeOnly(12, 30), false), "Another catalog's window does not count.");
            Assert.IsFalse(ExternalWorkLogic.MaySync(sync, night, new TimeOnly(23, 30), true), "It waits while Jularr is used.");
            Assert.IsTrue(ExternalWorkLogic.MaySync(new ExternalWorkSyncData("anilist", true, 20, false, null, null, null), night, new TimeOnly(23, 30), true));
            Assert.IsFalse(ExternalWorkLogic.MaySync(new ExternalWorkSyncData("anilist", false, 20, false, null, null, null), [], new TimeOnly(23, 30), false));
        }

        [TestMethod]
        public async Task Settings_SaveChoicesAndWindows_WithTheirVersion()
        {
            await using var host = await Host.CreateAsync(library: true);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/catalog/syncs");
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
            var aniList = (await read.Content.ReadFromJsonAsync<BaseDataList<ExternalWorkSyncV1>>())!.Single(sync => sync.Namespace == "anilist");
            Assert.AreEqual(20, aniList.RequestsPerMinute);
            Assert.IsTrue(aniList.PausesWhileUsed);

            var windows = new BaseDataList<ExternalWorkSyncWindowV1>([new(new TimeOnly(2, 0), new TimeOnly(6, 0)), new(new TimeOnly(22, 30), new TimeOnly(1, 0))]);
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/catalog/syncs/anilist", new UpdateExternalWorkSyncDataV1(true, 15, true, aniList.Version, windows));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            Assert.AreEqual("02:00:00-06:00:00,22:30:00-01:00:00", await host.ScalarAsync("SELECT string_agg(\"StartsAt\"::text || '-' || \"EndsAt\"::text, ',' ORDER BY \"StartsAt\") FROM \"ExternalWorkSyncWindows\""));

            // An old version and a window without length are refused; the saved windows stay.
            using var stale = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/catalog/syncs/anilist", new UpdateExternalWorkSyncDataV1(true, 10, true, aniList.Version));
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);
            using var empty = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/catalog/syncs/anilist",
                new UpdateExternalWorkSyncDataV1(true, 10, true, aniList.Version + 1, new([new(new TimeOnly(3, 0), new TimeOnly(3, 0))])));
            Assert.AreEqual(HttpStatusCode.BadRequest, empty.StatusCode);
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"ExternalWorkSyncWindows\""));
            Assert.AreEqual(15, await host.ScalarAsync("SELECT \"RequestsPerMinute\"::integer FROM \"ExternalWorkSyncs\" WHERE \"Namespace\" = 'anilist'"));
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

        private sealed class FakeAniListCatalog : HttpMessageHandler
        {
            public List<int> Pages { get; } = [];
            private const string Frieren = """{"id":1,"type":"ANIME","format":"TV","title":{"romaji":"Sousou no Frieren","english":"Frieren","native":"葬送のフリーレン"},"startDate":{"year":2023,"month":9,"day":29},"coverImage":{"large":"https://img.anili.st/1.jpg"},"popularity":500000,"averageScore":84}""";

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var after = body.RootElement.GetProperty("variables").GetProperty("after").GetInt32();
                Pages.Add(after);
                var json = after == 0
                    ? """{"data":{"Page":{"pageInfo":{"hasNextPage":true},"media":[""" + Frieren + "," + """{"id":2,"type":"ANIME","format":"MUSIC","title":{"romaji":"Song"},"startDate":{"year":2020}},{"id":3,"type":"MANGA","format":"NOVEL","title":{"romaji":"Novel"},"startDate":{"year":2019}}]}}}"""
                    : """{"data":{"Page":{"pageInfo":{"hasNextPage":false},"media":[{"id":5,"type":"MANGA","format":"MANGA","title":{"romaji":"Manga"},"averageScore":0}]}}}""";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
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
