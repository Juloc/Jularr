using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Service.Admin.Media.V1;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;
using AdminWork = Jularr.Backend.Data.Admin.Media.V1.WorkDetailsV1;
using UserWork = Jularr.Backend.Data.User.Media.V1.WorkDetailsV1;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class MediaTests
    {
        [TestMethod]
        public async Task Work_ManualCreationAndStaleEdit_PreserveIdentityAndTitleProvenance()
        {
            await using var host = await CreateHostAsync();
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works", new CreateWorkDataV1(MediaType.Movie, "Original"));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var work = (await created.Content.ReadFromJsonAsync<AdminWork>())!;
            Assert.AreEqual(1L, work.Version);
            await host.Fixture.RunSqlAsync("INSERT INTO \"WorkTitles\" (\"WorkId\", \"UiLocaleId\", \"Title\", \"IsManual\") SELECT \"Id\", 1, 'Localized manual', TRUE FROM \"Works\"");

            using var changed = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/works/" + work.Id, new UpdateWorkDataV1("Manual edit", work.Version));
            Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode, await changed.Content.ReadAsStringAsync());
            using var stale = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/works/" + work.Id, new UpdateWorkDataV1("Stale", work.Version));
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);
            Assert.AreEqual("Manual edit", await host.ScalarAsync("SELECT \"Title\" FROM \"WorkTitles\" WHERE \"UiLocaleId\" IS NULL"));
            Assert.AreEqual("Localized manual", await host.ScalarAsync("SELECT \"Title\" FROM \"WorkTitles\" WHERE \"UiLocaleId\" = 1"));
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"WorkTitles\""));
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT \"Version\" FROM \"Works\""));

            // Invalid input cannot write.
            using var invalid = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works", new CreateWorkDataV1((MediaType)99, "Invalid"));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Works\""));

            // Manual manager, not User.
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            using var manager = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works", new CreateWorkDataV1(MediaType.Series, "Series"));
            Assert.AreEqual(HttpStatusCode.OK, manager.StatusCode);
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 2 WHERE \"Id\" = 41");
            using var denied = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works", new CreateWorkDataV1(MediaType.Movie, "Denied"));
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"Works\""));
        }

        [TestMethod]
        public async Task Watchlist_RepeatedUpdatesAndPaging_StayProfileScoped()
        {
            await using var host = await CreateHostAsync();
            await host.Fixture.RunSqlAsync("INSERT INTO \"Works\" (\"Id\", \"MediaTypeId\", \"CanonicalTitle\") VALUES (10, 1, 'A'), (20, 2, 'B'); INSERT INTO \"WatchlistEntries\" (\"ProfileId\", \"WorkId\") VALUES (202, 20)");
            using var added = await host.SendAsync(HttpMethod.Put, "/api/v1/watchlist/10", new UpdateWatchlistDataV1(true));
            Assert.AreEqual(HttpStatusCode.OK, added.StatusCode, await added.Content.ReadAsStringAsync());
            using var repeated = await host.SendAsync(HttpMethod.Put, "/api/v1/watchlist/10", new UpdateWatchlistDataV1(true));
            Assert.AreEqual(HttpStatusCode.OK, repeated.StatusCode);

            using var first = await host.SendAsync(HttpMethod.Get, "/api/v1/watchlist?pageSize=1");
            var page = (await first.Content.ReadFromJsonAsync<PageResult<UserWork>>())!;
            Assert.AreEqual(1, page.Items.Count);
            Assert.AreEqual(10L, page.Items[0].Id);
            using var next = await host.SendAsync(HttpMethod.Get, "/api/v1/watchlist?page=2&pageSize=1");
            var nextPage = (await next.Content.ReadFromJsonAsync<PageResult<UserWork>>())!;
            Assert.AreEqual(0, nextPage.Items.Count);

            using var removed = await host.SendAsync(HttpMethod.Put, "/api/v1/watchlist/10", new UpdateWatchlistDataV1(false));
            Assert.AreEqual(HttpStatusCode.OK, removed.StatusCode);
            Assert.AreEqual(202L, await host.ScalarAsync("SELECT \"ProfileId\" FROM \"WatchlistEntries\""));
            using var missing = await host.SendAsync(HttpMethod.Put, "/api/v1/watchlist/999", new UpdateWatchlistDataV1(true));
            Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"WatchlistEntries\""));
        }

        [TestMethod]
        public async Task Razor_MediaForms_ValidateCreateEditAndPersonalList()
        {
            await using var host = await CreateHostAsync(razor: true);
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Works");
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, await page.Content.ReadAsStringAsync());
            var html = await page.Content.ReadAsStringAsync();
            var csrf = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);

            using var created = await host.SendAsync(HttpMethod.Post, "/Admin/Works", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = csrf, ["MediaTypeId"] = "Movie", ["CanonicalTitle"] = "<unsafe>"
            });
            Assert.AreEqual(HttpStatusCode.Redirect, created.StatusCode, await created.Content.ReadAsStringAsync());
            var id = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Works\""))!;
            using var editor = await host.SendAsync(HttpMethod.Get, "/Admin/Works/" + id);
            Assert.AreEqual(HttpStatusCode.OK, editor.StatusCode, await editor.Content.ReadAsStringAsync());
            StringAssert.Contains(await editor.Content.ReadAsStringAsync(), "&lt;unsafe&gt;");
            using var edited = await host.SendAsync(HttpMethod.Post, "/Admin/Works/" + id, new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = csrf, ["CanonicalTitle"] = "<edited>", ["Version"] = "1"
            });
            Assert.AreEqual(HttpStatusCode.Redirect, edited.StatusCode, await edited.Content.ReadAsStringAsync());
            Assert.AreEqual("<edited>", await host.ScalarAsync("SELECT \"CanonicalTitle\" FROM \"Works\""));
            using var details = await host.SendAsync(HttpMethod.Get, "/Details/" + id);
            Assert.AreEqual(HttpStatusCode.OK, details.StatusCode, await details.Content.ReadAsStringAsync());
            using var saved = await host.SendAsync(HttpMethod.Post, "/Details/" + id, new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = csrf, ["IsWatchlisted"] = "True"
            });
            Assert.AreEqual(HttpStatusCode.Redirect, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            using var watchlist = await host.SendAsync(HttpMethod.Get, "/Watchlist");
            Assert.AreEqual(HttpStatusCode.OK, watchlist.StatusCode, await watchlist.Content.ReadAsStringAsync());
            StringAssert.Contains(await watchlist.Content.ReadAsStringAsync(), "&lt;edited&gt;");
            using var users = await host.SendAsync(HttpMethod.Get, "/Admin/Requests/Users");
            Assert.AreEqual(HttpStatusCode.OK, users.StatusCode, await users.Content.ReadAsStringAsync());
            StringAssert.Contains(await users.Content.ReadAsStringAsync(), "a@example.test");
        }

        private static async Task<Host> CreateHostAsync(bool razor = false)
        {
            var host = await Host.CreateAsync(razor: razor);
            try
            {
                using var schema = typeof(ReadWorksV1).Assembly.GetManifestResourceStream("Jularr.Database.Media.sql")!;
                using var reader = new StreamReader(schema);
                await host.Fixture.RunSqlAsync(await reader.ReadToEndAsync());
                using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
                using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
                Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
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
