using System.Security.Claims;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Tests;

/// <summary>
/// The permissions matrix of docs/INFORMATION_ARCHITECTURE.md §8: which policy each restricted
/// page requires and which roles pass each policy.
/// </summary>
[TestClass]
public sealed class RoleAuthorizationTests
{
    private static readonly Dictionary<string, string> PagePolicies = new(StringComparer.Ordinal)
    {
        ["Jularr.Web.Pages.Acquisition.IndexModel"] = JularrPolicies.AdminMedia,
        ["Jularr.Web.Pages.Admin.BookManualSearchModel"] = JularrPolicies.AdminMedia,
        ["Jularr.Web.Pages.Admin.HistoryModel"] = JularrPolicies.AdminMedia,
        ["Jularr.Web.Pages.Admin.IndexModel"] = JularrPolicies.AdminMedia,
        ["Jularr.Web.Pages.Admin.ManualSearchModel"] = JularrPolicies.AdminMedia,
        ["Jularr.Web.Pages.Admin.ReadingManualSearchModel"] = JularrPolicies.AdminMedia,
        ["Jularr.Web.Pages.Admin.Music.IndexModel"] = JularrPolicies.AdminMedia,
        ["Jularr.Web.Pages.Admin.Music.ArtistModel"] = JularrPolicies.AdminMedia,
        ["Jularr.Web.Pages.Admin.Music.AlbumModel"] = JularrPolicies.AdminMedia,
        ["Jularr.Web.Pages.Admin.AcquisitionProfilesModel"] = JularrPolicies.AcquisitionSettings,
        ["Jularr.Web.Pages.Admin.LogsModel"] = JularrPolicies.AdminMedia,
        ["Jularr.Web.Pages.Admin.MediaDetailModel"] = JularrPolicies.AdminMedia,
        ["Jularr.Web.Pages.Admin.MediaWorkModel"] = JularrPolicies.AdminMedia,
        ["Jularr.Web.Pages.Admin.OperationModel"] = JularrPolicies.AdminMedia,
        ["Jularr.Web.Pages.Admin.OperationsModel"] = JularrPolicies.AdminMedia,
        ["Jularr.Web.Pages.Admin.Reconciliation.IndexModel"] = JularrPolicies.AdminSystem,
        ["Jularr.Web.Pages.Admin.RequestsModel"] = JularrPolicies.AdminMedia,
        ["Jularr.Web.Pages.Admin.ResourcesModel"] = JularrPolicies.AdminMedia,
        ["Jularr.Web.Pages.Admin.ScansModel"] = JularrPolicies.AdminMedia,
        ["Jularr.Web.Pages.Admin.SessionsModel"] = JularrPolicies.SessionsStopOthers,
        ["Jularr.Web.Pages.Admin.Storage.IndexModel"] = JularrPolicies.AdminMedia,
        ["Jularr.Web.Pages.Admin.SubtitlesModel"] = JularrPolicies.AdminMedia,
        ["Jularr.Web.Pages.Admin.WantedModel"] = JularrPolicies.AdminMedia,
        ["Jularr.Web.Pages.Library.AnimeRepairModel"] = JularrPolicies.AdminMedia,
        ["Jularr.Web.Pages.Settings.SubtitlesModel"] = JularrPolicies.AdminMedia,

        ["Jularr.Web.Pages.Admin.ReadingSourcesModel"] = JularrPolicies.AcquisitionSettings,
        ["Jularr.Web.Pages.Admin.UsenetModel"] = JularrPolicies.AcquisitionSettings,
        ["Jularr.Web.Pages.Settings.AcquisitionModel"] = JularrPolicies.AcquisitionSettings,
        ["Jularr.Web.Pages.Settings.NamingModel"] = JularrPolicies.AcquisitionSettings,
        ["Jularr.Web.Pages.Settings.ReadingNamingModel"] = JularrPolicies.AcquisitionSettings,
        ["Jularr.Web.Pages.Settings.DownloadClients.EditModel"] = JularrPolicies.AcquisitionSettings,
        ["Jularr.Web.Pages.Settings.DownloadClients.IndexModel"] = JularrPolicies.AcquisitionSettings,
        ["Jularr.Web.Pages.Settings.Indexers.EditModel"] = JularrPolicies.AcquisitionSettings,
        ["Jularr.Web.Pages.Settings.Indexers.IndexModel"] = JularrPolicies.AcquisitionSettings,

        ["Jularr.Web.Pages.Settings.MappingReviewModel"] = JularrPolicies.MappingEdit,
        ["Jularr.Web.Pages.Settings.MappingSegmentsModel"] = JularrPolicies.MappingEdit,
        ["Jularr.Web.Pages.Admin.MergeReview.IndexModel"] = JularrPolicies.MappingEdit,

        ["Jularr.Web.Pages.Library.RenameModel"] = JularrPolicies.MediaRename,

        ["Jularr.Web.Pages.Admin.AiModel"] = JularrPolicies.AdminSystem,
        ["Jularr.Web.Pages.Admin.AppearanceModel"] = JularrPolicies.AdminSystem,
        ["Jularr.Web.Pages.Admin.Capabilities.IndexModel"] = JularrPolicies.AdminSystem,
        ["Jularr.Web.Pages.Admin.DevicesModel"] = JularrPolicies.AdminSystem,
        ["Jularr.Web.Pages.Admin.HealthModel"] = JularrPolicies.AdminSystem,
        ["Jularr.Web.Pages.Admin.InstanceModel"] = JularrPolicies.AdminSystem,
        ["Jularr.Web.Pages.Admin.ProvidersModel"] = JularrPolicies.AdminSystem,
        ["Jularr.Web.Pages.Account.SetupProviderModel"] = JularrPolicies.AdminSystem,
        ["Jularr.Web.Pages.Admin.RolesModel"] = JularrPolicies.AdminSystem,
        ["Jularr.Web.Pages.Admin.SonarrModel"] = JularrPolicies.AdminSystem,
        ["Jularr.Web.Pages.Admin.SystemModel"] = JularrPolicies.AdminSystem,
        ["Jularr.Web.Pages.Admin.TranscodingModel"] = JularrPolicies.AdminSystem,
        ["Jularr.Web.Pages.Admin.UserModel"] = JularrPolicies.AdminSystem,
        ["Jularr.Web.Pages.Admin.UsersModel"] = JularrPolicies.AdminSystem,
        ["Jularr.Web.Pages.LocalizationAdmin.IndexModel"] = JularrPolicies.AdminSystem,
        ["Jularr.Web.Pages.Settings.ApiKeys.IndexModel"] = JularrPolicies.AdminSystem,
        ["Jularr.Web.Pages.Settings.SonarrModel"] = JularrPolicies.AdminSystem,
        ["Jularr.Web.Pages.Settings.SonarrMigrationModel"] = JularrPolicies.AdminSystem,
        ["Jularr.Web.Pages.Settings.UserModel"] = JularrPolicies.AdminSystem,
        ["Jularr.Web.Pages.Settings.UsersModel"] = JularrPolicies.AdminSystem
    };

    private static readonly Dictionary<string, AccountRole[]> ExpectedRoles = new(StringComparer.Ordinal)
    {
        [JularrPolicies.AdminMedia] = [AccountRole.Owner, AccountRole.MediaManager],
        [JularrPolicies.AdminSystem] = [AccountRole.Owner],
        [JularrPolicies.MediaDelete] = [AccountRole.Owner],
        [JularrPolicies.MediaRename] = [AccountRole.Owner],
        [JularrPolicies.MappingEdit] = [AccountRole.Owner, AccountRole.MediaManager],
        [JularrPolicies.AcquisitionSettings] = [AccountRole.Owner, AccountRole.MediaManager],
        [JularrPolicies.SessionsStopOthers] = [AccountRole.Owner, AccountRole.MediaManager]
    };

    [TestMethod]
    public void EveryRestrictedPageUsesANamedPolicyNotARole()
    {
        var restricted = typeof(JularrPolicies).Assembly.GetTypes()
            .Where(type => typeof(PageModel).IsAssignableFrom(type) && !type.IsAbstract)
            .Select(type => (type, attributes: type.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>()
                .ToArray()))
            .Where(page => page.attributes.Any(attribute => !string.IsNullOrEmpty(attribute.Roles) || !string.IsNullOrEmpty(attribute.Policy)))
            .ToArray();

        Assert.IsTrue(restricted.Length >= PagePolicies.Count, "Expected every restricted page to be found.");
        foreach (var (type, attributes) in restricted)
        {
            foreach (var attribute in attributes)
            {
                Assert.IsTrue(string.IsNullOrEmpty(attribute.Roles), $"{type.Name} must use a policy, not Roles = {attribute.Roles}.");
                Assert.IsNotNull(attribute.Policy, $"{type.Name} must name a policy.");
                Assert.IsTrue(JularrPolicies.Roles.ContainsKey(attribute.Policy), $"{type.Name} uses unknown policy {attribute.Policy}.");
            }
        }

        CollectionAssert.AreEquivalent(
            PagePolicies.Keys.ToArray(),
            restricted.Select(page => page.type.FullName!).ToArray(),
            "A restricted page was added or removed; update the permissions matrix.");
        foreach (var (type, attributes) in restricted)
        {
            Assert.AreEqual(PagePolicies[type.FullName!], attributes.Single().Policy, type.FullName);
        }
    }

    [TestMethod]
    public void PolicyTableMatchesThePermissionsMatrix()
    {
        CollectionAssert.AreEquivalent(ExpectedRoles.Keys.ToArray(), JularrPolicies.Roles.Keys.ToArray());
        foreach (var (policy, roles) in ExpectedRoles)
        {
            CollectionAssert.AreEquivalent(roles, JularrPolicies.Roles[policy].ToArray(), policy);
        }
    }

    [TestMethod]
    public async Task RegisteredPoliciesAdmitExactlyTheirRolesAsync()
    {
        await using var services = CreateServices();
        var authorization = services.GetRequiredService<IAuthorizationService>();

        foreach (var (policy, roles) in ExpectedRoles)
        {
            foreach (var role in Enum.GetValues<AccountRole>())
            {
                var user = Principal(role);
                var expected = roles.Contains(role);
                var result = await authorization.AuthorizeAsync(user, policy);

                Assert.AreEqual(expected, result.Succeeded, $"{role} on {policy}");
                Assert.AreEqual(expected, JularrPolicies.Allows(user, policy), $"Allows: {role} on {policy}");
                Assert.AreEqual(expected, CurrentAccount(user).Can(policy), $"Can: {role} on {policy}");
            }

            Assert.IsFalse((await authorization.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), policy)).Succeeded);
            Assert.IsFalse(JularrPolicies.Allows(new ClaimsPrincipal(new ClaimsIdentity()), policy));
        }

        Assert.ThrowsExactly<ArgumentException>(() => JularrPolicies.Allows(Principal(AccountRole.Owner), "admin.unknown"));
    }

    [TestMethod]
    [DataRow(typeof(Jularr.Web.Pages.Admin.UsersModel), false)]
    [DataRow(typeof(Jularr.Web.Pages.Admin.SystemModel), false)]
    [DataRow(typeof(Jularr.Web.Pages.Admin.HealthModel), false)]
    [DataRow(typeof(Jularr.Web.Pages.Admin.TranscodingModel), false)]
    [DataRow(typeof(Jularr.Web.Pages.Settings.ApiKeys.IndexModel), false)]
    [DataRow(typeof(Jularr.Web.Pages.Library.RenameModel), false)]
    [DataRow(typeof(Jularr.Web.Pages.Admin.OperationsModel), true)]
    [DataRow(typeof(Jularr.Web.Pages.Admin.RequestsModel), true)]
    [DataRow(typeof(Jularr.Web.Pages.Settings.MappingReviewModel), true)]
    [DataRow(typeof(Jularr.Web.Pages.Settings.Indexers.IndexModel), true)]
    public async Task MediaManagerIsForbiddenOnSystemPagesAndAllowedOnMediaPagesAsync(Type page, bool allowed)
    {
        var result = await EvaluatePageAsync(page, AccountRole.MediaManager);

        Assert.AreEqual(allowed, result.Succeeded, page.Name);
        Assert.AreEqual(!allowed, result.Forbidden, $"{page.Name} answers 403 when denied.");
    }

    [TestMethod]
    public async Task UserIsForbiddenOnEveryRestrictedPageAndOwnerAllowedAsync()
    {
        foreach (var name in PagePolicies.Keys)
        {
            var page = typeof(JularrPolicies).Assembly.GetType(name, throwOnError: true)!;
            Assert.IsTrue((await EvaluatePageAsync(page, AccountRole.User)).Forbidden, $"User on {page.FullName}");
            Assert.IsTrue((await EvaluatePageAsync(page, AccountRole.Owner)).Succeeded, $"Owner on {page.FullName}");
        }
    }

    [TestMethod]
    public async Task OwnerAssignsUserAndMediaManagerButStaysTheOnlyOwnerAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jularr-roles-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={path};Foreign Keys=True")
                .Options);
            await DatabaseMigrationBridge.UpgradeAsync(db);
            var auth = new OwnerAuthService(db, new PasswordHasher<OwnerAccount>());
            var owner = await auth.CreateOwnerAsync("owner", "a sufficiently long owner password");
            var user = await auth.CreateUserAsync("helper", "a sufficiently long user password");

            await auth.SetRoleAsync(user.Id, AccountRole.MediaManager);
            Assert.AreEqual(AccountRole.MediaManager, (await auth.GetAsync(user.Id))!.Role);
            var signedIn = await auth.ValidateCredentialsAsync("helper", "a sufficiently long user password");
            Assert.IsTrue(OwnerAuthService.CreatePrincipal(signedIn!).IsInRole(AccountRoles.MediaManager));

            await auth.SetRoleAsync(user.Id, AccountRole.User);
            Assert.AreEqual(AccountRole.User, (await auth.GetAsync(user.Id))!.Role);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => auth.SetRoleAsync(user.Id, AccountRole.Owner));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => auth.SetRoleAsync(owner.Id, AccountRole.User));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => auth.SetRoleAsync(owner.Id, AccountRole.MediaManager));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => auth.SetRoleAsync("missing", AccountRole.User));

            Assert.AreEqual(AccountRole.Owner, (await auth.GetAsync(owner.Id))!.Role);
            Assert.AreEqual(1, (await auth.ListAsync()).Count(account => account.Role == AccountRole.Owner));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void EveryNamedPolicyHasADisplayName()
    {
        var keys = JularrPolicies.Roles.Keys
            .Select(JularrPolicies.LabelKey)
            .ToArray();

        Assert.AreEqual(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        foreach (var key in keys)
        {
            Assert.IsTrue(
                Jularr.Web.Features.Localization.UiTranslationResources.TryGet(key, out _),
                $"Missing UI text {key}.");
        }

        Assert.ThrowsExactly<ArgumentException>(
            () => JularrPolicies.LabelKey("admin.unknown"));
    }

    [TestMethod]
    public void EveryRoleHasADisplayName()
    {
        var keys = Enum.GetValues<AccountRole>().Select(AccountRoles.LabelKey).ToArray();

        Assert.AreEqual(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        foreach (var key in keys)
        {
            Assert.IsTrue(
                Jularr.Web.Features.Localization.UiTranslationResources.TryGet(key, out _),
                $"Missing UI text {key}.");
        }
    }

    private static async Task<PolicyAuthorizationResult> EvaluatePageAsync(Type page, AccountRole role)
    {
        await using var services = CreateServices();
        var authorizeData = page.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<IAuthorizeData>()
            .ToArray();
        var policy = await AuthorizationPolicy.CombineAsync(
            services.GetRequiredService<IAuthorizationPolicyProvider>(),
            authorizeData);
        Assert.IsNotNull(policy, page.Name);

        var user = Principal(role);
        var context = new DefaultHttpContext { User = user, RequestServices = services };
        var evaluator = new PolicyEvaluator(services.GetRequiredService<IAuthorizationService>());
        var authentication = AuthenticateResult.Success(new AuthenticationTicket(user, "test"));

        return await evaluator.AuthorizeAsync(policy, authentication, context, resource: null);
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(JularrPolicies.Register);
        return services.BuildServiceProvider();
    }

    private static ClaimsPrincipal Principal(AccountRole role) =>
        OwnerAuthService.CreatePrincipal(new OwnerAccount
        {
            Id = $"{role}-account",
            UserName = role.ToString(),
            Role = role
        });

    private static CurrentAccountContext CurrentAccount(ClaimsPrincipal user) =>
        new(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } });
}
