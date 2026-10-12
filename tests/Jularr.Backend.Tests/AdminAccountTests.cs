using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Data;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Account.V1;
using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Account;
using Jularr.Backend.Service.Core;
using Npgsql;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class AdminAccountTests
    {
        [TestMethod]
        public async Task Accounts_ExactlyFullPage_HasNoFalseNextPage()
        {
            await using var host = await Host.CreateAsync();
            await SignInAsync(host);
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/accounts?pageSize=2");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(2, page.RootElement.GetProperty("items").GetArrayLength());
            Assert.IsFalse(page.RootElement.GetProperty("hasMore").GetBoolean());
        }

        [TestMethod]
        public async Task Owners_ConcurrentDemotion_AlwaysRetainsAnEnabledOwner()
        {
            await using var host = await Host.CreateAsync();
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 1 WHERE \"Id\" = 42");
            await using var first = new SqlContext(host.Fixture.Database);
            await using var second = new SqlContext(host.Fixture.Database);
            await first.BeginAsync(SqlAccessMode.ReadWrite, IsolationLevel.Serializable);
            await second.BeginAsync(SqlAccessMode.ReadWrite, IsolationLevel.Serializable);
            var firstContext = new LogicContext(first);
            var secondContext = new LogicContext(second);
            var firstAccount = await firstContext.ReadAccountAsync(41, default);
            var secondAccount = await secondContext.ReadAccountAsync(42, default);
            var firstVersion = firstAccount.UpdatedAt;
            var secondVersion = secondAccount.UpdatedAt;
            firstAccount.AccountRoleTypeId = AccountRoleType.User;
            secondAccount.AccountRoleTypeId = AccountRoleType.User;

            var results = await Task.WhenAll(DemoteAsync(firstContext, first, firstAccount, firstVersion), DemoteAsync(secondContext, second, secondAccount, secondVersion));
            Assert.AreEqual(1, results.Count(committed => committed));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Accounts\" WHERE \"AccountRoleTypeId\" = 1 AND \"IsEnabled\""));
        }

        private static async Task<bool> DemoteAsync(LogicContext context, SqlContext sql, Backend.Data.Logic.Account.AccountUpdateData account, DateTime version)
        {
            try
            {
                await context.UpdateAccountAsync(account, version, default);
                await sql.CommitAsync();
                return true;
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
            {
                Assert.AreEqual(409, ServiceError.FromException(exception).StatusCode);
                return false;
            }
        }

        [TestMethod]
        public async Task Account_OwnerEdits_RejectStaleVersionsAndLastOwnerRemoval()
        {
            await using var host = await Host.CreateAsync();
            await SignInAsync(host);
            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/accounts/42");
            var account = (await read.Content.ReadFromJsonAsync<AccountDetailsV1>())!;
            using var update = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/accounts/42",
                new UpdateAccountDataV1("Managed account", AccountRoleType.MediaManager, true, account.UpdatedAt));
            Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, await update.Content.ReadAsStringAsync());
            Assert.AreEqual((short)3, await host.ScalarAsync("SELECT \"AccountRoleTypeId\" FROM \"Accounts\" WHERE \"Id\" = 42"));
            using var stale = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/accounts/42",
                new UpdateAccountDataV1("Stale", AccountRoleType.User, true, account.UpdatedAt));
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);
            StringAssert.Contains(await stale.Content.ReadAsStringAsync(), "outdated_version");

            using var ownerRead = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/accounts/41");
            var owner = (await ownerRead.Content.ReadFromJsonAsync<AccountDetailsV1>())!;
            foreach (var data in new[] { new UpdateAccountDataV1(owner.DisplayName, AccountRoleType.User, true, owner.UpdatedAt),
                new UpdateAccountDataV1(owner.DisplayName, AccountRoleType.Owner, false, owner.UpdatedAt) })
            {
                using var denied = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/accounts/41", data);
                Assert.AreEqual(HttpStatusCode.BadRequest, denied.StatusCode, await denied.Content.ReadAsStringAsync());
                StringAssert.Contains(await denied.Content.ReadAsStringAsync(), "invalid_transition");
            }

            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Accounts\" WHERE \"AccountRoleTypeId\" = 1 AND \"IsEnabled\""));
        }

        [TestMethod]
        public async Task Approval_DisableAndReenable_DoesNotVerifyEmailOrRestoreSessions()
        {
            await using var host = await Host.CreateAsync();
            await SignInAsync(host);
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"IsEnabled\" = FALSE WHERE \"Id\" = 42");
            using var initial = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/accounts/42");
            var account = (await initial.Content.ReadFromJsonAsync<AccountDetailsV1>())!;
            using var approval = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/accounts/42",
                new UpdateAccountDataV1(account.DisplayName, AccountRoleType.User, true, account.UpdatedAt));
            Assert.AreEqual(HttpStatusCode.OK, approval.StatusCode, await approval.Content.ReadAsStringAsync());
            Assert.AreEqual(DBNull.Value, await host.ScalarAsync("SELECT \"EmailVerifiedAt\" FROM \"Accounts\" WHERE \"Id\" = 42"));
            await host.Fixture.RunSqlAsync(
                """
                INSERT INTO "AccountSessions" ("PublicId", "AccountId", "TokenHash", "CredentialRevision", "ExpiresAt")
                SELECT gen_random_uuid(), "Id", decode(repeat('33', 32), 'hex'), "CredentialRevision", now() + interval '1 hour'
                FROM "Accounts" WHERE "Id" = 42;
                """);
            foreach (var enabled in new[] { false, true })
            {
                using var current = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/accounts/42");
                account = (await current.Content.ReadFromJsonAsync<AccountDetailsV1>())!;
                using var mutation = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/accounts/42",
                    new UpdateAccountDataV1(account.DisplayName, AccountRoleType.User, enabled, account.UpdatedAt));
                Assert.AreEqual(HttpStatusCode.OK, mutation.StatusCode, await mutation.Content.ReadAsStringAsync());
            }

            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AccountSessions\" WHERE \"AccountId\" = 42 AND \"RevokedAt\" IS NOT NULL"));
        }

        [TestMethod]
        public async Task Razor_AccountEditor_UsesRoundTripFieldValuesAndOwnerPolicies()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await SignInAsync(host);
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Accounts?AccountId=42");
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, await page.Content.ReadAsStringAsync());
            var html = await page.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "value=\"2\" selected=\"selected\"");
            var csrf = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
            var version = Regex.Match(html, "name=\"UpdatedAt\" value=\"([^\"]+)\"").Groups[1].Value;
            using var update = await host.SendAsync(HttpMethod.Post, "/Admin/Accounts?AccountId=42", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(csrf), ["UpdatedAt"] = WebUtility.HtmlDecode(version),
                ["DisplayName"] = "Edited in Razor", ["AccountRoleTypeId"] = "3", ["IsEnabled"] = "True"
            });
            Assert.AreEqual(HttpStatusCode.Redirect, update.StatusCode, await update.Content.ReadAsStringAsync());
            Assert.AreEqual("Edited in Razor", await host.ScalarAsync("SELECT \"DisplayName\" FROM \"Accounts\" WHERE \"Id\" = 42"));

            // Failed binding must not become false.
            using var invalid = await host.SendAsync(HttpMethod.Post, "/Admin/Accounts?AccountId=42", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(csrf), ["UpdatedAt"] = WebUtility.HtmlDecode(version),
                ["DisplayName"] = "Unexpected mutation", ["AccountRoleTypeId"] = "3", ["IsEnabled"] = "not-a-boolean"
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"Accounts\" WHERE \"Id\" = 42"));
            Assert.AreEqual("Edited in Razor", await host.ScalarAsync("SELECT \"DisplayName\" FROM \"Accounts\" WHERE \"Id\" = 42"));

            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            using var forbidden = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/accounts");
            Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
            using var deniedPage = await host.SendAsync(HttpMethod.Get, "/Admin/Accounts");
            Assert.AreEqual(HttpStatusCode.Redirect, deniedPage.StatusCode);
        }

        private static async Task SignInAsync(Host host)
        {
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
        }
    }
}
