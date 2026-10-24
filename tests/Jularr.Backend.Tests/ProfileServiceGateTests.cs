using Jularr.Backend.Tests.Infrastructure;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security;
using System.Security.Claims;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.User.Profile.V1;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Fixture = Jularr.Backend.Tests.ProfileReferenceVerticalTests.ProfileFixture;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ProfileServiceGateTests
    {
        [TestMethod]
        public async Task ExecuteReadBatches_SameAccountCanReadConcurrentlyWithoutAuthorizationLocks()
        {
            await using var fixture = await Fixture.CreateAsync();
            using var services = CreateServices(fixture);
            using var firstScope = services.CreateScope();
            using var secondScope = services.CreateScope();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SetPrincipal(firstScope.ServiceProvider, fixture.SessionPublicId, fixture.SessionToken);
            var first = firstScope.ServiceProvider.GetRequiredService<ServiceRuntime>().Create(new ReadBatchProbe(whileReading: async ct =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(ct);
            }));
            var pending = first.ExecuteAsync<ReadProfileResultV1>(new ReadProfileParametersV1(fixture.ProfileAId), default(NoData), timeout.Token);
            try
            {
                await entered.Task.WaitAsync(timeout.Token);
                SetPrincipal(secondScope.ServiceProvider, fixture.SessionPublicId, fixture.SessionToken);
                var second = secondScope.ServiceProvider.GetRequiredService<ServiceRuntime>().Create(new ReadBatchProbe());
                var result = await second.ExecuteAsync<ReadProfileResultV1>(new ReadProfileParametersV1(fixture.ProfileAId), default(NoData), timeout.Token);
                Assert.AreEqual(fixture.ProfileAId, result.Id);
                Assert.IsFalse(pending.IsCompleted, "The second read must finish while the first read is still open.");
                await using var connection = await fixture.Database.OpenConnectionAsync(timeout.Token);
                await using var transaction = await connection.BeginTransactionAsync(timeout.Token);
                await using var command = new Npgsql.NpgsqlCommand("SELECT \"Id\" FROM \"Accounts\" WHERE \"Id\" = 41 FOR NO KEY UPDATE NOWAIT", connection, transaction);
                Assert.AreEqual(41L, await command.ExecuteScalarAsync(timeout.Token));
                await transaction.CommitAsync(timeout.Token);
            }
            finally
            {
                release.TrySetResult();
                await pending;
            }
        }

        [TestMethod]
        [DataRow("UPDATE \"AccountSessions\" SET \"RevokedAt\" = now()")]
        [DataRow("UPDATE \"Accounts\" SET \"IsEnabled\" = FALSE WHERE \"Id\" = 41")]
        [DataRow("UPDATE \"AccountSessions\" SET \"ActiveProfileId\" = NULL")]
        public async Task ExecuteReadBatch_AuthorizationChangedDuringExternalWork_DeniesBusinessRead(string mutation)
        {
            await using var fixture = await Fixture.CreateAsync();
            using var services = CreateServices(fixture);
            using var scope = services.CreateScope();
            SetPrincipal(scope.ServiceProvider, fixture.SessionPublicId, fixture.SessionToken);
            var service = scope.ServiceProvider.GetRequiredService<ServiceRuntime>().Create(new ReadBatchProbe(beforeReading: _ => fixture.RunSqlAsync(mutation)));
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => service.ExecuteAsync<ReadProfileResultV1>(new ReadProfileParametersV1(fixture.ProfileAId), default(NoData)));
            Assert.IsFalse(service.BusinessRead, "Authorization must fail before the business SELECT.");
        }

        [TestMethod]
        public async Task Gate_AnonymousLegacyAndUnverifiedSession_AreDenied()
        {
            await using var fixture = await Fixture.CreateAsync();
            using var services = CreateServices(fixture);
            using var scope = services.CreateScope();
            var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
            var read = scope.ServiceProvider.GetRequiredService<ReadProfileV1>();
            var parameters = new ReadProfileParametersV1(fixture.ProfileAId);

            accessor.HttpContext = new DefaultHttpContext();
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => read.ExecuteAsync<ReadProfileResultV1>(parameters));

            accessor.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "Cookies"));
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => read.ExecuteAsync<ReadProfileResultV1>(parameters));

            accessor.HttpContext.User = Principal(Guid.NewGuid());
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => read.ExecuteAsync<ReadProfileResultV1>(parameters));

            accessor.HttpContext.User = Principal(fixture.SessionPublicId, "untrusted");
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => read.ExecuteAsync<ReadProfileResultV1>(parameters));

            accessor.HttpContext.User = Principal(fixture.SessionPublicId, sessionToken: new string('a', 43));
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => read.ExecuteAsync<ReadProfileResultV1>(parameters));
        }

        [TestMethod]
        [DataRow("UPDATE \"AccountSessions\" SET \"RevokedAt\" = now()")]
        [DataRow("UPDATE \"AccountSessions\" SET \"CreatedAt\" = now() - interval '2 hours', \"ExpiresAt\" = now() - interval '1 second'")]
        [DataRow("UPDATE \"Accounts\" SET \"CredentialRevision\" = 2 WHERE \"Id\" = 41")]
        [DataRow("UPDATE \"Accounts\" SET \"IsEnabled\" = FALSE WHERE \"Id\" = 41")]
        [DataRow("UPDATE \"AccountSessions\" SET \"ActiveProfileId\" = NULL")]
        [DataRow("UPDATE \"AccountSessions\" SET \"RotationRevision\" = 2")]
        public async Task Gate_InvalidSession_ReadAndUpdateAreDenied(string mutation)
        {
            await using var fixture = await Fixture.CreateAsync();
            using var services = CreateServices(fixture);
            using var scope = services.CreateScope();
            SetPrincipal(scope.ServiceProvider, fixture.SessionPublicId, fixture.SessionToken);
            var read = scope.ServiceProvider.GetRequiredService<ReadProfileV1>();
            var rename = scope.ServiceProvider.GetRequiredService<UpdateProfileV1>();
            var parameters = new ReadProfileParametersV1(fixture.ProfileAId);
            await read.ExecuteAsync<ReadProfileResultV1>(parameters);

            await fixture.RunSqlAsync(mutation);

            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => read.ExecuteAsync<ReadProfileResultV1>(parameters));
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => rename.ExecuteAsync<UpdateResult>(new UpdateProfileParametersV1(parameters.ProfileId), new UpdateProfileDataV1("Denied")));
            Assert.AreEqual("Profile A", await fixture.NameAsync(fixture.ProfileAId));
        }

        [TestMethod]
        public async Task Gate_MemberWithoutOwnership_CanReadButCannotRename()
        {
            await using var fixture = await Fixture.CreateAsync();
            await fixture.RunSqlAsync("INSERT INTO \"AccountProfiles\" (\"AccountId\", \"ProfileId\") VALUES (42, 101); UPDATE \"Profiles\" SET \"OwnerAccountId\" = 42 WHERE \"Id\" = 101");
            using var services = CreateServices(fixture);
            using var scope = services.CreateScope();
            SetPrincipal(scope.ServiceProvider, fixture.SessionPublicId, fixture.SessionToken);
            var parameters = new ReadProfileParametersV1(fixture.ProfileAId);

            var read = await scope.ServiceProvider.GetRequiredService<ReadProfileV1>().ExecuteAsync<ReadProfileResultV1>(parameters);
            Assert.AreEqual("Profile A", read.DisplayName);
            await Assert.ThrowsExactlyAsync<SecurityException>(() => scope.ServiceProvider.GetRequiredService<UpdateProfileV1>()
                .ExecuteAsync<UpdateResult>(new UpdateProfileParametersV1(parameters.ProfileId), new UpdateProfileDataV1("Denied")));
            Assert.AreEqual("Profile A", await fixture.NameAsync(fixture.ProfileAId));
        }

        [TestMethod]
        public async Task Gate_UnknownPermissionForeignResourceAndDisabledModules_AreDenied()
        {
            await using var fixture = await Fixture.CreateAsync();
            using var services = CreateServices(fixture);
            using var scope = services.CreateScope();
            SetPrincipal(scope.ServiceProvider, fixture.SessionPublicId, fixture.SessionToken);
            var gate = scope.ServiceProvider.GetRequiredService<IServiceGate>();
            var caller = await gate.RequireCallerAsync(ServiceArea.User, default);

            await Assert.ThrowsExactlyAsync<SecurityException>(() => gate.RequireAccessAsync(caller, new ServicePermission("profile.anything"), default));
            await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => scope.ServiceProvider.GetRequiredService<ReadProfileV1>().ExecuteAsync<ReadProfileResultV1>(new ReadProfileParametersV1(fixture.ProfileBId)));
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => gate.RequireCallerAsync(ServiceArea.Admin, default));
            using var schema = typeof(ServiceApi).Assembly.GetManifestResourceStream("Jularr.Database.Instance.sql")!;
            using var reader = new StreamReader(schema);
            await fixture.RunSqlAsync(await reader.ReadToEndAsync());
            await using var sql = new SqlContext(fixture.Database);
            sql.PrepareReadTransaction(System.Data.IsolationLevel.ReadCommitted);
            var context = new LogicContext(sql);
            await Assert.ThrowsExactlyAsync<SecurityException>(() => gate.RequireModulesAsync(ModuleRequirement.All("Movie"), context, false, default));
            await Assert.ThrowsExactlyAsync<SecurityException>(() => gate.RequireModulesAsync(ModuleRequirement.Any("Novel", "Unknown"), context, false, default));
            await Assert.ThrowsExactlyAsync<SecurityException>(() => gate.RequireModulesAsync(ModuleRequirement.Any("Movie", "Novel"), context, false, default));
        }

        [TestMethod]
        [DataRow(AccountRoleType.User)]
        [DataRow(AccountRoleType.MediaManager)]
        [DataRow(AccountRoleType.Owner)]
        public async Task Gate_AdministrativeRoles_GrantOnlyTheDeclaredScope(AccountRoleType role)
        {
            await using var fixture = await Fixture.CreateAsync();
            await fixture.RunSqlAsync($"UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = {(byte)role} WHERE \"Id\" = 41");
            using var services = CreateServices(fixture);
            using var scope = services.CreateScope();
            SetPrincipal(scope.ServiceProvider, fixture.SessionPublicId, fixture.SessionToken);
            var gate = scope.ServiceProvider.GetRequiredService<IServiceGate>();
            if (role == AccountRoleType.User)
            {
                await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => gate.RequireCallerAsync(ServiceArea.Admin, default));
                return;
            }

            var caller = await gate.RequireCallerAsync(ServiceArea.Admin, default);
            Assert.AreEqual(role, caller.AccountRoleTypeId);
            foreach (var permission in new[] { "media.read", "media.manual.update", "requests.update", "wanted.update" })
            {
                await gate.RequireAccessAsync(caller, new ServicePermission(permission), default);
            }

            foreach (var permission in new[] { "accounts.update", "storage.update", "providers.update", "localization.update", "rules.update", "acquisition.manage" })
            {
                if (role == AccountRoleType.Owner) await gate.RequireAccessAsync(caller, new ServicePermission(permission), default);
                else await Assert.ThrowsExactlyAsync<SecurityException>(() => gate.RequireAccessAsync(caller, new ServicePermission(permission), default));
            }

            await Assert.ThrowsExactlyAsync<SecurityException>(() => gate.RequireAccessAsync(caller, new ServicePermission("unknown.permission"), default));
            if (role == AccountRoleType.MediaManager)
            {
                await Assert.ThrowsExactlyAsync<SecurityException>(() => gate.RequireAccessAsync(caller with { AccountRoleTypeId = AccountRoleType.Owner }, new ServicePermission("accounts.update"), default));
            }
        }

        [TestMethod]
        [DataRow("UPDATE \"AccountSessions\" SET \"RevokedAt\" = now()")]
        [DataRow("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41")]
        public async Task Rename_AuthorizationChangeAfterInitialGate_RollsBackWithoutMutation(string mutation)
        {
            await using var fixture = await Fixture.CreateAsync();
            using var services = CreateServices(fixture);
            using var scope = services.CreateScope();
            SetPrincipal(scope.ServiceProvider, fixture.SessionPublicId, fixture.SessionToken);
            var gate = new AuthorizationChangingGate(scope.ServiceProvider.GetRequiredService<IServiceGate>(), fixture, mutation);
            var rename = new ServiceRuntime(fixture.Database, gate, new ServiceTimings(TimeProvider.System)).Create<UpdateProfileV1>();

            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => rename.ExecuteAsync<UpdateResult>(
                new UpdateProfileParametersV1(fixture.ProfileAId), new UpdateProfileDataV1("Denied")));
            Assert.AreEqual("Profile A", await fixture.NameAsync(fixture.ProfileAId));
        }

        [TestMethod]
        public async Task Endpoints_AuthenticatedReadAndUpdate_UseRealDiGateAndPostgres()
        {
            await using var fixture = await Fixture.CreateAsync();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
            builder.Configuration["JULARR_DB"] = fixture.ConnectionString;
            builder.Services.AddJularrServices(builder.Configuration).WithTestInstalledSchema(fixture.ConnectionString);
            await using var app = builder.Build();
            app.UseExceptionHandler();
            app.UseRateLimiter();
            app.UseAuthentication();
            app.MapJularrServices();
            await app.StartAsync();
            using var client = app.GetTestClient();
            var path = "/api/v1/profiles/" + fixture.ProfileAId;

            Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);

            var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("Cookies");
            var ticket = new AuthenticationTicket(Principal(fixture.SessionPublicId, sessionToken: fixture.SessionToken), new AuthenticationProperties(), "Cookies");
            var cookie = options.Cookie.Name + "=" + options.TicketDataFormat.Protect(ticket);
            client.DefaultRequestHeaders.Add("Cookie", cookie);
            using var read = await client.GetAsync(path);
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
            var result = await read.Content.ReadFromJsonAsync<ReadProfileResultV1>();
            Assert.AreEqual(fixture.ProfileAId, result!.Id);
            Assert.AreEqual(1L, result.UiLocaleId);
            var body = await read.Content.ReadAsStringAsync();
            Assert.IsFalse(body.Contains("accountId", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(body.Contains("ownerAccountId", StringComparison.OrdinalIgnoreCase));

            using var noCsrf = await client.PutAsJsonAsync(path, new UpdateProfileDataV1("Denied"));
            Assert.AreEqual(HttpStatusCode.BadRequest, noCsrf.StatusCode);
            Assert.AreEqual("Profile A", await fixture.NameAsync(fixture.ProfileAId));

            var csrfCookie = read.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Add("Cookie", cookie + "; " + csrfCookie);
            client.DefaultRequestHeaders.Add("RequestVerificationToken", read.Headers.GetValues("RequestVerificationToken").Single());
            using var invalidData = await client.PutAsJsonAsync(path, new UpdateProfileDataV1(" "));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidData.StatusCode);
            Assert.IsTrue((await invalidData.Content.ReadAsStringAsync()).Contains("invalid_data", StringComparison.Ordinal));
            Assert.AreEqual("Profile A", await fixture.NameAsync(fixture.ProfileAId));

            using var update = await client.PutAsJsonAsync(path, new UpdateProfileDataV1("  Updated  "));
            Assert.AreEqual(HttpStatusCode.OK, update.StatusCode);
            Assert.IsTrue((await update.Content.ReadFromJsonAsync<UpdateResult>())!.Success);
            Assert.AreEqual("Updated", await fixture.NameAsync(fixture.ProfileAId));

            using var locale = await client.PutAsJsonAsync(path, new UpdateProfileDataV1 { UiLocaleId = 2 });
            Assert.AreEqual(HttpStatusCode.OK, locale.StatusCode);
            var afterLocale = await client.GetFromJsonAsync<ReadProfileResultV1>(path);
            Assert.AreEqual("Updated", afterLocale!.DisplayName);
            Assert.AreEqual(2L, afterLocale.UiLocaleId);

            using var unknownLocale = await client.PutAsJsonAsync(path, new UpdateProfileDataV1 { DisplayName = "Not applied", UiLocaleId = 999 });
            Assert.AreEqual(HttpStatusCode.BadRequest, unknownLocale.StatusCode);
            Assert.IsTrue((await unknownLocale.Content.ReadAsStringAsync()).Contains("unknown_reference", StringComparison.Ordinal));
            var afterInvalidLocale = await client.GetFromJsonAsync<ReadProfileResultV1>(path);
            Assert.AreEqual("Updated", afterInvalidLocale!.DisplayName);
            Assert.AreEqual(2L, afterInvalidLocale.UiLocaleId);

            using var foreign = await client.GetAsync("/api/v1/profiles/" + fixture.ProfileBId);
            Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
            await fixture.RunSqlAsync("UPDATE \"AccountSessions\" SET \"RevokedAt\" = now()");
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
            await app.StopAsync();
        }

        private sealed class ReadBatchProbe(Func<CancellationToken, Task>? beforeReading = null, Func<CancellationToken, Task>? whileReading = null) : UserService<ReadProfileParametersV1, NoData>
        {
            public bool BusinessRead { get; private set; }
            protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
            protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ReadProfileResultV1>()];
            protected override ModuleRequirement GetInstanceModules(ReadProfileParametersV1 parameters) => ModuleRequirement.None;
            protected override ServicePermission GetPermission(ReadProfileParametersV1 parameters, Type resultType) => new("profile.self.read");

            protected override async Task<IServiceOutput> OnExecute()
            {
                if (beforeReading is not null)
                {
                    await beforeReading(CancellationToken);
                }
                return await Context.ExecuteBatchAsync(async (ctx, ct) =>
                {
                    BusinessRead = true;
                    var sql = new SqlStatement();
                    sql.Statement = "SELECT \"DisplayName\" FROM \"Profiles\" WHERE \"Id\" = @ProfileId";
                    sql.Parameters.Add(Parameters);
                    var name = (string)(await ctx.ReadSql.ExecuteScalarAsync(sql, ct))!;
                    if (whileReading is not null)
                    {
                        await whileReading(ct);
                    }
                    return new ReadProfileResultV1(Parameters.ProfileId, name, 1, false);
                }, CancellationToken, mode: SqlAccessMode.ReadOnly);
            }
        }

        private static ServiceProvider CreateServices(Fixture fixture)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddJularrServices(fixture.Database, typeof(ServiceRuntime).Assembly);
            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        }

        private static void SetPrincipal(IServiceProvider services, Guid sessionId, string sessionToken) =>
            services.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext { User = Principal(sessionId, sessionToken: sessionToken) };

        private static ClaimsPrincipal Principal(Guid sessionId, string scheme = "Cookies", string? sessionToken = null) =>
            new(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "reference-account"),
                new Claim(ServiceGate.SessionRevisionClaimType, "1"),
                new Claim(ServiceGate.SessionTokenClaimType, sessionToken ?? new string('a', 43)),
                new Claim(ServiceGate.SessionClaimType, sessionId.ToString("D"))], scheme));

        private sealed class AuthorizationChangingGate(IServiceGate gate, Fixture fixture, string mutation) : IServiceGate
        {
            public Task RequireModulesAsync(ModuleRequirement modules, LogicContext context, bool canMutate, CancellationToken cancellationToken) =>
                gate.RequireModulesAsync(modules, context, canMutate, cancellationToken);

            public Task<ServiceCaller> RequireCallerAsync(ServiceArea area, CancellationToken cancellationToken) =>
                gate.RequireCallerAsync(area, cancellationToken);

            public async Task RequireAccessAsync(ServiceCaller caller, ServicePermission permission, CancellationToken cancellationToken)
            {
                await gate.RequireAccessAsync(caller, permission, cancellationToken);
                await fixture.RunSqlAsync(mutation);
            }

            public Task RequireTransactionalAccessAsync(ServiceCaller caller, ServicePermission permission, LogicContext context, CancellationToken cancellationToken, SqlAccessMode mode = SqlAccessMode.ReadWrite) =>
                gate.RequireTransactionalAccessAsync(caller, permission, context, cancellationToken, mode);
        }
    }
}
