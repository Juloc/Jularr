using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Admin.Localization.V1;
using Jularr.Backend.Data.Public.Authentication.V1;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class AdminLocaleTests
    {
        [TestMethod]
        public async Task Locales_CreationAndVersions_CanonicalizeWithoutOverwritingManualNames()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await SignInAsync(host);
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/localization/locales", new CreateUiLocaleDataV1("EN-us", "English (US)", false));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var id = (await created.Content.ReadFromJsonAsync<CreateUiLocaleResultV1>())!.Id;
            Assert.AreEqual("en-US", await host.ScalarAsync($"SELECT \"Locale\" FROM \"UiLocales\" WHERE \"Id\" = {id}"));
            using var duplicate = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/localization/locales", new CreateUiLocaleDataV1("en-US", "Overwrite", false));
            Assert.AreEqual(HttpStatusCode.BadRequest, duplicate.StatusCode);
            StringAssert.Contains(await duplicate.Content.ReadAsStringAsync(), "duplicate_value");
            using var invalid = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/localization/locales", new CreateUiLocaleDataV1("not_a_language", "Invalid", false));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            StringAssert.Contains(await invalid.Content.ReadAsStringAsync(), "invalid_locale");

            using var update = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/localization/locales/" + id, new UpdateUiLocaleDataV1("US English", false, false, 1));
            Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, await update.Content.ReadAsStringAsync());
            using var stale = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/localization/locales/" + id, new UpdateUiLocaleDataV1("Stale", true, false, 1));
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);
            Assert.AreEqual("US English", await host.ScalarAsync($"SELECT \"Name\" FROM \"UiLocales\" WHERE \"Id\" = {id}"));
            var englishId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"UiLocales\" WHERE \"Locale\" = 'en'"))!;
            var englishVersion = (long)(await host.ScalarAsync("SELECT \"CatalogVersion\" FROM \"UiLocales\" WHERE \"Locale\" = 'en'"))!;
            using var fallback = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/localization/locales/" + englishId, new UpdateUiLocaleDataV1("English", false, false, englishVersion));
            Assert.AreEqual(HttpStatusCode.BadRequest, fallback.StatusCode);
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"UiLocales\" WHERE \"Locale\" = 'en'"));

            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            using var denied = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/localization/locales", new CreateUiLocaleDataV1("fr-CA", "French", false));
            Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        [TestMethod]
        public async Task Razor_DisabledProfileLanguage_UsesParentInsteadOfSkippingToEnglish()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await host.Fixture.RunSqlAsync(
                """
                INSERT INTO "UiLocales" ("Id", "Locale", "Name") VALUES (4, 'de', 'Deutsch');
                INSERT INTO "UiTranslations" ("UiLocaleId", "UiTranslationMessageId", "Text", "UiTranslationStatusTypeId", "MessageVersion")
                SELECT 4, "Id", 'Einstellungen', 4, "Version" FROM "UiTranslationMessages" WHERE "Key" = 'settings.title';
                UPDATE "UiLocales" SET "IsEnabled" = FALSE WHERE "Id" = 1;
                """);
            await SignInAsync(host);
            using var page = await host.SendAsync(HttpMethod.Get, "/Settings");
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, await page.Content.ReadAsStringAsync());
            var html = await page.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "lang=\"de\"");
            StringAssert.Contains(html, "Einstellungen");
        }

        [TestMethod]
        public async Task Razor_LanguageCreation_UsesFieldValidationAndCanonicalService()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await SignInAsync(host);
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Instance/Languages");
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, await page.Content.ReadAsStringAsync());
            var csrf = Regex.Match(await page.Content.ReadAsStringAsync(), "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
            using var created = await host.SendAsync(HttpMethod.Post, "/Admin/Instance/Languages?handler=Create", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(csrf), ["Locale"] = "FR-ca", ["Name"] = "French (CA)", ["IsRightToLeft"] = "False"
            });
            Assert.AreEqual(HttpStatusCode.Redirect, created.StatusCode, await created.Content.ReadAsStringAsync());
            Assert.AreEqual("French (CA)", await host.ScalarAsync("SELECT \"Name\" FROM \"UiLocales\" WHERE \"Locale\" = 'fr-CA'"));
        }

        private static async Task SignInAsync(Host host)
        {
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
        }
    }
}
