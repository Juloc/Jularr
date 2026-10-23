using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Plex;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class AdminProviderTests
    {
        [TestMethod]
        public async Task PlexServerPin_GrantedByItsBrowser_ConnectsOnceWithoutExposingTheToken()
        {
            await using var host = await Host.CreateAsync(media: true, services: services => services.AddHttpClient<PlexServerClient>().ConfigurePrimaryHttpMessageHandler(() => new PinServer()));
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            using var started = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/providers/plex/pin", new SavePlexServerDataV1("http://pin-server.example.test:32400", null));
            Assert.AreEqual(HttpStatusCode.OK, started.StatusCode, await started.Content.ReadAsStringAsync());
            var attempt = (await started.Content.ReadFromJsonAsync<StartPlexResultV1>())!;
            var route = "/api/v1/admin/providers/plex/pin/" + attempt.AuthChallengePublicId;
            host.Plex.Pending = true;
            using var pending = await host.SendAsync(HttpMethod.Post, route);
            Assert.AreEqual(HttpStatusCode.OK, pending.StatusCode, await pending.Content.ReadAsStringAsync());
            Assert.IsFalse((await pending.Content.ReadFromJsonAsync<UpdateResult>())!.Success);
            host.Plex.Pending = false;
            using var completed = await host.SendAsync(HttpMethod.Post, route);
            Assert.AreEqual(HttpStatusCode.OK, completed.StatusCode, await completed.Content.ReadAsStringAsync());
            Assert.IsTrue((await completed.Content.ReadFromJsonAsync<UpdateResult>())!.Success);
            Assert.IsFalse((await completed.Content.ReadAsStringAsync()).Contains("verified-plex-token", StringComparison.Ordinal));
            Assert.AreEqual("http://pin-server.example.test:32400/", await host.ScalarAsync("SELECT \"BaseUrl\" FROM \"Providers\" WHERE \"Key\" = 'plex'"));
            Assert.AreEqual("pin-server", await host.ScalarAsync("SELECT \"ExternalId\" FROM \"Providers\" WHERE \"Key\" = 'plex'"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AccountAuthChallenges\" WHERE \"AuthChallengePurposeTypeId\" = 8 AND \"AccountId\" = 41 AND \"ConsumedAt\" IS NOT NULL AND \"ProtectedState\" IS NULL"));
            using var replay = await host.SendAsync(HttpMethod.Post, route);
            Assert.AreEqual(HttpStatusCode.Unauthorized, replay.StatusCode);
        }

        [TestMethod]
        [DataRow("browser")]
        [DataRow("owner")]
        [DataRow("expired")]
        [DataRow("login")]
        public async Task PlexServerPin_ForeignOrExpiredProof_CannotConnect(string reason)
        {
            await using var host = await Host.CreateAsync(plexEnabled: true, media: true);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            using var started = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/providers/plex/pin", new SavePlexServerDataV1("http://pin-server.example.test:32400", null));
            Assert.AreEqual(HttpStatusCode.OK, started.StatusCode, await started.Content.ReadAsStringAsync());
            var attempt = (await started.Content.ReadFromJsonAsync<StartPlexResultV1>())!;
            if (reason == "browser") host.Cookies[ServiceApi.AuthenticationNonceCookieName] = new string('x', 43);
            if (reason == "expired") await host.Fixture.RunSqlAsync("UPDATE \"AccountAuthChallenges\" SET \"CreatedAt\" = now() - interval '1 hour', \"ExpiresAt\" = now() - interval '1 minute'");
            if (reason == "owner") await host.Fixture.RunSqlAsync("UPDATE \"AccountAuthChallenges\" SET \"AccountId\" = 42");
            var route = reason == "login" ? "/api/v1/auth/plex/" : "/api/v1/admin/providers/plex/pin/";
            using var denied = await host.SendAsync(HttpMethod.Post, route + attempt.AuthChallengePublicId);
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode, await denied.Content.ReadAsStringAsync());
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"Providers\" WHERE \"Key\" = 'plex' AND \"ProtectedSecret\" IS NOT NULL"));
        }

        private sealed class PinServer : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Assert.AreEqual("pin-server.example.test", request.RequestUri!.Host);
                Assert.AreEqual("verified-plex-token", request.Headers.GetValues("X-Plex-Token").Single());
                object data = request.RequestUri.AbsolutePath == "/identity"
                    ? new { MediaContainer = (object)new { machineIdentifier = "pin-server", version = "1" } }
                    : new { MediaContainer = (object)new { Directory = new[] { new { key = "7", type = "movie", title = "Movies" } } } };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(data, options: new JsonSerializerOptions { PropertyNamingPolicy = null }) });
            }
        }

        [TestMethod]
        public async Task Provider_OwnerToggle_ChangesLoginAvailabilityAndRejectsStaleOrIncompleteInput()
        {
            await using var host = await Host.CreateAsync(plexEnabled: true);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            var id = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Providers\" WHERE \"Key\" = 'plex'"))!;
            var route = "/api/v1/admin/providers/" + id;
            using var missing = await host.SendAsync(HttpMethod.Put, route, new { expectedIsEnabled = true });
            Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"Providers\" WHERE \"Key\" = 'plex'"));

            using var disabled = await host.SendAsync(HttpMethod.Put, route, new UpdateProviderDataV1(false, true));
            Assert.AreEqual(HttpStatusCode.OK, disabled.StatusCode, await disabled.Content.ReadAsStringAsync());
            using var changed = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            var authentication = (await changed.Content.ReadFromJsonAsync<AuthenticationResultV1>())!;
            Assert.IsFalse(authentication.PlexLoginEnabled);
            Assert.IsTrue(authentication.IsAuthenticated);
            using var stale = await host.SendAsync(HttpMethod.Put, route, new UpdateProviderDataV1(true, true));
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);

            // Plex sign-in is the Owner's setting; registration needs sign-in and the connected server.
            using var enabled = await host.SendAsync(HttpMethod.Put, route, new UpdateProviderDataV1(true, false));
            Assert.AreEqual(HttpStatusCode.OK, enabled.StatusCode, await enabled.Content.ReadAsStringAsync());
            using var savedSignIn = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/plex/sign-in", new SavePlexSignInDataV1(false, true, false, ["3"]));
            Assert.AreEqual(HttpStatusCode.OK, savedSignIn.StatusCode, await savedSignIn.Content.ReadAsStringAsync());
            using var signIn = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/providers/plex/sign-in");
            var settings = (await signIn.Content.ReadFromJsonAsync<PlexSignInV1>())!;
            Assert.IsFalse(settings.SignIn);
            Assert.IsFalse(settings.Registration);
            Assert.IsFalse(settings.RegistrationApproval);
            CollectionAssert.AreEqual(new[] { "3" }, settings.RegistrationSections);
            using var off = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            Assert.IsFalse((await off.Content.ReadFromJsonAsync<AuthenticationResultV1>())!.PlexLoginEnabled);
            using var invalid = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/plex/sign-in", new SavePlexSignInDataV1(true, true, true, ["Movies"]));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var on = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/plex/sign-in", new SavePlexSignInDataV1(true, true, true, []));
            Assert.AreEqual(HttpStatusCode.OK, on.StatusCode, await on.Content.ReadAsStringAsync());
            using var offered = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            var plex = (await offered.Content.ReadFromJsonAsync<AuthenticationResultV1>())!;
            Assert.IsTrue(plex.PlexLoginEnabled);
            Assert.IsTrue(plex.PlexRegistrationEnabled);

            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            using var deniedRead = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/providers");
            Assert.AreEqual(HttpStatusCode.Forbidden, deniedRead.StatusCode);
            using var deniedWrite = await host.SendAsync(HttpMethod.Put, route, new UpdateProviderDataV1(true, false));
            Assert.AreEqual(HttpStatusCode.Forbidden, deniedWrite.StatusCode);
        }

        [TestMethod]
        public async Task Razor_ProviderForms_HaveUniqueLabelsAndRequireBooleanInput()
        {
            await using var host = await Host.CreateAsync(razor: true, media: true);
            await host.Fixture.RunSqlAsync("INSERT INTO \"Providers\" (\"Key\") VALUES ('test-registry')");
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Providers");
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, await page.Content.ReadAsStringAsync());
            var html = await page.Content.ReadAsStringAsync();
            // Every provider is one card with its own switch; the media servers are on their own chip (and their modules are off).
            var fields = Regex.Matches(html, "id=\"(provider-(?!settings-)[a-z0-9-]+)\"").Select(match => match.Groups[1].Value).ToArray();
            var registered = (long)(await host.ScalarAsync("SELECT count(*) FROM \"Providers\" WHERE \"Key\" NOT IN ('plex', 'jellyfin', 'emby')"))!;
            Assert.AreEqual(registered, fields.Length);
            Assert.AreEqual(registered, fields.Distinct(StringComparer.Ordinal).Count());
            var csrf = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
            var id = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Providers\" WHERE \"Key\" = 'plex'"))!;
            using var incomplete = await host.SendAsync(HttpMethod.Post, "/Admin/Providers?providerId=" + id, new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(csrf), ["ExpectedIsEnabled"] = "True"
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, incomplete.StatusCode);
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"Providers\" WHERE \"Key\" = 'plex'"));
            using var disabled = await host.SendAsync(HttpMethod.Post, "/Admin/Providers?providerId=" + id, new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(csrf), ["ExpectedIsEnabled"] = "True", ["IsEnabled"] = "False"
            });
            Assert.AreEqual(HttpStatusCode.Redirect, disabled.StatusCode, await disabled.Content.ReadAsStringAsync());
            Assert.AreEqual(false, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"Providers\" WHERE \"Key\" = 'plex'"));
        }
    }
}
