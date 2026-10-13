using System.Net;
using System.Net.Http.Json;
using System.Text;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Integrations.Logic.Igdb;
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
