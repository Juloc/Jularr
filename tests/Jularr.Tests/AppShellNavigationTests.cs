using System.Text.RegularExpressions;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.Http;

namespace Jularr.Tests;

[TestClass]
public sealed partial class AppShellNavigationTests
{
    private static readonly string[] AllowedLiteralText = ["Jularr"];

    private static readonly Func<string, bool> Owner = RoleNavigationTests.As(AccountRole.Owner);
    private static readonly Func<string, bool> User = RoleNavigationTests.As(AccountRole.User);

    [TestMethod]
    public void DesktopSidebarListsTheBaseDestinationsInOrder()
    {
        var owner = UiShellNavigation.Build("/", learningVisible: true, can: Owner);
        CollectionAssert.AreEqual(
            new[] { "home", "watchlist", "calendar", "library", "admin", "settings", "profile" },
            owner.Primary.Concat(owner.Secondary).Select(item => item.Id).ToArray());

        var user = UiShellNavigation.Build("/", learningVisible: false, can: User);
        CollectionAssert.AreEqual(
            new[] { "home", "watchlist", "calendar", "library", "settings", "profile" },
            user.Primary.Concat(user.Secondary).Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public void DiscoverReadingAndBooksAreNoSidebarItemsButStayReachable()
    {
        var nav = UiShellNavigation.Build("/", learningVisible: true, can: Owner);
        var ids = nav.Primary.Concat(nav.Secondary).Select(item => item.Id).ToArray();

        foreach (var removed in new[] { "discover", "reading", "books" })
        {
            CollectionAssert.DoesNotContain(ids, removed);
        }

        CollectionAssert.AreEqual(
            new[] { "/Library", "/Reading", "/Books" },
            UiNavigationCatalog.LibraryTabs.Select(tab => tab.Href).ToArray());

        var search = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Jularr.Web", "Pages", "Shared", "_AppSearch.cshtml"));
        StringAssert.Contains(search, "action=\"/Discover\"");
        StringAssert.Contains(search, "name=\"q\"");
    }

    [TestMethod]
    [DataRow("/Library", "library-video")]
    [DataRow("/Library/Anime/7a4c", "library-video")]
    [DataRow("/Reading", "library-reading")]
    [DataRow("/Novels/Work/7a4c", "library-reading")]
    [DataRow("/Manga", "library-reading")]
    [DataRow("/Books", "library-books")]
    [DataRow("/Books/Details/7a4c", "library-books")]
    public void LibraryTabsMarkExactlyOneMediaType(string path, string expectedId)
    {
        var active = UiShellNavigation.BuildLibraryTabs(path).Where(tab => tab.IsActive).ToArray();

        Assert.AreEqual(1, active.Length, path);
        Assert.AreEqual(expectedId, active[0].Id);
        Assert.AreEqual("library", UiShellNavigation.Build(path, learningVisible: true, can: Owner).Primary.Single(item => item.IsActive).Id);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void MobileBarFollowsTheUxDocAndProfileReachesEveryOtherDestination(
        bool learningVisible,
        bool isOwner)
    {
        var nav = UiShellNavigation.Build("/", learningVisible, isOwner ? Owner : User);

        CollectionAssert.AreEqual(new[] { "home", "library", "calendar", "profile" }, nav.MobilePrimary.Select(item => item.Id).ToArray());
        Assert.IsTrue(nav.MobilePrimary.Count <= UiShellNavigation.MaxMobilePrimaryItems);

        var (links, elsewhere) = UiShellNavigation.BuildProfile(learningVisible, isOwner ? Owner : User);
        var desktop = nav.Primary.Concat(nav.Secondary).Select(item => item.Id).ToArray();
        var mobile = nav.MobilePrimary.Concat(links).Concat(elsewhere).Select(item => item.Id).ToArray();
        CollectionAssert.IsSubsetOf(desktop, mobile, "Every sidebar destination is reachable on a phone.");
        Assert.AreEqual(mobile.Length, mobile.Distinct(StringComparer.Ordinal).Count(), "A destination is listed twice on a phone.");
        Assert.AreEqual(isOwner, mobile.Contains("admin"));

        var unfinished = UiShellNavigation.BuildSection(UiNavigationCatalog.UnfinishedSection.Id, isOwner ? Owner : User, learningVisible: learningVisible);
        Assert.IsNotNull(unfinished, "Unfinished destinations are reachable behind Profile.");
        CollectionAssert.AreEqual(
            nav.Unfinished.Select(item => item.Id).ToArray(),
            unfinished.Groups!.SelectMany(group => group.Items).Select(item => item.Id).ToArray(),
            "The phone lists the same Unfinished destinations as the sidebar.");
        Assert.AreEqual(learningVisible, unfinished.Groups!.SelectMany(group => group.Items).Any(item => item.Id == "learn"));
    }

    [TestMethod]
    public void ProfileListsAccountActivityDownloadsSettingsAndAdminInOrder()
    {
        var (owner, _) = UiShellNavigation.BuildProfile(learningVisible: true, can: Owner);
        var expected = UiNavigationCatalog.DevicesPageAvailable
            ? new[] { "settings-account", "activity", "profile-devices", "settings", "admin", "unfinished" }
            : new[] { "settings-account", "activity", "settings", "admin", "unfinished" };
        CollectionAssert.AreEqual(expected, owner.Select(item => item.Id).ToArray());

        var (user, _) = UiShellNavigation.BuildProfile(learningVisible: true, can: User);
        CollectionAssert.AreEqual(expected.Where(id => id != "admin").ToArray(), user.Select(item => item.Id).ToArray());

        Assert.AreEqual("/Profile/Account", owner.Single(item => item.Id == "settings-account").Href);
        Assert.AreEqual(UiShellNavigation.DrillInHref("unfinished"), owner.Single(item => item.Id == "unfinished").Href);
        Assert.AreEqual(UiShellNavigation.DrillInHref("settings"), owner.Single(item => item.Id == "settings").Href);
        Assert.AreEqual(UiShellNavigation.DrillInHref("admin"), owner.Single(item => item.Id == "admin").Href);
    }

    [TestMethod]
    public void DevicesLinkFollowsThePageThatOwnsIt()
    {
        var page = Path.Combine(RepositoryRoot(), "src", "Jularr.Web", "Pages", "Profile", "Devices.cshtml");
        Assert.AreEqual(File.Exists(page), UiNavigationCatalog.DevicesPageAvailable, "Set DevicesPageAvailable together with the Devices page.");

        var (links, _) = UiShellNavigation.BuildProfile(learningVisible: true, can: Owner);
        Assert.AreEqual(UiNavigationCatalog.DevicesPageAvailable, links.Any(item => item.Id == "profile-devices"));
    }

    [TestMethod]
    public void DrillInListsReuseTheSidebarGroupsAndAdminIsOwnerOnly()
    {
        var settings = UiShellNavigation.BuildSection("settings", can: User);
        Assert.IsNotNull(settings);
        CollectionAssert.AreEqual(
            UiNavigationCatalog.Settings.Where(section => section.Entries.Any(entry => !entry.Unfinished)).Select(section => section.TitleKey).Append(UiShellNavigation.UnfinishedGroupTitleKey).ToArray(),
            settings.Groups!.Select(group => group.TitleKey).ToArray());
        CollectionAssert.AreEqual(
            UiNavigationCatalog.Settings.SelectMany(section => section.Entries).OrderBy(entry => entry.Unfinished).Select(entry => entry.Id).ToArray(),
            settings.Groups!.SelectMany(group => group.Items).Select(item => item.Id).ToArray());
        Assert.IsFalse(settings.Groups!.SelectMany(group => group.Items).Any(item => item.IsActive));

        Assert.IsNull(UiShellNavigation.BuildSection("admin", can: User), "Users have no Admin drill-in.");
        Assert.IsNotNull(UiShellNavigation.BuildSection("ADMIN", can: Owner));
        Assert.IsNull(UiShellNavigation.BuildSection("profile", can: Owner), "Only sections drill in.");
        Assert.IsNull(UiShellNavigation.BuildSection("Devices", can: Owner));
    }

    [TestMethod]
    [DataRow("/", "home", "home")]
    [DataRow("/Library", "library", "library")]
    [DataRow("/Library/Anime/7a4c", "library", "library")]
    [DataRow("/Library/Movie/7a4c", "library", "library")]
    [DataRow("/Library/Series/7a4c", "library", "library")]
    [DataRow("/Library/Watch/7a4c", "library", "library")]
    [DataRow("/Novels/Read/7a4c", "library", "library")]
    [DataRow("/Books/Read/7a4c", "library", "library")]
    [DataRow("/Franchises/7", "watchlist", "profile")]
    [DataRow("/Calendar", "calendar", "calendar")]
    [DataRow("/Activity", "profile", "profile")]
    [DataRow("/Learn/Kana", "learn", "profile")]
    [DataRow("/Statistics", "learn", "profile")]
    [DataRow("/Profile", "profile", "profile")]
    [DataRow("/Profile/settings", "profile", "profile")]
    [DataRow("/Profile/Account", "settings", "profile")]
    [DataRow("/Settings/Language", "settings", "profile")]
    [DataRow("/Admin/Users", "admin", "profile")]
    [DataRow("/Settings/Acquisition", "admin", "profile")]
    public void CurrentLocationMarksExactlyOneDestination(string path, string expectedId, string expectedMobileId)
    {
        var nav = UiShellNavigation.Build(path, learningVisible: true, can: Owner);

        var active = nav.Primary.Concat(nav.Secondary).Concat(nav.Unfinished).Where(item => item.IsActive).ToArray();
        Assert.AreEqual(1, active.Length, path);
        Assert.AreEqual(expectedId, active[0].Id);
        Assert.AreEqual(expectedMobileId, nav.MobilePrimary.Single(item => item.IsActive).Id);
    }

    [TestMethod]
    [DataRow("/", null, null)]
    [DataRow("/Library", null, null)]
    [DataRow("/Admin/Providers", "admin", "admin-providers")]
    [DataRow("/Settings/Appearance", "settings", "settings-appearance")]
    [DataRow("/Activity", "profile", "activity")]
    [DataRow("/Profile", null, null)]
    public void BreadcrumbNamesTheAreaAndThePageInsideIt(string path, string? parentId, string? currentId)
    {
        var crumb = UiShellNavigation.Build(path, learningVisible: true, can: Owner).Breadcrumb;

        Assert.AreEqual(parentId, crumb?.Parent.Id);
        Assert.AreEqual(currentId, crumb?.Current?.Id);
    }

    [TestMethod]
    public void ActivityIsAProfilePageNotASidebarDestination()
    {
        var nav = UiShellNavigation.Build("/", learningVisible: true, can: Owner);

        CollectionAssert.DoesNotContain(nav.Primary.Select(item => item.Id).ToArray(), "activity");
        CollectionAssert.AreEqual(new[] { "home", "watchlist", "calendar", "library" }, nav.Primary.Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public void DiscoverHasNoSidebarItemSoNothingIsMarked()
    {
        var nav = UiShellNavigation.Build("/Discover", learningVisible: true, can: Owner);

        Assert.IsFalse(nav.Primary.Concat(nav.Secondary).Any(item => item.IsActive));
        Assert.IsFalse(nav.MobilePrimary.Any(item => item.IsActive));
    }

    [TestMethod]
    [DataRow("/Admin", "admin-overview")]
    [DataRow("/Admin/Users", "admin-users")]
    [DataRow("/Admin/User/42", "admin-users")]
    [DataRow("/Admin/Usenet", "admin-usenet")]
    [DataRow("/Settings/Indexers/Edit", "admin-usenet")]
    [DataRow("/Settings/Acquisition", "admin-import")]
    [DataRow("/Settings/MappingSegments", "admin-mapping")]
    [DataRow("/LocalizationAdmin", "admin-localization")]
    public void AdminExpandsInlineWithExactlyOneActivePage(string path, string expectedId)
    {
        var nav = UiShellNavigation.Build(path, learningVisible: true, can: Owner);

        Assert.AreEqual("admin", nav.Expanded?.Id);
        Assert.AreEqual(1, nav.Secondary.Count(item => item.IsExpanded), "Only one section is open.");
        var active = nav.Expanded!.Groups!.SelectMany(group => group.Items).Where(item => item.IsActive).ToArray();
        Assert.AreEqual(1, active.Length, path);
        Assert.AreEqual(expectedId, active[0].Id);
        Assert.IsFalse(nav.Expanded.IsCurrentPage, "The child page is current, not the section anchor.");

        CollectionAssert.AreEqual(
            new[] { "home", "watchlist", "calendar", "library" },
            nav.Primary.Select(item => item.Id).ToArray(),
            "The base navigation stays visible inside Admin.");
        Assert.IsFalse(nav.ShowCurrentReading);
    }

    [TestMethod]
    [DataRow("/Settings", "settings-overview")]
    [DataRow("/Settings/Appearance", "settings-appearance")]
    [DataRow("/Appearance/Accent", "settings-appearance")]
    [DataRow("/Profile/Account", "settings-account")]
    [DataRow("/Settings/Language", "settings-language")]
    public void SettingsExpandsInlineWithExactlyOneActivePage(string path, string expectedId)
    {
        foreach (var isOwner in new[] { true, false })
        {
            var nav = UiShellNavigation.Build(path, learningVisible: true, isOwner ? Owner : User);

            Assert.AreEqual("settings", nav.Expanded?.Id, path);
            Assert.AreEqual(1, nav.Secondary.Count(item => item.IsExpanded));
            Assert.AreEqual(expectedId, nav.Expanded!.Groups!.SelectMany(group => group.Items).Single(item => item.IsActive).Id);
            Assert.AreEqual(isOwner ? 7 : 6, nav.Primary.Count + nav.Secondary.Count, "The base navigation stays visible inside Settings.");
        }
    }

    [TestMethod]
    [DataRow("/")]
    [DataRow("/Library")]
    [DataRow("/Profile")]
    [DataRow("/Activity")]
    public void NoSectionIsExpandedOutsideAdminAndSettings(string path)
    {
        var nav = UiShellNavigation.Build(path, learningVisible: true, can: Owner);

        Assert.IsNull(nav.Expanded);
        Assert.IsTrue(nav.Secondary.All(item => item.Groups is null));
    }

    [TestMethod]
    public void UsersNeverSeeAdminDestinations()
    {
        foreach (var path in new[] { "/", "/Admin", "/Admin/Users", "/Settings/Acquisition", "/Settings" })
        {
            var nav = UiShellNavigation.Build(path, learningVisible: true, can: User);
            var ids = nav.Primary.Concat(nav.Secondary).Concat(nav.MobilePrimary).Concat(nav.Unfinished)
                .SelectMany(item => (item.Groups?.SelectMany(group => group.Items) ?? []).Prepend(item))
                .Select(item => item.Id)
                .ToArray();

            Assert.IsFalse(ids.Any(id => id.StartsWith("admin", StringComparison.Ordinal)), path);
            Assert.AreNotEqual("admin", nav.Expanded?.Id, path);
        }

        var (links, elsewhere) = UiShellNavigation.BuildProfile(learningVisible: true, can: User);
        Assert.IsFalse(links.Concat(elsewhere).Any(item => item.Id.StartsWith("admin", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void SettingsListsNoOwnerOnlyPage()
    {
        var pages = typeof(UiShellNavigation).Assembly.GetTypes()
            .Where(type => typeof(Microsoft.AspNetCore.Mvc.RazorPages.PageModel).IsAssignableFrom(type))
            .ToArray();

        foreach (var entry in UiNavigationCatalog.Settings.SelectMany(section => section.Entries))
        {
            Assert.IsNull(entry.Policy, entry.Id);
            var model = PageModelFor(pages, entry.Href);
            Assert.IsNotNull(model, $"No page model for {entry.Href}.");
            var ownerOnly = model.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true)
                .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
                .Any(attribute => attribute.Policy is not null || !string.IsNullOrEmpty(attribute.Roles));
            Assert.IsFalse(ownerOnly, $"{entry.Href} is restricted and belongs under Admin.");
        }
    }

    [TestMethod]
    [DataRow("/Books", false)]
    [DataRow("/Books/Read/7a4c", true)]
    [DataRow("/Novels/Read/7a4c", true)]
    [DataRow("/Manga/Read/7a4c", true)]
    [DataRow("/Reading", false)]
    [DataRow("/", false)]
    [DataRow("/Library", false)]
    [DataRow("/Settings/Appearance", false)]
    [DataRow("/Admin/Usenet", false)]
    public void CurrentReadingShowsOnlyInReaders(string path, bool expected)
    {
        Assert.AreEqual(expected, UiShellNavigation.Build(path, learningVisible: true, can: Owner).ShowCurrentReading);
    }

    [TestMethod]
    public void EveryPageAppearsOnceInTheNavigationCatalog()
    {
        var entries = UiNavigationCatalog.All.Concat(UiNavigationCatalog.LibraryTabs).ToArray();
        var icons = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Jularr.Web", "Pages", "Shared", "_AppIcon.cshtml"));

        Assert.AreEqual(entries.Length, entries.Select(entry => entry.Id).Distinct().Count(), "Duplicate navigation id.");
        var sectionHrefs = UiNavigationCatalog.Admin.Concat(UiNavigationCatalog.Settings)
            .SelectMany(section => section.Entries)
            .Select(entry => entry.Href)
            .ToArray();
        Assert.AreEqual(sectionHrefs.Length, sectionHrefs.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "A page is listed twice.");
        var catalogIds = UiNavigationCatalog.All.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in UiNavigationCatalog.ProfileLinkIds.Concat(UiNavigationCatalog.MobilePrimaryIds))
        {
            Assert.IsTrue(catalogIds.Contains(id), $"Unknown navigation id {id}.");
        }

        foreach (var entry in entries)
        {
            Assert.IsTrue(UiTranslationResources.TryGet(entry.LabelKey, out var message), $"Missing catalog key {entry.LabelKey}.");
            Assert.IsTrue(message.MaxLength is > 0 and <= 24, entry.LabelKey);
            StringAssert.Contains(icons, $"case \"{entry.Icon}\":", $"Missing icon {entry.Icon}.");
        }

        foreach (var section in UiNavigationCatalog.Admin.Concat(UiNavigationCatalog.Settings))
        {
            Assert.IsTrue(UiTranslationResources.TryGet(section.TitleKey, out _), $"Missing catalog key {section.TitleKey}.");
        }
    }

    [TestMethod]
    public void NavigationLabelsAreCatalogKeysWithShortLengthGuidance()
    {
        var nav = UiShellNavigation.Build("/", learningVisible: true, can: Owner);

        foreach (var item in nav.Primary.Concat(nav.Secondary))
        {
            Assert.IsTrue(
                UiTranslationResources.TryGet(item.LabelKey, out var message),
                $"Missing catalog key {item.LabelKey}.");
            Assert.AreEqual("Navigation", message.Surface, item.LabelKey);
            Assert.IsTrue(message.MaxLength is > 0 and <= 18, item.LabelKey);
        }

        foreach (var removed in new[] { "nav.more", "nav.backToApp" })
        {
            Assert.IsFalse(UiTranslationResources.TryGet(removed, out _), $"{removed} has no surface any more.");
        }
    }

    [TestMethod]
    public void ClosedSectionsReopenOnTheirLastUsedPage()
    {
        var nav = UiShellNavigation.Build("/", learningVisible: true, can: Owner);
        CollectionAssert.AreEqual(
            new[] { "admin", "settings" },
            nav.Secondary.Where(item => item.IsSection).Select(item => item.Id).ToArray());

        var web = Path.Combine(RepositoryRoot(), "src", "Jularr.Web");
        StringAssert.Contains(File.ReadAllText(Path.Combine(web, "Pages", "Shared", "_AppNavLink.cshtml")), "data-nav-section-link");
        StringAssert.Contains(File.ReadAllText(Path.Combine(web, "wwwroot", "js", "pwa.js")), "a[data-nav-section-link]");
    }

    [TestMethod]
    public void ShellHasNoMoreSheetOrContextSidebar()
    {
        var web = Path.Combine(RepositoryRoot(), "src", "Jularr.Web");
        var navigation = File.ReadAllText(Path.Combine(web, "Pages", "Shared", "_AppNavigation.cshtml"));
        var pwa = File.ReadAllText(Path.Combine(web, "wwwroot", "js", "pwa.js"));
        var css = File.ReadAllText(Path.Combine(web, "wwwroot", "css", "site.css"));

        foreach (var gone in new[] { "data-nav-more", "mobile-more", "nav-context", "nav-back" })
        {
            Assert.IsFalse(navigation.Contains(gone, StringComparison.Ordinal), gone);
            Assert.IsFalse(pwa.Contains(gone, StringComparison.Ordinal), gone);
            Assert.IsFalse(css.Contains(gone, StringComparison.Ordinal), gone);
        }

        StringAssert.Contains(navigation, "nav-children");
        StringAssert.Contains(File.ReadAllText(Path.Combine(web, "Pages", "Shared", "_AppHeader.cshtml")), "_AppSearch");
    }

    private static Type? PageModelFor(Type[] pages, string href)
    {
        var segments = UiNavigationCatalog.PathOf(href).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var folder = string.Join('.', segments.SkipLast(1).Prepend("Jularr.Web.Pages"));
        var name = segments.Length == 0 ? "Index" : segments[^1];

        return pages.FirstOrDefault(type => type.FullName == $"{folder}.{name}Model")
            ?? pages.FirstOrDefault(type => type.FullName == $"Jularr.Web.Pages.{string.Join('.', segments)}.IndexModel");
    }

    [TestMethod]
    public void ShellMarkupHasNoHardCodedUiText()
    {
        var root = RepositoryRoot();
        var pages = Path.Combine(root, "src", "Jularr.Web", "Pages");
        var files = Directory.EnumerateFiles(Path.Combine(pages, "Shared"), "_App*.cshtml")
            .Where(x => !x.EndsWith("_AppIcon.cshtml", StringComparison.Ordinal))
            .Append(Path.Combine(pages, "Shared", "_Layout.cshtml"))
            .Concat(Directory.EnumerateFiles(Path.Combine(pages, "Account"), "*.cshtml"))
            .Append(Path.Combine(pages, "Settings", "Index.cshtml"))
            .Append(Path.Combine(pages, "Shared", "_LibraryTypeTabs.cshtml"))
            .Concat(Directory.EnumerateFiles(Path.Combine(pages, "Profile"), "*.cshtml"))
            .Concat(Directory.EnumerateFiles(Path.Combine(pages, "Activity"), "*.cshtml"))
            .ToArray();

        Assert.IsTrue(files.Length >= 10, "Expected the shell partials, layout, account and settings pages.");

        var findings = files
            .SelectMany(file => FindLiteralText(File.ReadAllText(file))
                .Select(text => $"{Path.GetFileName(file)}: \"{text}\""))
            .ToArray();

        Assert.AreEqual(0, findings.Length, string.Join(Environment.NewLine, findings));
    }

    [TestMethod]
    public void NavigationUsesRealIconsInsteadOfLetterGlyphs()
    {
        var shared = Path.Combine(RepositoryRoot(), "src", "Jularr.Web", "Pages", "Shared");
        var navigation = File.ReadAllText(Path.Combine(shared, "_AppNavigation.cshtml"));
        var icons = File.ReadAllText(Path.Combine(shared, "_AppIcon.cshtml"));

        Assert.IsFalse(navigation.Contains("nav-glyph", StringComparison.Ordinal));
        Assert.IsFalse(navigation.Contains("brand-mark\">A<", StringComparison.Ordinal));
        StringAssert.Contains(navigation, "UiShellNavigation.Build");

        var nav = UiShellNavigation.Build("/", learningVisible: true, can: Owner);
        foreach (var icon in nav.Primary.Concat(nav.Secondary).Select(x => x.Icon).Append("search"))
        {
            StringAssert.Contains(icons, $"case \"{icon}\":", $"Missing icon {icon}.");
        }
    }

    [TestMethod]
    public void ReadmeDoesNotHardCodeTheBuildVersion()
    {
        var root = RepositoryRoot();
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        var project = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "Jularr.Web.csproj"));

        StringAssert.Matches(project, new Regex(@"<Version>\d+\.\d+\.\d+[^<]*</Version>"));
        Assert.IsFalse(
            Regex.IsMatch(readme, @"\b\d+\.\d+\.\d+-(alpha|beta|rc)\.\d+\b"),
            "README must point to the canonical version source instead of repeating a version.");
        StringAssert.Contains(readme, "src/Jularr.Web/Jularr.Web.csproj");
    }

    private static IEnumerable<string> FindLiteralText(string razor)
    {
        var markup = StripCodeBlocks(Regex.Replace(razor, @"@\*.*?\*@", string.Empty, RegexOptions.Singleline));
        markup = Regex.Replace(markup, @"<script\b[^>]*>.*?</script>", "<script></script>", RegexOptions.Singleline);

        foreach (Match match in Regex.Matches(markup, @">([^<>]+)<"))
        {
            var text = match.Groups[1].Value.Trim();
            if (text.Length > 0
                && !text.Contains('@')
                && text.Any(char.IsLetter)
                && !AllowedLiteralText.Contains(text))
            {
                yield return text;
            }
        }

        foreach (Match match in UserFacingAttribute().Matches(markup))
        {
            var value = match.Groups["value"].Value;
            if (value.Any(char.IsLetter) && !value.Contains('@'))
            {
                yield return $"{match.Groups["name"].Value}={value}";
            }
        }
    }

    private static string StripCodeBlocks(string razor)
    {
        var result = new System.Text.StringBuilder(razor.Length);
        for (var index = 0; index < razor.Length; index++)
        {
            if (razor[index] == '@' && index + 1 < razor.Length && razor[index + 1] == '{')
            {
                var depth = 0;
                for (index++; index < razor.Length; index++)
                {
                    depth += razor[index] switch { '{' => 1, '}' => -1, _ => 0 };
                    if (depth == 0)
                    {
                        break;
                    }
                }

                continue;
            }

            result.Append(razor[index]);
        }

        return result.ToString();
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

    [GeneratedRegex(@"\b(?<name>aria-label|title|placeholder|alt)=""(?<value>[^""]*)""")]
    private static partial Regex UserFacingAttribute();
}
