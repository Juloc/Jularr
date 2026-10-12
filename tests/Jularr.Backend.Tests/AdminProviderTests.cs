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

            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            using var deniedRead = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/providers");
            Assert.AreEqual(HttpStatusCode.Forbidden, deniedRead.StatusCode);
            using var deniedWrite = await host.SendAsync(HttpMethod.Put, route, new UpdateProviderDataV1(true, false));
            Assert.AreEqual(HttpStatusCode.Forbidden, deniedWrite.StatusCode);
        }

        [TestMethod]
        public async Task Razor_ProviderForms_HaveUniqueLabelsAndRequireBooleanInput()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await host.Fixture.RunSqlAsync("INSERT INTO \"Providers\" (\"Key\") VALUES ('test-registry')");
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Providers");
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, await page.Content.ReadAsStringAsync());
            var html = await page.Content.ReadAsStringAsync();
            var fields = Regex.Matches(html, "id=\"(form-[^\"]+-IsEnabled)\"").Select(match => match.Groups[1].Value).ToArray();
            Assert.AreEqual(3, fields.Length);
            Assert.AreEqual(3, fields.Distinct(StringComparer.Ordinal).Count());
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
