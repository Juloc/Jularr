using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Public.Authentication.V1;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class AdminProviderTests
    {
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
            // A two-way choice is one segmented control; its first option names the field.
            var fields = Regex.Matches(html, "id=\"(form-[^\"]+-IsEnabled)(?:-0)?\"").Select(match => match.Groups[1].Value).ToArray();
            // Media servers appear with their module, which is off.
            var registered = (long)(await host.ScalarAsync("SELECT count(*) FROM \"Providers\" WHERE \"Key\" NOT IN ('jellyfin', 'emby')"))!;
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
