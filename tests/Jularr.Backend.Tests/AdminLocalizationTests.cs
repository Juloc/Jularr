using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Localization.V1;
using Jularr.Backend.Data.Public.Authentication.V1;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class AdminLocalizationTests
    {
        [TestMethod]
        public async Task Catalog_OwnerEdits_ContextVersionsAndManualTextAreProtected()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await SignInAsync(host);
            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/localization/messages?uiLocaleId=1&pageSize=2");
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
            using var rows = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
            Assert.AreEqual(2, rows.RootElement.GetProperty("items").GetArrayLength());
            Assert.IsTrue(rows.RootElement.GetProperty("hasMore").GetBoolean());
            var item = rows.RootElement.GetProperty("items")[0].Deserialize<UiMessageDetailsV1>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var route = $"/api/v1/admin/localization/messages/{item.Id}/1";

            // Context and source revision.
            using var description = await host.SendAsync(HttpMethod.Put, route + "/description", new UpdateUiDescriptionDataV1(item.Version, "Account form label, not a media title."));
            Assert.AreEqual(HttpStatusCode.OK, description.StatusCode, await description.Content.ReadAsStringAsync());
            using var staleDescription = await host.SendAsync(HttpMethod.Put, route + "/description", new UpdateUiDescriptionDataV1(item.Version, "Stale context"));
            Assert.AreEqual(HttpStatusCode.BadRequest, staleDescription.StatusCode);

            // Create, edit, reject stale writes.
            using var first = await host.SendAsync(HttpMethod.Put, route + "/translation", new SaveUiTranslationDataV1(item.Version + 1, null, "Manueller Text"));
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, await first.Content.ReadAsStringAsync());
            using var second = await host.SendAsync(HttpMethod.Put, route + "/translation", new SaveUiTranslationDataV1(item.Version + 1, 1, "Aktueller Text"));
            Assert.AreEqual(HttpStatusCode.OK, second.StatusCode, await second.Content.ReadAsStringAsync());
            foreach (var version in new long?[] { null, 1, 999 })
            {
                using var stale = await host.SendAsync(HttpMethod.Put, route + "/translation", new SaveUiTranslationDataV1(item.Version + 1, version, "Lost update"));
                Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);
                StringAssert.Contains(await stale.Content.ReadAsStringAsync(), "outdated_version");
            }

            Assert.AreEqual("Aktueller Text", await host.ScalarAsync($"SELECT \"Text\" FROM \"UiTranslations\" WHERE \"UiTranslationMessageId\" = {item.Id} AND \"UiLocaleId\" = 1"));
            using var missingLocale = await host.SendAsync(HttpMethod.Put, $"/api/v1/admin/localization/messages/{item.Id}/999/translation",
                new SaveUiTranslationDataV1(item.Version + 1, null, "Unknown locale"));
            Assert.AreEqual(HttpStatusCode.BadRequest, missingLocale.StatusCode);
            StringAssert.Contains(await missingLocale.Content.ReadAsStringAsync(), "unknown_reference");
        }

        [TestMethod]
        public async Task Razor_OwnerEdits_UseEncodedFormsAndSharedErrors()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await SignInAsync(host);
            var id = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"UiTranslationMessages\" WHERE \"Key\" = 'account.field.email'"))!;
            var route = $"/Admin/Localization?UiLocaleId=1&UiTranslationMessageId={id}";
            using var page = await host.SendAsync(HttpMethod.Get, route);
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, await page.Content.ReadAsStringAsync());
            var html = await page.Content.ReadAsStringAsync();
            var csrf = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
            using var saved = await host.SendAsync(HttpMethod.Post, route + "&handler=Description", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(csrf), ["MessageVersion"] = "1", ["Description"] = "<script>context</script>"
            });
            Assert.AreEqual(HttpStatusCode.Redirect, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            using var changed = await host.SendAsync(HttpMethod.Get, route);
            html = await changed.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "&lt;script&gt;context&lt;/script&gt;");
            Assert.IsFalse(html.Contains("<script>context</script>", StringComparison.Ordinal));
            using var invalid = await host.SendAsync(HttpMethod.Post, route + "&handler=Translation", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(csrf), ["MessageVersion"] = "2", ["Text"] = ""
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            StringAssert.Contains(await invalid.Content.ReadAsStringAsync(), "aria-invalid=\"true\"");
        }

        [TestMethod]
        public async Task Catalog_MediaManager_CannotReadOrWriteConfiguration()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            await SignInAsync(host);
            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/localization/messages?uiLocaleId=1");
            Assert.AreEqual(HttpStatusCode.Forbidden, read.StatusCode);
            using var write = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/localization/messages/1/1/translation", new SaveUiTranslationDataV1(1, null, "Denied"));
            Assert.AreEqual(HttpStatusCode.Forbidden, write.StatusCode);
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Localization");
            Assert.AreEqual(HttpStatusCode.Redirect, page.StatusCode);
        }

        private static async Task SignInAsync(Host host)
        {
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
        }
    }
}
