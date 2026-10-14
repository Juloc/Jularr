using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Provider;
using Jularr.Backend.Logic.Provider;
using Jularr.Integrations.Logic.MediaServer;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class MediaServerTests
    {
        private const string Route = "/api/v1/admin/media-servers/Jellyfin";

        [TestMethod]
        public async Task Jellyfin_ConfirmedUser_SharesProgressBothWaysAndCatchesUpAfterAnOutage()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var jellyfin = new FakeServer();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                services: services => services.AddHttpClient<MediaServerClient>().ConfigurePrimaryHttpMessageHandler(() => jellyfin));
            jellyfin.ProfileName = (string)(await host.ScalarAsync("SELECT \"DisplayName\" FROM \"Profiles\" WHERE \"Id\" = 101"))!;
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (10, 1, 'Example Film'), (20, 2, 'Example Show'), (30, 1, 'Second Film');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (10, 'tmdb_movie', '603'), (20, 'tvdb', '81189'), (30, 'tmdb_movie', '604');
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (21, 20, 1, 1);
                INSERT INTO "WorkUnits" ("Id", "WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") VALUES (22, 20, 21, 2, 1);
                INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "ProgressPositionTypeId", "PositionMs") VALUES (101, 30, 1, 600000);
                """);

            // Off by default.
            using var off = await host.SendAsync(HttpMethod.Get, Route);
            Assert.AreEqual(HttpStatusCode.Forbidden, off.StatusCode);
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'ProgressSync'");

            // Only a server that accepts the key is stored; its users are proposed the profile of the same name.
            using var refused = await host.SendAsync(HttpMethod.Post, Route, new SaveMediaServerDataV1("http://jellyfin.example.test:8096", "wrong"));
            StringAssert.Contains(await refused.Content.ReadAsStringAsync(), "media_server_authentication_failed");
            using var connected = await host.SendAsync(HttpMethod.Post, Route, new SaveMediaServerDataV1("http://jellyfin.example.test:8096", FakeServer.ApiKey));
            Assert.AreEqual(HttpStatusCode.OK, connected.StatusCode, await connected.Content.ReadAsStringAsync());
            Assert.AreEqual(2, (await connected.Content.ReadFromJsonAsync<MediaServerTestV1>())!.UserCount);
            using var read = await host.SendAsync(HttpMethod.Get, Route);
            Assert.IsFalse((await read.Content.ReadAsStringAsync()).Contains(FakeServer.ApiKey, StringComparison.Ordinal));
            var links = await ReadLinksAsync(host);
            var proposed = links.Single(link => link.ExternalName == jellyfin.ProfileName);
            Assert.AreEqual(101L, proposed.ProfileId);
            Assert.IsFalse(proposed.IsConfirmed);
            Assert.IsNull(links.Single(link => link.ExternalName == "Guest").ProfileId);

            // Nothing moves before an administrator confirms.
            Assert.AreEqual((0, 0), await SyncAsync(host));
            using var confirmed = await host.SendAsync(HttpMethod.Put, Route + "/links/" + proposed.Id, new SaveMediaServerLinkDataV1(101));
            Assert.AreEqual(HttpStatusCode.OK, confirmed.StatusCode, await confirmed.Content.ReadAsStringAsync());
            var guest = links.Single(link => link.ExternalName == "Guest");
            using var taken = await host.SendAsync(HttpMethod.Put, Route + "/links/" + guest.Id, new SaveMediaServerLinkDataV1(101));
            StringAssert.Contains(await taken.Content.ReadAsStringAsync(), "duplicate_value");
            using var declined = await host.SendAsync(HttpMethod.Put, Route + "/links/" + guest.Id, new SaveMediaServerLinkDataV1(null));
            Assert.AreEqual(HttpStatusCode.OK, declined.StatusCode, await declined.Content.ReadAsStringAsync());

            // The server's film position and finished episode come over; Jularr's own position goes out.
            Assert.AreEqual((2, 1), await SyncAsync(host));
            Assert.AreEqual(3_000_000L, await host.ScalarAsync("SELECT \"PositionMs\" FROM \"MediaProgress\" WHERE \"WorkId\" = 10"));
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"IsCompleted\" FROM \"MediaProgress\" WHERE \"WorkUnitId\" = 22"));
            Assert.AreEqual(600_000L, jellyfin.Positions["m604"]);
            Assert.AreEqual((0, 0), await SyncAsync(host));

            // Later changes on either side follow, a rewatch on the server included.
            await host.Fixture.RunSqlAsync("UPDATE \"MediaProgress\" SET \"PositionMs\" = 4000000, \"UpdatedAt\" = now() WHERE \"WorkId\" = 10");
            jellyfin.Positions["m604"] = 100_000;
            Assert.AreEqual((1, 1), await SyncAsync(host));
            Assert.AreEqual(4_000_000L, jellyfin.Positions["m603"]);
            Assert.AreEqual(100_000L, await host.ScalarAsync("SELECT \"PositionMs\" FROM \"MediaProgress\" WHERE \"WorkId\" = 30"));

            // An unreachable server changes nothing; the next pass catches up.
            jellyfin.IsAvailable = false;
            await host.Fixture.RunSqlAsync("UPDATE \"MediaProgress\" SET \"PositionMs\" = 5000000, \"UpdatedAt\" = now() WHERE \"WorkId\" = 10");
            using var down = await host.SendAsync(HttpMethod.Post, Route + "/sync");
            StringAssert.Contains(await down.Content.ReadAsStringAsync(), "media_server_unavailable");
            jellyfin.IsAvailable = true;
            Assert.AreEqual((0, 1), await SyncAsync(host));
            Assert.AreEqual(5_000_000L, jellyfin.Positions["m603"]);

            // The providers page shows who shares progress with which profile.
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Providers");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, WebUtility.HtmlEncode(jellyfin.ProfileName + " shares progress with " + jellyfin.ProfileName));
            Assert.IsFalse(html.Contains(FakeServer.ApiKey, StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task Emby_TwoVersionsOfAFilm_AgreeOnTheFurtherPosition()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var emby = new FakeServer { IsEmby = true };
            emby.Positions["m603b"] = 200_000;
            emby.Played["m603b"] = false;
            emby.Positions["m603"] = 100_000;
            await using var host = await LibraryTests.CreateHostAsync(storage.Path,
                services: services => services.AddHttpClient<MediaServerClient>().ConfigurePrimaryHttpMessageHandler(() => emby));
            emby.ProfileName = (string)(await host.ScalarAsync("SELECT \"DisplayName\" FROM \"Profiles\" WHERE \"Id\" = 101"))!;
            await host.Fixture.RunSqlAsync("""
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" = 'ProgressSync';
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (10, 1, 'Example Film');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (10, 'tmdb_movie', '603');
                """);
            const string route = "/api/v1/admin/media-servers/Emby";
            using var connected = await host.SendAsync(HttpMethod.Post, route, new SaveMediaServerDataV1("http://emby.example.test:8096", FakeServer.ApiKey));
            Assert.AreEqual(HttpStatusCode.OK, connected.StatusCode, await connected.Content.ReadAsStringAsync());
            using var path = await host.SendAsync(HttpMethod.Post, route, new SaveMediaServerDataV1("http://emby.example.test:8096/emby", FakeServer.ApiKey));
            StringAssert.Contains(await path.Content.ReadAsStringAsync(), "invalid_url");
            using var linksResponse = await host.SendAsync(HttpMethod.Get, route + "/links");
            var link = (await linksResponse.Content.ReadFromJsonAsync<MediaServerLinkV1[]>())!.Single(item => item.ExternalName == emby.ProfileName);
            using var confirmed = await host.SendAsync(HttpMethod.Put, route + "/links/" + link.Id, new SaveMediaServerLinkDataV1(101));
            Assert.AreEqual(HttpStatusCode.OK, confirmed.StatusCode, await confirmed.Content.ReadAsStringAsync());

            // Jularr takes the further version; the other version is brought level, and the next pass has nothing to do.
            using var synced = await host.SendAsync(HttpMethod.Post, route + "/sync");
            var result = (await synced.Content.ReadFromJsonAsync<MediaServerSyncResultV1>())!;
            Assert.AreEqual((1, 1), (result.LocalCount, result.RemoteCount));
            Assert.AreEqual(200_000L, await host.ScalarAsync("SELECT \"PositionMs\" FROM \"MediaProgress\" WHERE \"WorkId\" = 10"));
            Assert.AreEqual(200_000L, emby.Positions["m603"]);
            using var again = await host.SendAsync(HttpMethod.Post, route + "/sync");
            var quiet = (await again.Content.ReadFromJsonAsync<MediaServerSyncResultV1>())!;
            Assert.AreEqual((0, 0), (quiet.LocalCount, quiet.RemoteCount));
        }

        [TestMethod]
        public void Merge_BothSidesMoved_FinishingWinsThenTheFurtherPosition()
        {
            var agreed = DateTime.UtcNow.AddHours(-1);
            var later = DateTime.UtcNow;

            // Watched on, both sides: the further position, sent to the server.
            var both = MediaServerLogic.Plan([Candidate(remote: (300, false), local: (500, false, later), synced: (100, false, agreed))]).Single();
            Assert.AreEqual((500L, false, false, true), (both.Target.PositionMs, both.Target.IsCompleted, both.WritesLocal, both.WritesRemote));

            // Finished on the server while Jularr was further along: finishing wins.
            var finished = MediaServerLogic.Plan([Candidate(remote: (0, true), local: (900, false, later), synced: (100, false, agreed))]).Single();
            Assert.AreEqual((0L, true, true, false), (finished.Target.PositionMs, finished.Target.IsCompleted, finished.WritesLocal, finished.WritesRemote));

            // Only the server moved back: a rewatch, taken over.
            var rewatch = MediaServerLogic.Plan([Candidate(remote: (50, true), local: (0, true, agreed.AddMinutes(-1)), synced: (0, true, agreed))]).Single();
            Assert.AreEqual((50L, true, true, false), (rewatch.Target.PositionMs, rewatch.Target.IsCompleted, rewatch.WritesLocal, rewatch.WritesRemote));

            // Nothing moved since the last agreement.
            Assert.AreEqual(0, MediaServerLogic.Plan([Candidate(remote: (100, false), local: (100, false, agreed.AddMinutes(-1)), synced: (100, false, agreed))]).Count);
        }

        private static ProgressCandidateData Candidate((long Position, bool Completed) remote, (long Position, bool Completed, DateTime UpdatedAt) local,
            (long Position, bool Completed, DateTime At) synced) =>
            new("m1", remote.Position, remote.Completed, null, 10, null, local.Position, local.Completed, local.UpdatedAt, synced.Position, synced.Completed, synced.At, DateTime.UtcNow);

        private static async Task<MediaServerLinkV1[]> ReadLinksAsync(Host host)
        {
            using var response = await host.SendAsync(HttpMethod.Get, Route + "/links");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<MediaServerLinkV1[]>())!;
        }

        private static async Task<(int, int)> SyncAsync(Host host)
        {
            using var response = await host.SendAsync(HttpMethod.Post, Route + "/sync");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var result = (await response.Content.ReadFromJsonAsync<MediaServerSyncResultV1>())!;
            return (result.LocalCount, result.RemoteCount);
        }

        // One server with two users; only u1 has watched anything. Emby names the user in the path and takes its own key header.
        private sealed class FakeServer : HttpMessageHandler
        {
            public const string ApiKey = "media-server-test-key";

            public bool IsEmby { get; init; }
            public string ProfileName { get; set; } = "";
            public bool IsAvailable { get; set; } = true;
            public Dictionary<string, long> Positions { get; } = new() { ["m603"] = 3_000_000, ["m604"] = 0, ["e1"] = 0 };
            public Dictionary<string, bool> Played { get; } = new() { ["m603"] = false, ["m604"] = false, ["e1"] = true };

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (!IsAvailable)
                {
                    throw new HttpRequestException("offline");
                }

                var key = IsEmby
                    ? request.Headers.TryGetValues("X-Emby-Token", out var token) ? token.Single() : null
                    : request.Headers.TryGetValues("Authorization", out var authorization) ? authorization.Single() : null;
                if (key != (IsEmby ? ApiKey : "MediaBrowser Token=\"" + ApiKey + "\""))
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }

                var path = request.RequestUri!.AbsolutePath;
                var query = request.RequestUri.Query;
                if (IsEmby && path == "/Users/u1/Items")
                {
                    path = "/Items";
                }

                if (IsEmby && path.StartsWith("/Users/u1/Items/", StringComparison.Ordinal) && path.EndsWith("/UserData", StringComparison.Ordinal))
                {
                    path = "/UserItems/" + path.Split('/')[4] + "/UserData";
                    query = "?userId=u1";
                }
                if (path == "/System/Info")
                {
                    return Json("""{"Id":"jf1","ServerName":"Home","Version":"10.10.7"}""");
                }

                if (path == "/Users")
                {
                    return Json(JsonSerializer.Serialize(new[] { new { Id = "u1", Name = ProfileName }, new { Id = "u2", Name = "Guest" } }));
                }

                if (path == "/Items" && query.Contains("IncludeItemTypes=Series&", StringComparison.Ordinal))
                {
                    return Json("""{"Items":[{"Id":"s1","Type":"Series","ProviderIds":{"Tvdb":"81189"}}],"TotalRecordCount":1}""");
                }

                if (path == "/Items")
                {
                    var items = Positions.Keys.Select(id => id switch
                    {
                        "e1" => Item(id, "Episode", new { }, "s1", 1, 1),
                        "m604" => Item(id, "Movie", new { Tmdb = "604" }, null, null, null),
                        _ => Item(id, "Movie", new { Tmdb = "603" }, null, null, null)
                    }).ToArray();
                    return Json(JsonSerializer.Serialize(new { Items = items, TotalRecordCount = items.Length }));
                }

                if (request.Method == HttpMethod.Post && path.StartsWith("/UserItems/", StringComparison.Ordinal) && query == "?userId=u1")
                {
                    var id = path.Split('/')[2];
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                    Positions[id] = body.RootElement.GetProperty("PlaybackPositionTicks").GetInt64() / TimeSpan.TicksPerMillisecond;
                    Played[id] = body.RootElement.GetProperty("Played").GetBoolean();
                    return Json("{}");
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            private object Item(string id, string type, object providers, string? seriesId, int? season, int? episode) => new
            {
                Id = id,
                Type = type,
                ProviderIds = providers,
                SeriesId = seriesId,
                ParentIndexNumber = season,
                IndexNumber = episode,
                RunTimeTicks = 72_000_000_000L,
                UserData = new { PlaybackPositionTicks = Positions[id] * TimeSpan.TicksPerMillisecond, Played = Played[id] }
            };

            private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
