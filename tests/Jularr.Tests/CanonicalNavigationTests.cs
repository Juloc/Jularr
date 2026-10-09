using System.Text.RegularExpressions;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Instance;

namespace Jularr.Tests;

[TestClass]
public sealed partial class CanonicalNavigationTests
{
    private static readonly Func<string, bool> Owner = RoleNavigationTests.As(AccountRole.Owner);

    [TestMethod]
    public void WorkingSpecializedPagesRemainReachableThroughTheirCanonicalParent()
    {
        var modules = Enum.GetValues<InstanceModule>().ToHashSet();
        foreach (var parent in UiNavigationCatalog.Admin.SelectMany(section => section.Entries))
        {
            foreach (var child in parent.Links ?? [])
            {
                var links = UiShellNavigation.BuildContextualLinks(parent.Href, Owner, modules);
                Assert.IsTrue(links.Any(link => link.Href == child.Href), child.Href);
                var nav = UiShellNavigation.Build(child.Href, false, Owner);
                Assert.AreEqual(parent.Id, nav.Expanded!.Groups!.SelectMany(group => group.Items).Single(item => item.IsActive).Id, child.Href);
                Assert.IsNotNull(nav.Breadcrumb, child.Href);
            }
        }
    }

    [TestMethod]
    public void UnfinishedFeaturesHaveNoNormalNavigationOrLegacyGroup()
    {
        Assert.IsNull(UiShellNavigation.BuildSection("unfinished", Owner));
        var section = UiShellNavigation.BuildSection("admin", Owner)!;
        Assert.IsFalse(section.Groups!.Any(group => group.TitleKey.Contains("unfinished", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(UiShellNavigation.BuildProfile(true, Owner).Links.Any(item => item.Id == "unfinished"));
        var root = Path.Combine(RepositoryRoot(), "src", "Jularr.Web");
        Assert.IsFalse(File.Exists(Path.Combine(root, "Pages", "Admin", "_AdminNavigation.cshtml")));
        var navigation = File.ReadAllText(Path.Combine(root, "Pages", "Shared", "_AppNavigation.cshtml"));
        Assert.IsFalse(navigation.Contains("nav-unfinished", StringComparison.Ordinal));
        foreach (var page in Directory.EnumerateFiles(Path.Combine(root, "Pages"), "*.cshtml", SearchOption.AllDirectories))
        {
            Assert.IsFalse(File.ReadAllText(page).Contains("_AdminNavigation", StringComparison.Ordinal), $"Obsolete navigation partial in {page}.");
        }
    }

    [TestMethod]
    public void CanonicalDestinationsResolveToActualRazorRoutes()
    {
        var pages = Path.Combine(RepositoryRoot(), "src", "Jularr.Web", "Pages");
        var routes = Directory.EnumerateFiles(pages, "*.cshtml", SearchOption.AllDirectories).SelectMany(file =>
        {
            var firstLine = File.ReadLines(file).FirstOrDefault() ?? "";
            var route = "/" + Path.GetRelativePath(pages, file).Replace('\\', '/')[..^7];
            var explicitRoute = Regex.Match(firstLine, "^@page\\s+\"(?<route>[^\"]+)\"");
            if (explicitRoute.Success)
            {
                var template = explicitRoute.Groups["route"].Value;
                var baseRoute = route.EndsWith("/Index", StringComparison.Ordinal) ? route[..^6] : route;
                return new[] { (template.StartsWith('/') ? template : baseRoute + "/" + template).Split('{')[0].TrimEnd('/') };
            }

            if (!Regex.IsMatch(firstLine, "^@page\\s*$"))
            {
                return Array.Empty<string>();
            }

            return route.EndsWith("/Index", StringComparison.Ordinal) ? new[] { route, route[..^6] } : new[] { route };
        }).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var destination in UiNavigationCatalog.All)
        {
            Assert.IsTrue(routes.Contains(destination.Href.TrimEnd('/')), $"Missing Razor destination {destination.Id}: {destination.Href}.");
        }
    }

    [TestMethod]
    public void DownloadsAlwaysMeanPersonalOfflineStorageIncludingForOwners()
    {
        var header = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Jularr.Web", "Pages", "Shared", "_AppHeader.cshtml"));
        StringAssert.Contains(header, "data-offline-download-preview");
        StringAssert.Contains(header, "href=\"/Settings/Offline\"");
        Assert.IsFalse(header.Contains("/Shell/Downloads", StringComparison.Ordinal));
        Assert.IsFalse(header.Contains("/Admin/Usenet", StringComparison.Ordinal));
        Assert.IsFalse(header.Contains("/Admin/Operation", StringComparison.Ordinal));
        var readingOnly = new HashSet<InstanceModule> { InstanceModule.Book };
        var links = UiShellNavigation.BuildSection("settings", Owner, enabledInstanceModules: readingOnly)!.Groups!.SelectMany(group => group.Items);
        Assert.IsTrue(links.Any(item => item.Id == "settings-offline"), "Offline reading does not require the playback module.");
    }

    [TestMethod]
    public void TheHeaderOwnsTheGlobalSearchAndTheSidebarHasNone()
    {
        var shared = Path.Combine(RepositoryRoot(), "src", "Jularr.Web", "Pages", "Shared");
        var header = File.ReadAllText(Path.Combine(shared, "_AppHeader.cshtml"));
        var navigation = File.ReadAllText(Path.Combine(shared, "_AppNavigation.cshtml"));
        var sidebar = navigation[navigation.IndexOf("<aside class=\"sidebar\"", StringComparison.Ordinal)..navigation.IndexOf("</aside>", StringComparison.Ordinal)];

        StringAssert.Contains(header, "<partial name=\"_AppSearch\" />");
        StringAssert.Contains(header, "<partial name=\"_NotificationBell\" />");
        StringAssert.Contains(header, "<partial name=\"_AppUserMenu\" model='\"header\"' />");
        StringAssert.Contains(sidebar, "sidebar-brand");
        foreach (var utility in new[] { "_AppSearch", "_AppThemeControl", "_AppAccountFooter", "_NotificationBell", "_AppUserMenu", "_AppSignOut" })
        {
            Assert.IsFalse(sidebar.Contains(utility, StringComparison.Ordinal), $"The sidebar is navigation only, not {utility}.");
        }

        var searchPartials = Directory.EnumerateFiles(Path.Combine(shared, ".."), "*.cshtml", SearchOption.AllDirectories)
            .Where(file => !file.Contains("Discover", StringComparison.Ordinal))
            .Sum(file => Regex.Matches(File.ReadAllText(file), "<partial name=\"_AppSearch\"").Count);
        Assert.AreEqual(1, searchPartials, "One shell search, so no duplicate search field or id.");

        var layout = File.ReadAllText(Path.Combine(shared, "_Layout.cshtml"));
        var ids = new[] { header, navigation, layout, File.ReadAllText(Path.Combine(shared, "_AppUserMenu.cshtml")), File.ReadAllText(Path.Combine(shared, "_AppSearch.cshtml")) }
            .SelectMany(markup => Regex.Matches(markup, "\\sid=\"([^\"@]+)\"").Select(match => match.Groups[1].Value))
            .ToArray();
        Assert.AreEqual(ids.Length, ids.Distinct(StringComparer.Ordinal).Count(), "Duplicate id in the shell markup.");
    }

    [TestMethod]
    public void DiscoverUsesTheGlobalSearchAndShowsNoSecondSearchField()
    {
        var root = Path.Combine(RepositoryRoot(), "src", "Jularr.Web");
        var navigation = File.ReadAllText(Path.Combine(root, "Pages", "Shared", "_AppNavigation.cshtml"));
        var search = File.ReadAllText(Path.Combine(root, "Pages", "Shared", "_AppSearch.cshtml"));
        
        Assert.IsFalse(navigation.Contains("/Discover", StringComparison.Ordinal), "The header search stays on every page, Discover included.");
        StringAssert.Contains(search, "Context.Request.Query[\"q\"]");
        Assert.IsFalse(File.ReadAllText(Path.Combine(root, "Pages", "Index.cshtml")).Contains("class=\"dc-search\"", StringComparison.Ordinal), "The page has no search field of its own.");
        StringAssert.Contains(search, "data-dc-search");
    }

    [TestMethod]
    public void MediaManagerShellLabelsHomeAsDiscoverAndHidesTheUserLibrary()
    {
        var navigation = UiShellNavigation.Build("/", learningVisible: true, Owner, mediaManagerMode: true);
        var discover = navigation.Primary.Single(item => item.Id == "home");
        var profile = UiShellNavigation.BuildProfile(learningVisible: true, Owner, mediaManagerMode: true);

        Assert.AreEqual("nav.discover", discover.LabelKey);
        Assert.AreEqual("search", discover.Icon);
        Assert.IsFalse(navigation.Primary.Any(item => item.Id == "library"));
        Assert.IsFalse(profile.Links.Concat(profile.Elsewhere).Any(item => item.Id == "library"));
    }

    [TestMethod]
    [DataRow("player.css", "data-player-frame")]
    [DataRow("reader-shell.css", "data-reader-frame")]
    public void ImmersiveModesSuppressTheWholeShellIncludingTheHeader(string stylesheet, string marker)
    {
        var css = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "css", stylesheet));

        foreach (var chrome in new[] { ".app-header", ".sidebar", ".mobile-nav" })
        {
            StringAssert.Contains(css, $"body:has([{marker}]) {chrome}");
        }
    }

    [TestMethod]
    public void EveryShellTextKeyExistsInTheUiCatalog()
    {
        var pages = Path.Combine(RepositoryRoot(), "src", "Jularr.Web", "Pages");
        var files = Directory.EnumerateFiles(Path.Combine(pages, "Shared"), "_App*.cshtml").Append(Path.Combine(pages, "Profile", "_ProfileLink.cshtml"));
        var keys = files
            .SelectMany(file => UiKey().Matches(File.ReadAllText(file)).Select(match => match.Groups["key"].Value))
            .Concat(UiNavigationCatalog.All.Select(entry => entry.LabelKey))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.IsTrue(keys.Length > 20);
        foreach (var key in keys)
        {
            Assert.IsTrue(UiTranslationResources.TryGet(key, out var message), $"Missing catalog key {key}.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(message.DefaultText), key);
        }
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

    [GeneratedRegex("ui\\[\"(?<key>[A-Za-z0-9.]+)\"\\]")]
    private static partial Regex UiKey();
}
