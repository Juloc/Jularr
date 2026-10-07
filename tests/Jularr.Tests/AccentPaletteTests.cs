using System.Globalization;
using Jularr.Web.Data;
using Jularr.Web.Features.Appearance;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class AccentPaletteTests
{
    private static readonly string[] TextGrounds =
        ["--bg", "--surface", "--surface-2", "--surface-raised", "--sidebar-bg", "--field-bg", "--row-bg", "--row-hover"];

    public static IEnumerable<object[]> SeedSweep()
    {
        foreach (var preset in AppAccent.Presets)
        {
            yield return [preset.Seed];
        }

        string[] extremes = ["#ffffff", "#000000", "#808080", "#ffff00", "#00ff00", "#00ffff", "#ff00ff", "#0000ff", "#ff0000", "#fff5f8", "#101830", "#7fff00"];
        foreach (var seed in extremes)
        {
            yield return [seed];
        }

        for (var hue = 0; hue < 360; hue += 15)
        {
            foreach (var lightness in new[] { 0.25, 0.5, 0.7, 0.92 })
            {
                foreach (var chroma in new[] { 0.04, 0.14, 0.3 })
                {
                    yield return [ColorMath.OklchToHex(new OklchColor(lightness, chroma, hue))];
                }
            }
        }
    }

    [TestMethod]
    [DynamicData(nameof(SeedSweep))]
    public void EveryDerivedTextPairMeetsWcagInBothModes(string seed)
    {
        var palette = AccentPalette.Build(seed);
        foreach (var mode in new[] { AppTheme.Light, AppTheme.Dark })
        {
            var tokens = palette.For(mode);
            var context = $"{seed} {mode}";

            AssertContrast(tokens, "--on-accent", "--accent", AccentPalette.TextContrast, context);
            AssertContrast(tokens, "--on-accent", "--accent-hover", AccentPalette.TextContrast, context);
            AssertContrast(tokens, "--accent-text", "--accent-soft", AccentPalette.TextContrast, context);
            foreach (var ground in TextGrounds)
            {
                AssertContrast(tokens, "--text", ground, 12, context);
                AssertContrast(tokens, "--text-secondary", ground, AccentPalette.SecondaryTextContrast, context);
                AssertContrast(tokens, "--muted", ground, AccentPalette.TextContrast, context);
                AssertContrast(tokens, "--accent-text", ground, AccentPalette.TextContrast, context);
            }

            AssertContrast(tokens, "--muted", "--surface-3", AccentPalette.TextContrast, context);
            AssertContrast(tokens, "--focus-ring", "--bg", 3, context);
        }
    }

    [TestMethod]
    public void DefaultSeedIsJularrLilaWithWhiteLabels()
    {
        // No stored accent (a fresh profile or an anonymous request) renders the default lila.
        var palette = AccentPalette.Build(null);

        Assert.AreEqual(AppAccent.DefaultSeed, palette.Seed);
        Assert.AreEqual("#ffffff", palette.Light["--on-accent"]);
        Assert.AreEqual("#ffffff", palette.Dark["--on-accent"]);
    }

    [TestMethod]
    public void AppearanceResetButtonRestoresTheDefaultLilaNotTheOldBrandRed()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory, "Could not locate Jularr repository root.");
        var web = Path.Combine(directory.FullName, "src", "Jularr.Web");
        var page = File.ReadAllText(Path.Combine(web, "Pages", "Settings", "Appearance.cshtml"));
        var script = File.ReadAllText(Path.Combine(web, "wwwroot", "js", "appearance-settings.js"));

        StringAssert.Contains(page, "data-default-seed=\"@AppAccent.DefaultSeed\"");
        Assert.IsFalse(page.Contains("BrandSeed", StringComparison.Ordinal), "The reset seed must be the default lila.");
        StringAssert.Contains(script, "host.dataset.defaultSeed");
        StringAssert.Contains(script, "save(defaultSeed)");
        Assert.IsFalse(script.Contains("brandSeed", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BrandSeedKeepsJularrRedWithWhiteLabels()
    {
        // The ink-seal red stays selectable and anchors the ink-and-paper artwork (0deg rotation).
        var palette = AccentPalette.Build(AppAccent.BrandSeed);

        Assert.AreEqual(AppAccent.BrandSeed, palette.Seed);
        Assert.AreEqual(AppAccent.BrandSeed, palette.Light["--accent"]);
        Assert.AreEqual("#ffffff", palette.Light["--on-accent"]);
        Assert.AreEqual("#ffffff", palette.Dark["--on-accent"]);
        Assert.AreEqual("0deg", palette.Light["--art-hue-rotate"]);
        Assert.AreEqual("1", palette.Light["--art-saturate"]);
    }

    [TestMethod]
    public void UsableSeedIsRenderedExactlyAsChosen()
    {
        var palette = AccentPalette.Build("#2c55a8");

        Assert.AreEqual("#2c55a8", palette.Light["--accent"]);
    }

    [TestMethod]
    public void MonochromeSeedsProduceAnInkInterfaceWithColourfulCharts()
    {
        foreach (var seed in new[] { "#ffffff", "#000000", "#8a8a8a" })
        {
            var palette = AccentPalette.Build(seed);

            Assert.IsTrue(palette.IsMonochrome, seed);
            Assert.IsTrue(ColorMath.HexToOklch(palette.Light["--accent"]).C < 0.01, seed);
            Assert.IsTrue(ColorMath.HexToOklch(palette.Light["--accent"]).L < 0.3, seed);
            Assert.IsTrue(ColorMath.HexToOklch(palette.Dark["--accent"]).L > 0.85, seed);
            Assert.AreEqual("0", palette.Light["--art-saturate"], seed);
            Assert.IsTrue(ColorMath.HexToOklch(palette.Light["--data-1"]).C > 0.1, seed);
        }
    }

    [TestMethod]
    public void NeutralsFollowTheSeedHue()
    {
        var blue = ColorMath.HexToOklch(AccentPalette.Build("#2c55a8").Light["--bg"]);
        var green = ColorMath.HexToOklch(AccentPalette.Build("#5b8a3c").Light["--bg"]);

        Assert.IsTrue(blue.C > 0.003 && blue.C < 0.02);
        Assert.IsTrue(Math.Abs(blue.H - green.H) > 60);
    }

    [TestMethod]
    public void ArtworkRotationFollowsAccentHue()
    {
        var blue = AccentPalette.Build("#2c55a8").Light["--art-hue-rotate"];
        var rotation = double.Parse(blue.TrimEnd('d', 'e', 'g'), CultureInfo.InvariantCulture);

        Assert.IsTrue(rotation is < -100 and > -150, blue);
    }

    [TestMethod]
    public void StyleSheetCoversLightDarkAndSystemModes()
    {
        var css = AccentPalette.Build("#5b8a3c").ToStyleSheet();

        StringAssert.Contains(css, ":root,:root[data-app-theme=\"light\"]{color-scheme:light;");
        StringAssert.Contains(css, ":root[data-app-theme=\"dark\"]{color-scheme:dark;");
        StringAssert.Contains(css, "@media (prefers-color-scheme: dark){:root[data-app-theme=\"system\"]{color-scheme:dark;");
        Assert.IsFalse(css.Contains('<'));
    }

    [TestMethod]
    [DataRow("", true, null)]
    [DataRow("  ", true, null)]
    [DataRow("#C8102E", true, "#c8102e")]
    [DataRow("2c55a8", true, "#2c55a8")]
    [DataRow("#abc", false, null)]
    [DataRow("red", false, null)]
    [DataRow("#12345g", false, null)]
    [DataRow("</style><script>", false, null)]
    public void AccentInputIsNormalised(string input, bool valid, string? expected)
    {
        Assert.AreEqual(valid, AppAccent.TryNormalize(input, out var normalized));
        Assert.AreEqual(expected, normalized);
    }

    [TestMethod]
    public void OutOfGammutColoursKeepTheirHue()
    {
        var hex = ColorMath.OklchToHex(new OklchColor(0.6, 0.5, 250));
        var back = ColorMath.HexToOklch(hex);

        Assert.AreEqual(250, back.H, 3);
        Assert.AreEqual(0.6, back.L, 0.02);
    }

    [TestMethod]
    public async Task StoreKeepsAccentAndThemeIndependentPerProfile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jularr-appearance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "app.db")};Foreign Keys=True")
                .Options;
            await using (var db = new AppDbContext(options))
            {
                await DatabaseMigrationBridge.UpgradeAsync(db);
                var store = new ProfileAppearanceStore(db);

                Assert.AreEqual(ProfileAppearance.Default, await store.GetAsync("alice", CancellationToken.None));

                Assert.AreEqual("#2c55a8", await store.SetAccentAsync("alice", "#2C55A8", CancellationToken.None));
                await store.SetThemeAsync("alice", AppTheme.Dark, CancellationToken.None);
                await store.SetThemeAsync("bob", AppTheme.Light, CancellationToken.None);

                Assert.AreEqual(new ProfileAppearance(AppTheme.Dark, "#2c55a8", AppSakura.Subtle), await store.GetAsync("alice", CancellationToken.None));
                Assert.AreEqual(new ProfileAppearance(AppTheme.Light, null, AppSakura.Subtle), await store.GetAsync("bob", CancellationToken.None));

                Assert.IsNull(await store.SetAccentAsync("alice", "", CancellationToken.None));
                Assert.AreEqual(new ProfileAppearance(AppTheme.Dark, null, AppSakura.Subtle), await store.GetAsync("alice", CancellationToken.None));

                await Assert.ThrowsExactlyAsync<ArgumentException>(
                    () => store.SetAccentAsync("alice", "blue", CancellationToken.None));

                Assert.IsTrue((await store.GetAsync("alice", CancellationToken.None)).ShowAdminShortcut, "The Admin shortcut is on by default.");
                await store.SetAdminShortcutAsync("alice", false, CancellationToken.None);
                Assert.IsFalse((await store.GetAsync("alice", CancellationToken.None)).ShowAdminShortcut);
                Assert.IsTrue((await store.GetAsync("bob", CancellationToken.None)).ShowAdminShortcut, "The switch is per profile.");
                Assert.AreEqual(AppTheme.Dark, (await store.GetAsync("alice", CancellationToken.None)).ThemeMode, "Changing the switch keeps the theme.");

                await Assert.ThrowsExactlyAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync(
                    "UPDATE \"UiProfileThemes\" SET \"AccentColor\" = 'blue' WHERE \"ProfileId\" = 'bob'"));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task StorePersistsSakuraModeIndependentlyOfThemeAndAccent()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jularr-appearance-sakura-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "app.db")};Foreign Keys=True")
                .Options;
            await using (var db = new AppDbContext(options))
            {
                await DatabaseMigrationBridge.UpgradeAsync(db);
                var store = new ProfileAppearanceStore(db);

                // A profile with no row yet defaults to the recommended "subtle" density.
                Assert.AreEqual(AppSakura.Subtle, (await store.GetAsync("alice", CancellationToken.None)).SakuraMode);

                await store.SetAccentAsync("alice", "#2c55a8", CancellationToken.None);
                await store.SetSakuraAsync("alice", AppSakura.Full, CancellationToken.None);
                await store.SetSakuraAsync("bob", AppSakura.Off, CancellationToken.None);

                Assert.AreEqual(
                    new ProfileAppearance(AppTheme.System, "#2c55a8", AppSakura.Full),
                    await store.GetAsync("alice", CancellationToken.None));
                Assert.AreEqual(
                    new ProfileAppearance(AppTheme.System, null, AppSakura.Off),
                    await store.GetAsync("bob", CancellationToken.None));

                // Changing theme/accent afterwards must not reset the Sakura choice.
                await store.SetThemeAsync("alice", AppTheme.Dark, CancellationToken.None);
                Assert.AreEqual(AppSakura.Full, (await store.GetAsync("alice", CancellationToken.None)).SakuraMode);

                await Assert.ThrowsExactlyAsync<ArgumentException>(
                    () => store.SetSakuraAsync("alice", "extreme", CancellationToken.None));

                await Assert.ThrowsExactlyAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync(
                    "UPDATE \"UiProfileThemes\" SET \"SakuraMode\" = 'extreme' WHERE \"ProfileId\" = 'bob'"));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("off", true, "off")]
    [DataRow("Subtle", true, "subtle")]
    [DataRow(" full ", true, "full")]
    [DataRow("", false, "subtle")]
    [DataRow("extreme", false, "subtle")]
    public void SakuraModeIsNormalised(string input, bool valid, string expected)
    {
        Assert.AreEqual(valid, AppSakura.TryNormalize(input, out var normalized));
        Assert.AreEqual(expected, normalized);
    }

    private static void AssertContrast(
        IReadOnlyDictionary<string, string> tokens,
        string foreground,
        string background,
        double minimum,
        string context)
    {
        var ratio = ColorMath.Contrast(tokens[foreground], tokens[background]);
        Assert.IsTrue(
            ratio >= minimum - 0.005,
            $"{context}: {foreground} {tokens[foreground]} on {background} {tokens[background]} is {ratio:0.00}:1, needs {minimum}:1");
    }
}
