using System.Text.RegularExpressions;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Localization;

namespace Jularr.Tests;

/// <summary>#870: one subordinate Unfinished navigation section, and the application header that carries the global search.</summary>
[TestClass]
public sealed partial class UnfinishedNavigationTests
{
    private static readonly string[] UnfinishedIds = ["learn", "settings-learning", "settings-ai", "settings-offline", "admin-subtitles", "admin-ai"];

    private static readonly string[] CoreIds =
    [
        "home", "library", "watchlist", "calendar", "activity", "profile", "admin-overview", "admin-users", "admin-requests", "admin-wanted", "admin-operations",
        "admin-sessions", "admin-devices", "admin-scans", "admin-logs", "admin-instance", "admin-system", "admin-transcoding", "admin-health",
        "settings-account", "settings-appearance", "settings-language", "profile-devices"
    ];

    private static readonly Func<string, bool> Owner = RoleNavigationTests.As(AccountRole.Owner);
    private static readonly Func<string, bool> Manager = RoleNavigationTests.As(AccountRole.MediaManager);
    private static readonly Func<string, bool> User = RoleNavigationTests.As(AccountRole.User);

    [TestMethod]
    public void TheCatalogFlagsExactlyTheIncompleteDestinations()
    {
        CollectionAssert.AreEquivalent(UnfinishedIds, UiNavigationCatalog.UnfinishedEntries.Select(candidate => candidate.Entry.Id).ToArray());

        var core = UiNavigationCatalog.All.Where(entry => CoreIds.Contains(entry.Id)).ToArray();
        Assert.AreEqual(CoreIds.Length, core.Length, "The core destinations must exist in the catalog.");
        Assert.IsFalse(core.Any(entry => entry.Unfinished), "A working core destination is never moved to Unfinished.");
    }

    [TestMethod]
    public void UnfinishedClosesTheSidebarAndNoSectionListsItsOwnUnfinishedPages()
    {
        var nav = UiShellNavigation.Build("/", learningVisible: true, Owner);

        CollectionAssert.AreEqual(UnfinishedIds, nav.Unfinished.Select(item => item.Id).ToArray(), "Consumer, then Settings, then Admin.");
        var listedElsewhere = nav.Primary.Concat(nav.Secondary).Select(item => item.Id).ToArray();
        Assert.IsFalse(listedElsewhere.Any(UnfinishedIds.Contains), "Unfinished destinations are not listed beside the finished ones.");

        foreach (var path in new[] { "/Admin", "/Settings" })
        {
            var expanded = UiShellNavigation.Build(path, learningVisible: true, Owner).Expanded!;
            Assert.IsFalse(expanded.Groups!.SelectMany(group => group.Items).Any(item => UnfinishedIds.Contains(item.Id)), path);
        }

        Assert.AreEqual(2, nav.Unfinished.Count(item => item.Id.EndsWith("-ai", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(
            new string?[] { null, "nav.settings", "nav.settings", "nav.settings", "nav.admin", "nav.admin" },
            nav.Unfinished.Select(item => item.AreaKey).ToArray(),
            "Pages that share a name (AI) stay distinguishable by their area.");
    }

    [TestMethod]
    [DataRow("admin")]
    [DataRow("settings")]
    public void DrillInListsEndWithTheirOwnUnfinishedGroup(string sectionId)
    {
        var section = UiShellNavigation.BuildSection(sectionId, Owner)!;
        var groups = section.Groups!;

        Assert.AreEqual(UiShellNavigation.UnfinishedGroupTitleKey, groups[^1].TitleKey);
        Assert.IsTrue(groups[^1].Items.All(item => UnfinishedIds.Contains(item.Id)));
        Assert.IsFalse(groups.SkipLast(1).SelectMany(group => group.Items).Any(item => UnfinishedIds.Contains(item.Id)));
        Assert.AreEqual(1, groups.Count(group => group.TitleKey == UiShellNavigation.UnfinishedGroupTitleKey), "One Unfinished section per list.");
    }

    [TestMethod]
    public void AdminProvidersSitsInTheMediaGroupNextToTheDownloader()
    {
        var media = UiNavigationCatalog.Admin.Single(section => section.TitleKey == "nav.group.adminMedia").Entries.Select(entry => entry.Id).ToArray();
        var providers = UiNavigationCatalog.Admin.SelectMany(section => section.Entries).Single(entry => entry.Id == "admin-providers");

        Assert.AreEqual("/Admin/Providers", providers.Href);
        Assert.AreEqual(Array.IndexOf(media, "admin-usenet") + 1, Array.IndexOf(media, "admin-providers"));
        Assert.IsFalse(providers.Unfinished);
        Assert.IsFalse(UiShellNavigation.BuildSection("admin", Manager)!.Groups!.SelectMany(group => group.Items).Any(item => item.Id == "admin-providers"));
        Assert.IsTrue(UiShellNavigation.BuildSection("admin", Owner)!.Groups!.SelectMany(group => group.Items).Any(item => item.Id == "admin-providers"));
    }

    [TestMethod]
    public void UnfinishedFollowsThePermissionsOfEachDestination()
    {
        Assert.IsFalse(UiShellNavigation.Build("/", learningVisible: true, User).Unfinished.Any(item => item.Id.StartsWith("admin", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(
            new[] { "learn", "settings-learning", "settings-ai", "settings-offline", "admin-subtitles" },
            UiShellNavigation.Build("/", learningVisible: true, Manager).Unfinished.Select(item => item.Id).ToArray(),
            "A media manager passes the subtitles policy but not the AI policy.");

        var userList = UiShellNavigation.BuildSection(UiNavigationCatalog.UnfinishedSection.Id, User);
        Assert.IsNotNull(userList);
        Assert.IsFalse(userList.Groups!.SelectMany(group => group.Items).Any(item => item.Id.StartsWith("admin", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void UnfinishedFollowsLearningAndInstanceModules()
    {
        var withoutProfileLearning = UiShellNavigation.Build("/", learningVisible: false, Owner).Unfinished.Select(item => item.Id).ToArray();
        CollectionAssert.DoesNotContain(withoutProfileLearning, "learn");
        CollectionAssert.Contains(withoutProfileLearning, "settings-learning");

        var modules = Enum.GetValues<InstanceModule>().Where(module => module != InstanceModule.Learning).ToHashSet();
        var withoutModule = UiShellNavigation.Build("/", learningVisible: true, Owner, enabledInstanceModules: modules).Unfinished.Select(item => item.Id).ToArray();
        CollectionAssert.DoesNotContain(withoutModule, "learn");
        CollectionAssert.DoesNotContain(withoutModule, "settings-learning");
        CollectionAssert.Contains(withoutModule, "settings-offline");
    }

    [TestMethod]
    public void PhonesReachUnfinishedBehindProfileAndNeverInTheBottomBar()
    {
        var nav = UiShellNavigation.Build("/", learningVisible: true, Owner);
        CollectionAssert.AreEqual(new[] { "home", "library", "calendar", "profile" }, nav.MobilePrimary.Select(item => item.Id).ToArray());
        Assert.IsFalse(UiNavigationCatalog.MobilePrimaryIds.Contains("search"));
        Assert.IsFalse(nav.MobilePrimary.Any(item => UnfinishedIds.Contains(item.Id) || item.Id == "unfinished"));

        var (links, elsewhere) = UiShellNavigation.BuildProfile(learningVisible: true, Owner);
        Assert.AreEqual("unfinished", links[^1].Id, "Unfinished closes the Profile list.");
        Assert.AreEqual("/Profile/unfinished", links[^1].Href);
        Assert.IsFalse(links.Concat(elsewhere).Any(item => UnfinishedIds.Contains(item.Id)), "The destinations themselves sit one level deeper.");

        var drillIn = UiShellNavigation.BuildSection("unfinished", Owner)!;
        Assert.AreEqual(1, drillIn.Groups!.Count);
        CollectionAssert.AreEqual(UnfinishedIds, drillIn.Groups![0].Items.Select(item => item.Id).ToArray());
        Assert.IsNull(UiShellNavigation.BuildSection("unfinished-x", Owner));
    }

    [TestMethod]
    [DataRow("/Settings/Offline", "settings-offline", "settings")]
    [DataRow("/Admin/Ai", "admin-ai", "admin")]
    [DataRow("/Settings/Subtitles", "admin-subtitles", "admin")]
    [DataRow("/Learn", "learn", null)]
    public void AnUnfinishedPageIsMarkedOnceAndOpensItsSection(string path, string expectedId, string? sectionId)
    {
        var nav = UiShellNavigation.Build(path, learningVisible: true, Owner);

        Assert.AreEqual(expectedId, nav.Unfinished.Single(item => item.IsActive).Id);
        Assert.AreEqual(sectionId, nav.Expanded?.Id);
        var current = nav.Primary.Concat(nav.Secondary).Concat(nav.Expanded?.Groups?.SelectMany(group => group.Items) ?? []).Concat(nav.Unfinished)
            .Where(item => item.IsCurrentPage)
            .Select(item => item.Id)
            .ToArray();
        CollectionAssert.AreEqual(new[] { expectedId }, current, "Exactly one link claims the current page.");
        Assert.AreEqual("profile", nav.MobilePrimary.Single(item => item.IsActive).Id);
    }

    [TestMethod]
    public void TheHeaderOwnsTheGlobalSearchAndTheSidebarHasNone()
    {
        var shared = Path.Combine(RepositoryRoot(), "src", "Jularr.Web", "Pages", "Shared");
        var header = File.ReadAllText(Path.Combine(shared, "_AppHeader.cshtml"));
        var navigation = File.ReadAllText(Path.Combine(shared, "_AppNavigation.cshtml"));
        var sidebar = navigation[navigation.IndexOf("<aside class=\"sidebar\"", StringComparison.Ordinal)..navigation.IndexOf("</aside>", StringComparison.Ordinal)];

        StringAssert.Contains(header, "<partial name=\"_AppSearch\" />");
        foreach (var utility in new[] { "_AppSearch", "_AppThemeControl", "_AppAccountFooter", "_AppUserMenu", "_NotificationBell", "_AppSignOut" })
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
    public void DiscoverLeavesTheSearchFocusToItsOwnField()
    {
        var navigation = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Jularr.Web", "Pages", "Shared", "_AppNavigation.cshtml"));
        var pwa = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "js", "pwa.js"));

        StringAssert.Contains(navigation, "StartsWithSegments(\"/Discover\"");
        StringAssert.Contains(pwa, "[data-dc-search], [data-app-search]");
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
            .Concat(UiNavigationCatalog.UnfinishedEntries.Select(candidate => candidate.AreaKey).OfType<string>())
            .Append(UiNavigationCatalog.UnfinishedSection.LabelKey)
            .Append(UiShellNavigation.UnfinishedGroupTitleKey)
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
