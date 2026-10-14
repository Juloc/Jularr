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
            var jellyfin = new FakeJellyfin();
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
            using var connected = await host.SendAsync(HttpMethod.Post, Route, new SaveMediaServerDataV1("http://jellyfin.example.test:8096", FakeJellyfin.ApiKey));
            Assert.AreEqual(HttpStatusCode.OK, connected.StatusCode, await connected.Content.ReadAsStringAsync());
            Assert.AreEqual(2, (await connected.Content.ReadFromJsonAsync<MediaServerTestV1>())!.UserCount);
            using var read = await host.SendAsync(HttpMethod.Get, Route);
            Assert.IsFalse((await read.Content.ReadAsStringAsync()).Contains(FakeJellyfin.ApiKey, StringComparison.Ordinal));
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
            Assert.IsFalse(html.Contains(FakeJellyfin.ApiKey, StringComparison.Ordinal));
        }

        [TestMethod]
        public void Merge_BothSidesMoved_FinishingWinsThenTheFurtherPosition()
        {
            var agreed = DateTime.UtcNow.AddHours(-1);
            var later = DateTime.UtcNow;

            // Watched on, both sides: the further position.
            var both = MediaServerLogic.Merge(Candidate(remote: (300, false), local: (500, false, later), synced: (100, false, agreed)))!;
            Assert.AreEqual((500L, false, false, true), (both.PositionMs, both.IsCompleted, both.WritesLocal, both.WritesRemote));

            // Finished on the server while Jularr was further along: finishing wins.
            var finished = MediaServerLogic.Merge(Candidate(remote: (0, true), local: (900, false, later), synced: (100, false, agreed)))!;
            Assert.AreEqual((0L, true, true, false), (finished.PositionMs, finished.IsCompleted, finished.WritesLocal, finished.WritesRemote));

            // Only the server moved back: a rewatch, taken over.
            var rewatch = MediaServerLogic.Merge(Candidate(remote: (50, true), local: (0, true, agreed.AddMinutes(-1)), synced: (0, true, agreed)))!;
            Assert.AreEqual((50L, true, true, false), (rewatch.PositionMs, rewatch.IsCompleted, rewatch.WritesLocal, rewatch.WritesRemote));

            // Nothing moved since the last agreement.
            Assert.IsNull(MediaServerLogic.Merge(Candidate(remote: (100, false), local: (100, false, agreed.AddMinutes(-1)), synced: (100, false, agreed))));
        }

        private static ProgressCandidateData Candidate((long Position, bool Completed) remote, (long Position, bool Completed, DateTime UpdatedAt) local,
            (long Position, bool Completed, DateTime At) synced) =>
            new("m1", remote.Position, remote.Completed, null, 10, null, local.Position, local.Completed, local.UpdatedAt, synced.Position, synced.Completed, synced.At);

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

        // One server with two users; only u1 has watched anything.
        private sealed class FakeJellyfin : HttpMessageHandler
        {
            public const string ApiKey = "jellyfin-test-key";

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

                if (!request.Headers.TryGetValues("Authorization", out var authorization) || authorization.Single() != "MediaBrowser Token=\"" + ApiKey + "\"")
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }

                var path = request.RequestUri!.AbsolutePath;
                var query = request.RequestUri.Query;
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
                    return Json(JsonSerializer.Serialize(new
                    {
                        Items = new object[]
                        {
                            Item("m603", "Movie", new { Tmdb = "603" }, null, null, null),
                            Item("m604", "Movie", new { Tmdb = "604" }, null, null, null),
                            Item("e1", "Episode", new { }, "s1", 1, 1)
                        },
                        TotalRecordCount = 3
                    }));
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
