using System.Security.Claims;
using System.Threading.RateLimiting;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Providers;
using Jularr.Web.Pages.Account;
using Jularr.Web.Pages.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;

namespace Jularr.Tests;

/// <summary>The shared Provider UI handlers of Admin → Providers and the Setup provider step: who may use them, what they do and what they refuse.</summary>
[TestClass]
public sealed class ProviderSettingsPageTests
{
    private sealed class FakeProvider(ProviderView initial) : IProviderSettings
    {
        public string Key => View.Key;

        public ProviderView View { get; set; } = initial;

        public List<string> Calls { get; } = [];

        public Dictionary<string, string?>? LastSecrets { get; private set; }

        public Task<ProviderView> GetViewAsync(CancellationToken cancellationToken) => Task.FromResult(View);

        public Task<ProviderFeedback> SaveAsync(bool enabled, IReadOnlyDictionary<string, string?> secrets, CancellationToken cancellationToken)
        {
            Calls.Add("save:" + enabled);
            LastSecrets = new Dictionary<string, string?>(secrets);
            return Task.FromResult(ProviderFeedback.Saved);
        }

        public Task<ProviderFeedback> TestAsync(IReadOnlyDictionary<string, string?> secrets, CancellationToken cancellationToken)
        {
            Calls.Add("test");
            LastSecrets = new Dictionary<string, string?>(secrets);
            return Task.FromResult(ProviderFeedback.TestSucceeded);
        }

        public Task RemoveSavedValuesAsync(CancellationToken cancellationToken)
        {
            Calls.Add("remove");
            return Task.CompletedTask;
        }

        public Task DisableDependentFeaturesAsync(CancellationToken cancellationToken)
        {
            Calls.Add("disable");
            return Task.CompletedTask;
        }
    }

    private static ProviderView View(string key = "tmdb", ProviderBlocking? blocking = null, string? required = "admin.providers.requiredReason", ProviderConnectionState state = ProviderConnectionState.NotConfigured) =>
        new(key, "TMDB", "admin.providers.summary.tmdb", ProviderFamily.Metadata, true, state, required, ["admin.providers.capability.movies"],
            [new ProviderSecretField("readAccessToken", "admin.providers.field.readAccessToken", "admin.providers.field.readAccessTokenHint", 2048, false)], false, false, false,
            "admin.providers.fact.sourceNone", null, null, blocking);

    private static readonly ProviderBlocking Blocks = new("setup.provider.blocked.title", "setup.provider.blocked.body", "setup.provider.blocked.configure", "setup.provider.blocked.disable");

    private static T Prepare<T>(T model, string? provider = null, string? returnUrl = null) where T : PageModel
    {
        var http = new DefaultHttpContext();
        http.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>());
        model.PageContext = new PageContext(new ActionContext(http, new RouteData(), new ActionDescriptor(), new ModelStateDictionary()));
        model.TempData = new TempDataDictionary(http, new NullTempDataProvider());
        model.Url = new FixedPageUrl();
        switch (model)
        {
            case ProvidersModel admin:
                admin.Provider = provider;
                break;
            case SetupProviderModel setup:
                setup.Provider = provider;
                setup.ReturnUrl = returnUrl ?? "/";
                break;
        }

        return model;
    }

    private sealed class FixedPageUrl : IUrlHelper
    {
        public ActionContext ActionContext { get; } = new(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());

        public bool IsLocalUrl(string? url) => url is { Length: > 0 } && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));

        public string? Action(UrlActionContext actionContext) => null;

        public string? Content(string? contentPath) => contentPath;

        public string? Link(string? routeName, object? values) => null;

        public string? RouteUrl(UrlRouteContext routeContext) => "/Admin/Providers";
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }

    private static ClaimsPrincipal PrincipalIn(AccountRole role) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "p"), new Claim(ClaimTypes.Role, role.ToString())], "test"));

    [TestMethod]
    public void BothPagesAreOwnerOnlyAndNoOtherRoleOrAnonymousViewerPassesTheirPolicy()
    {
        foreach (var page in new[] { typeof(ProvidersModel), typeof(SetupProviderModel) })
        {
            var policy = page.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>().Single().Policy;
            Assert.AreEqual(JularrPolicies.AdminSystem, policy, page.Name);
        }

        Assert.IsTrue(JularrPolicies.Allows(PrincipalIn(AccountRole.Owner), JularrPolicies.AdminSystem));
        Assert.IsFalse(JularrPolicies.Allows(PrincipalIn(AccountRole.MediaManager), JularrPolicies.AdminSystem));
        Assert.IsFalse(JularrPolicies.Allows(PrincipalIn(AccountRole.User), JularrPolicies.AdminSystem));
        Assert.IsFalse(JularrPolicies.Allows(new ClaimsPrincipal(new ClaimsIdentity()), JularrPolicies.AdminSystem));
        Assert.IsFalse(typeof(ProvidersModel).GetCustomAttributes(typeof(IgnoreAntiforgeryTokenAttribute), inherit: true).Any(), "Posts to the provider handlers are antiforgery validated.");
        Assert.IsTrue(typeof(ProvidersModel).GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: true).Any());
        Assert.IsTrue(typeof(ProvidersModel).GetCustomAttributes(typeof(RequestSizeLimitAttribute), inherit: true).Any(), "The request body is bounded.");
    }

    [TestMethod]
    public async Task SaveTestAndRemoveWorkOnTheNamedProviderAndForwardOnlyTheFieldsOfItsSchema()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var provider = new FakeProvider(View());
        var page = Prepare(new ProvidersModel(db, [provider]), "tmdb");
        page.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["readAccessToken"] = "typed-token",
            ["injected"] = "ignored"
        });

        var save = await page.OnPostSaveAsync(true, "/Account/SetupProvider?returnUrl=%2F", CancellationToken.None);
        await page.OnPostTestAsync(null, CancellationToken.None);
        await page.OnPostRemoveAsync(null, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "save:True", "test", "remove" }, provider.Calls);
        Assert.AreEqual("/Account/SetupProvider?returnUrl=%2F", ((LocalRedirectResult)save).Url, "The Setup step returns to itself.");
        CollectionAssert.AreEqual(new[] { "readAccessToken" }, provider.LastSecrets!.Keys.ToArray());
        Assert.AreEqual("typed-token", provider.LastSecrets["readAccessToken"]);
        var (feedback, key) = ProviderPageModel.Take(page.TempData);
        Assert.AreEqual(ProviderFeedback.Removed, feedback);
        Assert.AreEqual("tmdb", key);
    }

    [TestMethod]
    public async Task AnUnknownProviderIsNotFoundAndAForeignReturnAddressIsNeverFollowed()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var provider = new FakeProvider(View());

        Assert.IsInstanceOfType<NotFoundResult>(await Prepare(new ProvidersModel(db, [provider]), "nope").OnPostSaveAsync(true, null, CancellationToken.None));
        Assert.IsInstanceOfType<NotFoundResult>(await Prepare(new ProvidersModel(db, [provider]), null).OnPostTestAsync(null, CancellationToken.None));
        var foreign = await Prepare(new ProvidersModel(db, [provider]), "tmdb").OnPostSaveAsync(true, "https://evil.example/", CancellationToken.None);
        Assert.IsInstanceOfType<RedirectToPageResult>(foreign);
        Assert.AreEqual(1, provider.Calls.Count, "Only the valid post reached the provider.");
    }

    [TestMethod]
    public void AConnectionTestHasASmallBudgetOfItsOwnPerAccountWhileOtherRequestsStayGenerous()
    {
        using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(DiscoveryRegistration.ProviderSettingsPartitionFor);

        HttpContext Request(string handler)
        {
            var context = new DefaultHttpContext { User = PrincipalIn(AccountRole.Owner) };
            context.Request.QueryString = new QueryString("?handler=" + handler);
            return context;
        }

        var tests = Enumerable.Range(0, DiscoveryRegistration.ProviderTestsPerMinute + 2).Select(_ => limiter.AttemptAcquire(Request("Test")).IsAcquired).ToArray();
        var saves = Enumerable.Range(0, DiscoveryRegistration.ProviderTestsPerMinute + 2).Select(_ => limiter.AttemptAcquire(Request("Save")).IsAcquired).ToArray();

        Assert.AreEqual(DiscoveryRegistration.ProviderTestsPerMinute, tests.Count(acquired => acquired));
        Assert.IsTrue(saves.All(acquired => acquired), "A test never uses up the budget of the page and the other handlers.");
    }

    [TestMethod]
    public async Task SetupFinishesOnlyWhenNoProviderBlocksAndOtherwiseShowsTheBlockingResult()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var blocked = new FakeProvider(View(blocking: Blocks));

        var stays = Prepare(new SetupProviderModel(db, [blocked]), returnUrl: "/");
        var blockedResult = await stays.OnPostFinishAsync(CancellationToken.None);

        Assert.IsInstanceOfType<PageResult>(blockedResult);
        Assert.AreEqual("tmdb", stays.Blocking.Single().Key);
        Assert.AreEqual("setup.provider.blocked.title", stays.Blocking.Single().Blocking!.TitleKey);

        blocked.View = View(blocking: null, state: ProviderConnectionState.Healthy);
        var done = await Prepare(new SetupProviderModel(db, [blocked]), returnUrl: "/").OnPostFinishAsync(CancellationToken.None);
        Assert.AreEqual("/", ((LocalRedirectResult)done).Url);
    }

    [TestMethod]
    public async Task SetupOffersToTurnTheDependentFeaturesOffAndNeverRedirectsToAForeignAddress()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var provider = new FakeProvider(View(blocking: Blocks));
        var page = Prepare(new SetupProviderModel(db, [provider]), "tmdb", "https://evil.example/");

        var result = await page.OnPostDisableFeaturesAsync(CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "disable" }, provider.Calls);
        Assert.AreEqual("/", ((LocalRedirectResult)result).Url);
    }

    [TestMethod]
    public async Task SetupSkipsTheStepWhenNothingNeedsAProvider()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var provider = new FakeProvider(View(required: null));

        var result = await Prepare(new SetupProviderModel(db, [provider]), returnUrl: "/Library").OnGetAsync(CancellationToken.None);

        Assert.AreEqual("/Library", ((LocalRedirectResult)result).Url);
    }
}
