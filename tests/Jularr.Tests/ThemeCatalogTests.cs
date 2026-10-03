using Jularr.Web.Data;
using Jularr.Web.Features.Appearance;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class ThemeCatalogTests
{
    [TestMethod]
    public void BuiltInCatalogHasOriginalAndCleanPurpleOnly()
    {
        CollectionAssert.AreEqual(
            new[] { ThemeCatalog.Original, ThemeCatalog.CleanPurple },
            ThemeCatalog.All.Select(theme => theme.Id).ToArray());
        Assert.IsTrue(ThemeCatalog.All.Single(theme => theme.Id == ThemeCatalog.Original).UsesOriginalArtwork);
        Assert.IsTrue(ThemeCatalog.All.Where(theme => theme.Id != ThemeCatalog.Original).All(theme => !theme.UsesOriginalArtwork));
    }

    [TestMethod]
    public void DefaultInstanceThemeIsCleanPurple()
    {
        // The out-of-the-box default is the clean, lilac theme (owner-directed design default).
        Assert.AreEqual(ThemeCatalog.CleanPurple, InstanceAppearanceSettings.Default.DefaultThemeId);
    }

    [TestMethod]
    [DataRow(" CLEAN-PURPLE ", ThemeCatalog.CleanPurple)]
    [DataRow("clean-summit", ThemeCatalog.Original)]
    [DataRow("clean-orbit", ThemeCatalog.Original)]
    [DataRow("clean-horizon", ThemeCatalog.Original)]
    [DataRow("not-a-theme", ThemeCatalog.Original)]
    [DataRow(null, ThemeCatalog.Original)]
    public void ThemeIdsAreNormalizedToTheVettedCatalog(string? input, string expected)
    {
        Assert.AreEqual(expected, ThemeCatalog.NormalizeOrOriginal(input));
    }

    [TestMethod]
    public async Task InstancePolicyAndProfileSelectionPersistIndependently()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jularr-theme-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "app.db")};Foreign Keys=True")
                .Options;
            await using var db = new AppDbContext(options);
            await DatabaseMigrationBridge.UpgradeAsync(db);

            var instanceStore = new InstanceAppearanceSettingsStore(db);
            Assert.AreEqual(InstanceAppearanceSettings.Default, await instanceStore.LoadAsync(CancellationToken.None));

            var saved = await instanceStore.SaveAsync(
                new InstanceAppearanceSettings(ThemeCatalog.CleanPurple, false, false),
                CancellationToken.None);
            Assert.AreEqual(new InstanceAppearanceSettings(ThemeCatalog.CleanPurple, false, false), saved);
            Assert.AreEqual(saved, await instanceStore.LoadAsync(CancellationToken.None));

            var profileStore = new ProfileAppearanceStore(db);
            await profileStore.SetThemeIdAsync("alice", ThemeCatalog.CleanPurple, CancellationToken.None);
            await profileStore.SetAccentAsync("alice", "#2C55A8", CancellationToken.None);
            Assert.AreEqual(ThemeCatalog.CleanPurple, (await profileStore.GetAsync("alice", CancellationToken.None)).ThemeId);

            await profileStore.SetThemeIdAsync("alice", null, CancellationToken.None);
            Assert.IsNull((await profileStore.GetAsync("alice", CancellationToken.None)).ThemeId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
