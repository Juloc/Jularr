using System.Security.Claims;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Shell;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Tests;

/// <summary>
/// The permission-derived app shell (#598): a media type the profile cannot browse is absent from
/// the sidebar, the Library tabs and the Profile list, and its consumer routes answer 404.
/// </summary>
[TestClass]
public sealed class PermissionDerivedShellTests
{
    private static readonly Func<string, bool> User = RoleNavigationTests.As(AccountRole.User);
    private static readonly Func<string, bool> Owner = RoleNavigationTests.As(AccountRole.Owner);

    private static readonly string[] AnimeRoots = ["/Library"];
    private static readonly string[] ReadingRoots = ["/Reading", "/Novels", "/Manga"];
    private static readonly string[] BookRoots = ["/Books"];

    [TestMethod]
    public void BooksOnlyProfileSeesAPureBookApp()
    {
        WorkMediaType[] books = [WorkMediaType.Book];

        var nav = UiShellNavigation.Build("/Books", learningVisible: false, User, books);

        var library = nav.Primary.Single(item => item.Id == "library");
        Assert.AreEqual("/Books", library.Href, "Library opens the only media type the profile has.");
        Assert.IsTrue(library.IsActive);
        CollectionAssert.AreEqual(
            new[] { "library-books" },
            UiShellNavigation.BuildLibraryTabs("/Books", books).Select(tab => tab.Id).ToArray());

        var (_, elsewhere) = UiShellNavigation.BuildProfile(learningVisible: false, User, books);
        Assert.AreEqual("/Books", nav.MobilePrimary.Single(item => item.Id == "library").Href, "The phone bar leads to the same only media type.");

        var hrefs = nav.Primary.Concat(nav.Secondary).Concat(nav.MobilePrimary).Concat(elsewhere)
            .Select(item => item.Href)
            .ToArray();
        foreach (var hiddenRoot in AnimeRoots.Concat(ReadingRoots))
        {
            Assert.IsFalse(hrefs.Any(href => IsUnder(href, hiddenRoot)), $"{hiddenRoot} must be absent, not greyed out.");
        }
    }

    [TestMethod]
    public void AProfileWithoutAnyMediaTypeKeepsTheDestinationsThatAreNotMediaScoped()
    {
        var nav = UiShellNavigation.Build("/", learningVisible: true, User, visibleMediaTypes: []);

        CollectionAssert.AreEqual(
            new[] { "home", "watchlist", "calendar", "settings", "profile" },
            nav.Primary.Concat(nav.Secondary).Select(item => item.Id).ToArray());
        Assert.AreEqual(0, UiShellNavigation.BuildLibraryTabs("/", []).Count);

        var (links, elsewhere) = UiShellNavigation.BuildProfile(learningVisible: true, User, visibleMediaTypes: []);
        Assert.IsFalse(links.Concat(elsewhere).Any(item => item.Id == "library"));
    }

    [TestMethod]
    [DataRow("anime,book", "/Library", "library-video,library-books")]
    [DataRow("book,manga", "/Reading", "library-reading,library-books")]
    [DataRow("lightNovel", "/Reading", "library-reading")]
    [DataRow("manga", "/Reading", "library-reading")]
    [DataRow("book", "/Books", "library-books")]
    [DataRow("anime,manga,lightNovel,book", "/Library", "library-video,library-reading,library-books")]
    public void LibraryOpensTheFirstVisibleTabAndListsOnlyVisibleTabs(string visible, string expectedHref, string expectedTabs)
    {
        var media = Types(visible);

        var nav = UiShellNavigation.Build("/", learningVisible: false, User, media);

        Assert.AreEqual(expectedHref, nav.Primary.Single(item => item.Id == "library").Href);
        CollectionAssert.AreEqual(
            expectedTabs.Split(','),
            UiShellNavigation.BuildLibraryTabs("/", media).Select(tab => tab.Id).ToArray());
    }

    [TestMethod]
    [DataRow("/Novels/Work/7a4c", "lightNovel", "library-reading")]
    [DataRow("/Manga/Series/7a4c", "manga,book", "library-reading")]
    [DataRow("/Books/Read/7a4c", "anime,book", "library-books")]
    [DataRow("/Library/Anime/7a4c", "anime,book", "library-video")]
    public void TheCurrentLibraryTabStaysMarkedWhenOtherTypesAreHidden(string path, string visible, string expectedTab)
    {
        var media = Types(visible);

        var active = UiShellNavigation.BuildLibraryTabs(path, media).Single(tab => tab.IsActive);
        Assert.AreEqual(expectedTab, active.Id);
        Assert.AreEqual("library", UiShellNavigation.Build(path, learningVisible: false, User, media).Primary.Single(item => item.IsActive).Id);
    }

    [TestMethod]
    public void OwnerAndUnrestrictedProfilesKeepTheFullLibrary()
    {
        var everything = UiShellNavigation.Build("/", learningVisible: true, Owner, WorkMediaTypes.All);
        var unscoped = UiShellNavigation.Build("/", learningVisible: true, Owner);

        CollectionAssert.AreEqual(
            unscoped.Primary.Concat(unscoped.Secondary).Select(item => (item.Id, item.Href)).ToArray(),
            everything.Primary.Concat(everything.Secondary).Select(item => (item.Id, item.Href)).ToArray());
        CollectionAssert.AreEqual(
            new[] { "library-video", "library-reading", "library-books", "library-music" },
            UiShellNavigation.BuildLibraryTabs("/Library", WorkMediaTypes.All).Select(tab => tab.Id).ToArray());
        Assert.AreEqual("/Library", everything.Primary.Single(item => item.Id == "library").Href);
    }

    [TestMethod]
    public void EveryMediaTypeHasAConsumerDestinationInTheLibraryTabs()
    {
        var covered = UiNavigationCatalog.LibraryTabs.SelectMany(UiNavigationCatalog.MediaTypesOf).Distinct().ToArray();

        CollectionAssert.AreEquivalent(
            WorkMediaTypes.All.ToArray(),
            covered,
            "A media type gained or lost its consumer destination; update the shell tests.");

        foreach (var type in WorkMediaTypes.All)
        {
            var nav = UiShellNavigation.Build("/", learningVisible: false, User, [type]);
            Assert.IsTrue(nav.Primary.Any(item => item.Id == "library"), $"{type} must open Library.");
        }

        CollectionAssert.AreEquivalent(
            new[] { WorkMediaType.Anime, WorkMediaType.Series, WorkMediaType.Movie },
            UiNavigationCatalog.MediaTypesOf(UiNavigationCatalog.LibraryTabs.Single(tab => tab.Id == UiNavigationCatalog.LibraryVideoTabId)).ToArray(),
            "Anime, Series and Movies are one Library destination.");
    }
    [TestMethod]
    public void EveryMediaRouteRootIsAPageFolderOfTheNavigationTab()
    {
        foreach (var tab in UiNavigationCatalog.LibraryTabs)
        {
            Assert.IsNotNull(tab.MediaRoutes, tab.Id);
            Assert.IsTrue(tab.MediaRoutes.Length > 0, tab.Id);
            Assert.IsTrue(
                tab.MediaRoutes.Any(route => string.Equals(route.Root, UiNavigationCatalog.PathOf(tab.Href), StringComparison.OrdinalIgnoreCase)),
                $"{tab.Id} must list the route its own tab opens.");
            Assert.IsTrue(tab.MediaRoutes.All(route => route.MediaTypes.Length > 0), tab.Id);
        }

        var roots = UiNavigationCatalog.MediaRoutes.Select(route => route.Root).ToArray();
        Assert.AreEqual(roots.Length, roots.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "A route root is gated twice.");
    }

    [TestMethod]
    public async Task ShellExposesExactlyTheMediaTypesAProfileMayBrowse()
    {
        using var fixture = new ShellFixture();
        await fixture.HideAllExceptAsync("alice", WorkMediaType.Book);

        var access = await fixture.Shell.GetMediaAccessAsync(Principal("alice", AccountRole.User));

        CollectionAssert.AreEqual(new[] { WorkMediaType.Book }, access.VisibleMediaTypes.ToArray());
        Assert.IsTrue(access.IsVisible(WorkMediaType.Book));
        Assert.IsFalse(access.IsVisible(WorkMediaType.Anime));
        Assert.AreEqual(MediaCapability.Request, access.Capability(WorkMediaType.Book), "The effective capability, not just visibility.");
        Assert.AreEqual(MediaCapability.Hidden, access.Capability(WorkMediaType.Manga));
        Assert.IsTrue(access.CanOpen("/Books"));
        Assert.IsTrue(access.CanOpen("/books"));
        Assert.IsFalse(access.CanOpen("/Library"));
        Assert.IsFalse(access.CanOpen("/Novels"));
        Assert.IsFalse(access.CanOpen("/Reading"));
        Assert.IsFalse(access.CanOpen("/NotAMediaRoute"), "A route the catalog does not know is closed.");
    }

    [TestMethod]
    public async Task ABrowseOnlyTypeIsStillVisible()
    {
        using var fixture = new ShellFixture();
        await fixture.HideAllExceptAsync("alice");
        await fixture.Store.SetUserOverrideAsync("alice", WorkMediaType.Manga, MediaCapability.Browse);

        var access = await fixture.Shell.GetMediaAccessAsync(Principal("alice", AccountRole.User));

        CollectionAssert.AreEqual(new[] { WorkMediaType.Manga }, access.VisibleMediaTypes.ToArray());
        Assert.IsTrue(access.CanOpen("/Manga"));
        Assert.IsTrue(access.CanOpen("/Reading"), "The reading hub opens when either reading type is visible.");
        Assert.IsFalse(access.CanOpen("/Novels"));
    }

    [TestMethod]
    public async Task OwnerSeesEveryMediaTypeWhateverThePolicySays()
    {
        using var fixture = new ShellFixture();
        foreach (var type in WorkMediaTypes.All)
        {
            await fixture.Store.SetRoleDefaultAsync(AccountRole.User, type, MediaCapability.Hidden);
            await fixture.Store.SetRoleDefaultAsync(AccountRole.MediaManager, type, MediaCapability.Hidden);
        }

        var owner = await fixture.Shell.GetMediaAccessAsync(Principal("owner", AccountRole.Owner));
        var user = await fixture.Shell.GetMediaAccessAsync(Principal("bob", AccountRole.User));

        Assert.IsTrue(owner.IsOwner);
        CollectionAssert.AreEqual(WorkMediaTypes.All.ToArray(), owner.VisibleMediaTypes.ToArray());
        Assert.IsTrue(UiNavigationCatalog.MediaRoutes.All(route => owner.CanOpen(route.Root)));
        Assert.AreEqual(0, user.VisibleMediaTypes.Count);
        Assert.IsFalse(UiNavigationCatalog.MediaRoutes.Any(route => user.CanOpen(route.Root)));
    }

    [TestMethod]
    public async Task ASignedOutRequestSeesNoMediaType()
    {
        using var fixture = new ShellFixture();

        var access = await fixture.Shell.GetMediaAccessAsync(new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.AreEqual(0, access.VisibleMediaTypes.Count);
        Assert.IsFalse((await fixture.Shell.GetMediaAccessAsync(null)).IsAnyVisible(WorkMediaTypes.All));
    }

    [TestMethod]
    public async Task ShellResolvesThePolicyOncePerPrincipalWithinARequest()
    {
        var counting = new CountingCapabilityService();
        var shell = new AppShellService(counting);
        var alice = Principal("alice", AccountRole.User);

        var first = await shell.GetMediaAccessAsync(alice);
        var second = await shell.GetMediaAccessAsync(alice);
        Assert.AreSame(first, second);
        Assert.AreEqual(1, counting.Calls, "Sidebar, tabs and route gate share one resolution.");

        await shell.GetMediaAccessAsync(Principal("bob", AccountRole.User));
        Assert.AreEqual(2, counting.Calls, "Another principal is never served alice's answer.");
    }

    [TestMethod]
    public async Task GateHidesAMediaTypeThatTheProfileCannotBrowse()
    {
        using var fixture = new ShellFixture();
        await fixture.HideAllExceptAsync("alice", WorkMediaType.Book);

        Assert.IsInstanceOfType<NotFoundResult>(await Authorize(fixture, "alice", AccountRole.User, WorkMediaType.Anime));
        Assert.IsInstanceOfType<NotFoundResult>(await Authorize(fixture, "alice", AccountRole.User, WorkMediaType.Manga, WorkMediaType.LightNovel));
        Assert.IsNull(await Authorize(fixture, "alice", AccountRole.User, WorkMediaType.Book));
    }

    [TestMethod]
    public async Task GateLetsAReadingHubThroughWhileEitherOfItsTypesIsVisible()
    {
        using var fixture = new ShellFixture();
        await fixture.HideAllExceptAsync("alice", WorkMediaType.LightNovel);

        Assert.IsNull(await Authorize(fixture, "alice", AccountRole.User, WorkMediaType.Manga, WorkMediaType.LightNovel));
        Assert.IsNull(await Authorize(fixture, "alice", AccountRole.User, WorkMediaType.LightNovel));
        Assert.IsInstanceOfType<NotFoundResult>(await Authorize(fixture, "alice", AccountRole.User, WorkMediaType.Manga));
    }

    [TestMethod]
    public async Task GateNeverBlocksTheOwner()
    {
        using var fixture = new ShellFixture();
        foreach (var type in WorkMediaTypes.All)
        {
            await fixture.Store.SetRoleDefaultAsync(AccountRole.User, type, MediaCapability.Hidden);
        }

        foreach (var type in WorkMediaTypes.All)
        {
            Assert.IsNull(await Authorize(fixture, "owner", AccountRole.Owner, type), type.ToString());
        }
    }

    [TestMethod]
    public async Task GateAnswersNotFoundForASignedOutRequest()
    {
        using var fixture = new ShellFixture();
        var context = new DefaultHttpContext { RequestServices = fixture.Services };
        var authorization = new AuthorizationFilterContext(
            new ActionContext(context, new RouteData(), new ActionDescriptor()),
            []);

        await new MediaTypeGateFilter([WorkMediaType.Book]).OnAuthorizationAsync(authorization);

        Assert.IsInstanceOfType<NotFoundResult>(authorization.Result);
    }

    [TestMethod]
    public void ShellPartialsAndProfileScopeTheirNavigationToTheVisibleMediaTypes()
    {
        var root = RepositoryRoot();
        var shared = Path.Combine(root, "src", "Jularr.Web", "Pages", "Shared");

        StringAssert.Contains(File.ReadAllText(Path.Combine(shared, "_AppNavigation.cshtml")), "media.VisibleMediaTypes");
        StringAssert.Contains(File.ReadAllText(Path.Combine(shared, "_LibraryTypeTabs.cshtml")), "media.VisibleMediaTypes");
        StringAssert.Contains(
            File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "Pages", "Profile", "Index.cshtml.cs")),
            "media.VisibleMediaTypes");

        var tabs = File.ReadAllText(Path.Combine(shared, "_LibraryTypeTabs.cshtml"));
        StringAssert.Contains(tabs, "tabs.Count > 1", "A single visible media type has nothing to switch between.");
    }

    private static async Task<IActionResult?> Authorize(
        ShellFixture fixture,
        string profileId,
        AccountRole role,
        params WorkMediaType[] mediaTypes)
    {
        var context = new DefaultHttpContext
        {
            User = Principal(profileId, role),
            RequestServices = fixture.Services
        };
        var authorization = new AuthorizationFilterContext(
            new ActionContext(context, new RouteData(), new ActionDescriptor()),
            []);

        await new MediaTypeGateFilter(mediaTypes).OnAuthorizationAsync(authorization);
        return authorization.Result;
    }

    private static WorkMediaType[] Types(string names) =>
        names.Split(',').Select(name => WorkMediaTypes.Parse(name) ?? throw new AssertFailedException(name)).ToArray();

    private static bool IsUnder(string href, string root) =>
        new PathString(UiNavigationCatalog.PathOf(href)).StartsWithSegments(root, StringComparison.OrdinalIgnoreCase);

    internal static ClaimsPrincipal Principal(string profileId, AccountRole role)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, profileId) };
        if (role != AccountRole.User)
        {
            claims.Add(new Claim(ClaimTypes.Role, role == AccountRole.Owner ? AccountRoles.Owner : AccountRoles.MediaManager));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Jularr repository root.");
    }

    /// <summary>The real capability service over a real JSON policy store in a temp directory.</summary>
    private sealed class ShellFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"jularr-shell-{Guid.NewGuid():N}");
        private readonly ServiceProvider provider;

        public ShellFixture()
        {
            Directory.CreateDirectory(directory);
            Store = new MediaCapabilityStore(directory);
            Capabilities = new MediaCapabilityService(Store);
            Shell = new AppShellService(Capabilities);
            provider = new ServiceCollection()
                .AddSingleton<IAppShellService>(Shell)
                .BuildServiceProvider();
        }

        public MediaCapabilityStore Store { get; }

        public MediaCapabilityService Capabilities { get; }

        public AppShellService Shell { get; }

        public IServiceProvider Services => provider;

        /// <summary>Per-user overrides that hide every media type except <paramref name="visible"/>.</summary>
        public async Task HideAllExceptAsync(string profileId, params WorkMediaType[] visible)
        {
            foreach (var type in WorkMediaTypes.All.Except(visible))
            {
                await Store.SetUserOverrideAsync(profileId, type, MediaCapability.Hidden);
            }
        }

        public void Dispose()
        {
            provider.Dispose();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class CountingCapabilityService : IMediaCapabilityService
    {
        public int Calls { get; private set; }

        public Task<MediaCapabilityView> GetViewAsync(ClaimsPrincipal? user, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new MediaCapabilityView(
                IsOwner: false,
                WorkMediaTypes.All.ToDictionary(type => type, _ => MediaCapability.Browse)));
        }

        public Task<MediaCapability> GetEffectiveCapabilityAsync(ClaimsPrincipal? user, WorkMediaType mediaType, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<WorkMediaType>> GetVisibleMediaTypesAsync(ClaimsPrincipal? user, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task EnsureCapabilityAsync(ClaimsPrincipal? user, WorkMediaType mediaType, MediaCapability required, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
