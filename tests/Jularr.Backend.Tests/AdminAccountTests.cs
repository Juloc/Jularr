using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Data;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Account.V1;
using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Logic.Account;
using Jularr.Backend.Data;
using Jularr.Backend.Data.User.Profile.V1;
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
            // Establish competing transaction snapshots.
            var sql = new SqlStatement();
            sql.Statement = """SELECT count(*) FROM "Accounts" WHERE "AccountRoleTypeId" = 1;""";
            await firstContext.ReadSql.ExecuteScalarAsync(sql);
            await secondContext.ReadSql.ExecuteScalarAsync(sql);
            var firstAccount = new AccountUpdateData(41, "First", AccountRoleType.User, true);
            var secondAccount = new AccountUpdateData(42, "Second", AccountRoleType.User, true);

            var results = await Task.WhenAll(DemoteAsync(firstContext, first, firstAccount), DemoteAsync(secondContext, second, secondAccount));
            Assert.AreEqual(1, results.Count(committed => committed));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Accounts\" WHERE \"AccountRoleTypeId\" = 1 AND \"IsEnabled\""));
        }

        private static async Task<bool> DemoteAsync(LogicContext context, SqlContext sql, AccountUpdateData account)
        {
            try
            {
                await context.UpdateAccountAsync(account, default);
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
            Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode, await foreign.Content.ReadAsStringAsync());
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
        public async Task Account_OwnerEdits_LastWriteWinsAndLastOwnerIsRetained()
        {
            await using var host = await Host.CreateAsync();
            await SignInAsync(host);
            using var update = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/accounts/42",
                new UpdateAccountDataV1("Managed account", AccountRoleType.MediaManager, true));
            Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, await update.Content.ReadAsStringAsync());
            Assert.AreEqual((short)3, await host.ScalarAsync("SELECT \"AccountRoleTypeId\" FROM \"Accounts\" WHERE \"Id\" = 42"));
            using var latest = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/accounts/42",
                new UpdateAccountDataV1("Latest", AccountRoleType.User, true));
            Assert.AreEqual(HttpStatusCode.OK, latest.StatusCode, await latest.Content.ReadAsStringAsync());
            Assert.AreEqual("Latest", await host.ScalarAsync("SELECT \"DisplayName\" FROM \"Accounts\" WHERE \"Id\" = 42"));

            using var ownerRead = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/accounts/41");
            var owner = (await ownerRead.Content.ReadFromJsonAsync<AccountDetailsV1>())!;
            foreach (var data in new[] { new UpdateAccountDataV1(owner.DisplayName, AccountRoleType.User, true),
                new UpdateAccountDataV1(owner.DisplayName, AccountRoleType.Owner, false) })
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
                new UpdateAccountDataV1(account.DisplayName, AccountRoleType.User, true));
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
                    new UpdateAccountDataV1(account.DisplayName, AccountRoleType.User, enabled));
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
            StringAssert.Contains(html, "value=\"2\" checked=\"checked\"");

            // The list names each account by its username and still shows the email where one is stored.
            StringAssert.Contains(html, ">account_b<");
            StringAssert.Contains(html, ">b@example.test<");
            var csrf = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
            Assert.IsFalse(html.Contains("name=\"UpdatedAt\"", StringComparison.Ordinal));
            using var update = await host.SendAsync(HttpMethod.Post, "/Admin/Accounts?AccountId=42", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(csrf),
                ["DisplayName"] = "Edited in Razor", ["AccountRoleTypeId"] = "3", ["IsEnabled"] = "True"
            });
            Assert.AreEqual(HttpStatusCode.Redirect, update.StatusCode, await update.Content.ReadAsStringAsync());
            Assert.AreEqual("Edited in Razor", await host.ScalarAsync("SELECT \"DisplayName\" FROM \"Accounts\" WHERE \"Id\" = 42"));

            // Failed binding must not become false.
            using var invalid = await host.SendAsync(HttpMethod.Post, "/Admin/Accounts?AccountId=42", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(csrf),
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

        [TestMethod]
        public async Task PasswordLink_SetsANewPasswordOnce_AndEndsTheOpenSessions()
        {
            await using var host = await Host.CreateAsync();
            await SignInAsync(host);
            var accountId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Accounts\" WHERE \"Email\" = 'a@example.test'"))!;

            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/accounts/" + accountId + "/password-link");
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var token = (await created.Content.ReadFromJsonAsync<AccountPasswordLinkResultV1>())!.Token;
            Assert.AreEqual(43, token.Length);

            const string next = "Brand-New-Password-99";
            using var weak = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/password-reset", new ResetPasswordDataV1(token, "short-1", "short-1"));
            Assert.AreEqual(HttpStatusCode.BadRequest, weak.StatusCode);
            using var unknown = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/password-reset", new ResetPasswordDataV1(new string('A', 43), next, next));
            Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);

            using var reset = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/password-reset", new ResetPasswordDataV1(token, next, next));
            Assert.AreEqual(HttpStatusCode.OK, reset.StatusCode, await reset.Content.ReadAsStringAsync());
            using var again = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/password-reset", new ResetPasswordDataV1(token, next + "x", next + "x"));
            Assert.AreEqual(HttpStatusCode.BadRequest, again.StatusCode);

            // The new password ends the sessions that were open.
            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/accounts/" + accountId);
            Assert.AreEqual(HttpStatusCode.Unauthorized, read.StatusCode);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var oldLogin = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
            using var newLogin = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", next));
            Assert.AreEqual(HttpStatusCode.OK, newLogin.StatusCode);
        }

        [TestMethod]
        public async Task ResetPasswordPage_RendersForAVisitor_WithTheTokenAndPasswordFields()
        {
            await using var host = await Host.CreateAsync(razor: true);
            var token = new string('A', 43);
            using var page = await host.SendAsync(HttpMethod.Get, "/Account/ResetPassword?token=" + token);
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
            var html = await page.Content.ReadAsStringAsync();
            StringAssert.Contains(html, token);
            StringAssert.Contains(html, "type=\"password\"");
        }

        private static async Task SignInAsync(Host host)
        {
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
        }

        [TestMethod]
        public async Task ProfilesModule_Off_EveryAccountUsesItsMainProfileWithoutPickerOrPin()
        {
            // Off by default: a second linked profile and a PIN change nothing.
            await using var host = await Host.CreateAsync(razor: true);
            await host.Fixture.RunSqlAsync("""
                UPDATE "InstanceModules" SET "IsEnabled" = FALSE WHERE "Key" = 'Profiles';
                INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (41, 202);
                UPDATE "Profiles" SET "PinHash" = 'selection-guard', "PinLength" = 4 WHERE "Id" = 101;
                """);
            await SignInAsync(host);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            Assert.AreEqual(101L, (await state.Content.ReadFromJsonAsync<AuthenticationResultV1>())!.ProfileId);
            using var profiles = await host.SendAsync(HttpMethod.Get, "/api/v1/profiles");
            var listed = (await profiles.Content.ReadFromJsonAsync<PageResult<ProfileSelectionDetailsV1>>())!.Items.Single();
            Assert.AreEqual(101L, listed.Id);
            Assert.IsFalse(listed.IsLocked);

            // No other profile, no new ones, no PIN management; the pages offer none of it.
            using var foreign = await host.SendAsync(HttpMethod.Put, "/api/v1/auth/profile/202");
            Assert.IsFalse(foreign.IsSuccessStatusCode);
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/profiles", new CreateProfileDataV1("Child", 1));
            Assert.IsFalse(created.IsSuccessStatusCode);
            using var pin = await host.SendAsync(HttpMethod.Put, "/api/v1/profiles/101/pin", new UpdateProfilePinDataV1("1234"));
            Assert.IsFalse(pin.IsSuccessStatusCode);
            using var settings = await host.SendAsync(HttpMethod.Get, "/Settings");
            var html = await settings.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, settings.StatusCode, html);
            Assert.IsFalse(html.Contains("Save PIN", StringComparison.Ordinal));
            Assert.IsFalse(html.Contains("/Account/Profiles?", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task ProfilePin_FailuresLockAcrossErrorsAndTheOwnerResets()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await SignInAsync(host);
            using var selected = await host.SendAsync(HttpMethod.Put, "/api/v1/auth/profile/101");
            Assert.AreEqual(HttpStatusCode.OK, selected.StatusCode, await selected.Content.ReadAsStringAsync());
            using var invalid = await host.SendAsync(HttpMethod.Put, "/api/v1/profiles/101/pin", new UpdateProfilePinDataV1("12a4"));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/profiles/101/pin", new UpdateProfilePinDataV1("1234"));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            Assert.IsFalse(((string)(await host.ScalarAsync("SELECT \"PinHash\" FROM \"Profiles\" WHERE \"Id\" = 101"))!).Contains("1234", StringComparison.Ordinal));

            // The picker announces the length; selection without the PIN stays closed.
            using var profiles = await host.SendAsync(HttpMethod.Get, "/api/v1/profiles");
            var listed = (await profiles.Content.ReadFromJsonAsync<PageResult<ProfileSelectionDetailsV1>>())!.Items.Single(profile => profile.Id == 101);
            Assert.IsTrue(listed.IsLocked);
            Assert.AreEqual(4, listed.PinLength);
            using var closed = await host.SendAsync(HttpMethod.Put, "/api/v1/auth/profile/101");
            Assert.AreEqual(HttpStatusCode.Forbidden, closed.StatusCode);
            using var admin = await host.SendAsync(HttpMethod.Get, "/Admin/Accounts?AccountId=41");
            StringAssert.Contains(await admin.Content.ReadAsStringAsync(), "Reset PIN");
            using var page = await host.SendAsync(HttpMethod.Get, "/Account/Profiles/Pin?profileId=101");
            StringAssert.Contains(await page.Content.ReadAsStringAsync(), "data-pin-length=\"4\"");

            // Failures count across error responses; the fifth locks for a minute, even the right PIN.
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                using var wrong = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/profile/101/pin", new VerifyProfilePinDataV1("0000"));
                Assert.AreEqual(HttpStatusCode.BadRequest, wrong.StatusCode);
                StringAssert.Contains(await wrong.Content.ReadAsStringAsync(), "invalid_pin");
                Assert.AreEqual(attempt % 5, await host.ScalarAsync("SELECT \"PinFailedCount\" FROM \"Profiles\" WHERE \"Id\" = 101"));
            }

            using var locked = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/profile/101/pin", new VerifyProfilePinDataV1("1234"));
            StringAssert.Contains(await locked.Content.ReadAsStringAsync(), "pin_locked");
            await host.Fixture.RunSqlAsync("UPDATE \"Profiles\" SET \"PinLockedUntil\" = now() - interval '1 second' WHERE \"Id\" = 101");
            using var unlocked = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/profile/101/pin", new VerifyProfilePinDataV1("1234"));
            Assert.AreEqual(HttpStatusCode.OK, unlocked.StatusCode, await unlocked.Content.ReadAsStringAsync());
            Assert.AreEqual(0, await host.ScalarAsync("SELECT \"PinFailedCount\" FROM \"Profiles\" WHERE \"Id\" = 101"));
            using var settings = await host.SendAsync(HttpMethod.Get, "/Settings");
            StringAssert.Contains(await settings.Content.ReadAsStringAsync(), "Remove PIN");

            // The profile removes its PIN explicitly; the Owner removes a forgotten one.
            using var removed = await host.SendAsync(HttpMethod.Delete, "/api/v1/profiles/101/pin");
            Assert.AreEqual(HttpStatusCode.OK, removed.StatusCode, await removed.Content.ReadAsStringAsync());
            Assert.AreEqual(DBNull.Value, await host.ScalarAsync("SELECT \"PinLength\" FROM \"Profiles\" WHERE \"Id\" = 101"));
            using var again = await host.SendAsync(HttpMethod.Put, "/api/v1/profiles/101/pin", new UpdateProfilePinDataV1("567890"));
            Assert.AreEqual(HttpStatusCode.OK, again.StatusCode, await again.Content.ReadAsStringAsync());
            using var reset = await host.SendAsync(HttpMethod.Delete, "/api/v1/admin/profiles/101/pin");
            Assert.AreEqual(HttpStatusCode.OK, reset.StatusCode, await reset.Content.ReadAsStringAsync());
            Assert.AreEqual(DBNull.Value, await host.ScalarAsync("SELECT \"PinHash\" FROM \"Profiles\" WHERE \"Id\" = 101"));
        }

        [TestMethod]
        public async Task Profiles_AccountCreatesUpToTenAndDeletionCancelsItsOpenRequests()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await SignInAsync(host);
            using var page = await host.SendAsync(HttpMethod.Get, "/Account/Profiles/New");
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
            var locale = (long)(await host.ScalarAsync("SELECT \"UiLocaleId\" FROM \"Profiles\" WHERE \"Id\" = 101"))!;

            // Ten owned profiles at most; the picker stops offering more.
            var owned = (long)(await host.ScalarAsync("SELECT count(*) FROM \"Profiles\" WHERE \"OwnerAccountId\" = 41"))!;
            for (var index = owned; index < 10; index++)
            {
                using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/profiles", new CreateProfileDataV1("Child " + index, locale));
                Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            }

            using var limited = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/profiles", new CreateProfileDataV1("One too many", locale));
            StringAssert.Contains(await limited.Content.ReadAsStringAsync(), "profile_limit");
            using var picker = await host.SendAsync(HttpMethod.Get, "/Account/Profiles");
            Assert.IsFalse((await picker.Content.ReadAsStringAsync()).Contains("Add profile", StringComparison.Ordinal));

            // Deleting the active profile cancels its open requests, keeps them with the account and returns the session to the picker.
            var child = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Profiles\" WHERE \"DisplayName\" = 'Child 9'"))!;
            await host.Fixture.RunSqlAsync($$"""
                WITH work AS (INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (1, 'Requested Film') RETURNING "Id")
                INSERT INTO "AcquisitionRequests" ("WorkId", "RequestedByProfileId", "RequestedByAccountId", "UiLocaleId") SELECT "Id", {{child}}, 41, {{locale}} FROM work
                """);
            using var foreign = await host.SendAsync(HttpMethod.Delete, "/api/v1/profiles/" + child);
            Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode, await foreign.Content.ReadAsStringAsync());
            using var selected = await host.SendAsync(HttpMethod.Put, "/api/v1/auth/profile/" + child);
            Assert.AreEqual(HttpStatusCode.OK, selected.StatusCode, await selected.Content.ReadAsStringAsync());
            using var deleted = await host.SendAsync(HttpMethod.Delete, "/api/v1/profiles/" + child);
            Assert.AreEqual(HttpStatusCode.OK, deleted.StatusCode, await deleted.Content.ReadAsStringAsync());
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"Profiles\" WHERE \"Id\" = " + child));
            Assert.AreEqual((short)9, await host.ScalarAsync("SELECT \"AcquisitionRequestStatusTypeId\" FROM \"AcquisitionRequests\" WHERE \"RequestedByProfileId\" IS NULL"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Works\" WHERE \"CanonicalTitle\" = 'Requested Film'"));
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            Assert.IsNull((await state.Content.ReadFromJsonAsync<AuthenticationResultV1>())!.ProfileId);
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
