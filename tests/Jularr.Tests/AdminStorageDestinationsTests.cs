using System.Security.Claims;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Storage;
using Jularr.Web.Features.Storage.Insights;
using Jularr.Web.Pages.Admin.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>
/// Admin → Storage owns the default destination root and placement policy of the content types whose importers route through Storage
/// (#815): the Owner can choose them, nobody else can, and the choice is the one the importers read.
/// </summary>
[TestClass]
public sealed class AdminStorageDestinationsTests
{
    [TestMethod]
    public async Task TheOwnerChoosesADefaultRootAndPlacementPolicyThatTheImporterRoutingResolves()
    {
        await using var fixture = await Fixture.CreateAsync();
        var cinema = await fixture.AddRootAsync("Cinema");

        var result = await fixture.Page().OnPostDestinationAsync(LibraryContentType.Movie, cinema.Id, LibraryPlacementPolicy.Move, CancellationToken.None);

        Assert.IsInstanceOfType<RedirectToPageResult>(result);
        var route = await fixture.Routing.ResolveDefaultAsync(LibraryContentType.Movie);
        Assert.AreEqual(cinema.Id, route!.LibraryRootId);
        Assert.AreEqual(LibraryPlacementPolicy.Move, route.PlacementPolicy);
        Assert.IsNull(await fixture.Routing.ResolveDefaultAsync(LibraryContentType.Tv), "No cross-content fallback: TV has no default of its own.");
    }

    [TestMethod]
    public async Task ChangingTheDefaultKeepsExactlyOneAndClearingItMakesImportsWait()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.AddRootAsync("First");
        var second = await fixture.AddRootAsync("Second");
        await fixture.Page().OnPostDestinationAsync(LibraryContentType.Tv, first.Id, LibraryPlacementPolicy.Copy, CancellationToken.None);

        await fixture.Page().OnPostDestinationAsync(LibraryContentType.Tv, second.Id, LibraryPlacementPolicy.Hardlink, CancellationToken.None);

        Assert.AreEqual(second.Id, (await fixture.Routing.ResolveDefaultAsync(LibraryContentType.Tv))!.LibraryRootId);
        Assert.AreEqual(1, (await fixture.Routing.ListAsync(LibraryContentType.Tv)).Count(route => route.IsDefault));
        Assert.AreEqual(2, (await fixture.Routing.ListAsync(LibraryContentType.Tv)).Count, "The previous root keeps supporting the type; it is just no longer the default.");

        await fixture.Page().OnPostDestinationAsync(LibraryContentType.Tv, null, LibraryPlacementPolicy.Copy, CancellationToken.None);

        Assert.IsNull(await fixture.Routing.ResolveDefaultAsync(LibraryContentType.Tv));
    }

    [TestMethod]
    public async Task OnlyTheOwnerMayChangeADestination()
    {
        await using var fixture = await Fixture.CreateAsync();
        var cinema = await fixture.AddRootAsync("Cinema");

        var result = await fixture.Page(asOwner: false).OnPostDestinationAsync(LibraryContentType.Movie, cinema.Id, LibraryPlacementPolicy.Move, CancellationToken.None);

        Assert.IsInstanceOfType<ForbidResult>(result);
        Assert.IsNull(await fixture.Routing.ResolveDefaultAsync(LibraryContentType.Movie));
        Assert.AreEqual(LibraryPlacementPolicy.HardlinkOrCopy, (await fixture.Db.LibraryRoots.AsNoTracking().SingleAsync()).PlacementPolicy, "A refused request changes nothing, not even the policy.");
    }

    [TestMethod]
    public async Task ADisabledRootAnUnknownRootOrANonRoutedTypeIsRefused()
    {
        await using var fixture = await Fixture.CreateAsync();
        var disabled = await fixture.AddRootAsync("Disabled", enabled: false);
        var page = fixture.Page();

        await page.OnPostDestinationAsync(LibraryContentType.Movie, disabled.Id, LibraryPlacementPolicy.Copy, CancellationToken.None);
        Assert.AreEqual(page.Ui["admin.storage.destinations.failed"], page.TempData["StorageError"]);

        var unknown = fixture.Page();
        await unknown.OnPostDestinationAsync(LibraryContentType.Movie, Guid.NewGuid(), LibraryPlacementPolicy.Copy, CancellationToken.None);
        Assert.AreEqual(unknown.Ui["admin.storage.destinations.failed"], unknown.TempData["StorageError"]);

        var notRouted = await fixture.Page().OnPostDestinationAsync(LibraryContentType.Anime, disabled.Id, LibraryPlacementPolicy.Copy, CancellationToken.None);
        Assert.IsInstanceOfType<BadRequestResult>(notRouted, "Importers that do not route through Storage yet have no destination to set here.");
        Assert.IsInstanceOfType<BadRequestResult>(await fixture.Page().OnPostDestinationAsync(LibraryContentType.Movie, disabled.Id, (LibraryPlacementPolicy)42, CancellationToken.None));
        Assert.IsNull(await fixture.Routing.ResolveDefaultAsync(LibraryContentType.Movie));
        Assert.AreEqual(0, await fixture.Db.LibraryRootContentAssignments.CountAsync());
    }

    [TestMethod]
    public async Task AnAnimeRootIsRefusedWithAnExplainingMessage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var anime = await fixture.AddRootAsync("Anime");
        await fixture.Routing.SetSupportedAsync(anime.Id, LibraryContentType.Anime, true);
        var page = fixture.Page();

        await page.OnPostDestinationAsync(LibraryContentType.Movie, anime.Id, LibraryPlacementPolicy.Copy, CancellationToken.None);

        Assert.AreEqual(page.Ui["admin.storage.destinations.conflict"], page.TempData["StorageError"]);
        Assert.IsNull(await fixture.Routing.ResolveDefaultAsync(LibraryContentType.Movie));
        Assert.IsTrue(await fixture.Routing.ServesAnimeAsync(anime.Id), "The Anime root still serves Anime.");
    }

    [TestMethod]
    public async Task ThePageListsEveryRoutedTypeWithItsRootAndHidesAModuleThatIsDisabled()
    {
        await using var fixture = await Fixture.CreateAsync();
        var cinema = await fixture.AddRootAsync("Cinema");
        await fixture.AddRootAsync("Archive", enabled: false);
        await fixture.Page().OnPostDestinationAsync(LibraryContentType.Movie, cinema.Id, LibraryPlacementPolicy.Copy, CancellationToken.None);

        var page = fixture.Page();
        await page.OnGetAsync(CancellationToken.None);

        CollectionAssert.AreEqual(LibraryRootRoutingService.ManagedTypes.ToArray(), page.Destinations.Select(destination => destination.ContentType).ToArray());
        Assert.AreEqual(cinema.Id, page.Destinations[0].Default!.LibraryRootId);
        Assert.IsNull(page.Destinations[1].Default);
        CollectionAssert.AreEqual(new[] { "Cinema" }, page.EnabledRoots.Select(root => root.Name).ToArray(), "Only enabled roots can be chosen.");
        Assert.IsTrue(page.CanManageDestinations);

        var tvOff = fixture.Page(modules: InstanceModuleSettings.Default.With(InstanceModule.Tv, false));
        await tvOff.OnGetAsync(CancellationToken.None);
        CollectionAssert.AreEqual(LibraryRootRoutingService.ManagedTypes.Where(type => type != LibraryContentType.Tv).ToArray(), tvOff.Destinations.Select(destination => destination.ContentType).ToArray(), "A disabled module leaves no dead control.");

        Assert.IsFalse(fixture.Page(asOwner: false).CanManageDestinations);
    }

    [TestMethod]
    public async Task TheStoragePageRendersTheDestinationFormsForTheOwnerAndReadOnlyForAMediaManager()
    {
        await using var host = await ManageSheetPageTestHost.CreateAsync();
        var cinema = new LibraryRoot { Name = "Cinema", Path = "/srv/cinema", PlacementPolicy = LibraryPlacementPolicy.Hardlink };
        host.Db.LibraryRoots.Add(cinema);
        await host.Db.SaveChangesAsync();
        var routing = new LibraryRootRoutingService(host.Db);
        await routing.SetSupportedAsync(cinema.Id, LibraryContentType.Movie, true);
        await routing.SetDefaultAsync(LibraryContentType.Movie, cinema.Id);

        var owner = await host.GetHtmlAsync("/Admin/Storage", asOwner: true);
        var manager = await host.GetHtmlAsync("/Admin/Storage", asOwner: false, asMediaManager: true);

        StringAssert.Contains(owner, "Default destinations");
        Assert.AreEqual(LibraryRootRoutingService.ManagedTypes.Count, System.Text.RegularExpressions.Regex.Matches(owner, "handler=Destination").Count, "One form per routed type.");
        StringAssert.Contains(owner, "name=\"libraryRootId\"");
        StringAssert.Contains(owner, "name=\"placementPolicy\"");
        StringAssert.Contains(owner, "No default root: imports of this type wait until one is chosen.", "TV has no default yet.");
        StringAssert.Contains(owner, "No default root: the library folder of Import &amp; naming is used", "Manga falls back to the folder of Import & naming.");
        Assert.IsFalse(manager.Contains("handler=Destination", StringComparison.Ordinal), "A media manager sees the destinations but cannot change them.");
        StringAssert.Contains(manager, "Default destinations");
        StringAssert.Contains(manager, "Cinema");
        Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(owner, "<h1[ >]").Count, "One page heading.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root;

        private Fixture(string root, AppDbContext db)
        {
            this.root = root;
            Db = db;
            Routing = new LibraryRootRoutingService(db);
        }

        public AppDbContext Db { get; }
        public LibraryRootRoutingService Routing { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"jularr-storage-destinations-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(root, "app.db")};Foreign Keys=True").Options);
            await DatabaseMigrationBridge.UpgradeAsync(db);
            return new Fixture(root, db);
        }

        public async Task<LibraryRoot> AddRootAsync(string name, bool enabled = true)
        {
            var library = new LibraryRoot { Name = name, Path = Directory.CreateDirectory(Path.Combine(root, name.ToLowerInvariant())).FullName, IsEnabled = enabled };
            Db.LibraryRoots.Add(library);
            await Db.SaveChangesAsync();
            return library;
        }

        public IndexModel Page(bool asOwner = true, InstanceModuleSettings? modules = null)
        {
            var httpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, asOwner ? "owner" : "manager"), new Claim(ClaimTypes.Role, asOwner ? AccountRoles.Owner : AccountRoles.MediaManager)], "test"))
            };
            string Cache(string name) => Path.Combine(root, "cache", name);
            var layout = new StorageCacheLayout(Cache("playback"), Cache("hls"), Cache("trickplay"), Cache("artwork"), Cache("fingerprints"), Cache("manga"));
            var usage = new StorageUsageService(Db, new LibraryRootAvailabilityService(Db, new StorageAvailabilityCoordinator()), new StorageIntegrityService(Db));
            var cleanup = new StorageCleanupService(Db, new StorageCacheScanner(Db, layout, TimeProvider.System), layout, NullLogger<StorageCleanupService>.Instance);
            return new IndexModel(Db, usage, cleanup, Routing, new CurrentAccountContext(new HttpContextAccessor { HttpContext = httpContext }), modules is null ? null : new FixedModules(modules))
            {
                PageContext = new PageContext { HttpContext = httpContext, ViewData = new ViewDataDictionary<IndexModel>(new EmptyModelMetadataProvider(), new ModelStateDictionary()) },
                TempData = new TempDataDictionary(httpContext, new NoTempData())
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class NoTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }

    private sealed class FixedModules(InstanceModuleSettings settings) : IInstanceModuleService
    {
        public Task<InstanceModuleSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);

        public Task<bool> IsEnabledAsync(InstanceModule module, CancellationToken cancellationToken = default) => Task.FromResult(settings.IsEnabled(module));

        public Task<InstanceModuleSettings> SetAsync(InstanceModule module, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<InstanceModuleSettings> SaveAsync(InstanceModuleSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
