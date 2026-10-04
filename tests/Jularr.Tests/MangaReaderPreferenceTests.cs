using Jularr.Web.Data;
using Jularr.Web.Features.Manga;
using Jularr.Web.Features.ReaderCore;
using Jularr.Web.Features.ReaderPreferences;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class MangaReaderPreferenceTests
{
    [TestMethod]
    public async Task ImagePreferencesUseFieldLevelWorkOverrides()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jularr-manga-reader-pref-{Guid.NewGuid():N}.db");

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var seriesId = Guid.NewGuid();
            var workId = Guid.NewGuid();

            db.ReaderPreferences.Add(new ReaderPreference
            {
                ProfileId = "reader-a",
                ScopeKey = ReaderPreferenceRules.UserDefaultScope,
                ReadingMode = "continuous",
                ImageFlowMode = "webtoon",
                ImagePageDirection = "auto",
                ImageFit = "width",
                ImageZoomPercent = 120,
                ImagePageGapPx = 12,
                ImageColorScheme = "sepia",
                AutoContinueChapters = false
            });
            await db.SaveChangesAsync();

            var inherited = await MangaReaderPreferenceStore.GetAsync(db, "reader-a", workId, seriesId, CancellationToken.None);
            Assert.AreEqual("webtoon", inherited.UiMode);
            Assert.AreEqual("width", inherited.ImageFit);
            Assert.AreEqual(120, inherited.ImageZoomPercent);
            Assert.AreEqual("sepia", inherited.ImageColorScheme);
            Assert.IsFalse(inherited.AutoContinueChapters);

            await MangaReaderPreferenceStore.SaveAsync(db, "reader-a", workId, new MangaReaderPreferenceInput { ImageZoomPercent = 175 }, "imageZoomPercent", CancellationToken.None);

            var overridden = await MangaReaderPreferenceStore.GetAsync(db, "reader-a", workId, seriesId, CancellationToken.None);
            Assert.AreEqual(180, overridden.ImageZoomPercent);
            Assert.AreEqual("width", overridden.ImageFit);
            Assert.AreEqual("sepia", overridden.ImageColorScheme);
            Assert.AreEqual("webtoon", overridden.UiMode);
            Assert.IsFalse(overridden.AutoContinueChapters);
            Assert.IsTrue(overridden.HasSeriesOverride);

            await MangaReaderPreferenceStore.ResetWorkAsync(db, "reader-a", workId, seriesId, CancellationToken.None);

            var reset = await MangaReaderPreferenceStore.GetAsync(db, "reader-a", workId, seriesId, CancellationToken.None);
            Assert.AreEqual(120, reset.ImageZoomPercent);
            Assert.IsFalse(reset.HasSeriesOverride);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task FullTypePresetPersistsImageSequenceSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jularr-manga-media-pref-{Guid.NewGuid():N}.db");

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var seriesId = Guid.NewGuid();
            var workId = Guid.NewGuid();
            var input = new MangaReaderPreferenceInput
            {
                Mode = "horizontal",
                PageDirection = "ltr",
                ImageFit = "width",
                ImageZoomPercent = 140,
                ImagePageGapPx = 18,
                ImageFirstPageAlone = true,
                AutoContinueChapters = false,
                ImageSharpen = true,
                ImageCropBorders = true,
                ImageColorScheme = "dark"
            };

            await MangaReaderPreferenceStore.SaveAsync(db, "reader-a", workId: null, input, changedKey: null, CancellationToken.None);

            var settings = await MangaReaderPreferenceStore.GetAsync(db, "reader-a", workId, seriesId, CancellationToken.None);
            Assert.AreEqual("horizontal", settings.UiMode);
            Assert.AreEqual("ltr", settings.PageDirection);
            Assert.AreEqual("width", settings.ImageFit);
            Assert.AreEqual(140, settings.ImageZoomPercent);
            Assert.AreEqual(18, settings.ImagePageGapPx);
            Assert.IsTrue(settings.ImageFirstPageAlone);
            Assert.IsFalse(settings.AutoContinueChapters);
            Assert.IsTrue(settings.ImageSharpen);
            Assert.IsTrue(settings.ImageCropBorders);
            Assert.AreEqual("dark", settings.ImageColorScheme);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task LegacyMangaScopesMigrateToCanonicalTypeAndWorkScopes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jularr-manga-pref-migrate-{Guid.NewGuid():N}.db");

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var seriesId = Guid.NewGuid();
            var workId = Guid.NewGuid();
            db.ReaderPreferences.AddRange(
                new ReaderPreference
                {
                    ProfileId = "reader-a",
                    ScopeKey = "media:manga",
                    ReadingMode = "continuous",
                    ImageFlowMode = "webtoon",
                    ImagePageGapPx = 14
                },
                new ReaderPreference
                {
                    ProfileId = "reader-a",
                    ScopeKey = $"media:manga:series:{seriesId:N}",
                    ImageZoomPercent = 170,
                    ImageColorScheme = "dark"
                });
            await db.SaveChangesAsync();

            await MangaReaderPreferenceStore.MigrateLegacyScopesAsync(db, "reader-a", workId, seriesId, CancellationToken.None);

            var preferences = await db.ReaderPreferences.AsNoTracking().Where(x => x.ProfileId == "reader-a").ToListAsync();
            Assert.IsFalse(preferences.Any(x => x.ScopeKey.StartsWith("media:manga", StringComparison.Ordinal)));
            var type = preferences.Single(x => x.ScopeKey == ReaderPreferenceScopes.Type(ReaderContentType.Manga));
            var work = preferences.Single(x => x.ScopeKey == ReaderPreferenceScopes.Work(workId));
            Assert.AreEqual("webtoon", type.ImageFlowMode);
            Assert.AreEqual(14, type.ImagePageGapPx);
            Assert.AreEqual(170, work.ImageZoomPercent);
            Assert.AreEqual("dark", work.ImageColorScheme);
            Assert.AreEqual(workId, work.WorkId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void MangaClientHasNoDurableLocalSettingsStore()
    {
        var root = RepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "manga-reader.js"));
        var page = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "Pages", "Manga", "Read.cshtml"));

        StringAssert.Contains(page, "data-manga-settings");
        StringAssert.Contains(script, "readJson(\"[data-manga-settings]\"");
        StringAssert.Contains(script, "queuePreferenceSave");
        StringAssert.Contains(script, "savePreference(\"series\", \"mode\")");
        StringAssert.Contains(script, "imageZoomPercent");
        StringAssert.Contains(script, "imagePageDirection");
        Assert.IsFalse(script.Contains("localStorage.", StringComparison.Ordinal), "Durable Manga reader settings must use ReaderPreference.");
        Assert.IsFalse(script.Contains("anilingo.reader.manga.", StringComparison.Ordinal), "The legacy per-device Manga settings key must not return.");
    }

    private static async Task<AppDbContext> CreateDatabaseAsync(string path)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path};Foreign Keys=True").Options;
        var db = new AppDbContext(options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        return db;
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
