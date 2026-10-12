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
        public async Task Accounts_FullAndEmptyPages_ReturnOnlyRequestedRows()
        {
            await using var host = await Host.CreateAsync();
            await SignInAsync(host);
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/accounts?pageSize=2");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(2, page.RootElement.GetProperty("items").GetArrayLength());
            Assert.IsFalse(page.RootElement.TryGetProperty("hasMore", out _));
            Assert.IsFalse(page.RootElement.TryGetProperty("totalCount", out _));

            using var next = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/accounts?page=2&pageSize=2");
            using var empty = JsonDocument.Parse(await next.Content.ReadAsStringAsync());
            Assert.AreEqual(0, empty.RootElement.GetProperty("items").GetArrayLength());
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
        public async Task Sessions_OwnerRevokesOnlySelectedAccount_AndMediaManagerIsDenied()
        {
            await using var host = await Host.CreateAsync();
            await SignInAsync(host);
            var sessionId = Guid.NewGuid();
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "AccountSessions" ("PublicId", "AccountId", "TokenHash", "CredentialRevision", "ExpiresAt")
                VALUES ('{sessionId}', 42, decode(repeat('ab', 32), 'hex'), 1, now() + interval '1 hour');
                """);

            // No sequential IDs or secrets.
            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/accounts/42/sessions");
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
            using var page = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
            var item = page.RootElement.GetProperty("items")[0];
            Assert.AreEqual(sessionId, item.GetProperty("id").GetGuid());
            Assert.IsFalse(item.TryGetProperty("tokenHash", out _));
            Assert.IsFalse(page.RootElement.TryGetProperty("hasMore", out _));

            // Account scope is enforced.
            using var foreign = await host.SendAsync(HttpMethod.Delete, $"/api/v1/admin/accounts/41/sessions/{sessionId}");
            Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
            using var revoked = await host.SendAsync(HttpMethod.Delete, $"/api/v1/admin/accounts/42/sessions/{sessionId}");
            Assert.AreEqual(HttpStatusCode.OK, revoked.StatusCode, await revoked.Content.ReadAsStringAsync());
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AccountSessions\" WHERE \"AccountId\" = 42 AND \"RevokedAt\" IS NOT NULL"));

            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            using var deniedRead = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/accounts/42/sessions");
            Assert.AreEqual(HttpStatusCode.Forbidden, deniedRead.StatusCode);
            using var deniedWrite = await host.SendAsync(HttpMethod.Delete, $"/api/v1/admin/accounts/42/sessions/{sessionId}");
            Assert.AreEqual(HttpStatusCode.Forbidden, deniedWrite.StatusCode);
        }

        [TestMethod]
        public async Task Razor_OwnerRevokesCurrentSession_ClearsAuthentication()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await SignInAsync(host);
            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/accounts/41/sessions");
            using var sessions = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
            var sessionId = sessions.RootElement.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("isCurrent").GetBoolean()).GetProperty("id").GetGuid();
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Accounts?AccountId=41");
            var csrf = Regex.Match(await page.Content.ReadAsStringAsync(), "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
            using var revoke = await host.SendAsync(HttpMethod.Post, "/Admin/Accounts?AccountId=41&handler=RevokeSession", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(csrf), ["SessionPublicId"] = sessionId.ToString()
            });
            Assert.AreEqual(HttpStatusCode.Redirect, revoke.StatusCode, await revoke.Content.ReadAsStringAsync());
            StringAssert.Contains(revoke.Headers.Location!.OriginalString, "/Account/Login");
            using var protectedRead = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/accounts");
            Assert.AreEqual(HttpStatusCode.Unauthorized, protectedRead.StatusCode);
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

        [TestMethod]
        public async Task AccountProfiles_AccessRemoval_RevokesAffectedSessionAndPreservesProfile()
        {
            await using var host = await Host.CreateAsync();
            await SignInAsync(host);
            using var assigned = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/accounts/41/profiles/202");
            Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());
            using var duplicate = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/accounts/41/profiles/202");
            Assert.AreEqual(HttpStatusCode.BadRequest, duplicate.StatusCode);
            using var selected = await host.SendAsync(HttpMethod.Put, "/api/v1/auth/profile/202");
            Assert.AreEqual(HttpStatusCode.OK, selected.StatusCode, await selected.Content.ReadAsStringAsync());
            using var removed = await host.SendAsync(HttpMethod.Delete, "/api/v1/admin/accounts/41/profiles/202");
            Assert.AreEqual(HttpStatusCode.OK, removed.StatusCode, await removed.Content.ReadAsStringAsync());
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"AccountProfiles\" WHERE \"AccountId\" = 41 AND \"ProfileId\" = 202"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AccountProfiles\" WHERE \"AccountId\" = 42 AND \"ProfileId\" = 202"));
            Assert.AreEqual(42L, await host.ScalarAsync("SELECT \"OwnerAccountId\" FROM \"Profiles\" WHERE \"Id\" = 202"));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"AccountSessions\" WHERE \"AccountId\" = 41 AND \"ActiveProfileId\" = 202"));
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            Assert.IsFalse((await state.Content.ReadFromJsonAsync<AuthenticationResultV1>())!.IsAuthenticated);
        }

        [TestMethod]
        public async Task AccountProfiles_OwnershipAndUnknownReferences_DoNotMutate()
        {
            await using var host = await Host.CreateAsync();
            await SignInAsync(host);
            using var owned = await host.SendAsync(HttpMethod.Delete, "/api/v1/admin/accounts/41/profiles/101");
            Assert.AreEqual(HttpStatusCode.BadRequest, owned.StatusCode);
            using var missing = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/accounts/41/profiles/999");
            Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AccountProfiles\" WHERE \"AccountId\" = 41 AND \"ProfileId\" = 101"));
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            Assert.IsTrue((await state.Content.ReadFromJsonAsync<AuthenticationResultV1>())!.IsAuthenticated);

            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            using var denied = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/accounts/41/profiles/202");
            Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"AccountProfiles\" WHERE \"AccountId\" = 41 AND \"ProfileId\" = 202"));
        }

        [TestMethod]
        public async Task Razor_ProfileAssignments_UseServicesAndAntiforgery()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await SignInAsync(host);
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Accounts?AccountId=42");
            var html = await page.Content.ReadAsStringAsync();
            var csrf = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
            using var assigned = await host.SendAsync(HttpMethod.Post, "/Admin/Accounts?AccountId=42&handler=AddProfile", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(csrf), ["ProfileId"] = "101"
            });
            Assert.AreEqual(HttpStatusCode.Redirect, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AccountProfiles\" WHERE \"AccountId\" = 42 AND \"ProfileId\" = 101"));
            using var removed = await host.SendAsync(HttpMethod.Post, "/Admin/Accounts?AccountId=42&handler=RemoveProfile", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(csrf), ["ProfileId"] = "101"
            });
            Assert.AreEqual(HttpStatusCode.Redirect, removed.StatusCode, await removed.Content.ReadAsStringAsync());
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"AccountProfiles\" WHERE \"AccountId\" = 42 AND \"ProfileId\" = 101"));
        }
    }
}
