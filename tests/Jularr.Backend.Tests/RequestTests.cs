using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Request.V1;
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
                INSERT INTO "AcquisitionRequests" ("WorkId", "RequestedByProfileId", "UiLocaleId") VALUES (20, 202, 1);
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
        public async Task Approval_TargetFailureRollsBack_AndTargetsAreShared()
        {
            await using var host = await CreateHostAsync();
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
                INSERT INTO "AcquisitionRequests" ("Id", "WorkId", "RequestedByProfileId", "UiLocaleId") VALUES (100, 10, 202, 2);
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
            Assert.IsFalse(state.RootElement.GetProperty("canRequest").GetBoolean());
            Assert.IsFalse(state.RootElement.GetProperty("canManageWanted").GetBoolean());
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
