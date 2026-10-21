using System.Net;
using System.Net.Http.Json;
using System.Security;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.User.Instance.V1;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Data.Enums.Sql;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class InstanceCapabilitiesTests
    {
        [TestMethod]
        public async Task Library_Disabled_BlocksMediaOperationsAndRetainsData()
        {
            await using var host = await Host.CreateAsync(media: true);
            using var anonymous = await host.SendAsync(HttpMethod.Get, "/api/v1/instance/capabilities");
            Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            await host.Fixture.RunSqlAsync("INSERT INTO \"Works\" (\"Id\", \"MediaTypeId\", \"CanonicalTitle\") VALUES (10, 1, 'Retained'); INSERT INTO \"WatchlistEntries\" (\"ProfileId\", \"WorkId\") VALUES (101, 10)");

            using var before = await host.SendAsync(HttpMethod.Get, "/api/v1/instance/capabilities");
            var capabilities = (await before.Content.ReadFromJsonAsync<InstanceCapabilitiesDetailsV1>())!;
            CollectionAssert.IsSubsetOf(new[] { "profile.media.read", "media.manual.update", "profile.watchlist.update", "instance.settings.read", "storage.update" }, capabilities.Permissions);
            CollectionAssert.Contains(capabilities.Modules, "Library");

            // Disable through the canonical writer.
            using var disabled = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/modules/1", new UpdateInstanceModuleDataV1(false, 1));
            Assert.AreEqual(HttpStatusCode.OK, disabled.StatusCode, await disabled.Content.ReadAsStringAsync());
            foreach (var route in new[] { "/api/v1/works/10", "/api/v1/watchlist", "/api/v1/admin/works", "/api/v1/admin/works/10" })
            {
                using var denied = await host.SendAsync(HttpMethod.Get, route);
                Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode, route);
            }

            using var deniedWatchlist = await host.SendAsync(HttpMethod.Put, "/api/v1/watchlist/10", new UpdateWatchlistDataV1(false));
            Assert.AreEqual(HttpStatusCode.Forbidden, deniedWatchlist.StatusCode);
            using var deniedCreate = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works", new { mediaTypeId = 1, canonicalTitle = "Denied" });
            Assert.AreEqual(HttpStatusCode.Forbidden, deniedCreate.StatusCode);
            using var deniedEdit = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/works/10", new { canonicalTitle = "Denied", version = 1 });
            Assert.AreEqual(HttpStatusCode.Forbidden, deniedEdit.StatusCode);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Works\""));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"WatchlistEntries\""));

            using var after = await host.SendAsync(HttpMethod.Get, "/api/v1/instance/capabilities");
            capabilities = (await after.Content.ReadFromJsonAsync<InstanceCapabilitiesDetailsV1>())!;
            CollectionAssert.DoesNotContain(capabilities.Modules, "Library");
            CollectionAssert.Contains(capabilities.Permissions, "instance.settings.read");

            // Re-enable without recreating media.
            using var enabled = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/modules/1", new UpdateInstanceModuleDataV1(true, 2));
            Assert.AreEqual(HttpStatusCode.OK, enabled.StatusCode);
            using var stale = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/modules/1", new UpdateInstanceModuleDataV1(false, 1));
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);
            using var restored = await host.SendAsync(HttpMethod.Get, "/api/v1/works/10");
            Assert.AreEqual(HttpStatusCode.OK, restored.StatusCode);

            // Missing profile reduces user capabilities.
            await host.Fixture.RunSqlAsync("UPDATE \"AccountSessions\" SET \"ActiveProfileId\" = NULL");
            using var noProfile = await host.SendAsync(HttpMethod.Get, "/api/v1/instance/capabilities");
            capabilities = (await noProfile.Content.ReadFromJsonAsync<InstanceCapabilitiesDetailsV1>())!;
            CollectionAssert.DoesNotContain(capabilities.Permissions, "profile.media.read");
            CollectionAssert.DoesNotContain(capabilities.Permissions, "profile.watchlist.update");
            CollectionAssert.Contains(capabilities.Permissions, "media.manual.update");
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            using var managerDenied = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/modules/1", new UpdateInstanceModuleDataV1(false, 3));
            Assert.AreEqual(HttpStatusCode.Forbidden, managerDenied.StatusCode);
            using var manager = await host.SendAsync(HttpMethod.Get, "/api/v1/instance/capabilities");
            capabilities = (await manager.Content.ReadFromJsonAsync<InstanceCapabilitiesDetailsV1>())!;
            CollectionAssert.DoesNotContain(capabilities.Permissions, "instance.settings.read");
            CollectionAssert.DoesNotContain(capabilities.Permissions, "storage.update");
        }

        [TestMethod]
        public async Task ModuleGate_AllAnyAndWriteLocks_UseTheOperationTransaction()
        {
            await using var host = await Host.CreateAsync();
            using var scope = host.Services.CreateScope();
            var gate = scope.ServiceProvider.GetRequiredService<IServiceGate>();
            await using var sql = new SqlContext(host.Fixture.Database);
            await sql.BeginAsync(SqlAccessMode.ReadWrite);
            var context = new LogicContext(sql);
            await gate.RequireModulesAsync(ModuleRequirement.Any("Library", "Unknown"), context, true, default);
            await Assert.ThrowsExactlyAsync<SecurityException>(() => gate.RequireModulesAsync(ModuleRequirement.All("Library", "Unknown"), context, true, default));

            // Another writer cannot bypass locks.
            await using var connection = await host.Fixture.Database.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("SET lock_timeout = '200ms'; UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Id\" = 1", connection);
            var blocked = await Assert.ThrowsExactlyAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync());
            Assert.AreEqual(PostgresErrorCodes.LockNotAvailable, blocked.SqlState);
            await sql.CommitAsync();
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync());
        }

        [TestMethod]
        public async Task Razor_InstancePresentationAndModules_UpdateBrandLocaleAndNavigation()
        {
            await using var host = await Host.CreateAsync(razor: true, media: true);
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceSettings\" SET \"Name\" = '<Home>', \"UiLocaleId\" = 2");
            using var page = await host.SendAsync(HttpMethod.Get, "/Account/Login");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
            StringAssert.Contains(html, "&lt;Home&gt;");
            StringAssert.Contains(html, "by Jularr");
            StringAssert.Contains(html, "lang=\"en-GB\"");

            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            using var settings = await host.SendAsync(HttpMethod.Get, "/Settings");
            html = await settings.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "lang=\"de-DE\"");
            StringAssert.Contains(html, "href=\"/Favorites\"");
            StringAssert.Contains(html, "href=\"/Admin/Works\"");

            using var editor = await host.SendAsync(HttpMethod.Get, "/Admin/Instance");
            html = await editor.Content.ReadAsStringAsync();
            var csrf = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
            using var missing = await host.SendAsync(HttpMethod.Post, "/Admin/Instance?handler=Module&instanceModuleId=1", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = csrf, ["Version"] = "1"
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);
            using var disabled = await host.SendAsync(HttpMethod.Post, "/Admin/Instance?handler=Module&instanceModuleId=1", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = csrf, ["Version"] = "1", ["IsEnabled"] = "False"
            });
            Assert.AreEqual(HttpStatusCode.Redirect, disabled.StatusCode, await disabled.Content.ReadAsStringAsync());
            using var updated = await host.SendAsync(HttpMethod.Get, "/Settings");
            html = await updated.Content.ReadAsStringAsync();
            Assert.IsFalse(html.Contains("href=\"/Favorites\"", StringComparison.Ordinal));
            Assert.IsFalse(html.Contains("href=\"/Admin/Works\"", StringComparison.Ordinal));
            using var denied = await host.SendAsync(HttpMethod.Get, "/Favorites");
            Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            using var stillEditable = await host.SendAsync(HttpMethod.Get, "/Admin/Instance");
            Assert.AreEqual(HttpStatusCode.OK, stillEditable.StatusCode);
        }
    }
}
