using Jularr.Web.Data;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.ReaderCore;
using Jularr.Web.Features.ReaderPreferences;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class ReaderPreferenceCascadeTests
{
    [TestMethod]
    public async Task ReaderSettingsCascadeGlobalTypeGenreWorkPerField()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var work = NewWork();
            db.NovelWorks.Add(work);
            await db.SaveChangesAsync();

            await ReaderPreferenceStore.SaveScopeFieldAsync(
                db,
                "profile",
                ReaderPreferenceRules.UserDefaultScope,
                "fontSizeRem",
                new ReaderSettingsInput { FontSizeRem = 1.15 },
                CancellationToken.None);

            await ReaderPreferenceStore.SaveScopeFieldAsync(
                db,
                "profile",
                ReaderPreferenceScopes.Type(ReaderContentType.LightNovel),
                "readingMode",
                new ReaderSettingsInput { ReadingMode = "paged" },
                CancellationToken.None);

            await ReaderPreferenceStore.SaveScopeFieldAsync(
                db,
                "profile",
                ReaderPreferenceScopes.Genre("Horror", 700),
                "paperStyle",
                new ReaderSettingsInput { PaperStyle = "oled" },
                CancellationToken.None);

            await ReaderPreferenceStore.SaveWorkOverrideAsync(
                db,
                "profile",
                work.Id,
                "fontSizeRem",
                new ReaderSettingsInput { FontSizeRem = 1.3 },
                CancellationToken.None);

            var settings = await ReaderPreferenceStore.GetAsync(
                db,
                "profile",
                work.Id,
                """["Romance","Horror"]""",
                ReaderContentType.LightNovel,
                CancellationToken.None);

            Assert.AreEqual("paged", settings.ReadingMode);
            Assert.AreEqual("oled", settings.PaperStyle);
            Assert.AreEqual(1.3, settings.FontSizeRem);
            Assert.AreEqual("work:" + work.Id.ToString("N"), settings.EffectiveSources["fontSizeRem"]);
            Assert.AreEqual("type:light-novel", settings.EffectiveSources["readingMode"]);
            StringAssert.StartsWith(settings.EffectiveSources["paperStyle"], "genre:700:horror");
            Assert.IsTrue(settings.HasTypeOverride);
            Assert.IsTrue(settings.HasGenreOverride);
            Assert.IsTrue(settings.HasWorkOverride);

            await ReaderPreferenceStore.ResetScopeFieldAsync(
                db,
                "profile",
                ReaderPreferenceScopes.Work(work.Id),
                "fontSizeRem",
                CancellationToken.None);

            var inherited = await ReaderPreferenceStore.GetAsync(
                db,
                "profile",
                work.Id,
                """["Horror"]""",
                ReaderContentType.LightNovel,
                CancellationToken.None);

            Assert.AreEqual(1.15, inherited.FontSizeRem);
            Assert.AreEqual("default", inherited.EffectiveSources["fontSizeRem"]);
            Assert.IsFalse(inherited.HasWorkOverride);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task HigherPriorityGenreWinsOnlyFieldsItDefines()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var work = NewWork();
            db.NovelWorks.Add(work);
            await db.SaveChangesAsync();

            await ReaderPreferenceStore.SaveScopeFieldAsync(
                db,
                "profile",
                ReaderPreferenceScopes.Genre("Fantasy", 400),
                "paperStyle",
                new ReaderSettingsInput { PaperStyle = "cream" },
                CancellationToken.None);

            await ReaderPreferenceStore.SaveScopeFieldAsync(
                db,
                "profile",
                ReaderPreferenceScopes.Genre("Romance", 800),
                "paperStyle",
                new ReaderSettingsInput { PaperStyle = "sepia" },
                CancellationToken.None);

            await ReaderPreferenceStore.SaveScopeFieldAsync(
                db,
                "profile",
                ReaderPreferenceScopes.Genre("Fantasy", 400),
                "fontFamily",
                new ReaderSettingsInput { FontFamily = "book-serif" },
                CancellationToken.None);

            var settings = await ReaderPreferenceStore.GetAsync(
                db,
                "profile",
                work.Id,
                """["Fantasy","Romance"]""",
                ReaderContentType.Book,
                CancellationToken.None);

            Assert.AreEqual("sepia", settings.PaperStyle);
            Assert.AreEqual("book-serif", settings.FontFamily);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task SavingAScopeSnapshotDoesNotEraseExistingNullableLayoutOverrides()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var work = NewWork();
            db.NovelWorks.Add(work);
            await db.SaveChangesAsync();

            var scope = ReaderPreferenceScopes.Type(ReaderContentType.LightNovel);
            await ReaderPreferenceStore.SaveScopeFieldAsync(
                db,
                "profile",
                scope,
                "hyphenation",
                new ReaderSettingsInput { Hyphenation = false },
                CancellationToken.None);
            await ReaderPreferenceStore.SaveScopeFieldAsync(
                db,
                "profile",
                scope,
                "showPageNumbers",
                new ReaderSettingsInput { ShowPageNumbers = false },
                CancellationToken.None);
            await ReaderPreferenceStore.SaveScopeFieldAsync(
                db,
                "profile",
                scope,
                "showIllustrations",
                new ReaderSettingsInput { ShowIllustrations = true },
                CancellationToken.None);
            await ReaderPreferenceStore.SaveScopeFieldAsync(
                db,
                "profile",
                scope,
                "paragraphIndent",
                new ReaderSettingsInput { ParagraphIndent = false },
                CancellationToken.None);
            await ReaderPreferenceStore.SaveScopeFieldAsync(
                db,
                "profile",
                scope,
                "autoContinueChapters",
                new ReaderSettingsInput { AutoContinueChapters = true },
                CancellationToken.None);

            // Scope-copy/save payloads created by older clients do not contain the
            // newer nullable layout fields. Missing values must mean "leave the
            // existing override alone", not false/reset.
            await ReaderPreferenceStore.SaveTypeDefaultsAsync(
                db,
                "profile",
                ReaderContentType.LightNovel,
                new ReaderSettingsInput
                {
                    ReadingMode = "paged",
                    ChapterStyle = "light-novel",
                    PaperStyle = "cream"
                },
                CancellationToken.None);

            var settings = await ReaderPreferenceStore.GetAsync(
                db,
                "profile",
                work.Id,
                null,
                ReaderContentType.LightNovel,
                CancellationToken.None);

            Assert.IsFalse(settings.Hyphenation);
            Assert.IsFalse(settings.ShowPageNumbers);
            Assert.IsTrue(settings.ShowIllustrations);
            Assert.IsFalse(settings.ParagraphIndent);
            Assert.IsTrue(settings.AutoContinueChapters);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void ScopeKeysAreStableAndGenreSafe()
    {
        Assert.AreEqual("type:web-novel", ReaderPreferenceScopes.Type(ReaderContentType.WebNovel));
        Assert.AreEqual("genre:500:slice-of-life", ReaderPreferenceScopes.Genre("Slice of Life"));
        Assert.AreEqual("genre:900:sci-fi", ReaderPreferenceScopes.Genre("Sci-Fi", 900));
    }

    private static NovelWork NewWork() =>
        new()
        {
            SourceProvider = "test",
            SourceKey = Guid.NewGuid().ToString("N"),
            SourceUrl = "https://example.invalid/work",
            Title = "Reader test"
        };

    private static string TempDatabasePath() =>
        Path.Combine(
            Path.GetTempPath(),
            $"jularr-reader-cascade-{Guid.NewGuid():N}.db");

    private static async Task<AppDbContext> CreateDatabaseAsync(string path)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Foreign Keys=True")
            .Options;
        var db = new AppDbContext(options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        return db;
    }
}
