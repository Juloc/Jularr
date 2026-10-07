using System.Security.Claims;
using Jularr.Web.Data;
using Jularr.Web.Features.Appearance;
using Jularr.Web.Features.Branding;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Providers;
using Jularr.Web.Pages.Account;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Jularr.Tests;

/// <summary>The instance step of Setup (#877): it writes the real module, branding and appearance settings and nothing of its own.</summary>
[TestClass]
public sealed class SetupInstanceTests
{
    private const string Owner = "owner-1";

    [TestMethod]
    public async Task AFreshInstanceOffersTheMediaManagerStartingPointWithItsFeaturesAlreadyOff()
    {
        await using var fixture = await Fixture.CreateAsync();
        var page = fixture.Page();

        var result = await page.OnGetAsync(CancellationToken.None);

        Assert.IsInstanceOfType<PageResult>(result);
        Assert.AreEqual(InstancePreset.MediaManager, page.Start);
        Assert.IsFalse(page.Settings.IsEnabled(InstanceModule.Playback));
        Assert.IsFalse(page.Settings.IsEnabled(InstanceModule.Learning));
        Assert.IsTrue(page.Settings.IsEnabled(InstanceModule.Acquisition));
        Assert.IsTrue(page.Settings.IsEnabled(InstanceModule.Movie), "The media types stay as they are.");
    }

    [TestMethod]
    public async Task AMediaManagerFinishesSetupWithoutPlaybackLearningOrTrackingAndKeepsTheChosenMediaTypes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var page = fixture.Page(Form(("Start", "MediaManager"), (Field(InstanceModule.Movie), "true"), (Field(InstanceModule.Tv), "true"), (Field(InstanceModule.Book), "true"),
            (Field(InstanceModule.Acquisition), "true"), (Field(InstanceModule.Playback), "true"), (Field(InstanceModule.Learning), "true"), (Field(InstanceModule.Tracking), "true")));
        page.Start = InstancePreset.MediaManager;

        var result = await page.OnPostAsync(CancellationToken.None);

        Assert.IsInstanceOfType<LocalRedirectResult>(result);
        var saved = await fixture.ModuleStore.GetAsync();
        Assert.IsTrue(saved.IsEnabled(InstanceModule.Movie) && saved.IsEnabled(InstanceModule.Tv) && saved.IsEnabled(InstanceModule.Book));
        Assert.IsFalse(saved.IsEnabled(InstanceModule.Anime), "An unticked media type is switched off.");
        Assert.IsFalse(saved.IsEnabled(InstanceModule.Music));
        Assert.IsTrue(saved.IsEnabled(InstanceModule.Acquisition));
        Assert.IsFalse(saved.IsEnabled(InstanceModule.Playback), "The starting point decides the features, whatever the switches said.");
        Assert.IsFalse(saved.IsEnabled(InstanceModule.Learning));
        Assert.IsFalse(saved.IsEnabled(InstanceModule.Tracking));
        Assert.AreEqual(InstancePreset.MediaManager, InstanceModulePresets.Detect(saved));
    }

    [TestMethod]
    public async Task ACustomStartingPointKeepsEverySwitchExactlyAsItWasSet()
    {
        await using var fixture = await Fixture.CreateAsync();
        var page = fixture.Page(Form((Field(InstanceModule.Anime), "true"), (Field(InstanceModule.Playback), "true"), (Field(InstanceModule.Acquisition), "true")));
        page.Start = InstancePreset.Custom;

        await page.OnPostAsync(CancellationToken.None);

        var saved = await fixture.ModuleStore.GetAsync();
        Assert.IsTrue(saved.IsEnabled(InstanceModule.Anime) && saved.IsEnabled(InstanceModule.Playback) && saved.IsEnabled(InstanceModule.Acquisition));
        Assert.IsFalse(saved.IsEnabled(InstanceModule.Movie));
        Assert.IsFalse(saved.IsEnabled(InstanceModule.Learning));
        Assert.AreEqual(InstancePreset.Custom, InstanceModulePresets.Detect(saved));
    }

    [TestMethod]
    public async Task TheNameAndTheOwnersAppearanceAreWrittenToTheStoresTheSettingsPagesUse()
    {
        await using var fixture = await Fixture.CreateAsync();
        var page = fixture.Page(Form((Field(InstanceModule.Movie), "true")));
        page.Start = InstancePreset.Full;
        page.InstanceName = "  Casa Media ";
        page.ThemeMode = AppTheme.Dark;
        page.Accent = "#2c55a8";

        await page.OnPostAsync(CancellationToken.None);

        Assert.AreEqual("Casa Media", (await fixture.Branding.GetAsync(CancellationToken.None)).Name);
        var appearance = await new ProfileAppearanceStore(fixture.Db).GetAsync(Owner, CancellationToken.None);
        Assert.AreEqual(AppTheme.Dark, appearance.ThemeMode);
        Assert.AreEqual("#2c55a8", appearance.AccentColor);
        Assert.IsTrue((await fixture.ModuleStore.GetAsync()).IsEnabled(InstanceModule.Playback), "Full Jularr turns every module on.");
    }

    [TestMethod]
    public async Task ABadNameOrAccentChangesNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tooLong = fixture.Page(Form((Field(InstanceModule.Movie), "true")));
        tooLong.InstanceName = new string('x', 41);

        var shown = await tooLong.OnPostAsync(CancellationToken.None);

        Assert.IsInstanceOfType<PageResult>(shown);
        Assert.IsFalse(tooLong.ModelState.IsValid);
        Assert.IsTrue((await fixture.ModuleStore.GetAsync()).IsEnabled(InstanceModule.Anime), "The modules were not saved with the rejected name.");
        Assert.AreEqual(InstanceBrandingSettings.Default, await fixture.Branding.GetAsync(CancellationToken.None));

        var badAccent = fixture.Page(Form());
        badAccent.Accent = "red";
        Assert.IsInstanceOfType<BadRequestResult>(await badAccent.OnPostAsync(CancellationToken.None));
        Assert.AreEqual(InstanceModuleSettings.Default.IsEnabled(InstanceModule.Playback), (await fixture.ModuleStore.GetAsync()).IsEnabled(InstanceModule.Playback), "Nothing was saved.");
    }

    [TestMethod]
    public async Task SkippingLeavesEverythingAsItIsAndStillFinishesSetup()
    {
        await using var fixture = await Fixture.CreateAsync();
        var page = fixture.Page(Form());
        page.ReturnUrl = "/Library";

        var result = await page.OnPostSkipAsync(CancellationToken.None);

        Assert.AreEqual("/Library", ((LocalRedirectResult)result).Url);
        Assert.AreEqual(InstanceModuleSettings.Default.Modules.Count, (await fixture.ModuleStore.GetAsync()).Modules.Count);
        Assert.IsTrue((await fixture.ModuleStore.GetAsync()).IsEnabled(InstanceModule.Playback));
        Assert.AreEqual(InstanceBrandingSettings.Default, await fixture.Branding.GetAsync(CancellationToken.None));
    }

    private static string Field(InstanceModule module) => Jularr.Web.Pages.Admin.InstanceModel.FieldName(module);

    private static IFormCollection Form(params (string Name, string Value)[] fields) =>
        new FormCollection(fields.ToDictionary(field => field.Name, field => new StringValues(field.Value)));

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root;
        private ServiceProvider? services;

        private Fixture(string root, AppDbContext db, InstanceModuleStore modules, InstanceBrandingStore branding)
        {
            this.root = root;
            Db = db;
            ModuleStore = modules;
            Branding = branding;
        }

        public AppDbContext Db { get; }

        public InstanceModuleStore ModuleStore { get; }

        public InstanceBrandingStore Branding { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"jularr-setup-instance-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var dataSource = $"Data Source=setup-instance-{Guid.NewGuid():N}.db";
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(dataSource).Options);
            await DatabaseMigrationBridge.UpgradeAsync(db);
            var collection = new ServiceCollection();
            collection.AddDbContext<AppDbContext>(options => options.UseSqlite(dataSource));
            collection.AddSingleton(TimeProvider.System);
            collection.AddSingleton<InstanceBrandingStore>();
            var provider = collection.BuildServiceProvider();
            var fixture = new Fixture(root, db, new InstanceModuleStore(root), provider.GetRequiredService<InstanceBrandingStore>()) { services = provider };
            return fixture;
        }

        public SetupInstanceModel Page(IFormCollection? form = null)
        {
            var context = new DefaultHttpContext();
            context.Request.ContentType = "application/x-www-form-urlencoded";
            context.Request.Form = form ?? new FormCollection([]);
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Owner)], "test"));
            var page = new SetupInstanceModel(Db, ModuleStore, Branding, Array.Empty<IProviderSettings>())
            {
                PageContext = new PageContext(new ActionContext(context, new RouteData(), new PageActionDescriptor())),
                Url = new LocalOnlyUrlHelper()
            };
            return page;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            if (services is not null)
            {
                await services.DisposeAsync();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class LocalOnlyUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext { get; } = new();

        public string? Action(UrlActionContext actionContext) => null;

        public string? Content(string? contentPath) => contentPath;

        public bool IsLocalUrl(string? url) => url is { Length: > 0 } && url[0] == '/' && !url.StartsWith("//", StringComparison.Ordinal);

        public string? Link(string? routeName, object? values) => null;

        public string? RouteUrl(UrlRouteContext routeContext) => null;
    }
}
