using System.Net;
using System.Security;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.Public.Setup.V1;
using Jularr.Backend.Service.Public.Setup.V1;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class SetupTests
    {
        [TestMethod]
        public async Task Razor_FirstOwner_ValidatesRollsBackAndSignsIn()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await host.Fixture.RunSqlAsync("TRUNCATE \"Accounts\" RESTART IDENTITY CASCADE");

            // Fresh instances enter setup.
            using var login = await host.SendAsync(HttpMethod.Get, "/Account/Login");
            Assert.AreEqual(HttpStatusCode.Redirect, login.StatusCode);
            Assert.AreEqual("/Setup", login.Headers.Location!.OriginalString);
            using var setup = await host.SendAsync(HttpMethod.Get, "/Setup");
            var html = await setup.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, setup.StatusCode, html);
            StringAssert.Contains(html, "minlength=\"12\"");
            StringAssert.Contains(html, "name=\"PasswordConfirmation\"");
            using var missingToken = await host.SendAsync(HttpMethod.Post, "/Setup", new Dictionary<string, string>());
            Assert.AreEqual(HttpStatusCode.BadRequest, missingToken.StatusCode);

            // Fields and cross-field validation.
            using var shortPassword = await host.SendAsync(HttpMethod.Post, "/Setup", Form(html, "short", "short"));
            Assert.AreEqual(HttpStatusCode.BadRequest, shortPassword.StatusCode);
            using var invalidBinding = await host.SendAsync(HttpMethod.Post, "/Setup", Form(html, "owner-password-123", "owner-password-123", "not-a-number"));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidBinding.StatusCode);
            StringAssert.Contains(await invalidBinding.Content.ReadAsStringAsync(), "name=\"PasswordConfirmation\"");
            using var mismatch = await host.SendAsync(HttpMethod.Post, "/Setup", Form(html, "owner-password-123", "another-password-123"));
            Assert.AreEqual(HttpStatusCode.BadRequest, mismatch.StatusCode);
            StringAssert.Contains(await mismatch.Content.ReadAsStringAsync(), "The passwords do not match.");
            using var unknownLocale = await host.SendAsync(HttpMethod.Post, "/Setup", Form(html, "owner-password-123", "owner-password-123", "999"));
            Assert.AreEqual(HttpStatusCode.BadRequest, unknownLocale.StatusCode);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"Accounts\""));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"InstanceSetupState\" WHERE \"OwnerCreatedAt\" IS NOT NULL"));
            Assert.IsFalse((await unknownLocale.Content.ReadAsStringAsync()).Contains("owner-password-123", StringComparison.Ordinal));

            // Owner, profile and session commit together.
            using var created = await host.SendAsync(HttpMethod.Post, "/Setup", Form(html, "owner-password-123", "owner-password-123"));
            Assert.AreEqual(HttpStatusCode.Redirect, created.StatusCode);
            Assert.AreEqual("/Settings", created.Headers.Location!.OriginalString);
            using var settings = await host.SendAsync(HttpMethod.Get, "/Settings");
            Assert.AreEqual(HttpStatusCode.OK, settings.StatusCode);
            StringAssert.Contains(await settings.Content.ReadAsStringAsync(), "First Owner");
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Accounts\" WHERE \"AccountRoleTypeId\" = 1 AND \"IsEnabled\" AND \"EmailVerifiedAt\" IS NULL"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AccountProfiles\""));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AccountSessions\""));

            // The public API cannot create another Owner.
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/setup");
            StringAssert.Contains(await state.Content.ReadAsStringAsync(), "\"requiresOwner\":false");
            using var replay = await host.SendAsync(HttpMethod.Post, "/api/v1/setup/owner",
                new CreateOwnerDataV1("second@example.test", "Second", "owner-password-123", "owner-password-123", 1));
            Assert.AreEqual(HttpStatusCode.Forbidden, replay.StatusCode);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Accounts\""));
        }

        [TestMethod]
        public async Task FirstOwner_ConcurrentCreation_OnlyOneSucceedsAndBootstrapStaysClosed()
        {
            await using var host = await Host.CreateAsync();
            await host.Fixture.RunSqlAsync("TRUNCATE \"Accounts\" RESTART IDENTITY CASCADE");

            // Independent service scopes race.
            var attempts = await Task.WhenAll(CreateOwnerAttemptAsync(host, "first@example.test"), CreateOwnerAttemptAsync(host, "second@example.test"));
            Assert.AreEqual(1, attempts.Count(created => created));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Accounts\""));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AccountPasswords\""));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Profiles\""));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AccountSessions\""));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"InstanceSetupState\" WHERE \"OwnerCreatedAt\" IS NOT NULL"));

            // Deletion does not reopen public bootstrap.
            await host.Fixture.RunSqlAsync("TRUNCATE \"Accounts\" CASCADE");
            using var scope = host.Services.CreateScope();
            var state = await scope.ServiceProvider.GetRequiredService<ReadSetupV1>().ExecuteAsync<ReadSetupResultV1>();
            Assert.IsFalse(state.RequiresOwner);
            Assert.IsFalse(await CreateOwnerAttemptAsync(host, "replacement@example.test"));
        }

        private static async Task<bool> CreateOwnerAttemptAsync(Host host, string email)
        {
            using var scope = host.Services.CreateScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<CreateOwnerV1>().ExecuteAsync<LoginResultV1>(
                    new CreateOwnerDataV1(email, "Owner", "owner-password-123", "owner-password-123", 1));
                return true;
            }
            catch (SecurityException)
            {
                return false;
            }
        }

        private static Dictionary<string, string> Form(string html, string password, string confirmation, string uiLocaleId = "1")
        {
            var token = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
            Assert.IsTrue(token.Success);
            return new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value),
                ["Email"] = "owner@example.test",
                ["DisplayName"] = "First Owner",
                ["Password"] = password,
                ["PasswordConfirmation"] = confirmation,
                ["UiLocaleId"] = uiLocaleId
            };
        }
    }
}
