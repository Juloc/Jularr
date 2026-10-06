namespace Jularr.Tests;

[TestClass]
public sealed class ProfileScopedBrowserPreferenceTests
{
    [TestMethod]
    public void LayoutExposesAuthenticatedProfileForBrowserState()
    {
        var root = FindRepositoryRoot();
        var layout = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Jularr.Web",
            "Pages",
            "Shared",
            "_Layout.cshtml"));

        StringAssert.Contains(layout, "ClaimTypes.NameIdentifier");
        StringAssert.Contains(layout, "data-profile-id=\"@profileId\"");
    }

    [TestMethod]
    public void PlayerPreferenceIsProfileScoped()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Jularr.Web",
            "wwwroot",
            "js",
            "episode-player.js"));

        StringAssert.Contains(
            script,
            "jularr.profile.${profileId}.playbackMode");
        Assert.IsFalse(
            script.Contains(
                "\"jularr.playbackMode\"",
                StringComparison.Ordinal));
    }

    [TestMethod]
    public void NovelReaderPreferencesAreProfileScoped()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Jularr.Web",
            "wwwroot",
            "js",
            "novel-reader.js"));

        StringAssert.Contains(
            script,
            "jularr.profile.${profileId}.novel");
        Assert.IsFalse(
            script.Contains(
                "\"jularr.novel.view\"",
                StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
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

        throw new DirectoryNotFoundException(
            "Could not locate Jularr repository root.");
    }
}
