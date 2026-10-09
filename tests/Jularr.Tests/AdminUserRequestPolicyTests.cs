namespace Jularr.Tests;

[TestClass]
public sealed class AdminUserRequestPolicyTests
{
    [TestMethod]
    public void UserDetailExplainsRequestPolicyFromCanonicalStores()
    {
        var root = RepositoryRoot();
        var page = File.ReadAllText(
            Path.Combine(root, "src", "Jularr.Web", "Pages", "Admin", "User.cshtml"));
        var model = File.ReadAllText(
            Path.Combine(root, "src", "Jularr.Web", "Pages", "Admin", "User.cshtml.cs"));

        StringAssert.Contains(page, "data-admin-user-request-policy");
        StringAssert.Contains(page, "Model.RequestCapabilities");
        StringAssert.Contains(page, "Model.AutoApprovalRules");
        StringAssert.Contains(page, "admin.capabilities.level.");
        StringAssert.Contains(page, "asp-page=\"/Admin/Requests\"");
        StringAssert.Contains(page, "asp-fragment=\"auto-approval\"");

        StringAssert.Contains(model, "MediaCapabilityStore? mediaCapabilities");
        StringAssert.Contains(model, "AcquisitionRequestSettingsStore? requestSettings");
        StringAssert.Contains(model, "IInstanceModuleService? instanceModules");
        StringAssert.Contains(model, "mediaCapabilities.LoadAsync(cancellationToken)");
        StringAssert.Contains(model, "requestSettings.LoadAsync(cancellationToken)");
        StringAssert.Contains(model, "policy.Resolve(");
        StringAssert.Contains(model, "InstanceModule.Acquisition");
        StringAssert.Contains(model, "AcquisitionInstanceModules.For(kind)");

        Assert.IsFalse(
            page.Contains("asp-page-handler=\"RequestPolicy", StringComparison.Ordinal),
            "The user page must not create a second request-policy editor.");
    }

    [TestMethod]
    public void RequestPolicyHasRecoverableErrorAndResponsiveTable()
    {
        var root = RepositoryRoot();
        var page = File.ReadAllText(
            Path.Combine(root, "src", "Jularr.Web", "Pages", "Admin", "User.cshtml"));
        var css = File.ReadAllText(
            Path.Combine(root, "src", "Jularr.Web", "wwwroot", "css", "site.css"));

        StringAssert.Contains(page, "Model.RequestPolicyFailed");
        StringAssert.Contains(page, "capability-matrix-scroll");
        StringAssert.Contains(css, ".admin-user-request-policy {");
        StringAssert.Contains(css, ".admin-user-request-rule {");
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
