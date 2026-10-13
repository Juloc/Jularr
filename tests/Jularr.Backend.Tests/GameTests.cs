using System.Net;
using System.Net.Http.Json;
using System.Text;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Request.V1;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Data.User.Request.V1;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.System.Acquisition.V1;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Integrations.Logic.Igdb;
using Jularr.Integrations.Logic.Newznab;
using Jularr.Integrations.Logic.Sabnzbd;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class GameTests
    {
        private const string ClientId = "twitch-client-id";
        private const string ClientSecret = "twitch-client-secret";

        [TestMethod]
        public async Task Game_IgdbApplicationSearchAndImport_KeepsPlatformsAsIdentities()
        {
            var igdb = new FakeIgdb();
            await using var host = await Host.CreateAsync(razor: true, library: true, services: services => services.AddHttpClient<IgdbClient>().ConfigurePrimaryHttpMessageHandler(() => igdb));
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            var search = new SearchMetadataDataV1(MediaType.Game, "Example Quest");

            // Without the Twitch application there is no games provider.
            using var unavailable = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/metadata/search", search);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            var provider = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Providers\" WHERE \"Key\" = 'igdb'"))!;
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/" + provider + "/secret", new SaveProviderSecretDataV1(ClientSecret, ClientId));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            using var providers = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/providers");
            Assert.IsFalse((await providers.Content.ReadAsStringAsync()).Contains(ClientSecret, StringComparison.Ordinal));

            // IGDB names the game, its developer and platforms.
            using var found = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/metadata/search", search);
            Assert.AreEqual(HttpStatusCode.OK, found.StatusCode, await found.Content.ReadAsStringAsync());
            var result = (await found.Content.ReadFromJsonAsync<BaseDataList<MetadataSearchResultV1>>())!.Single();
            Assert.AreEqual("1022", result.ExternalId);
            Assert.AreEqual("Example Studio", result.Author);
            Assert.AreEqual(2017, result.ReleaseYear);

            // Import keeps the platforms as shared identities; a repeat changes nothing.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Game, "1022"));
                Assert.AreEqual(HttpStatusCode.OK, imported.StatusCode, await imported.Content.ReadAsStringAsync());
            }

            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Works\" WHERE \"MediaTypeId\" = 7"));
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"GamePlatforms\""));
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"WorkPlatforms\""));
            Assert.AreEqual("Switch", await host.ScalarAsync("SELECT \"Abbreviation\" FROM \"GamePlatforms\" WHERE \"IgdbId\" = 130"));
            Assert.AreEqual("Example Studio", await host.ScalarAsync("SELECT \"Value\" FROM \"WorkMetadataFacts\" WHERE \"FactKey\" = 'Developer'"));
            Assert.AreEqual("https://images.igdb.com/igdb/image/upload/t_cover_big/co1abc.jpg", await host.ScalarAsync("SELECT \"Value\" FROM \"WorkMetadataFacts\" WHERE \"FactKey\" = 'Poster'"));

            // One Twitch token serves every request; IGDB only sees the application id.
            Assert.AreEqual(1, igdb.TokenRequests);
            Assert.IsTrue(igdb.ApiClientIds.All(id => id == ClientId));
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Providers");
            StringAssert.Contains(await page.Content.ReadAsStringAsync(), "ClientId");
        }

        [TestMethod]
        public async Task Game_RequestedPlatform_IsSearchedGrabbedAndImportedForThatPlatform()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            using var downloads = new LibraryTests.TemporaryDirectory();
            var igdb = new FakeIgdb();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, services: services =>
            {
                services.AddHttpClient<IgdbClient>().ConfigurePrimaryHttpMessageHandler(() => igdb);
                services.AddHttpClient<NewznabClient>().ConfigurePrimaryHttpMessageHandler(() => igdb);
                services.AddHttpClient<SabnzbdClient>().ConfigurePrimaryHttpMessageHandler(() => igdb);
            });
            var mountId = await LibraryTests.CreateMountAsync(host);
            await host.Fixture.RunSqlAsync("UPDATE \"StorageMounts\" SET \"IsReadOnly\" = FALSE");
            Directory.CreateDirectory(Path.Combine(storage.Path, mountId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Games"));
            await LibraryTests.CreateRootAsync(host, mountId, "Games", MediaType.Game);
            foreach (var data in new[]
            {
                new SaveAcquisitionConnectionDataV1("Indexer", AcquisitionConnectionType.Newznab, "https://indexer.example.test/", "indexer-key", true, 10, [], null, null, null),
                new SaveAcquisitionConnectionDataV1("SAB", AcquisitionConnectionType.Sabnzbd, "https://sab.example.test", "client-key", true, 10, [], "games", "/downloads", downloads.Path)
            })
            {
                using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/acquisition/connections", data);
                Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            }

            var provider = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Providers\" WHERE \"Key\" = 'igdb'"))!;
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/" + provider + "/secret", new SaveProviderSecretDataV1(ClientSecret, ClientId));
            using var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Game, "1022"));
            var work = (await imported.Content.ReadFromJsonAsync<Jularr.Backend.Data.Admin.Media.V1.WorkDetailsV1>())!;
            using var listed = await host.SendAsync(HttpMethod.Get, "/api/v1/works/" + work.Id + "/platforms");
            var platforms = (await listed.Content.ReadFromJsonAsync<BaseDataList<GamePlatformV1>>())!;
            var nsw = platforms.Single(platform => platform.Name == "Nintendo Switch");

            // A game request names one of the game's platforms.
            using var missing = await host.SendAsync(HttpMethod.Post, "/api/v1/works/" + work.Id + "/requests", new CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);
            using var foreign = await host.SendAsync(HttpMethod.Post, "/api/v1/works/" + work.Id + "/requests", new CreateRequestDataV1(1, 999_999));
            Assert.AreEqual(HttpStatusCode.BadRequest, foreign.StatusCode);
            using var requested = await host.SendAsync(HttpMethod.Post, "/api/v1/works/" + work.Id + "/requests", new CreateRequestDataV1(1, nsw.Id));
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, await requested.Content.ReadAsStringAsync());
            var request = (await requested.Content.ReadFromJsonAsync<CreateRequestResultV1>())!;
            using var approved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/requests/" + request.Id, new UpdateRequestDataV1(AcquisitionRequestStatusType.Approved, 1));
            Assert.AreEqual(HttpStatusCode.OK, approved.StatusCode, await approved.Content.ReadAsStringAsync());
            Assert.AreEqual(nsw.Id, (long)(await host.ScalarAsync("SELECT \"GamePlatformId\" FROM \"WantedItems\""))!);

            // Only the wanted platform's release is taken; the best form wins.
            await RunAsync<SearchDueWantedV1>(host);
            CollectionAssert.AreEqual(new[] { "https://indexer.example.test/getnzb/nsp" }, igdb.Grabbed);

            // The release lands below its platform and tags the version.
            var job = Directory.CreateDirectory(Path.Combine(downloads.Path, "Example.Quest.Switch.NSP-GRP2")).FullName;
            File.WriteAllBytes(Path.Combine(job, "example.quest.nsp"), new byte[5000]);
            File.WriteAllText(Path.Combine(job, "grp2.nfo"), "release notes");
            igdb.History = """{"history":{"slots":[{"nzo_id":"SABnzbd_nzo_1","name":"Game","status":"Completed","storage":"/downloads/Example.Quest.Switch.NSP-GRP2"}]}}""";
            await RunAsync<MonitorDownloadsV1>(host);
            var folder = Path.Combine(storage.Path, "1", "Games", "Nintendo Switch", "Example Quest (2017)");
            CollectionAssert.AreEqual(new[] { "example.quest.nsp" }, Directory.GetFiles(folder).Select(Path.GetFileName).ToArray());
            Assert.AreEqual(nsw.Id, (long)(await host.ScalarAsync("SELECT \"GamePlatformId\" FROM \"GameReleases\""))!);
            Assert.AreEqual((short)6, await host.ScalarAsync("SELECT \"AcquisitionRequestStatusTypeId\" FROM \"AcquisitionRequests\""));

            // A scanned copy takes the platform its folder names; details list every platform on hand.
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt")
                SELECT "Id", 'PC/Example Quest (2017)/setup.iso', 10, now(), now() FROM "LibraryRoots"
                """);
            var scanned = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\" WHERE \"RelativePath\" LIKE 'PC/%'"))!;
            using var identified = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + scanned + "/assignment", new AssignLibraryFileDataV1(work.Id, null, null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, identified.StatusCode, await identified.Content.ReadAsStringAsync());
            using var details = await host.SendAsync(HttpMethod.Get, "/api/v1/works/" + work.Id);
            CollectionAssert.AreEqual(new[] { "Nintendo Switch", "PC (Microsoft Windows)" }, (await details.Content.ReadFromJsonAsync<Jularr.Backend.Data.User.Media.V1.WorkDetailsV1>())!.Platforms);
        }

        [TestMethod]
        [DataRow("Example.Quest.Switch.NSP-GRP2", 130, "Matches")]
        [DataRow("Example.Quest.PS4-DUPLEX", 130, "WrongPlatform")]
        [DataRow("Example.Quest-CODEX", 130, "AmbiguousTitle")]
        [DataRow("Example.Quest-CODEX", 6, "Matches")]
        [DataRow("Example.Quest.Update.v1.2.NSW-GRP", 130, "AmbiguousTitle")]
        [DataRow("Example.Quest.2.NSW-GRP", 130, "AmbiguousTitle")]
        [DataRow("Example.Quest.XBOX.ONE-GRP", 49, "Matches")]
        [DataRow("Other.Game.NSW-GRP", 130, "WrongTitle")]
        [DataRow("Example.Quest.XBOX.ONE-GRP", 11, "WrongPlatform")]
        [DataRow("Example.Quest.XBOX360-GRP", 11, "WrongPlatform")]
        [DataRow("Example.Quest.XBOX-GRP", 11, "Matches")]
        [DataRow("Example.Quest.Wii.U-GRP", 5, "WrongPlatform")]
        [DataRow("Example.Quest.v1.0.3-CODEX", 6, "Matches")]
        [DataRow("Example.Quest.Amiga-GRP", 16, "Matches")]
        [DataRow("Example.Quest.Xbox.Series.X-GRP", 169, "Matches")]
        public void Game_ReleaseNames_AreJudgedForTheWantedPlatform(string release, int platform, string expected) =>
            Assert.AreEqual(expected, GameReleaseJudge.Judge(["Example Quest"], platform, platform == 16 ? "Amiga" : null,
                new ReleaseOffer(release, 1, "Indexer", 25, "guid", new Uri("https://indexer.example.test/guid"), null, 1_000_000, DateTimeOffset.UtcNow, ["title"])).Match.ToString());

        private static async Task RunAsync<TService>(Host host) where TService : Service.Core.SystemService<AcquisitionCycleParametersV1, NoData>
        {
            await using var scope = host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TService>().ExecuteAsync<UpdateResult>(new AcquisitionCycleParametersV1(), default);
        }

        private sealed class FakeIgdb : HttpMessageHandler
        {
            private const string Game = """
                {"id":1022,"name":"Example Quest","first_release_date":1488499200,"summary":"A test adventure.","cover":{"image_id":"co1abc"},
                 "platforms":[{"id":130,"name":"Nintendo Switch","abbreviation":"Switch"},{"id":130,"name":"Nintendo Switch","abbreviation":"NSW"},{"id":6,"name":"PC (Microsoft Windows)","abbreviation":"PC"}],
                 "involved_companies":[{"company":{"name":"Example Studio"},"developer":true},{"company":{"name":"Example Publisher"},"developer":false}],
                 "alternative_names":[{"name":"Beispiel Quest"}]}
                """;

            public int TokenRequests { get; private set; }
            public List<string> ApiClientIds { get; } = [];
            public List<string> Grabbed { get; } = [];
            public string History { get; set; } = """{"history":{"slots":[]}}""";

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                if (uri.Host == "id.twitch.tv")
                {
                    var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                    TokenRequests++;
                    return form.Contains("client_secret=" + ClientSecret, StringComparison.Ordinal) && form.Contains("client_id=" + ClientId, StringComparison.Ordinal)
                        ? Json("""{"access_token":"igdb-access-token","expires_in":5000000,"token_type":"bearer"}""")
                        : new HttpResponseMessage(HttpStatusCode.BadRequest);
                }

                if (uri.Host == "indexer.example.test")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(uri.Query.Contains("t=caps", StringComparison.Ordinal)
                            ? """<caps><limits max="100" default="100" /><searching><search available="yes" supportedParams="q" /></searching></caps>"""
                            : """
                              <rss><channel>
                                <item><title>Example.Quest.NSW-GRP</title><guid>nsw</guid><link>https://indexer.example.test/getnzb/nsw</link><size>5000000000</size></item>
                                <item><title>Example.Quest.Switch.NSP-GRP2</title><guid>nsp</guid><link>https://indexer.example.test/getnzb/nsp</link><size>5000000000</size></item>
                                <item><title>Example.Quest-CODEX</title><guid>pc</guid><link>https://indexer.example.test/getnzb/pc</link><size>5000000000</size></item>
                                <item><title>Example.Quest.PS4-DUPLEX</title><guid>ps4</guid><link>https://indexer.example.test/getnzb/ps4</link><size>5000000000</size></item>
                              </channel></rss>
                              """, Encoding.UTF8, "application/rss+xml")
                    };
                }

                if (uri.Host == "sab.example.test")
                {
                    var form = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
                    switch (form["mode"])
                    {
                        case "addurl":
                            Grabbed.Add(form["name"]!);
                            return Json("{\"status\":true,\"nzo_ids\":[\"SABnzbd_nzo_" + Grabbed.Count + "\"]}");
                        case "queue":
                            return Json("""{"queue":{"paused":false,"slots":[]}}""");
                        case "history":
                            return Json(History);
                        default:
                            return Json("""{"version":"4.5.0"}""");
                    }
                }

                if (request.Headers.Authorization?.Parameter != "igdb-access-token")
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }

                ApiClientIds.Add(request.Headers.GetValues("Client-ID").Single());
                return Json("[" + Game + "]");
            }

            private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
