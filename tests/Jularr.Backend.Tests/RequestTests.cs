using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Request.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.User.Request.V1;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;
using AdminRequest = Jularr.Backend.Data.Admin.Request.V1.RequestDetailsV1;
using UserRequest = Jularr.Backend.Data.User.Request.V1.RequestDetailsV1;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class RequestTests
    {
        [TestMethod]
        public async Task Requests_RepeatedIntentAndPaging_StayProfileScoped()
        {
            await using var host = await CreateHostAsync();
            await host.Fixture.RunSqlAsync("""
                UPDATE "Accounts" SET "AccountRoleTypeId" = 2 WHERE "Id" = 41;
                INSERT INTO "AcquisitionRequests" ("WorkId", "RequestedByProfileId", "RequestedByAccountId", "UiLocaleId") VALUES (20, 202, 42, 1);
                """);
            await SignInAsync(host);

            using var first = await host.SendAsync(HttpMethod.Post, "/api/v1/works/10/requests", new CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, await first.Content.ReadAsStringAsync());
            var request = (await first.Content.ReadFromJsonAsync<CreateRequestResultV1>())!;
            using var repeated = await host.SendAsync(HttpMethod.Post, "/api/v1/works/10/requests", new CreateRequestDataV1(1));
            Assert.AreEqual(request.Id, (await repeated.Content.ReadFromJsonAsync<CreateRequestResultV1>())!.Id);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"WantedItems\""));

            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/requests?pageSize=1");
            var page = (await read.Content.ReadFromJsonAsync<PageResult<UserRequest>>())!;
            Assert.AreEqual(request.Id, page.Items.Single().Id);
            using var next = await host.SendAsync(HttpMethod.Get, "/api/v1/requests?page=2&pageSize=1");
            Assert.AreEqual(0, (await next.Content.ReadFromJsonAsync<PageResult<UserRequest>>())!.Items.Count);
            using var denied = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/requests");
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);

            using var unknownWork = await host.SendAsync(HttpMethod.Post, "/api/v1/works/999/requests", new CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.NotFound, unknownWork.StatusCode);
            using var unknownLocale = await host.SendAsync(HttpMethod.Post, "/api/v1/works/10/requests", new CreateRequestDataV1(999));
            Assert.AreEqual(HttpStatusCode.BadRequest, unknownLocale.StatusCode);
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionRequests\""));
        }

        [TestMethod]
        public async Task RequestRules_BoundMediaLimitApprovalAndQuality()
        {
            await using var host = await CreateHostAsync();
            await SignInAsync(host);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (30, 1, 'Second film');
                INSERT INTO "AcquisitionProfiles" ("Name", "MediaTypeId", "Definition") SELECT 'Movies 4K', 1, "Definition" FROM "AcquisitionProfiles" WHERE "Name" = 'Movies 1080p';
                """);
            var uhd = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"AcquisitionProfiles\" WHERE \"Name\" = 'Movies 4K'"))!;
            var standard = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"AcquisitionProfiles\" WHERE \"Name\" = 'Movies 1080p'"))!;

            // A rule trusting its accounts with one film a month in 4K.
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/requests/rules", new SaveRequestRuleDataV1(" Trusted ", "  ", 1, 30, true, ["Movie"], [uhd], false, null));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var rule = (await created.Content.ReadFromJsonAsync<CreateRequestRuleResultV1>())!.Id;
            using var duplicate = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/requests/rules", new SaveRequestRuleDataV1("trusted", null, 1, 30, true, ["Movie"], [], false, null));
            StringAssert.Contains(await duplicate.Content.ReadAsStringAsync(), "duplicate_value");
            using var rules = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/requests/rules");
            var listed = (await rules.Content.ReadFromJsonAsync<BaseDataList<RequestRuleDetailsV1>>())!;
            Assert.AreEqual("Standard", listed.First().Name);
            Assert.IsNull(listed.Single(item => item.Id == rule).Description);
            CollectionAssert.AreEqual(new[] { "Movie" }, listed.Single(item => item.Id == rule).AllowedMediaKeys);

            // The account follows the rule; only a changed period overrides it.
            using var assigned = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/accounts/41/request-rule", new SaveAccountRequestRuleDataV1(rule, true, 1, 7, true, ["Movie"], [uhd]));
            Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());
            Assert.AreEqual(DBNull.Value, await host.ScalarAsync("SELECT \"BlockedMediaKeys\" FROM \"AccountRequestRules\""));
            using var users = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/requests/users");
            var account = (await users.Content.ReadFromJsonAsync<PageResult<AccountRequestRuleV1>>())!.Items.Single(item => item.AccountId == 41);
            Assert.AreEqual("Trusted", account.RequestRuleName);
            Assert.IsTrue(account.IsOverridden);
            Assert.AreEqual(7, account.PeriodDays);
            Assert.AreEqual(1, account.RequestLimit);

            // As a user: only films, only the offered quality, approved at once, once a week.
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 2 WHERE \"Id\" = 41");
            await SignInAsync(host);
            using var offered = await host.SendAsync(HttpMethod.Get, "/api/v1/works/10/request-profiles");
            Assert.AreEqual("Movies 4K", (await offered.Content.ReadFromJsonAsync<BaseDataList<RequestProfileOptionV1>>())!.Single().Name);
            using var book = await host.SendAsync(HttpMethod.Post, "/api/v1/works/20/requests", new CreateRequestDataV1(1));
            StringAssert.Contains(await book.Content.ReadAsStringAsync(), "request_media_blocked");
            using var other = await host.SendAsync(HttpMethod.Post, "/api/v1/works/10/requests", new CreateRequestDataV1(1, null, standard));
            Assert.AreEqual(HttpStatusCode.BadRequest, other.StatusCode);
            using var film = await host.SendAsync(HttpMethod.Post, "/api/v1/works/10/requests", new CreateRequestDataV1(1, null, uhd));
            Assert.AreEqual(HttpStatusCode.OK, film.StatusCode, await film.Content.ReadAsStringAsync());
            Assert.AreEqual((short)2, await host.ScalarAsync("SELECT \"AcquisitionRequestStatusTypeId\" FROM \"AcquisitionRequests\""));
            Assert.AreEqual(uhd, (long)(await host.ScalarAsync("SELECT \"AcquisitionProfileId\" FROM \"WantedItems\""))!);
            using var repeated = await host.SendAsync(HttpMethod.Post, "/api/v1/works/10/requests", new CreateRequestDataV1(1, null, uhd));
            Assert.AreEqual(HttpStatusCode.OK, repeated.StatusCode);
            using var limited = await host.SendAsync(HttpMethod.Post, "/api/v1/works/30/requests", new CreateRequestDataV1(1));
            StringAssert.Contains(await limited.Content.ReadAsStringAsync(), "request_limit");
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionRequests\""));
        }

        [TestMethod]
        public async Task Razor_RulesAndUsers_EditRulesAndAssignments()
        {
            await using var host = await CreateHostAsync(razor: true);
            await SignInAsync(host);
            using var empty = await host.SendAsync(HttpMethod.Get, "/Admin/Rules?New=true");
            var html = await empty.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, empty.StatusCode, html);
            StringAssert.Contains(html, "Standard");
            StringAssert.Contains(html, "10 requests / 30 days");

            // A new rule from the editor; unchecked media are blocked.
            using var created = await host.SendAsync(HttpMethod.Post, "/Admin/Rules?New=true", new FormUrlEncodedContent([
                new("Name", "Family"), new("RequestLimit", "5"), new("PeriodDays", "14"), new("IsAutomaticApproval", "True"), new("AllowedMediaKeys", "Movie"),
                new("AllowedMediaKeys", "Series"), new("IsDefault", "false")]));
            Assert.AreEqual(HttpStatusCode.Redirect, created.StatusCode, await created.Content.ReadAsStringAsync());
            var family = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"RequestRules\" WHERE \"Name\" = 'Family'"))!;
            Assert.IsTrue(((string[])(await host.ScalarAsync("SELECT \"BlockedMediaKeys\" FROM \"RequestRules\" WHERE \"Name\" = 'Family'"))!).Contains("Book"));
            using var rules = await host.SendAsync(HttpMethod.Get, "/Admin/Rules?RequestRuleId=" + family);
            StringAssert.Contains(await rules.Content.ReadAsStringAsync(), "5 requests / 14 days &#xB7; Automatic approval");

            // The first account opens on entry and takes the rule.
            using var users = await host.SendAsync(HttpMethod.Get, "/Admin/Requests/Users");
            html = await users.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, users.StatusCode, html);
            StringAssert.Contains(html, "Override the rule");
            using var assigned = await host.SendAsync(HttpMethod.Post, "/Admin/Requests/Users?PageNumber=1&AccountId=41", new FormUrlEncodedContent([
                new("RequestRuleId", family.ToString(System.Globalization.CultureInfo.InvariantCulture)), new("IsOverridden", "false"), new("PeriodDays", "14"),
                new("IsAutomaticApproval", "True"), new("AllowedMediaKeys", "Movie")]));
            Assert.AreEqual(HttpStatusCode.Redirect, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());
            Assert.AreEqual(family, (long)(await host.ScalarAsync("SELECT \"RequestRuleId\" FROM \"AccountRequestRules\" WHERE \"AccountId\" = 41"))!);
            Assert.AreEqual(DBNull.Value, await host.ScalarAsync("SELECT \"BlockedMediaKeys\" FROM \"AccountRequestRules\" WHERE \"AccountId\" = 41"));

            // A deleted rule returns its accounts to the default, without its overrides.
            using var deleted = await host.SendAsync(HttpMethod.Delete, "/api/v1/admin/requests/rules/" + family + "?version=1");
            Assert.AreEqual(HttpStatusCode.OK, deleted.StatusCode, await deleted.Content.ReadAsStringAsync());
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"AccountRequestRules\""));
        }

        [TestMethod]
        public async Task Approval_TargetFailureRollsBack_AndTargetsAreShared()
        {
            // A manager's request waits for moderation.
            await using var host = await CreateHostAsync();
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            await SignInAsync(host);
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/works/10/requests", new CreateRequestDataV1(1));
            var request = (await created.Content.ReadFromJsonAsync<CreateRequestResultV1>())!;

            // Force a downstream failure.
            await host.Fixture.RunSqlAsync("ALTER TABLE \"WantedItems\" ADD CONSTRAINT \"TestTargetFailure\" CHECK (\"WorkId\" <> 10)");
            using var failed = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/requests/" + request.Id, new UpdateRequestDataV1(AcquisitionRequestStatusType.Approved, 1));
            Assert.IsFalse(failed.IsSuccessStatusCode);
            Assert.AreEqual((short)1, await host.ScalarAsync("SELECT \"AcquisitionRequestStatusTypeId\" FROM \"AcquisitionRequests\""));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT \"Version\" FROM \"AcquisitionRequests\""));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"WantedItems\""));
            await host.Fixture.RunSqlAsync("""
                ALTER TABLE "WantedItems" DROP CONSTRAINT "TestTargetFailure";
                INSERT INTO "AcquisitionRequests" ("Id", "WorkId", "RequestedByProfileId", "RequestedByAccountId", "UiLocaleId") VALUES (100, 10, 202, 42, 2);
                UPDATE "Accounts" SET "AccountRoleTypeId" = 3 WHERE "Id" = 41;
                """);
            await SignInAsync(host);

            // Manager moderates, not configures.
            foreach (var id in new[] { request.Id, 100L })
            {
                using var approved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/requests/" + id, new UpdateRequestDataV1(AcquisitionRequestStatusType.Approved, 1));
                Assert.AreEqual(HttpStatusCode.OK, approved.StatusCode, await approved.Content.ReadAsStringAsync());
            }

            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"WantedItems\""));
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"RequestTargets\""));
            using var wantedRead = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/wanted");
            var wanted = (await wantedRead.Content.ReadFromJsonAsync<PageResult<WantedDetailsV1>>())!.Items.Single();
            CollectionAssert.AreEquivalent(new[] { "English", "Deutsch" }, wanted.RequestedLanguages);
            using var paused = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/wanted/" + wanted.Id, new UpdateWantedDataV1(true, wanted.Version));
            Assert.AreEqual(HttpStatusCode.OK, paused.StatusCode);
            using var stale = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/wanted/" + wanted.Id, new UpdateWantedDataV1(false, wanted.Version));
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);
            using var repeated = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/requests/" + request.Id, new UpdateRequestDataV1(AcquisitionRequestStatusType.Rejected, 2));
            Assert.AreEqual(HttpStatusCode.BadRequest, repeated.StatusCode);
            using var approvedTab = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/requests?requestTabTypeId=Approved");
            Assert.AreEqual(2, (await approvedTab.Content.ReadFromJsonAsync<PageResult<AdminRequest>>())!.Items.Count);
            using var denied = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/accounts");
            Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        [TestMethod]
        public async Task Razor_RequestModerationAndWanted_UseEncodedFormsAndModuleGate()
        {
            await using var host = await CreateHostAsync(razor: true);
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            await SignInAsync(host);
            using var details = await host.SendAsync(HttpMethod.Get, "/Details/10");
            var html = await details.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, details.StatusCode, html);
            StringAssert.Contains(html, "&lt;Work&gt;");
            StringAssert.Contains(html, "handler=Request");
            var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
            // Browser forms have no API header.
            using var browser = ((TestServer)host.Services.GetRequiredService<IServer>()).CreateClient();
            browser.DefaultRequestHeaders.Add("Cookie", string.Join("; ", host.Cookies.Select(cookie => cookie.Key + "=" + cookie.Value)));
            using var missingCsrf = await browser.PostAsync("/Details/10?handler=Request", new FormUrlEncodedContent(new Dictionary<string, string> { ["UiLocaleId"] = "1" }));
            Assert.AreEqual(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
            using var request = await browser.PostAsync("/Details/10?handler=Request", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token, ["UiLocaleId"] = "1"
            }));
            Assert.AreEqual(HttpStatusCode.Redirect, request.StatusCode, await request.Content.ReadAsStringAsync());
            var id = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"AcquisitionRequests\""))!;
            using var queue = await host.SendAsync(HttpMethod.Get, "/Admin/Requests?Tab=Open");
            html = await queue.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, queue.StatusCode, html);
            StringAssert.Contains(html, "&lt;Work&gt;");
            StringAssert.Contains(html, "Approve");
            token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
            using var approve = await browser.PostAsync("/Admin/Requests?AcquisitionRequestId=" + id, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token, ["AcquisitionRequestStatusTypeId"] = "Approved", ["Version"] = "1"
            }));
            Assert.AreEqual(HttpStatusCode.Redirect, approve.StatusCode, await approve.Content.ReadAsStringAsync());
            using var wantedPage = await host.SendAsync(HttpMethod.Get, "/Admin/Wanted");
            html = await wantedPage.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, wantedPage.StatusCode, html);
            StringAssert.Contains(html, "&lt;Work&gt;");
            token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
            var wantedId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"WantedItems\""))!;
            using var pause = await browser.PostAsync("/Admin/Wanted?WantedItemId=" + wantedId, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token, ["IsPaused"] = "True", ["Version"] = "1"
            }));
            Assert.AreEqual(HttpStatusCode.Redirect, pause.StatusCode, await pause.Content.ReadAsStringAsync());
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"IsPaused\" FROM \"WantedItems\""));
            using var mine = await host.SendAsync(HttpMethod.Get, "/Requests");
            Assert.AreEqual(HttpStatusCode.OK, mine.StatusCode, await mine.Content.ReadAsStringAsync());

            // Disabled does not erase data.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Requests'");
            using var denied = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/wanted");
            Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            using var capabilities = await host.SendAsync(HttpMethod.Get, "/api/v1/instance/capabilities");
            using var state = JsonDocument.Parse(await capabilities.Content.ReadAsStringAsync());
            Assert.IsFalse(state.RootElement.GetProperty("modules").EnumerateArray().Any(module => module.GetString() == "Requests"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionRequests\""));
        }

        private static async Task<Host> CreateHostAsync(bool razor = false)
        {
            var host = await Host.CreateAsync(razor: razor, media: true);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (10, 1, '<Work>'), (20, 3, 'Book');
                """);
            return host;
        }

        private static async Task SignInAsync(Host host)
        {
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
        }
    }
}
