using System.Net;
using System.Text.RegularExpressions;
using System.Text.Encodings.Web;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Service.Core;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class FrontendAuthenticationTests
    {
        [TestMethod]
        public async Task Razor_PageFolders_PreservePublicRoutes()
        {
            await using var host = await Host.CreateAsync(razor: true);
            using var login = await host.SendAsync(HttpMethod.Get, "/Account/Login");
            using var signedIn = await host.SendAsync(HttpMethod.Post, "/Account/Login", Form(await login.Content.ReadAsStringAsync(), ("Email", "a@example.test"), ("Password", "test-password-123")));
            Assert.AreEqual(HttpStatusCode.Redirect, signedIn.StatusCode);

            // Folder moves preserve URLs.
            foreach (var path in new[] { "/", "/Details/101", "/Player/101", "/Watchlist", "/Settings", "/Account/Profiles", "/Account/Sessions", "/Account/Logout" })
            {
                using var page = await host.SendAsync(HttpMethod.Get, path);
                Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, path);
            }

            using var discover = await host.SendAsync(HttpMethod.Get, "/Discover?media=movie");
            Assert.AreEqual(HttpStatusCode.MovedPermanently, discover.StatusCode);
            Assert.AreEqual("/?media=movie", discover.Headers.Location!.OriginalString);
        }

        [TestMethod]
        public async Task Form_PasswordMetadataAndBackendError_RenderAutomaticallyWithoutSecrets()
        {
            await using var host = await Host.CreateAsync(razor: true);
            using var scope = host.Services.CreateScope();
            var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
            var texts = scope.ServiceProvider.GetRequiredService<UiText>();
            await texts.InitializeAsync(http);

            // Validate through the backend field.
            var data = new RegisterDataV1("new@example.test", "New", "short", 1);
            var validation = Assert.ThrowsExactly<DataValidationException>(data.Validate);
            Assert.AreEqual("Password", validation.Field);
            Assert.AreEqual("too_short", validation.Code);
            new LoginDataV1("a@example.test", "short").Validate();
            new RegisterDataV1("new@example.test", "New", "  valid passphrase  ", 1).Validate();
            Assert.ThrowsExactly<DataValidationException>(() => new RegisterDataV1("new@example.test", "New", new string('x', 129), 1).Validate());

            // The component consumes shared metadata/errors.
            var form = new Form<RegisterDataV1>(fields => fields.Password(item => item.Password, "account.field.password", "new-password"),
                "/Account/Login", "test-antiforgery-token", data, ServiceError.FromException(validation));
            using var writer = new StringWriter();
            form.WriteTo(writer, HtmlEncoder.Default, texts);
            var html = writer.ToString();
            StringAssert.Contains(html, "minlength=\"12\"");
            StringAssert.Contains(html, "maxlength=\"128\"");
            StringAssert.Contains(html, "required=\"required\"");
            StringAssert.Contains(html, "aria-invalid=\"true\"");
            StringAssert.Contains(html, texts["errors.validation.too_short"]);
            Assert.IsFalse(html.Contains("value=\"short\"", StringComparison.Ordinal));
        }

        [TestMethod]
        [DataRow(3, true, false)]
        [DataRow(1, true, true)]
        [DataRow(2, false, false)]
        public async Task Razor_RolePolicies_SeparateManualMediaAndConfiguration(int role, bool mediaAllowed, bool configurationAllowed)
        {
            await using var host = await Host.CreateAsync(razor: true);
            await host.Fixture.RunSqlAsync($"UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = {role} WHERE \"Id\" = 41");
            using var login = await host.SendAsync(HttpMethod.Get, "/Account/Login");
            using var signedIn = await host.SendAsync(HttpMethod.Post, "/Account/Login", Form(await login.Content.ReadAsStringAsync(), ("Email", "a@example.test"), ("Password", "test-password-123")));
            foreach (var path in new[] { "/Admin/Requests", "/Admin/Wanted", "/Admin/Works", "/Admin/Works/101" })
            {
                using var page = await host.SendAsync(HttpMethod.Get, path);
                Assert.AreEqual(mediaAllowed ? HttpStatusCode.OK : HttpStatusCode.Redirect, page.StatusCode, path);
                if (path == "/Admin/Requests" && mediaAllowed)
                {
                    var html = await page.Content.ReadAsStringAsync();
                    Assert.AreEqual(configurationAllowed, html.Contains("href=\"/Admin/Rules\"", StringComparison.Ordinal));
                    Assert.AreEqual(configurationAllowed, html.Contains("href=\"/Admin/Requests/Users\"", StringComparison.Ordinal));
                }
            }

            foreach (var path in new[] { "/Admin/Rules", "/Admin/Requests/Users", "/Admin/Library" })
            {
                using var page = await host.SendAsync(HttpMethod.Get, path);
                Assert.AreEqual(configurationAllowed ? HttpStatusCode.OK : HttpStatusCode.Redirect, page.StatusCode, path);
            }

            // Existing cookies observe a demotion.
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 2 WHERE \"Id\" = 41");
            using var demoted = await host.SendAsync(HttpMethod.Get, "/Admin/Wanted");
            Assert.AreEqual(HttpStatusCode.Redirect, demoted.StatusCode);
        }

        [TestMethod]
        public async Task Razor_LoginSettingsLogout_UseServicesAndAntiforgery()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 2 WHERE \"Id\" = 41");
            using var anonymous = await host.SendAsync(HttpMethod.Get, "/Settings");
            Assert.AreEqual(HttpStatusCode.Redirect, anonymous.StatusCode);

            using var missingToken = await host.SendAsync(HttpMethod.Post, "/Account/Login", new Dictionary<string, string> { ["Email"] = "a@example.test", ["Password"] = "test-password-123" });
            Assert.AreEqual(HttpStatusCode.BadRequest, missingToken.StatusCode);
            using var loginPage = await host.SendAsync(HttpMethod.Get, "/Account/Login?ReturnUrl=https://foreign.example.test");
            var html = await loginPage.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, loginPage.StatusCode, html);
            StringAssert.Contains(html, "type=\"submit\"");
            Assert.IsFalse(html.Contains("disabled=", StringComparison.Ordinal));

            using var invalidLogin = await host.SendAsync(HttpMethod.Post, "/Account/Login", Form(html, ("Email", "a@example.test"), ("Password", "wrong-password")));
            Assert.AreEqual(HttpStatusCode.Unauthorized, invalidLogin.StatusCode);
            var invalidHtml = await invalidLogin.Content.ReadAsStringAsync();
            StringAssert.Contains(invalidHtml, "role=\"alert\"");
            Assert.IsFalse(invalidHtml.Contains("wrong-password", StringComparison.Ordinal));

            using var signedIn = await host.SendAsync(HttpMethod.Post, "/Account/Login?ReturnUrl=https://foreign.example.test", Form(invalidHtml, ("Email", "a@example.test"), ("Password", "test-password-123")));
            Assert.AreEqual(HttpStatusCode.Redirect, signedIn.StatusCode);
            Assert.AreEqual("/", signedIn.Headers.Location!.OriginalString);

            using var settings = await host.SendAsync(HttpMethod.Get, "/Settings");
            html = await settings.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, settings.StatusCode);
            StringAssert.Contains(html, "lang=\"de-DE\"");
            StringAssert.Contains(html, "value=\"Profile A\"");
            using var admin = await host.SendAsync(HttpMethod.Get, "/Admin/Requests/Users");
            Assert.AreEqual(HttpStatusCode.Redirect, admin.StatusCode);
            using var denied = await host.SendAsync(HttpMethod.Get, admin.Headers.Location!.PathAndQuery);
            Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            StringAssert.Contains(await denied.Content.ReadAsStringAsync(), "role=\"alert\"");
            using var invalidLocale = await host.SendAsync(HttpMethod.Post, "/Settings", Form(html, ("DisplayName", "Must roll back"), ("UiLocaleId", "999")));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidLocale.StatusCode);
            Assert.AreEqual("Profile A", await host.Fixture.NameAsync(host.Fixture.ProfileAId));
            var invalidSettings = await invalidLocale.Content.ReadAsStringAsync();
            StringAssert.Contains(invalidSettings, "aria-invalid=\"true\"");

            await host.Fixture.RunSqlAsync("UPDATE \"UiLocales\" SET \"IsEnabled\" = FALSE WHERE \"Id\" = 2");
            using var disabledLocale = await host.SendAsync(HttpMethod.Post, "/Settings", Form(invalidSettings, ("DisplayName", "Must also roll back"), ("UiLocaleId", "2")));
            Assert.AreEqual(HttpStatusCode.BadRequest, disabledLocale.StatusCode);
            Assert.AreEqual("Profile A", await host.Fixture.NameAsync(host.Fixture.ProfileAId));
            await host.Fixture.RunSqlAsync("UPDATE \"UiLocales\" SET \"IsEnabled\" = TRUE WHERE \"Id\" = 2");

            using var saved = await host.SendAsync(HttpMethod.Post, "/Settings", Form(invalidSettings, ("DisplayName", "<b>New name</b>"), ("UiLocaleId", "2")));
            Assert.AreEqual(HttpStatusCode.Redirect, saved.StatusCode);
            using var updated = await host.SendAsync(HttpMethod.Get, "/Settings");
            html = await updated.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "lang=\"en-GB\"");
            StringAssert.Contains(html, "&lt;b&gt;New name&lt;/b&gt;");

            using var sessions = await host.SendAsync(HttpMethod.Get, "/Account/Sessions");
            html = await sessions.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, sessions.StatusCode, html);
            StringAssert.Contains(html, "name=\"SessionPublicId\"");
            using var foreignSession = await host.SendAsync(HttpMethod.Post, "/Account/Sessions", Form(html, ("SessionPublicId", Guid.NewGuid().ToString())));
            Assert.AreEqual(HttpStatusCode.NotFound, foreignSession.StatusCode);
            using var revoked = await host.SendAsync(HttpMethod.Post, "/Account/Sessions", Form(html, ("SessionPublicId", host.Fixture.SessionPublicId.ToString())));
            Assert.AreEqual(HttpStatusCode.Redirect, revoked.StatusCode);

            using var logoutPage = await host.SendAsync(HttpMethod.Get, "/Account/Logout");
            using var loggedOut = await host.SendAsync(HttpMethod.Post, "/Account/Logout", Form(await logoutPage.Content.ReadAsStringAsync()));
            Assert.AreEqual(HttpStatusCode.Redirect, loggedOut.StatusCode);
            using var protectedPage = await host.SendAsync(HttpMethod.Get, "/Settings");
            Assert.AreEqual(HttpStatusCode.Redirect, protectedPage.StatusCode);
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"AccountSessions\" WHERE \"RevokedAt\" IS NOT NULL"));
        }

        [TestMethod]
        public async Task Razor_ProfileSelection_RotatesSessionAndRejectsPinAndForeignProfiles()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await host.Fixture.RunSqlAsync("INSERT INTO \"Profiles\" (\"Id\", \"OwnerAccountId\", \"DisplayName\", \"UiLocaleId\") VALUES (303, 41, 'Second profile', 2); INSERT INTO \"AccountProfiles\" (\"AccountId\", \"ProfileId\") VALUES (41, 303)");
            using var login = await host.SendAsync(HttpMethod.Get, "/Account/Login");
            using var signedIn = await host.SendAsync(HttpMethod.Post, "/Account/Login", Form(await login.Content.ReadAsStringAsync(), ("Email", "a@example.test"), ("Password", "test-password-123")));
            Assert.AreEqual(HttpStatusCode.Redirect, signedIn.StatusCode);
            StringAssert.Contains(signedIn.Headers.Location!.OriginalString, "/Account/Profiles");
            var oldCookies = new Dictionary<string, string>(host.Cookies);

            using var picker = await host.SendAsync(HttpMethod.Get, "/Account/Profiles?returnUrl=%2FSettings");
            var html = await picker.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, picker.StatusCode);
            StringAssert.Contains(html, "Second profile");
            Assert.IsFalse(html.Contains("Profile B", StringComparison.Ordinal));
            using var foreign = await host.SendAsync(HttpMethod.Post, "/Account/Profiles", Form(html, ("ProfileId", "202")));
            Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);

            await host.Fixture.RunSqlAsync("UPDATE \"Profiles\" SET \"PinHash\" = 'selection-guard' WHERE \"Id\" = 303");
            using var locked = await host.SendAsync(HttpMethod.Post, "/Account/Profiles", Form(html, ("ProfileId", "303")));
            Assert.AreEqual(HttpStatusCode.Forbidden, locked.StatusCode);
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"AccountSessions\" WHERE \"RotationRevision\" = 1"));
            await host.Fixture.RunSqlAsync("UPDATE \"Profiles\" SET \"PinHash\" = NULL WHERE \"Id\" = 303");

            using var selected = await host.SendAsync(HttpMethod.Post, "/Account/Profiles?returnUrl=%2FSettings", Form(html, ("ProfileId", "303")));
            Assert.AreEqual(HttpStatusCode.Redirect, selected.StatusCode);
            Assert.AreEqual("/Settings", selected.Headers.Location!.OriginalString);
            using var settings = await host.SendAsync(HttpMethod.Get, "/Settings");
            StringAssert.Contains(await settings.Content.ReadAsStringAsync(), "Second profile");
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AccountSessions\" WHERE \"ActiveProfileId\" = 303 AND \"RotationRevision\" = 2"));

            host.Cookies.Clear();
            foreach (var cookie in oldCookies) host.Cookies.Add(cookie.Key, cookie.Value);
            using var stale = await host.SendAsync(HttpMethod.Get, "/Settings");
            Assert.AreEqual(HttpStatusCode.Redirect, stale.StatusCode);
        }

        [TestMethod]
        public async Task Razor_Plex_PublicServices_CompleteApprovalWithoutExposingProof()
        {
            await using var host = await Host.CreateAsync(plexEnabled: true, razor: true);
            using var login = await host.SendAsync(HttpMethod.Get, "/Account/Login");
            var html = await login.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "Sign in with Plex");
            using var started = await host.SendAsync(HttpMethod.Post, "/Account/Login?handler=Plex", Form(html));
            html = await started.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, started.StatusCode);
            StringAssert.Contains(html, "https://app.plex.tv/auth");
            Assert.IsFalse(html.Contains(host.Cookies["jularr.auth_nonce"], StringComparison.Ordinal));
            var id = Regex.Match(html, "name=\"AuthChallengePublicId\" value=\"([^\"]+)\"").Groups[1].Value;
            Assert.IsTrue(Guid.TryParse(id, out _));
            using var completed = await host.SendAsync(HttpMethod.Post, "/Account/Login?handler=PlexComplete", Form(html, ("AuthChallengePublicId", id)));
            var approval = await completed.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, completed.StatusCode);
            StringAssert.Contains(approval, "awaiting administrator approval");
            Assert.IsFalse(approval.Contains("verified-plex-token", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task Razor_NonOwner_HidesProfileWritesAndRejectsForgedPost()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await host.Fixture.RunSqlAsync("INSERT INTO \"AccountProfiles\" (\"AccountId\", \"ProfileId\") VALUES (42, 101); UPDATE \"Profiles\" SET \"OwnerAccountId\" = 42 WHERE \"Id\" = 101");
            using var login = await host.SendAsync(HttpMethod.Get, "/Account/Login");
            using var signedIn = await host.SendAsync(HttpMethod.Post, "/Account/Login", Form(await login.Content.ReadAsStringAsync(), ("Email", "a@example.test"), ("Password", "test-password-123")));
            using var settings = await host.SendAsync(HttpMethod.Get, "/Settings");
            var html = await settings.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, settings.StatusCode);
            Assert.IsFalse(html.Contains("name=\"DisplayName\"", StringComparison.Ordinal));

            using var logoutPage = await host.SendAsync(HttpMethod.Get, "/Account/Logout");
            using var forged = await host.SendAsync(HttpMethod.Post, "/Settings", Form(await logoutPage.Content.ReadAsStringAsync(), ("DisplayName", "Forbidden"), ("UiLocaleId", "2")));
            Assert.AreEqual(HttpStatusCode.Forbidden, forged.StatusCode);
            Assert.AreEqual("Profile A", await host.Fixture.NameAsync(host.Fixture.ProfileAId));
        }

        private static Dictionary<string, string> Form(string html, params (string Name, string Value)[] values)
        {
            var match = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
            Assert.IsTrue(match.Success, "The SSR form must contain an antiforgery token.");
            var form = values.ToDictionary(value => value.Name, value => value.Value);
            form.Add("__RequestVerificationToken", WebUtility.HtmlDecode(match.Groups[1].Value));
            return form;
        }
    }
}
