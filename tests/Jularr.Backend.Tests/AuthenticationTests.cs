using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data.Logic.Account;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Integrations.Logic.Plex;
using Jularr.Backend.Service.Core;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.DataProtection;
using AuthenticationTicket = Microsoft.AspNetCore.Authentication.AuthenticationTicket;
using AuthenticationProperties = Microsoft.AspNetCore.Authentication.AuthenticationProperties;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Fixture = Jularr.Backend.Tests.ProfileReferenceVerticalTests.ProfileFixture;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Localization;
using Jularr.Frontend.Pages.Shared;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Http;
using Jularr.Backend.Service.Core.Localization;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class AuthenticationTests
    {
        private const string Password = "test-password-123";

        [TestMethod]
        public async Task Session_ActiveAfterHalfLife_RenewsDatabaseAndPersistentCookie()
        {
            await using var host = await Host.CreateAsync();
            await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", Password));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
            var options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("Cookies");
            var ticket = options.TicketDataFormat.Unprotect(host.Cookies[options.Cookie.Name!])!;
            Assert.IsTrue(ticket.Properties.IsPersistent);
            Assert.IsTrue(Math.Abs((ticket.Properties.ExpiresUtc!.Value - DateTimeOffset.UtcNow).TotalDays - 30) < .01);
            var sessionId = ticket.Principal.FindFirst(ServiceGate.SessionClaimType)!.Value;
            var originalClaims = ticket.Principal.Claims.Select(claim => (claim.Type, claim.Value)).ToArray();

            // Simulate twenty inactive days.
            ticket.Properties.IssuedUtc = DateTimeOffset.UtcNow.AddDays(-20);
            ticket.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddDays(10);
            await host.Fixture.RunSqlAsync($"UPDATE \"AccountSessions\" SET \"ExpiresAt\" = now() + interval '10 days' WHERE \"PublicId\" = '{sessionId}'");
            var oldCookie = options.TicketDataFormat.Protect(ticket);
            host.Cookies[options.Cookie.Name!] = oldCookie;

            using var renewed = await host.SendAsync(HttpMethod.Get, "/api/v1/profiles/101");
            Assert.AreEqual(HttpStatusCode.OK, renewed.StatusCode);
            Assert.AreNotEqual(oldCookie, host.Cookies[options.Cookie.Name!]);
            var updated = options.TicketDataFormat.Unprotect(host.Cookies[options.Cookie.Name!])!;
            CollectionAssert.AreEqual(originalClaims, updated.Principal.Claims.Select(claim => (claim.Type, claim.Value)).ToArray());
            Assert.IsTrue(updated.Properties.IsPersistent);
            Assert.IsTrue(Math.Abs((updated.Properties.ExpiresUtc!.Value - DateTimeOffset.UtcNow).TotalDays - 30) < .01);
            var databaseExpiry = (DateTime)(await host.ScalarAsync($"SELECT \"ExpiresAt\" FROM \"AccountSessions\" WHERE \"PublicId\" = '{sessionId}'"))!;
            Assert.IsTrue(Math.Abs((databaseExpiry - updated.Properties.ExpiresUtc.Value.UtcDateTime).TotalSeconds) < 1);
            Assert.IsNotNull(await host.ScalarAsync($"SELECT \"LastSeenAt\" FROM \"AccountSessions\" WHERE \"PublicId\" = '{sessionId}'"));

            // Ordinary reads avoid renewal.
            var currentCookie = host.Cookies[options.Cookie.Name!];
            using var subsequent = await host.SendAsync(HttpMethod.Get, "/api/v1/profiles/101");
            Assert.AreEqual(HttpStatusCode.OK, subsequent.StatusCode);
            Assert.AreEqual(currentCookie, host.Cookies[options.Cookie.Name!]);
            Assert.AreEqual(databaseExpiry, await host.ScalarAsync($"SELECT \"ExpiresAt\" FROM \"AccountSessions\" WHERE \"PublicId\" = '{sessionId}'"));
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Session_ExpiredOrRevoked_IsNotRenewed(bool revoked)
        {
            await using var host = await Host.CreateAsync();
            await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", Password));
            var options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("Cookies");
            var ticket = options.TicketDataFormat.Unprotect(host.Cookies[options.Cookie.Name!])!;
            var sessionId = ticket.Principal.FindFirst(ServiceGate.SessionClaimType)!.Value;
            ticket.Properties.IssuedUtc = DateTimeOffset.UtcNow.AddDays(-20);
            ticket.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddDays(10);
            host.Cookies[options.Cookie.Name!] = options.TicketDataFormat.Protect(ticket);

            // Invalidate server-side proof.
            var change = revoked ? "\"RevokedAt\" = now()" : "\"CreatedAt\" = now() - interval '20 days', \"ExpiresAt\" = now() - interval '1 minute'";
            await host.Fixture.RunSqlAsync($"UPDATE \"AccountSessions\" SET {change} WHERE \"PublicId\" = '{sessionId}'");
            var expiry = await host.ScalarAsync($"SELECT \"ExpiresAt\" FROM \"AccountSessions\" WHERE \"PublicId\" = '{sessionId}'");
            using var rejected = await host.SendAsync(HttpMethod.Get, "/api/v1/profiles/101");
            Assert.AreEqual(HttpStatusCode.Unauthorized, rejected.StatusCode);
            Assert.AreEqual(expiry, await host.ScalarAsync($"SELECT \"ExpiresAt\" FROM \"AccountSessions\" WHERE \"PublicId\" = '{sessionId}'"));
        }

        [TestMethod]
        public async Task Session_NewDeploymentRoot_PreservesCookieAndProtectedSecrets()
        {
            // Share only persisted keys.
            var directory = Directory.CreateTempSubdirectory("jularr-protection-");
            try
            {
                var keyDirectory = Path.Combine(directory.FullName, "keys");
                string? cookie = null;
                byte[]? secret = null;
                foreach (var release in new[] { "first", "second" })
                {
                    var contentRoot = Directory.CreateDirectory(Path.Combine(directory.FullName, release));
                    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = contentRoot.FullName });
                    builder.Services.AddAuthentication("Cookies").AddCookie();
                    builder.Configuration["JULARR_DB"] = "Host=127.0.0.1;Port=1;Username=unused;Database=unused;Timeout=1";
                    builder.Configuration["DataProtection:KeyDirectory"] = keyDirectory;
                    builder.Services.AddJularrServices(builder.Configuration);
                    await using var app = builder.Build();
                    var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("Cookies");
                    var protection = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("restart-test");
                    if (release == "first")
                    {
                        var principal = new ClaimsPrincipal(new ClaimsIdentity("Cookies"));
                        cookie = options.TicketDataFormat.Protect(new AuthenticationTicket(principal,
                            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) }, "Cookies"));
                        secret = protection.Protect([1, 2, 3]);
                    }
                    else
                    {
                        Assert.IsNotNull(options.TicketDataFormat.Unprotect(cookie!));
                        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, protection.Unprotect(secret!));
                    }
                }
            }
            finally
            {
                directory.Delete(recursive: true);
            }
        }

        [TestMethod]
        public async Task Logout_AccountSessionWithoutProfile_RevokesCookieAndDatabaseSession()
        {
            await using var host = await Host.CreateAsync();
            await host.Fixture.RunSqlAsync("UPDATE \"Profiles\" SET \"PinHash\" = 'selection-guard', \"PinLength\" = 4 WHERE \"Id\" = 101");
            await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", Password));
            var result = (await login.Content.ReadFromJsonAsync<LoginResultV1>())!;
            Assert.IsNull(result.ProfileId);

            using var logout = await host.SendAsync(HttpMethod.Delete, "/api/v1/auth/session");
            Assert.AreEqual(HttpStatusCode.OK, logout.StatusCode);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            Assert.IsFalse((await state.Content.ReadFromJsonAsync<AuthenticationResultV1>())!.IsAuthenticated);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AccountSessions\" WHERE \"RevokedAt\" IS NOT NULL"));
        }

        [TestMethod]
        public async Task RevokeSession_ForeignAndMissingSession_AreHidden()
        {
            await using var host = await Host.CreateAsync();
            var foreignId = Guid.NewGuid();
            await host.Fixture.RunSqlAsync($"INSERT INTO \"AccountSessions\" (\"PublicId\", \"AccountId\", \"TokenHash\", \"ExpiresAt\") VALUES ('{foreignId}', 42, decode(repeat('00', 32), 'hex'), now() + interval '1 hour')");
            await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", Password));

            foreach (var id in new[] { foreignId, Guid.NewGuid() })
            {
                using var revoke = await host.SendAsync(HttpMethod.Delete, "/api/v1/sessions/" + id);
                Assert.AreEqual(HttpStatusCode.NotFound, revoke.StatusCode);
            }

            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"AccountSessions\" WHERE \"RevokedAt\" IS NOT NULL"));
        }

        [TestMethod]
        public async Task Registration_PendingAndDuplicateRequests_DoNotCreateSessionsOrReplacePasswords()
        {
            await using var host = await Host.CreateAsync();
            await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            var request = new RegisterDataV1("new@example.test", "New", Password, 1);

            // Only with the Registration module.
            using var closed = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/register", request);
            Assert.AreEqual(HttpStatusCode.Forbidden, closed.StatusCode);
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Registration'");

            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/register", request);
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode);
            Assert.IsTrue((await created.Content.ReadFromJsonAsync<RegisterResultV1>())!.RequiresApproval);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Accounts\" WHERE \"Email\" = 'new@example.test' AND NOT \"IsEnabled\" AND \"EmailVerifiedAt\" IS NULL AND \"AccountRoleTypeId\" = 2"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AccountProfiles\" AS membership JOIN \"Profiles\" AS profile ON profile.\"Id\" = membership.\"ProfileId\" JOIN \"Accounts\" AS account ON account.\"Id\" = membership.\"AccountId\" WHERE account.\"Email\" = 'new@example.test' AND profile.\"OwnerAccountId\" = account.\"Id\""));
            var hash = await host.ScalarAsync("SELECT \"PasswordHash\" FROM \"AccountPasswords\" AS password JOIN \"Accounts\" AS account ON account.\"Id\" = password.\"AccountId\" WHERE account.\"Email\" = 'new@example.test'");
            Assert.AreNotEqual(Password, hash);

            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1(request.Email, Password));
            Assert.AreEqual(HttpStatusCode.Unauthorized, login.StatusCode);
            using var duplicate = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/register", new RegisterDataV1("NEW@example.test", "Replacement", "different-password", 1));
            Assert.AreEqual(HttpStatusCode.OK, duplicate.StatusCode);
            Assert.AreEqual(hash, await host.ScalarAsync("SELECT \"PasswordHash\" FROM \"AccountPasswords\" AS password JOIN \"Accounts\" AS account ON account.\"Id\" = password.\"AccountId\" WHERE account.\"Email\" = 'new@example.test'"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AccountSessions\""));

            using var invalidLocale = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/register", new RegisterDataV1("rollback@example.test", "Rollback", Password, 999));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidLocale.StatusCode);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"Accounts\" WHERE \"Email\" = 'rollback@example.test'"));
        }

        [TestMethod]
        public async Task Login_RealCookieAndRevocation_WorkWithoutExposingSessionSecrets()
        {
            await using var host = await Host.CreateAsync();
            using var noCsrf = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", Password));
            Assert.AreEqual(HttpStatusCode.BadRequest, noCsrf.StatusCode);
            await host.SendAsync(HttpMethod.Get, "/api/v1/auth");

            foreach (var email in new[] { "a@example.test", "missing@example.test" })
            {
                using var wrong = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1(email, "wrong-password"));
                Assert.AreEqual(HttpStatusCode.Unauthorized, wrong.StatusCode);
            }

            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("A@example.test", Password));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
            var result = (await login.Content.ReadFromJsonAsync<LoginResultV1>())!;
            Assert.IsTrue(result.IsAuthenticated);
            Assert.AreEqual(host.Fixture.ProfileAId, result.ProfileId);
            var body = await login.Content.ReadAsStringAsync();
            Assert.IsFalse(body.Contains("token", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(body.Contains("password", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(login.Headers.GetValues("Set-Cookie").Any(value => value.Contains("httponly", StringComparison.OrdinalIgnoreCase)
                && value.Contains("secure", StringComparison.OrdinalIgnoreCase)));

            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/profiles/101");
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
            using var update = await host.SendAsync(HttpMethod.Put, "/api/v1/profiles/101", new { displayName = "Logged in" });
            Assert.AreEqual(HttpStatusCode.OK, update.StatusCode);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            Assert.IsTrue((await state.Content.ReadFromJsonAsync<AuthenticationResultV1>())!.IsAuthenticated);

            await host.Fixture.RunSqlAsync("UPDATE \"AccountSessions\" SET \"RevokedAt\" = now()");
            using var revoked = await host.SendAsync(HttpMethod.Get, "/api/v1/profiles/101");
            Assert.AreEqual(HttpStatusCode.Unauthorized, revoked.StatusCode);
        }

        [TestMethod]
        public async Task Login_ProfilePinAndConfirmedSecondFactor_AreNotBypassed()
        {
            await using var host = await Host.CreateAsync();
            await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            await host.Fixture.RunSqlAsync("UPDATE \"Profiles\" SET \"PinHash\" = 'selection-guard', \"PinLength\" = 4 WHERE \"Id\" = 101");

            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", Password));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
            var result = (await login.Content.ReadFromJsonAsync<LoginResultV1>())!;
            Assert.IsTrue(result.IsAuthenticated);
            Assert.IsNull(result.ProfileId);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            Assert.IsTrue((await state.Content.ReadFromJsonAsync<AuthenticationResultV1>())!.IsAuthenticated);
            using var profile = await host.SendAsync(HttpMethod.Get, "/api/v1/profiles/101");
            Assert.AreEqual(HttpStatusCode.Unauthorized, profile.StatusCode);

            await host.Fixture.RunSqlAsync("INSERT INTO \"AccountTotpFactors\" (\"AccountId\", \"EncryptedSecret\", \"ConfirmedAt\") VALUES (41, decode('aa', 'hex'), now())");
            using var secondFactor = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", Password));
            Assert.AreEqual(HttpStatusCode.Unauthorized, secondFactor.StatusCode);
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"AccountSessions\""));
        }

        [TestMethod]
        public async Task Plex_BrowserBoundLibraryGateApprovalAndReplay_UseStableIdentityOnly()
        {
            await using var host = await Host.CreateAsync(plexEnabled: true);
            await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var started = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/plex");
            Assert.IsTrue(started.Headers.GetValues("Set-Cookie").Any(cookie =>
                cookie.Contains("path=/;", StringComparison.OrdinalIgnoreCase)));
            Assert.AreEqual(HttpStatusCode.OK, started.StatusCode);
            var attempt = (await started.Content.ReadFromJsonAsync<StartPlexResultV1>())!;
            var path = "/api/v1/auth/plex/" + attempt.AuthChallengePublicId;
            Assert.IsTrue(attempt.AuthorizationUrl.StartsWith("https://app.plex.tv/auth#?", StringComparison.Ordinal));
            Assert.IsFalse((await started.Content.ReadAsStringAsync()).Contains("browserNonce", StringComparison.OrdinalIgnoreCase));

            var nonce = host.Cookies[ServiceApi.AuthenticationNonceCookieName];
            host.Cookies[ServiceApi.AuthenticationNonceCookieName] = new string('x', 43);
            using var otherBrowser = await host.SendAsync(HttpMethod.Post, path);
            Assert.AreEqual(HttpStatusCode.Unauthorized, otherBrowser.StatusCode);
            host.Cookies[ServiceApi.AuthenticationNonceCookieName] = nonce;

            host.Plex.Pending = true;
            using var pending = await host.SendAsync(HttpMethod.Post, path);
            Assert.IsFalse((await pending.Content.ReadFromJsonAsync<LoginResultV1>())!.IsAuthenticated);
            host.Plex.Pending = false;
            host.Plex.LibraryKey = "8";
            using var denied = await host.SendAsync(HttpMethod.Post, path);
            Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"AccountExternalLogins\""));

            host.Plex.LibraryKey = "7";
            host.Plex.Email = "a@example.test";
            using var collision = await host.SendAsync(HttpMethod.Post, path);
            Assert.AreEqual(HttpStatusCode.Forbidden, collision.StatusCode);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"AccountExternalLogins\""));

            host.Plex.Email = "plex@example.test";
            using var requested = await host.SendAsync(HttpMethod.Post, path);
            var request = (await requested.Content.ReadFromJsonAsync<LoginResultV1>())!;
            Assert.IsFalse(request.IsAuthenticated);
            Assert.IsTrue(request.RequiresApproval);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Accounts\" WHERE \"Email\" = 'plex@example.test' AND NOT \"IsEnabled\" AND \"EmailVerifiedAt\" IS NOT NULL"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AccountAuthChallenges\" WHERE \"ConsumedAt\" IS NOT NULL AND \"ProtectedState\" IS NULL"));
            using var replay = await host.SendAsync(HttpMethod.Post, path);
            Assert.AreEqual(HttpStatusCode.Unauthorized, replay.StatusCode);

            // Approval is a test setup action, not an endpoint supplied by this auth slice.
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"IsEnabled\" = TRUE WHERE \"Email\" = 'plex@example.test'");
            using var next = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/plex");
            var nextAttempt = (await next.Content.ReadFromJsonAsync<StartPlexResultV1>())!;
            host.Plex.Email = "changed@example.test";
            host.Plex.LibraryKey = "8";
            host.Plex.ResourcesUnavailable = true;
            using var signedIn = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/plex/" + nextAttempt.AuthChallengePublicId);
            Assert.AreEqual(HttpStatusCode.OK, signedIn.StatusCode);
            var signedInResult = (await signedIn.Content.ReadFromJsonAsync<LoginResultV1>())!;
            Assert.IsTrue(signedInResult.IsAuthenticated);
            Assert.AreEqual(signedInResult.AccountId, await host.ScalarAsync("SELECT \"AccountId\" FROM \"AccountExternalLogins\" WHERE \"ExternalAccountId\" = 'plex-123'"));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"Accounts\" WHERE \"Email\" = 'changed@example.test'"));
            Assert.IsFalse((await signedIn.Content.ReadAsStringAsync()).Contains("verified-plex-token", StringComparison.Ordinal));

            await host.Fixture.RunSqlAsync("INSERT INTO \"AccountTotpFactors\" (\"AccountId\", \"EncryptedSecret\", \"ConfirmedAt\") SELECT \"AccountId\", decode('aa', 'hex'), now() FROM \"AccountExternalLogins\" WHERE \"ExternalAccountId\" = 'plex-123'");
            var sessions = await host.ScalarAsync("SELECT count(*) FROM \"AccountSessions\"");
            using var protectedAttempt = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/plex");
            var protectedFlow = (await protectedAttempt.Content.ReadFromJsonAsync<StartPlexResultV1>())!;
            using var secondFactor = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/plex/" + protectedFlow.AuthChallengePublicId);
            Assert.AreEqual(HttpStatusCode.Unauthorized, secondFactor.StatusCode);
            Assert.AreEqual(sessions, await host.ScalarAsync("SELECT count(*) FROM \"AccountSessions\""));
        }

        [TestMethod]
        [DataRow("unconfirmed", HttpStatusCode.Forbidden)]
        [DataRow("wrong-server", HttpStatusCode.Forbidden)]
        [DataRow("expired", HttpStatusCode.Unauthorized)]
        [DataRow("unavailable", HttpStatusCode.ServiceUnavailable)]
        [DataRow("provider-disabled", HttpStatusCode.Forbidden)]
        public async Task Plex_InvalidProofOrProvider_NoAccountOrSessionIsCreated(string failure, HttpStatusCode expected)
        {
            await using var host = await Host.CreateAsync(plexEnabled: true);
            await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            if (failure == "provider-disabled")
            {
                await host.Fixture.RunSqlAsync("UPDATE \"Providers\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'plex'");
            }

            using var started = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/plex");
            if (failure == "provider-disabled")
            {
                Assert.AreEqual(expected, started.StatusCode);
                Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"AccountAuthChallenges\""));
                Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"AccountExternalLogins\""));
                Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AccountSessions\""));
                return;
            }

            var attempt = (await started.Content.ReadFromJsonAsync<StartPlexResultV1>())!;
            host.Plex.Confirmed = failure != "unconfirmed";
            host.Plex.ServerIdentifier = failure == "wrong-server" ? "another-server" : "connected-server";
            host.Plex.Unavailable = failure == "unavailable";
            if (failure == "expired")
            {
                await host.Fixture.RunSqlAsync("UPDATE \"AccountAuthChallenges\" SET \"CreatedAt\" = now() - interval '1 hour', \"ExpiresAt\" = now() - interval '1 minute'");
            }

            using var completed = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/plex/" + attempt.AuthChallengePublicId);
            Assert.AreEqual(expected, completed.StatusCode);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"AccountExternalLogins\""));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AccountSessions\""));
        }

        internal sealed class Host : IAsyncDisposable
        {
            private readonly WebApplication _app;
            private readonly HttpClient _client;
            private string? _csrf;

            private Host(Fixture fixture, WebApplication app, PlexHandler plex)
            {
                Fixture = fixture;
                _app = app;
                _client = app.GetTestClient();
                Plex = plex;
            }

            public Fixture Fixture { get; }
            public PlexHandler Plex { get; }
            public IServiceProvider Services => _app.Services;
            public Dictionary<string, string> Cookies { get; } = new(StringComparer.Ordinal);

            public static async Task<Host> CreateAsync(bool plexEnabled = false, bool razor = false, bool media = false, bool storage = false, bool library = false, IReadOnlyDictionary<string, string>? settings = null,
                Action<IServiceCollection>? services = null)
            {
                // Catalog baselines are installed together, as in a fresh installation; Razor pages need all of them.
                library |= media || storage || razor;
                media = storage = library;
                var fixture = await Fixture.CreateAsync();
                using (var schema = typeof(ServiceApi).Assembly.GetManifestResourceStream("Jularr.Database.Instance.sql")!)
                using (var reader = new StreamReader(schema))
                {
                    await fixture.RunSqlAsync(await reader.ReadToEndAsync());
                }

                // The fixtures have several profiles per account; the default-off state has its own test.
                await fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Profiles'");

                if (media)
                {
                    // Install the requested baseline.
                    using var schema = typeof(ServiceApi).Assembly.GetManifestResourceStream("Jularr.Database.Media.sql")!;
                    using var reader = new StreamReader(schema);
                    await fixture.RunSqlAsync(await reader.ReadToEndAsync());
                    using var acquisitionSchema = typeof(ServiceApi).Assembly.GetManifestResourceStream("Jularr.Database.Acquisition.sql")!;
                    using var acquisitionReader = new StreamReader(acquisitionSchema);
                    await fixture.RunSqlAsync(await acquisitionReader.ReadToEndAsync());
                }

                if (storage)
                {
                    using var schema = typeof(ServiceApi).Assembly.GetManifestResourceStream("Jularr.Database.Storage.sql")!;
                    using var reader = new StreamReader(schema);
                    await fixture.RunSqlAsync(await reader.ReadToEndAsync());
                }

                if (library)
                {
                    foreach (var name in new[] { "Library", "Playback" })
                    {
                        using var schema = typeof(ServiceApi).Assembly.GetManifestResourceStream("Jularr.Database." + name + ".sql")!;
                        using var reader = new StreamReader(schema);
                        await fixture.RunSqlAsync(await reader.ReadToEndAsync());
                    }
                }

                var hasher = new PasswordHasher<AccountAuthenticationData>();
                var hash = hasher.HashPassword(new AccountAuthenticationData(41, 1, null, true, false), Password);
                await fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 1 WHERE \"Id\" = 41");
                await using (var connection = await fixture.Database.OpenConnectionAsync())
                await using (var command = new NpgsqlCommand("INSERT INTO \"AccountPasswords\" (\"AccountId\", \"PasswordHash\") VALUES (41, @hash)", connection))
                {
                    command.Parameters.AddWithValue("hash", hash);
                    await command.ExecuteNonQueryAsync();
                }

                var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                {
                    ApplicationName = razor ? typeof(UiText).Assembly.GetName().Name : typeof(AuthenticationTests).Assembly.GetName().Name,
                    ContentRootPath = razor ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Jularr.Frontend")) : null
                });
                builder.WebHost.UseTestServer();
                builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
                builder.Configuration["JULARR_DB"] = fixture.ConnectionString;
                builder.Configuration["Storage:RequireMountPoint"] = "false";
                foreach (var (key, value) in settings ?? new Dictionary<string, string>())
                {
                    builder.Configuration[key] = value;
                }

                builder.Services.AddJularrServices(builder.Configuration);
                if (razor)
                {
                    builder.Services.AddRazorPages(options => options.Conventions.AddJularrPages());
                    builder.Services.AddAuthorization();
                    builder.Services.AddJularrLocalization(UiResources.English);
                    builder.Services.AddScoped<UiText>();
                    builder.Services.AddSingleton<FrontendAssets>();
                    builder.Services.AddSingleton<ShellNavigation>();
                }
                builder.Services.Configure<AuthenticationOptions>(options =>
                {
                    options.PlexLoginEnabled = plexEnabled;
                    options.PlexRegistrationEnabled = plexEnabled;
                    options.PlexServerUri = new Uri("https://plex-server.example.test/");
                    options.PlexServerIdentifier = "connected-server";
                    options.PlexRegistrationLibraryKeys = ["7"];
                });
                var plex = new PlexHandler();
                builder.Services.AddHttpClient<PlexClient>().ConfigurePrimaryHttpMessageHandler(() => plex);
                services?.Invoke(builder.Services);

                var app = builder.Build();
                app.UseExceptionHandler();
                app.UseTrustedProxies();
                app.UseRateLimiter();
                app.UseAuthentication();
                if (razor)
                {
                    app.UseAuthorization();
                    app.Use(async (http, next) =>
                    {
                        if (http.GetEndpoint()?.Metadata.GetMetadata<PageActionDescriptor>() is not null)
                        {
                            http.Response.Headers.CacheControl = "private,no-store";
                            await http.RequestServices.GetRequiredService<UiText>().InitializeAsync(http);
                        }

                        await next(http);
                    });
                    app.MapRazorPages();
                }
                app.MapJularrServices();
                await app.StartAsync();
                return new Host(fixture, app, plex);
            }

            public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? data = null)
            {
                using var request = new HttpRequestMessage(method, path);
                if (data is not null)
                {
                    request.Content = data switch { HttpContent content => content, IReadOnlyDictionary<string, string> form => new FormUrlEncodedContent(form), _ => JsonContent.Create(data) };
                }

                if (Cookies.Count > 0)
                {
                    request.Headers.Add("Cookie", string.Join("; ", Cookies.Select(cookie => cookie.Key + "=" + cookie.Value)));
                }

                if (_csrf is not null)
                {
                    request.Headers.Add("RequestVerificationToken", _csrf);
                }

                var response = await _client.SendAsync(request);
                if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
                {
                    foreach (var cookie in cookies)
                    {
                        var pair = cookie.Split(';')[0].Split('=', 2);
                        Cookies[pair[0]] = pair[1];
                    }
                }

                if (response.Headers.TryGetValues("RequestVerificationToken", out var tokens))
                {
                    _csrf = tokens.Single();
                }

                return response;
            }

            public HttpClient Client => _client;

            public async Task<HttpResponseMessage> SendAsAppAsync(HttpMethod method, string path, string token, object? data = null)
            {
                using var request = new HttpRequestMessage(method, path);
                if (data is not null)
                {
                    request.Content = JsonContent.Create(data);
                }

                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                return await _client.SendAsync(request);
            }

            public async Task<object?> ScalarAsync(string sql)
            {
                await using var connection = await Fixture.Database.OpenConnectionAsync();
                await using var command = new NpgsqlCommand(sql, connection);
                return await command.ExecuteScalarAsync();
            }

            public async ValueTask DisposeAsync()
            {
                _client.Dispose();
                await _app.DisposeAsync();
                await Fixture.DisposeAsync();
            }
        }

        internal sealed class PlexHandler : HttpMessageHandler
        {
            private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
            public bool Pending { get; set; }
            public bool Unavailable { get; set; }
            public bool ResourcesUnavailable { get; set; }
            public bool Confirmed { get; set; } = true;
            public string LibraryKey { get; set; } = "7";
            public string Email { get; set; } = "plex@example.test";
            public string ServerIdentifier { get; set; } = "connected-server";

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (Unavailable)
                {
                    throw new HttpRequestException("private provider error");
                }

                var uri = request.RequestUri!;
                var clientId = request.Headers.GetValues("X-Plex-Client-Identifier").Single();
                object payload;
                if (uri.AbsolutePath == "/api/v2/pins")
                {
                    using var input = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                    Assert.IsTrue(input.RootElement.GetProperty("strong").GetBoolean());
                    var jwk = input.RootElement.GetProperty("jwk");
                    Assert.AreEqual("EdDSA", jwk.GetProperty("alg").GetString());
                    _keys[clientId] = WebEncoders.Base64UrlDecode(jwk.GetProperty("x").GetString()!);
                    payload = new { id = 321, code = "verified-pin" };
                }
                else if (uri.AbsolutePath == "/api/v2/pins/321")
                {
                    var jwt = QueryHelpers.ParseQuery(uri.Query)["deviceJWT"].ToString().Split('.');
                    using var claims = JsonDocument.Parse(WebEncoders.Base64UrlDecode(jwt[1]));
                    Assert.AreEqual("plex.tv", claims.RootElement.GetProperty("aud").GetString());
                    Assert.AreEqual(clientId, claims.RootElement.GetProperty("iss").GetString());
                    var message = Encoding.ASCII.GetBytes(jwt[0] + "." + jwt[1]);
                    var verifier = new Ed25519Signer();
                    verifier.Init(false, new Ed25519PublicKeyParameters(_keys[clientId]));
                    verifier.BlockUpdate(message, 0, message.Length);
                    Assert.IsTrue(verifier.VerifySignature(WebEncoders.Base64UrlDecode(jwt[2])));
                    payload = new { authToken = Pending ? null : "verified-plex-token" };
                }
                else if (uri.AbsolutePath == "/api/v2/user")
                {
                    Assert.AreEqual("verified-plex-token", request.Headers.GetValues("X-Plex-Token").Single());
                    payload = new { id = "plex-123", email = Email, username = "Plex User", confirmed = Confirmed };
                }
                else if (uri.AbsolutePath == "/api/v2/resources")
                {
                if (ResourcesUnavailable)
                {
                    throw new HttpRequestException("server unavailable");
                }

                    payload = new[] { new { clientIdentifier = "connected-server", provides = "server", accessToken = "scoped-server-token" } };
                }
                else
                {
                    Assert.AreEqual("plex-server.example.test", uri.Host);
                    Assert.AreEqual("scoped-server-token", request.Headers.GetValues("X-Plex-Token").Single());
                    payload = uri.AbsolutePath == "/identity"
                        ? new { MediaContainer = (object)new { machineIdentifier = ServerIdentifier } }
                        : new { MediaContainer = (object)new { Directory = new[] { new { key = LibraryKey } } } };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(payload, options: new JsonSerializerOptions { PropertyNamingPolicy = null })
                };
            }
        }
    }
}
