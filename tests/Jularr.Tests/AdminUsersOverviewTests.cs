namespace Jularr.Tests;

/// <summary>
/// Keeps the Admin → Users overview aligned with the Users & Permissions spec: account administration
/// belongs here, while consumption progress belongs to user/media progress surfaces.
/// </summary>
[TestClass]
public sealed class AdminUsersOverviewTests
{
    [TestMethod]
    public void OverviewIsAnAccountDirectoryAndNotAProgressDashboard()
    {
        var root = RepositoryRoot();
        var page = File.ReadAllText(
            Path.Combine(root, "src", "Jularr.Web", "Pages", "Admin", "Users.cshtml"));
        var model = File.ReadAllText(
            Path.Combine(root, "src", "Jularr.Web", "Pages", "Admin", "Users.cshtml.cs"));

        StringAssert.Contains(page, "data-admin-users");
        StringAssert.Contains(page, "class=\"user-directory-row\"");
        StringAssert.Contains(page, "asp-page=\"/Admin/Capabilities/Index\"");
        StringAssert.Contains(page, "asp-page-handler=\"Create\"");
        StringAssert.Contains(page, "asp-page-handler=\"Approve\"");
        StringAssert.Contains(page, "asp-page-handler=\"Disable\"");
        StringAssert.Contains(page, "asp-page=\"/Admin/User\"");

        Assert.IsFalse(page.Contains("user-progress-grid", StringComparison.Ordinal));
        Assert.IsFalse(page.Contains("CurrentAnime", StringComparison.Ordinal));
        Assert.IsFalse(page.Contains("CurrentNovel", StringComparison.Ordinal));
        Assert.IsFalse(page.Contains(".Learning", StringComparison.Ordinal));

        StringAssert.Contains(model, "adminAccounts.ReadUsersV1(");
        Assert.IsFalse(model.Contains("authService.ListAsync(", StringComparison.Ordinal));
        Assert.IsFalse(model.Contains("AdminUserProgressService", StringComparison.Ordinal));
    }

    [TestMethod]
    public void OverviewHasAResponsiveDirectoryLayout()
    {
        var css = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "css", "site.css"));

        StringAssert.Contains(css, ".user-directory-row {");
        StringAssert.Contains(css, ".user-directory-actions {");
        StringAssert.Contains(css, ".admin-users-create { position: sticky;");
        StringAssert.Contains(css, ".admin-users-create { position: static;");
        StringAssert.Contains(css, ".user-directory-row { align-items: stretch; flex-direction: column;");
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
}
