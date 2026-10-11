using System.Net;
using System.Net.Http.Json;
using System.Security;
using System.Security.Claims;
using Jularr.Data.User.Profile.V1;
using Jularr.Infrastructure.Sql;
using Jularr.Service.Core;
using Jularr.Service.User.Profile.V1;
using Jularr.Web.Features.Instance;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Fixture = Jularr.Tests.ProfileReferenceVerticalTests.ProfileFixture;

namespace Jularr.Tests;

[TestClass]
public sealed class ProfileServiceGateTests
{
    [TestMethod]
    public async Task Gate_AnonymousLegacyAndUnverifiedSession_AreDenied()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var services = CreateServices(fixture);
        using var scope = services.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var read = scope.ServiceProvider.GetRequiredService<GetProfile>();
        var parameters = new ProfileParameters(fixture.ProfileAPublicId);

        accessor.HttpContext = new DefaultHttpContext();
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => read.ExecuteAsync<ProfileDetails>(parameters));

        accessor.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "Cookies"));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => read.ExecuteAsync<ProfileDetails>(parameters));

        accessor.HttpContext.User = Principal(Guid.NewGuid());
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => read.ExecuteAsync<ProfileDetails>(parameters));

        accessor.HttpContext.User = Principal(fixture.SessionPublicId, "untrusted");
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => read.ExecuteAsync<ProfileDetails>(parameters));
    }

    [TestMethod]
    [DataRow("UPDATE \"AccountSessions\" SET \"RevokedAt\" = now()")]
    [DataRow("UPDATE \"AccountSessions\" SET \"ExpiresAt\" = now() - interval '1 second'")]
    [DataRow("UPDATE \"Accounts\" SET \"CredentialRevision\" = 2 WHERE \"Id\" = 41")]
    [DataRow("UPDATE \"Accounts\" SET \"IsEnabled\" = FALSE WHERE \"Id\" = 41")]
    [DataRow("UPDATE \"AccountSessions\" SET \"ActiveProfileId\" = NULL")]
    [DataRow("UPDATE \"AccountSessions\" SET \"RotationRevision\" = 2")]
    public async Task Gate_InvalidSession_ReadAndUpdateAreDenied(string mutation)
    {
        await using var fixture = await Fixture.CreateAsync();
        using var services = CreateServices(fixture);
        using var scope = services.CreateScope();
        SetPrincipal(scope.ServiceProvider, fixture.SessionPublicId);
        var read = scope.ServiceProvider.GetRequiredService<GetProfile>();
        var rename = scope.ServiceProvider.GetRequiredService<RenameProfile>();
        var parameters = new ProfileParameters(fixture.ProfileAPublicId);
        await read.ExecuteAsync<ProfileDetails>(parameters);

        await fixture.RunSqlAsync(mutation);

        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => read.ExecuteAsync<ProfileDetails>(parameters));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => rename.ExecuteAsync<ProfileDetails>(parameters, new RenameProfileData("Denied")));
        Assert.AreEqual("Profile A", await fixture.NameAsync(fixture.ProfileAId));
    }

    [TestMethod]
    public async Task Gate_MemberWithoutOwnership_CanReadButCannotRename()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.RunSqlAsync("INSERT INTO \"AccountProfiles\" VALUES (42, 101); UPDATE \"Profiles\" SET \"OwnerAccountId\" = 42 WHERE \"Id\" = 101");
        using var services = CreateServices(fixture);
        using var scope = services.CreateScope();
        SetPrincipal(scope.ServiceProvider, fixture.SessionPublicId);
        var parameters = new ProfileParameters(fixture.ProfileAPublicId);

        var read = await scope.ServiceProvider.GetRequiredService<GetProfile>().ExecuteAsync<ProfileDetails>(parameters);
        Assert.AreEqual("Profile A", read.DisplayName);
        await Assert.ThrowsExactlyAsync<SecurityException>(() => scope.ServiceProvider.GetRequiredService<RenameProfile>()
            .ExecuteAsync<ProfileDetails>(parameters, new RenameProfileData("Denied")));
        Assert.AreEqual("Profile A", await fixture.NameAsync(fixture.ProfileAId));
    }

    [TestMethod]
    public async Task Gate_UnknownPermissionForeignResourceAndDisabledModules_AreDenied()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var services = CreateServices(fixture);
        using var scope = services.CreateScope();
        SetPrincipal(scope.ServiceProvider, fixture.SessionPublicId);
        var gate = scope.ServiceProvider.GetRequiredService<IServiceGate>();
        var caller = await gate.RequireCallerAsync(ServiceArea.User, default);

        await Assert.ThrowsExactlyAsync<SecurityException>(() => gate.RequireAccessAsync(caller, new ServicePermission("profile.anything"), [], default));
        await Assert.ThrowsExactlyAsync<SecurityException>(() => gate.RequireAccessAsync(caller, new ServicePermission("profile.self.read"), [new ResourceTarget("profile", 202)], default));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => gate.RequireCallerAsync(ServiceArea.Admin, default));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => gate.RequireModulesAsync(ModuleRequirement.All("Movie"), default));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => gate.RequireModulesAsync(ModuleRequirement.Any("Novel", "Unknown"), default));
        await gate.RequireModulesAsync(ModuleRequirement.Any("Movie", "Novel"), default);
    }

    [TestMethod]
    public async Task Rename_RevocationAfterInitialGate_RollsBackWithoutMutation()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var services = CreateServices(fixture);
        using var scope = services.CreateScope();
        SetPrincipal(scope.ServiceProvider, fixture.SessionPublicId);
        var gate = new RevokingGate(scope.ServiceProvider.GetRequiredService<IServiceGate>(), fixture);
        var rename = new RenameProfile(new ServiceRuntime(fixture.Database, gate));

        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => rename.ExecuteAsync<ProfileDetails>(
            new ProfileParameters(fixture.ProfileAPublicId), new RenameProfileData("Denied")));
        Assert.AreEqual("Profile A", await fixture.NameAsync(fixture.ProfileAId));
    }

    [TestMethod]
    public async Task Endpoints_AuthenticatedReadAndUpdate_UseRealDiGateAndPostgres()
    {
        await using var fixture = await Fixture.CreateAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        builder.Services.AddSingleton<IInstanceModuleService>(new FixedModules());
        builder.Services.AddProfileReferenceServices(fixture.Database);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.MapProfileReferenceEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        var path = "/api/v1/profiles/" + fixture.ProfileAPublicId;

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);

        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("Cookies");
        var ticket = new AuthenticationTicket(Principal(fixture.SessionPublicId), new AuthenticationProperties(), "Cookies");
        var cookie = options.Cookie.Name + "=" + options.TicketDataFormat.Protect(ticket);
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        using var read = await client.GetAsync(path);
        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
        var result = await read.Content.ReadFromJsonAsync<ProfileDetails>();
        Assert.AreEqual(fixture.ProfileAPublicId, result!.PublicId);
        var body = await read.Content.ReadAsStringAsync();
        Assert.IsFalse(body.Contains("accountId", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(body.Contains("ownerAccountId", StringComparison.OrdinalIgnoreCase));

        using var noCsrf = await client.PutAsJsonAsync(path + "/name", new RenameProfileData("Denied"));
        Assert.AreEqual(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        Assert.AreEqual("Profile A", await fixture.NameAsync(fixture.ProfileAId));

        var csrfCookie = read.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", cookie + "; " + csrfCookie);
        client.DefaultRequestHeaders.Add("RequestVerificationToken", read.Headers.GetValues("RequestVerificationToken").Single());
        using var update = await client.PutAsJsonAsync(path + "/name", new RenameProfileData("  Updated  "));
        Assert.AreEqual(HttpStatusCode.OK, update.StatusCode);
        Assert.AreEqual("Updated", (await update.Content.ReadFromJsonAsync<ProfileDetails>())!.DisplayName);
        Assert.AreEqual("Updated", await fixture.NameAsync(fixture.ProfileAId));

        using var foreign = await client.GetAsync("/api/v1/profiles/" + fixture.ProfileBPublicId);
        Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
        await fixture.RunSqlAsync("UPDATE \"AccountSessions\" SET \"RevokedAt\" = now()");
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        await app.StopAsync();
    }

    private static ServiceProvider CreateServices(Fixture fixture)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IInstanceModuleService>(new FixedModules());
        services.AddProfileReferenceServices(fixture.Database);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static void SetPrincipal(IServiceProvider services, Guid sessionId) =>
        services.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext { User = Principal(sessionId) };

    private static ClaimsPrincipal Principal(Guid sessionId, string scheme = "Cookies") =>
        new(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "reference-account"),
            new Claim(ProfileServiceGate.SessionRevisionClaimType, "1"),
            new Claim(ProfileServiceGate.SessionClaimType, sessionId.ToString("D"))], scheme));

    private sealed class FixedModules : IInstanceModuleService
    {
        public Task<InstanceModuleSettings> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(InstanceModuleSettings.Default.With(InstanceModule.Movie, false));

        public async Task<bool> IsEnabledAsync(InstanceModule module, CancellationToken cancellationToken = default) =>
            (await GetAsync(cancellationToken)).IsEnabled(module);

        public Task<InstanceModuleSettings> SetAsync(InstanceModule module, bool enabled, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstanceModuleSettings> SaveAsync(InstanceModuleSettings settings, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RevokingGate(IServiceGate gate, Fixture fixture) : IServiceGate
    {
        public Task RequireModulesAsync(ModuleRequirement modules, CancellationToken cancellationToken) =>
            gate.RequireModulesAsync(modules, cancellationToken);

        public Task<ServiceCaller> RequireCallerAsync(ServiceArea area, CancellationToken cancellationToken) =>
            gate.RequireCallerAsync(area, cancellationToken);

        public async Task RequireAccessAsync(ServiceCaller caller, ServicePermission permission, IReadOnlyList<ResourceTarget> targets, CancellationToken cancellationToken)
        {
            await gate.RequireAccessAsync(caller, permission, targets, cancellationToken);
            await fixture.RunSqlAsync("UPDATE \"AccountSessions\" SET \"RevokedAt\" = now()");
        }

        public Task RequireTransactionalAccessAsync(ServiceCaller caller, ServicePermission permission, IReadOnlyList<ResourceTarget> targets, LogicContext context, CancellationToken cancellationToken) =>
            gate.RequireTransactionalAccessAsync(caller, permission, targets, context, cancellationToken);
    }
}
