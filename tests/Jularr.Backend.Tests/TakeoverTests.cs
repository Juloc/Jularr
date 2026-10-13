using System.Net;
using System.Net.Http.Json;
using System.Text;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Integrations.Logic.Servarr;
using Jularr.Integrations.Logic.Tmdb;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class TakeoverTests
    {
        private const string Takeover = "/api/v1/admin/acquisition/takeover";
        private const string RadarrKey = "radarr-secret-key";
        private const string TmdbToken = "eyJ-takeover-token";

        [TestMethod]
        public async Task Takeover_RadarrPreviewThenApply_AddsReadableIndexersAndWantsMissingMovies()
        {
            var radarr = new FakeRadarr();
            await using var host = await Host.CreateAsync(razor: true, library: true, services: services =>
            {
                services.AddHttpClient<ServarrApiClient>().ConfigurePrimaryHttpMessageHandler(() => radarr);
                services.AddHttpClient<TmdbClient>().ConfigurePrimaryHttpMessageHandler(() => radarr);
            });
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            var tmdb = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Providers\" WHERE \"Key\" = 'tmdb'"))!;
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/" + tmdb + "/secret", new SaveProviderSecretDataV1(TmdbToken));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());

            // A wrong key is refused without touching anything.
            using var refused = await host.SendAsync(HttpMethod.Post, Takeover, new RunTakeoverDataV1(TakeoverSourceType.Radarr, "http://radarr.example.test:7878", "wrong", false));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, refused.StatusCode);

            // The preview shows what would happen and changes nothing.
            var preview = await RunAsync(host, false);
            Assert.AreEqual("takeover_will_add", preview.Single(item => item.Name == "Readable").OutcomeKey);
            Assert.AreEqual("takeover_needs_key", preview.Single(item => item.Name == "Hidden").OutcomeKey);
            Assert.AreEqual("https://hidden.example.test", preview.Single(item => item.Name == "Hidden").SuggestedUrl);
            Assert.AreEqual("takeover_needs_key", preview.Single(item => item.TakeoverItemTypeId == TakeoverItemType.Client).OutcomeKey);
            Assert.AreEqual("takeover_will_want", preview.Single(item => item.Name == "The Matrix (1999)").OutcomeKey);
            Assert.AreEqual("takeover_no_tmdb", preview.Single(item => item.Name == "Unknown (2001)").OutcomeKey);
            Assert.IsFalse(preview.Any(item => item.Name.StartsWith("Owned", StringComparison.Ordinal) || item.Name.StartsWith("Ignored", StringComparison.Ordinal)));
            Assert.IsFalse(preview.Any(item => item.Name == "Tracker"));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionConnections\""));

            // Taking over adds the readable indexer and wants the missing movie; a second run only repeats nothing.
            var applied = await RunAsync(host, true);
            Assert.AreEqual("takeover_added", applied.Single(item => item.Name == "Readable").OutcomeKey);
            Assert.AreEqual("takeover_wanted", applied.Single(item => item.Name == "The Matrix (1999)").OutcomeKey);
            Assert.AreEqual("https://readable.example.test", await host.ScalarAsync("SELECT \"BaseUrl\" FROM \"AcquisitionConnections\""));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"WantedItems\""));
            var again = await RunAsync(host, true);
            Assert.AreEqual("takeover_exists", again.Single(item => item.Name == "Readable").OutcomeKey);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionConnections\""));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"WantedItems\""));

            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Acquisition/Takeover");
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, await page.Content.ReadAsStringAsync());
        }

        private static async Task<IReadOnlyList<TakeoverItemV1>> RunAsync(Host host, bool apply)
        {
            using var response = await host.SendAsync(HttpMethod.Post, Takeover, new RunTakeoverDataV1(TakeoverSourceType.Radarr, "http://radarr.example.test:7878", RadarrKey, apply));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<BaseDataList<TakeoverItemV1>>())!;
        }

        private sealed class FakeRadarr : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                if (uri.Host == "api.themoviedb.org")
                {
                    return Task.FromResult(request.Headers.Authorization?.Parameter != TmdbToken ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : uri.AbsolutePath switch
                    {
                        "/3/movie/603" => Json("""{"id":603,"title":"The Matrix","original_title":"The Matrix","release_date":"1999-03-31","overview":"A hacker learns the truth.","external_ids":{"imdb_id":"tt0133093"}}"""),
                        _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                    });
                }

                if (!request.Headers.TryGetValues("X-Api-Key", out var keys) || keys.Single() != RadarrKey)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                }

                return Task.FromResult(uri.AbsolutePath switch
                {
                    "/api/v3/indexer" => Json("""
                        [
                          {"name":"Readable","protocol":"usenet","implementation":"Newznab","enable":true,"priority":10,
                           "fields":[{"name":"baseUrl","value":"https://readable.example.test"},{"name":"apiPath","value":"/api"},{"name":"apiKey","value":"readable-key"},{"name":"categories","value":[2000,2040]}]},
                          {"name":"Hidden","protocol":"usenet","implementation":"Newznab","enable":true,"priority":20,
                           "fields":[{"name":"baseUrl","value":"https://hidden.example.test"},{"name":"apiPath","value":"/api"},{"name":"apiKey","value":"********"}]},
                          {"name":"Tracker","protocol":"torrent","implementation":"Torznab","enable":true,"priority":5,"fields":[]}
                        ]
                        """),
                    "/api/v3/downloadclient" => Json("""
                        [{"name":"SAB","implementation":"Sabnzbd","enable":true,"priority":1,
                          "fields":[{"name":"host","value":"sab.example.test"},{"name":"port","value":8080},{"name":"useSsl","value":false},{"name":"apiKey","value":"********"},{"name":"movieCategory","value":"movies"}]}]
                        """),
                    "/api/v3/movie" => Json("""
                        [
                          {"title":"The Matrix","year":1999,"tmdbId":603,"monitored":true,"hasFile":false},
                          {"title":"Owned","year":2000,"tmdbId":604,"monitored":true,"hasFile":true},
                          {"title":"Ignored","year":2000,"tmdbId":605,"monitored":false,"hasFile":false},
                          {"title":"Unknown","year":2001,"monitored":true,"hasFile":false}
                        ]
                        """),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                });
            }

            private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
