using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Service.Admin.Instance.V1;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class InstanceSettingsTests
    {
        private const string Route = "/api/v1/admin/instance/settings";

        [TestMethod]
        public async Task Settings_OwnerEdits_RejectInvalidLocaleInputAndStaleChanges()
        {
            await using var host = await CreateHostAsync();
            using var read = await host.SendAsync(HttpMethod.Get, Route);
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
            var settings = (await read.Content.ReadFromJsonAsync<InstanceSettingsDetailsV1>())!;
            Assert.AreEqual("Jularr", settings.Name);
            Assert.AreEqual(1L, settings.Version);
            Assert.AreEqual(settings.UiLocaleId, await host.ScalarAsync("SELECT \"Id\" FROM \"UiLocales\" WHERE \"Locale\" = 'en'"));

            // Field validation precedes writes.
            using var blank = await host.SendAsync(HttpMethod.Put, Route, new UpdateInstanceSettingsDataV1(" ", 1, 1));
            Assert.AreEqual(HttpStatusCode.BadRequest, blank.StatusCode);
            using var longName = await host.SendAsync(HttpMethod.Put, Route, new UpdateInstanceSettingsDataV1(new string('x', 129), 1, 1));
            Assert.AreEqual(HttpStatusCode.BadRequest, longName.StatusCode);
            using var incomplete = await host.SendAsync(HttpMethod.Put, Route, new { name = "Missing version", uiLocaleId = 1 });
            Assert.AreEqual(HttpStatusCode.BadRequest, incomplete.StatusCode);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT \"Version\" FROM \"InstanceSettings\""));

            // Commit the shared configuration.
            using var changed = await host.SendAsync(HttpMethod.Put, Route, new UpdateInstanceSettingsDataV1("Home", 1, 1, libraryScanIntervalHours: 12, historyMegabytes: 25));
            Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode, await changed.Content.ReadAsStringAsync());
            Assert.AreEqual((short)12, await host.ScalarAsync("SELECT \"LibraryScanIntervalHours\" FROM \"InstanceSettings\""));
            Assert.AreEqual(25, await host.ScalarAsync("SELECT \"HistoryMegabytes\" FROM \"InstanceSettings\""));
            Assert.AreEqual((short)30, await host.ScalarAsync("SELECT \"HistoryRetentionDays\" FROM \"InstanceSettings\""));
            using var stale = await host.SendAsync(HttpMethod.Put, Route, new UpdateInstanceSettingsDataV1("Stale", 2, 1));
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);
            using var unknownLocale = await host.SendAsync(HttpMethod.Put, Route, new UpdateInstanceSettingsDataV1("Unknown", 999, 2));
            Assert.AreEqual(HttpStatusCode.BadRequest, unknownLocale.StatusCode);
            await host.Fixture.RunSqlAsync("UPDATE \"UiLocales\" SET \"IsEnabled\" = FALSE WHERE \"Id\" = 2");
            using var disabledLocale = await host.SendAsync(HttpMethod.Put, Route, new UpdateInstanceSettingsDataV1("Disabled", 2, 2));
            Assert.AreEqual(HttpStatusCode.BadRequest, disabledLocale.StatusCode);

            Assert.AreEqual("Home", await host.ScalarAsync("SELECT \"Name\" FROM \"InstanceSettings\""));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT \"UiLocaleId\" FROM \"InstanceSettings\""));
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT \"Version\" FROM \"InstanceSettings\""));
        }

        [TestMethod]
        public async Task Settings_NonOwner_ReceivesNoConfigurationOrWriteAccess()
        {
            await using var host = await CreateHostAsync(login: false);
            using var anonymous = await host.SendAsync(HttpMethod.Get, Route);
            Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);

            // MediaManager is not Owner.
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            using var managerRead = await host.SendAsync(HttpMethod.Get, Route);
            Assert.AreEqual(HttpStatusCode.Forbidden, managerRead.StatusCode);
            using var managerWrite = await host.SendAsync(HttpMethod.Put, Route, new UpdateInstanceSettingsDataV1("Denied", 1, 1));
            Assert.AreEqual(HttpStatusCode.Forbidden, managerWrite.StatusCode);

            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 2 WHERE \"Id\" = 41");
            using var userRead = await host.SendAsync(HttpMethod.Get, Route);
            Assert.AreEqual(HttpStatusCode.Unauthorized, userRead.StatusCode);
            using var userWrite = await host.SendAsync(HttpMethod.Put, Route, new UpdateInstanceSettingsDataV1("Denied", 1, 1));
            Assert.AreEqual(HttpStatusCode.Unauthorized, userWrite.StatusCode);
            Assert.AreEqual("Jularr", await host.ScalarAsync("SELECT \"Name\" FROM \"InstanceSettings\""));
        }

        [TestMethod]
        public async Task Razor_SettingsForm_UsesFieldMetadataAndPreservesFailedInput()
        {
            await using var host = await CreateHostAsync(razor: true);
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Instance");
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, await page.Content.ReadAsStringAsync());
            var html = await page.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "maxlength=\"128\"");
            StringAssert.Contains(html, "name=\"Version\"");
            StringAssert.Contains(html, "English");
            var csrf = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);

            using var changed = await host.SendAsync(HttpMethod.Post, "/Admin/Instance", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = csrf, ["Name"] = "<Home>", ["UiLocaleId"] = "1", ["Version"] = "1", ["HistoryRetentionDays"] = "7", ["HistoryMegabytes"] = "3"
            });
            Assert.AreEqual(HttpStatusCode.Redirect, changed.StatusCode, await changed.Content.ReadAsStringAsync());
            Assert.AreEqual((short)7, await host.ScalarAsync("SELECT \"HistoryRetentionDays\" FROM \"InstanceSettings\""));
            Assert.AreEqual(3, await host.ScalarAsync("SELECT \"HistoryMegabytes\" FROM \"InstanceSettings\""));
            using var saved = await host.SendAsync(HttpMethod.Get, "/Admin/Instance");
            StringAssert.Contains(await saved.Content.ReadAsStringAsync(), "&lt;Home&gt;");

            // Central error redisplay.
            using var stale = await host.SendAsync(HttpMethod.Post, "/Admin/Instance", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = csrf, ["Name"] = "<Stale>", ["UiLocaleId"] = "2", ["Version"] = "1"
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode, await stale.Content.ReadAsStringAsync());
            StringAssert.Contains(await stale.Content.ReadAsStringAsync(), "&lt;Stale&gt;");
            Assert.AreEqual("<Home>", await host.ScalarAsync("SELECT \"Name\" FROM \"InstanceSettings\""));

            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            using var denied = await host.SendAsync(HttpMethod.Get, "/Admin/Instance");
            Assert.AreEqual(HttpStatusCode.Redirect, denied.StatusCode);
            StringAssert.Contains(denied.Headers.Location!.OriginalString, "/Account/AccessDenied");
        }

        private static async Task<Host> CreateHostAsync(bool razor = false, bool login = true)
        {
            var host = await Host.CreateAsync(razor: razor);
            try
            {
                using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
                if (login)
                {
                    using var signedIn = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
                    Assert.AreEqual(HttpStatusCode.OK, signedIn.StatusCode);
                }

                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }
    }
}
