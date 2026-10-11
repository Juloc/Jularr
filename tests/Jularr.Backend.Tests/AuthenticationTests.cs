using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data.Logic.Account;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Integrations.Plex;
using Jularr.Backend.Service.Core;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Fixture = Jularr.Backend.Tests.ProfileReferenceVerticalTests.ProfileFixture;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class AuthenticationTests
    {
        private const string Password = "test-password-123";

        [TestMethod]
        public async Task Registration_PendingAndDuplicateRequests_DoNotCreateSessionsOrReplacePasswords()
        {
            await using var host = await Host.CreateAsync();
            await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            var request = new RegisterDataV1("new@example.test", "New", Password, 1);

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
            await host.Fixture.RunSqlAsync("UPDATE \"Profiles\" SET \"PinHash\" = 'selection-guard' WHERE \"Id\" = 101");

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

        private sealed class Host : IAsyncDisposable
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
            public Dictionary<string, string> Cookies { get; } = new(StringComparer.Ordinal);

            public static async Task<Host> CreateAsync(bool plexEnabled = false)
            {
                var fixture = await Fixture.CreateAsync();
                var hasher = new PasswordHasher<AccountAuthenticationData>();
                var hash = hasher.HashPassword(new AccountAuthenticationData(41, 1, null, true, false), Password);
                await fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 1 WHERE \"Id\" = 41");
                await using (var connection = await fixture.Database.OpenConnectionAsync())
                await using (var command = new NpgsqlCommand("INSERT INTO \"AccountPasswords\" (\"AccountId\", \"PasswordHash\") VALUES (41, @hash)", connection))
                {
                    command.Parameters.AddWithValue("hash", hash);
                    await command.ExecuteNonQueryAsync();
                }

                var builder = WebApplication.CreateBuilder();
                builder.WebHost.UseTestServer();
                builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
                builder.Services.AddJularrServices(fixture.Database, typeof(ServiceRuntime).Assembly);
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

                var app = builder.Build();
                app.UseExceptionHandler();
                app.UseRateLimiter();
                app.UseAuthentication();
                app.MapJularrServices();
                await app.StartAsync();
                return new Host(fixture, app, plex);
            }

            public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? data = null)
            {
                using var request = new HttpRequestMessage(method, path);
                if (data is not null)
                {
                    request.Content = JsonContent.Create(data);
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

        private sealed class PlexHandler : HttpMessageHandler
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
