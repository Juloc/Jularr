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
using Jularr.Backend.Data.Enums.Media;
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
    // Full local discovery for TMDB films (series come from TheTVDB): the daily id list fills the catalog, entries are read by popularity, changes are read again.
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
            // Caching alone keeps no full catalog: its sub-option LocalDiscovery does.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'LocalDiscovery'");

            // The id list adds titles without adult ones and video extras; entries are read and gone ones leave.
            await RunAsync(host);
            Assert.AreEqual("Matrix", await host.ScalarAsync(Catalog("tmdb_movie", "603", "\"CanonicalTitle\"")));
            Assert.AreEqual("tt0133093", await host.ScalarAsync(Catalog("tmdb_movie", "603", "(SELECT imdb.\"Value\" FROM \"WorkExternalIdentities\" AS imdb WHERE imdb.\"WorkId\" = work.\"Id\" AND imdb.\"Namespace\" = 'imdb')")));
            Assert.AreEqual(8.2m, await host.ScalarAsync(Catalog("tmdb_movie", "603", "\"Rating\"")));
            Assert.AreEqual(1999, await host.ScalarAsync(Catalog("tmdb_movie", "603", "\"ReleaseYear\"::integer")));
            Assert.AreEqual("https://image.tmdb.org/t/p/w500/matrix.jpg", await host.ScalarAsync(Catalog("tmdb_movie", "603", "\"ImageUrl\"")));
            Assert.AreEqual(DBNull.Value, await host.ScalarAsync(Catalog("tmdb_movie", "604", "\"Rating\"")));
            Assert.AreEqual(true, await host.ScalarAsync(Catalog("tmdb_movie", "604", "\"CatalogNamespace\" = 'tmdb_movie' AND \"DetailedAt\" IS NULL")),
                "A catalog title is a minimal Work without details.");
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"WorkExternalIdentities\" WHERE \"Value\" IN ('9', '10', '999')"));
            Assert.IsFalse(tmdb.Requests.Any(request => request.AbsolutePath.Contains("tv", StringComparison.Ordinal)), "TMDB's series are no longer kept whole.");
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"Works\" WHERE \"CatalogNamespace\" = 'tmdb_tv'"));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"Works\" WHERE \"CatalogNamespace\" IS NOT NULL AND \"FetchedAt\" IS NULL"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ExternalWorkSyncs\" WHERE \"ListedOn\" = '2026-10-19'"));
            // The following pass finds no pending details and publishes the complete catalogs.
            await RunAsync(host);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ExternalWorkSyncs\" WHERE \"CompletedAt\" IS NOT NULL"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM pg_stats WHERE schemaname = current_schema() AND tablename = 'Works' AND attname = 'TrendingRank'"),
                "Completing the initial catalog must prepare its ranking statistics before local discovery uses it.");
            Assert.IsFalse(tmdb.Requests.Any(request => request.AbsolutePath.EndsWith("/changes", StringComparison.Ordinal)), "The first pass reads everything anyway.");

            // The same day the list is not read again; six hours later the changed entries are read again.
            var listed = tmdb.Requests.Count(request => request.Host == "files.tmdb.org");
            time.Now = time.Now.AddHours(7);
            await host.Fixture.RunSqlAsync("UPDATE \"Works\" SET \"CanonicalTitle\" = 'Stale' WHERE \"Id\" IN (SELECT \"WorkId\" FROM \"WorkExternalIdentities\" WHERE \"Namespace\" = 'tmdb_movie' AND \"Value\" IN ('603', '604'))");
            await RunAsync(host);
            Assert.AreEqual(listed, tmdb.Requests.Count(request => request.Host == "files.tmdb.org"));
            Assert.IsTrue(tmdb.Requests.Any(request => request.AbsolutePath == "/3/movie/changes" && request.Query.Contains("start_date=2026-10-19", StringComparison.Ordinal)));
            Assert.AreEqual("Matrix", await host.ScalarAsync(Catalog("tmdb_movie", "603", "\"CanonicalTitle\"")));
            Assert.AreEqual("Stale", await host.ScalarAsync(Catalog("tmdb_movie", "604", "\"CanonicalTitle\"")));
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
            Assert.AreEqual("2:1", await host.ScalarAsync(Catalog("anilist", "1", "\"MediaTypeId\" || ':' || \"MediaSubtypeId\"")));
            Assert.AreEqual(8.4m, await host.ScalarAsync(Catalog("anilist", "1", "\"Rating\"")));
            Assert.AreEqual("Frieren", await host.ScalarAsync(Catalog("anilist", "1", "\"CanonicalTitle\"")));
            Assert.AreEqual("2023-09-29", await host.ScalarAsync(Catalog("anilist", "1", "\"ReleasedOn\"::text")));
            Assert.AreEqual("3:3", await host.ScalarAsync(Catalog("anilist", "3", "\"MediaTypeId\" || ':' || \"MediaSubtypeId\"")));
            Assert.AreEqual("3:2", await host.ScalarAsync(Catalog("anilist", "5", "\"MediaTypeId\" || ':' || \"MediaSubtypeId\"")));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"WorkExternalIdentities\" WHERE \"Namespace\" = 'anilist' AND \"Value\" = '2'"), "A music video is no Discover title.");
            Assert.AreEqual("2026-10-19", await host.ScalarAsync("SELECT \"ListedOn\"::text FROM \"ExternalWorkSyncs\" WHERE \"Namespace\" = 'anilist' AND \"Cursor\" IS NULL"));

            // A finished walk rests for a week.
            await RunAsync(host);
            Assert.AreEqual(2, aniList.Pages.Count);
        }

        [TestMethod]
        public void Windows_AndUse_DecideWhetherACatalogSyncs()
        {
            var sync = new ExternalWorkSyncData("anilist", 20, true, null, null, null);
            IReadOnlyList<ExternalWorkSyncWindowData> night = [new("anilist", new TimeOnly(22, 0), new TimeOnly(6, 0)), new("tmdb_movie", new TimeOnly(12, 0), new TimeOnly(13, 0))];
            Assert.IsTrue(ExternalWorkLogic.MaySync(CachingTier.Full, sync, [], new TimeOnly(15, 0), false), "Without windows a catalog syncs any time.");
            Assert.IsTrue(ExternalWorkLogic.MaySync(CachingTier.Full, sync, night, new TimeOnly(23, 30), false));
            Assert.IsTrue(ExternalWorkLogic.MaySync(CachingTier.Full, sync, night, new TimeOnly(5, 59), false), "A window past midnight ends the next day.");
            Assert.IsFalse(ExternalWorkLogic.MaySync(CachingTier.Full, sync, night, new TimeOnly(12, 30), false), "Another catalog's window does not count.");
            Assert.IsFalse(ExternalWorkLogic.MaySync(CachingTier.Full, sync, night, new TimeOnly(23, 30), true), "It waits while Jularr is used.");
            var eager = new ExternalWorkSyncData("anilist", 20, false, null, null, null);
            Assert.IsTrue(ExternalWorkLogic.MaySync(CachingTier.Full, eager, night, new TimeOnly(23, 30), true));
            Assert.IsFalse(ExternalWorkLogic.MaySync(CachingTier.Standard, eager, [], new TimeOnly(23, 30), false), "Only the full catalog runs the full sync.");
            Assert.IsFalse(ExternalWorkLogic.MaySync(CachingTier.Lean, eager, [], new TimeOnly(23, 30), false));
            Assert.IsFalse(ExternalWorkLogic.MaySync(CachingTier.Full, new ExternalWorkSyncData("openlibrary", 20, false, null, null, null), [], new TimeOnly(23, 30), false),
                "A catalog without a full list is never kept whole.");
            Assert.IsFalse(ExternalWorkLogic.MaySync(CachingTier.Full, new ExternalWorkSyncData("tvdb", 120, false, null, null, null), [], new TimeOnly(23, 30), false),
                "TheTVDB's card only budgets the details of the series lists.");
        }

        [TestMethod]
        public void CachingTier_ComesFromTheModuleAndItsSubOption_ForTheWholeInstance()
        {
            Assert.AreEqual(CachingTier.Lean, ExternalWorkLogic.CachingTierOf(false, false));
            Assert.AreEqual(CachingTier.Lean, ExternalWorkLogic.CachingTierOf(false, true), "The full catalog is a sub-option of the Caching module.");
            Assert.AreEqual(CachingTier.Standard, ExternalWorkLogic.CachingTierOf(true, false));
            Assert.AreEqual(CachingTier.Full, ExternalWorkLogic.CachingTierOf(true, true));
            Assert.AreEqual(CachingTier.Full, ExternalWorkLogic.CatalogTier(CachingTier.Full, "anilist"));
            Assert.AreEqual(CachingTier.Full, ExternalWorkLogic.CatalogTier(CachingTier.Full, "tmdb_movie"));
            Assert.AreEqual(CachingTier.Standard, ExternalWorkLogic.CatalogTier(CachingTier.Full, "openlibrary"), "Without a full list a catalog keeps Discover's lists.");
            Assert.AreEqual(CachingTier.Lean, ExternalWorkLogic.CatalogTier(CachingTier.Lean, "anilist"));
        }

        [TestMethod]
        public async Task Syncs_ShowEachCatalogsTier_AsTheModulesSetIt()
        {
            await using var host = await Host.CreateAsync(library: true);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            await host.Fixture.RunSqlAsync("INSERT INTO \"ExternalWorkSyncs\" (\"Namespace\", \"RequestsPerMinute\") VALUES ('openlibrary', 30)");

            async Task<string> TiersAsync()
            {
                using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/catalog/syncs");
                Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
                return string.Join(',', (await read.Content.ReadFromJsonAsync<BaseDataList<ExternalWorkSyncV1>>())!.Select(sync => sync.Namespace + ":" + sync.Tier));
            }

            // Caching is on and the full catalog off by default.
            Assert.AreEqual("anilist:Standard,openlibrary:Standard,tmdb_movie:Standard,tvdb:Standard", await TiersAsync());
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'LocalDiscovery'");
            Assert.AreEqual("anilist:Full,openlibrary:Standard,tmdb_movie:Full,tvdb:Standard", await TiersAsync(), "TheTVDB has no full list and is never kept whole.");
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" IN ('LocalDiscovery', 'DiscoverCache')");
            Assert.AreEqual("anilist:Lean,openlibrary:Lean,tmdb_movie:Lean,tvdb:Lean", await TiersAsync());
        }

        [TestMethod]
        public async Task Settings_RefuseAnyoneButTheOwner()
        {
            await using var host = await Host.CreateAsync(library: true);
            using var anonymous = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/catalog/syncs");
            Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            var version = (long)(await host.ScalarAsync("SELECT \"Version\" FROM \"ExternalWorkSyncs\" WHERE \"Namespace\" = 'anilist'"))!;
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/catalog/syncs");
            Assert.AreEqual(HttpStatusCode.Forbidden, read.StatusCode);
            using var write = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/catalog/syncs/anilist", new UpdateExternalWorkSyncDataV1(5, false, version));
            Assert.AreEqual(HttpStatusCode.Forbidden, write.StatusCode);
            Assert.AreEqual("20:true", await host.ScalarAsync("SELECT \"RequestsPerMinute\" || ':' || \"PausesWhileUsed\" FROM \"ExternalWorkSyncs\" WHERE \"Namespace\" = 'anilist'"));
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
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/catalog/syncs/anilist", new UpdateExternalWorkSyncDataV1(15, true, aniList.Version, windows));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            Assert.AreEqual("02:00:00-06:00:00,22:30:00-01:00:00", await host.ScalarAsync("SELECT string_agg(\"StartsAt\"::text || '-' || \"EndsAt\"::text, ',' ORDER BY \"StartsAt\") FROM \"ExternalWorkSyncWindows\""));

            // An old version and a window without length are refused; the saved windows stay.
            using var stale = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/catalog/syncs/anilist", new UpdateExternalWorkSyncDataV1(10, true, aniList.Version));
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);
            using var empty = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/catalog/syncs/anilist",
                new UpdateExternalWorkSyncDataV1(10, true, aniList.Version + 1, new([new(new TimeOnly(3, 0), new TimeOnly(3, 0))])));
            Assert.AreEqual(HttpStatusCode.BadRequest, empty.StatusCode);
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"ExternalWorkSyncWindows\""));
            Assert.AreEqual(15, await host.ScalarAsync("SELECT \"RequestsPerMinute\"::integer FROM \"ExternalWorkSyncs\" WHERE \"Namespace\" = 'anilist'"));
        }

        [TestMethod]
        public async Task CatalogPage_ShowsHowTheModulesKeepEachCatalog_WithoutAChoiceOfItsOwn()
        {
            await using var host = await Host.CreateAsync(library: true, razor: true);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "ExternalWorkSyncs" ("Namespace", "RequestsPerMinute") VALUES ('openlibrary', 30);
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" = 'LocalDiscovery';
                """);

            async Task<string> PageAsync()
            {
                using var response = await host.SendAsync(HttpMethod.Get, "/Admin/Catalog");
                var html = await response.Content.ReadAsStringAsync();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, html);
                return html;
            }

            static string Card(string html, string @namespace) => html[html.IndexOf("id=\"catalog-" + @namespace + "\"", StringComparison.Ordinal)..] is var rest
                ? rest[..(rest.IndexOf("</article>", StringComparison.Ordinal) + 10)] : "";

            // With the full catalog a catalog with a full list is kept whole, the others keep Discover's lists; each card keeps its pace, pause and windows.
            var html = await PageAsync();
            Assert.IsFalse(html.Contains("id=\"catalog-tmdb_tv\"", StringComparison.Ordinal), "TMDB's series catalog has no card.");
            foreach (var (@namespace, mode) in new[] { ("anilist", "Whole"), ("tmdb_movie", "Whole"), ("openlibrary", "Kept"), ("tvdb", "Kept") })
            {
                var card = Card(html, @namespace);
                StringAssert.Contains(card, ">" + mode + "<", @namespace);
                StringAssert.Contains(card, "name=\"RequestsPerMinute\"", @namespace);
                StringAssert.Contains(card, "name=\"PausesWhileUsed\"", @namespace);
            }
            foreach (var gone in new[] { "name=\"CacheTier\"", "name=\"IsEnabled\"", "name=\"CatalogDetailKeepDays\"", "name=\"DetailsDiscoverAhead\"", "handler=Cache\"" })
            {
                Assert.IsFalse(html.Contains(gone, StringComparison.Ordinal), gone);
            }

            // The images of other titles stay the page's one section.
            StringAssert.Contains(html, "<h2>Images of other titles</h2>");
            StringAssert.Contains(html, "action=\"/Admin/Catalog?handler=Covers\"");
            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(html, "<h2>").Count, "No other heading.");

            // Without the Caching module Discover asks live.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" IN ('LocalDiscovery', 'DiscoverCache')");
            html = await PageAsync();
            StringAssert.Contains(Card(html, "anilist"), ">Live<");
            StringAssert.Contains(html, "Discover keeps nothing locally and always asks the providers.");
        }

        // One column of the catalog Work a provider identity names.
        private static string Catalog(string @namespace, string value, string column) =>
            $"SELECT {column} FROM \"Works\" AS work JOIN \"WorkExternalIdentities\" AS identity ON identity.\"WorkId\" = work.\"Id\" WHERE identity.\"Namespace\" = '{@namespace}' AND identity.\"Value\" = '{value}'";

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
                        _ => null
                    };
                    return Task.FromResult(lines is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Gzip(lines)) });
                }

                if (request.Headers.Authorization?.Parameter != Token) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                var body = uri.AbsolutePath switch
                {
                    "/3/movie/603" => """{"id":603,"title":"Matrix","original_title":"The Matrix","release_date":"1999-03-31","overview":"Ein Hacker.","poster_path":"/matrix.jpg","popularity":60,"vote_average":8.2,"imdb_id":"tt0133093"}""",
                    "/3/movie/604" => """{"id":604,"title":"Matrix Reloaded","original_title":"The Matrix Reloaded","release_date":"2003-05-15","vote_average":0}""",
                    "/3/movie/changes" => """{"results":[{"id":603,"adult":false}],"page":1,"total_pages":1}""",
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
