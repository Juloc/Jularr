using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;

namespace Jularr.Tests;

[TestClass]
public sealed class RoleNavigationTests
{
    private static readonly string[] MediaManagerAdminIds =
    [
        "admin-overview",
        "admin-requests",
        "admin-wanted",
        "admin-music",
        "admin-profiles",
        "admin-usenet",
        "admin-anime-acquisition",
        "admin-import",
        "admin-mapping",
        "admin-subtitles",
        "admin-operations",
        "admin-scans",
        "admin-logs",
        "admin-sessions"
    ];

    /// <summary>The policy check a signed-in account with exactly this role passes.</summary>
    public static Func<string, bool> As(AccountRole role) =>
        policy => JularrPolicies.Roles[policy].Contains(role);

    private static IEnumerable<UiNavigationEntry> AdminEntries =>
        UiNavigationCatalog.Admin.SelectMany(section => section.Entries);

    [TestMethod]
    public void EveryAdminEntryDeclaresAKnownPolicy()
    {
        var anchor = UiNavigationCatalog.Secondary.Single(entry => entry.Id == "admin");
        Assert.AreEqual(JularrPolicies.AdminMedia, anchor.Policy);

        foreach (var entry in AdminEntries)
        {
            Assert.IsNotNull(entry.Policy, $"{entry.Id} must say which policy may see it.");
            Assert.IsTrue(JularrPolicies.Roles.ContainsKey(entry.Policy), entry.Id);
        }
    }

    [TestMethod]
    public void MediaManagerSeesOnlyTheMediaAdminPages()
    {
        var nav = UiShellNavigation.Build("/Admin/Operations", learningVisible: true, As(AccountRole.MediaManager));

        Assert.AreEqual("admin", nav.Expanded?.Id);
        CollectionAssert.AreEquivalent(
            MediaManagerAdminIds,
            nav.Expanded!.Groups!.SelectMany(group => group.Items).Concat(nav.Unfinished).Select(item => item.Id).Where(id => id.StartsWith("admin", StringComparison.Ordinal)).ToArray());
        Assert.AreEqual(
            "admin-operations",
            nav.Expanded.Groups!.SelectMany(group => group.Items).Single(item => item.IsActive).Id);

        var section = UiShellNavigation.BuildSection("admin", As(AccountRole.MediaManager));
        Assert.IsNotNull(section);
        CollectionAssert.AreEquivalent(
            MediaManagerAdminIds,
            section.Groups!.SelectMany(group => group.Items).Select(item => item.Id).ToArray());

        var (links, _) = UiShellNavigation.BuildProfile(learningVisible: true, As(AccountRole.MediaManager));
        CollectionAssert.Contains(links.Select(item => item.Id).ToArray(), "admin");
    }

    [TestMethod]
    [DataRow(AccountRole.Owner)]
    [DataRow(AccountRole.MediaManager)]
    [DataRow(AccountRole.User)]
    public void AdminPagesAreListedExactlyWhenTheRolePassesTheirPolicy(AccountRole role)
    {
        var can = As(role);
        var section = UiShellNavigation.BuildSection("admin", can);
        var expected = AdminEntries.Where(entry => can(entry.Policy!)).OrderBy(entry => entry.Unfinished).Select(entry => entry.Id).ToArray();

        if (role == AccountRole.User)
        {
            Assert.IsNull(section, "Users have no Admin section.");
            Assert.IsFalse(UiShellNavigation.Build("/", learningVisible: true, can).Secondary.Any(item => item.Id == "admin"));
            return;
        }

        Assert.IsNotNull(section);
        CollectionAssert.AreEqual(
            expected,
            section.Groups!.SelectMany(group => group.Items).Select(item => item.Id).ToArray());
        Assert.IsTrue(section.Groups!.All(group => group.Items.Count > 0), "Empty groups are dropped.");
    }

    [TestMethod]
    public void OwnerSeesEveryAdminPage()
    {
        var section = UiShellNavigation.BuildSection("admin", As(AccountRole.Owner));

        CollectionAssert.AreEqual(
            AdminEntries.OrderBy(entry => entry.Unfinished).Select(entry => entry.Id).ToArray(),
            section!.Groups!.SelectMany(group => group.Items).Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public void EveryAdminLinkOpensForTheRolesThatSeeIt()
    {
        var pages = typeof(UiShellNavigation).Assembly.GetTypes()
            .Where(type => typeof(Microsoft.AspNetCore.Mvc.RazorPages.PageModel).IsAssignableFrom(type))
            .ToArray();

        foreach (var entry in AdminEntries)
        {
            var model = PageModelFor(pages, entry.Href);
            Assert.IsNotNull(model, $"No page model for {entry.Href}.");
            var policy = model.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true)
                .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
                .Select(attribute => attribute.Policy)
                .SingleOrDefault(value => value is not null);
            Assert.IsNotNull(policy, $"{entry.Href} must declare a policy.");

            foreach (var role in Enum.GetValues<AccountRole>().Where(role => As(role)(entry.Policy!)))
            {
                Assert.IsTrue(As(role)(policy), $"{role} sees {entry.Id} but {entry.Href} requires {policy}.");
            }
        }
    }

    private static Type? PageModelFor(Type[] pages, string href)
    {
        var segments = UiNavigationCatalog.PathOf(href).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var folder = string.Join('.', segments.SkipLast(1).Prepend("Jularr.Web.Pages"));
        var name = segments.Length == 0 ? "Index" : segments[^1];

        return pages.FirstOrDefault(type => type.FullName == $"{folder}.{name}Model")
            ?? pages.FirstOrDefault(type => type.FullName == $"Jularr.Web.Pages.{string.Join('.', segments)}.IndexModel");
    }
}
