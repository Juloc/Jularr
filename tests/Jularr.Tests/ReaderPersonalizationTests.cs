using Jularr.Web.Data;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.ReaderPreferences;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class ReaderPersonalizationTests
{
    [TestMethod]
    public async Task ReaderSettingsUseFieldLevelBookOverrides()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var work = NewWork("settings");
            db.NovelWorks.Add(work);
            await db.SaveChangesAsync();

            await ReaderPreferenceStore.SaveUserDefaultsAsync(
                db,
                "reader-a",
                Defaults(font: "book-serif", paper: "cream"),
                CancellationToken.None);

            await ReaderPreferenceStore.SaveWorkOverrideAsync(
                db,
                "reader-a",
                work.Id,
                "paperStyle",
                new ReaderSettingsInput { PaperStyle = "sepia" },
                CancellationToken.None);

            var first = await ReaderPreferenceStore.GetAsync(
                db,
                "reader-a",
                work.Id,
                """["Fantasy","Adventure"]""",
                CancellationToken.None);

            Assert.AreEqual("book-serif", first.FontFamily);
            Assert.AreEqual("sepia", first.PaperStyle);
            CollectionAssert.Contains(first.SourceGenres.ToArray(), "Fantasy");
            Assert.AreEqual("auto", first.ResolvedGenreTheme);
            Assert.IsTrue(first.HasWorkOverride);

            await ReaderPreferenceStore.SaveUserDefaultsAsync(
                db,
                "reader-a",
                Defaults(font: "system-sans", paper: "white"),
                CancellationToken.None);

            var afterDefaultChange = await ReaderPreferenceStore.GetAsync(
                db,
                "reader-a",
                work.Id,
                null,
                CancellationToken.None);

            Assert.AreEqual("system-sans", afterDefaultChange.FontFamily);
            Assert.AreEqual("sepia", afterDefaultChange.PaperStyle);

            await ReaderPreferenceStore.ResetWorkAsync(
                db,
                "reader-a",
                work.Id,
                CancellationToken.None);

            var reset = await ReaderPreferenceStore.GetAsync(
                db,
                "reader-a",
                work.Id,
                null,
                CancellationToken.None);

            Assert.AreEqual("white", reset.PaperStyle);
            Assert.IsFalse(reset.HasWorkOverride);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task ReaderDefaultsAreProfileScoped()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var work = NewWork("profiles");
            db.NovelWorks.Add(work);
            await db.SaveChangesAsync();

            await ReaderPreferenceStore.SaveUserDefaultsAsync(
                db,
                "reader-a",
                Defaults(font: "literary-serif", paper: "white"),
                CancellationToken.None);
            await ReaderPreferenceStore.SaveUserDefaultsAsync(
                db,
                "reader-b",
                Defaults(font: "noto-sans-jp", paper: "oled"),
                CancellationToken.None);

            var a = await ReaderPreferenceStore.GetAsync(
                db, "reader-a", work.Id, null, CancellationToken.None);
            var b = await ReaderPreferenceStore.GetAsync(
                db, "reader-b", work.Id, null, CancellationToken.None);

            Assert.AreEqual("white", a.PaperStyle);
            Assert.AreEqual("literary-serif", a.FontFamily);
            Assert.AreEqual("oled", b.PaperStyle);
            Assert.AreEqual("noto-sans-jp", b.FontFamily);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task BookmarkAppearanceStaysOnCanonicalBookmark()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var work = NewWork("bookmarks");
            var volume = new NovelVolume { WorkId = work.Id, Number = 1, SourceKey = "web" };
            var chapter = new NovelChapter
            {
                WorkId = work.Id,
                VolumeId = volume.Id,
                Number = 1,
                SourceUrl = "https://example.invalid/bookmarks/1",
                Title = "Chapter One",
                OriginalText = "First paragraph.\n\nSecond paragraph.",
                SourceHash = "BOOKMARK-SOURCE"
            };
            db.Add(work);
            db.Add(volume);
            db.Add(chapter);
            await db.SaveChangesAsync();

            var service = new NovelAnnotationService(db);
            var bookmark = await service.AddBookmarkAsync(
                "reader-a",
                chapter.Id,
                250,
                "ja",
                0,
                2,
                "Important",
                "paper",
                "#123456",
                CancellationToken.None);

            var changed = await service.UpdateBookmarkAppearanceAsync(
                "reader-a",
                bookmark.Id,
                "leather",
                "#abcdef",
                CancellationToken.None);

            Assert.IsNotNull(changed);
            Assert.AreEqual(bookmark.Id, changed.Id);
            Assert.AreEqual("leather", changed.Style);
            Assert.AreEqual("#abcdef", changed.Color);
            Assert.AreEqual(1, await db.NovelBookmarks.CountAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void ReaderUsesAccessibleTextForPagedModeAndSharedSettingsSurface()
    {
        var root = FindRepositoryRoot();
        var page = File.ReadAllText(Path.Combine(
            root, "src", "Jularr.Web", "Pages", "Novels", "Read.cshtml"));
        var script = File.ReadAllText(Path.Combine(
            root, "src", "Jularr.Web", "wwwroot", "js", "reader-personalization.js"));
        var themeScript = File.ReadAllText(Path.Combine(
            root, "src", "Jularr.Web", "wwwroot", "js", "reader-themes.js"));
        var css = File.ReadAllText(Path.Combine(
            root, "src", "Jularr.Web", "wwwroot", "css", "novels.css"));
        var bookPage = File.ReadAllText(Path.Combine(
            root, "src", "Jularr.Web", "Pages", "Books", "Read.cshtml"));
        var sharedSettings = File.ReadAllText(Path.Combine(
            root, "src", "Jularr.Web", "Pages", "Shared", "_ReaderSettingsPanel.cshtml"));
        var bookScript = File.ReadAllText(Path.Combine(
            root, "src", "Jularr.Web", "wwwroot", "js", "books-reader.js"));

        StringAssert.Contains(page, "data-reader-autoscroll-toggle");
        StringAssert.Contains(page, "data-reader-frame");
        StringAssert.Contains(page, "data-reader-progress-slider");
        Assert.IsFalse(page.Contains("data-reader-page-step", StringComparison.Ordinal), "Phones turn pages by tap and swipe; Previous/Next are chapters.");
        StringAssert.Contains(page, "data-reader-settings-open=\"reading\"");
        StringAssert.Contains(page, "_ReaderSettingsPanel");
        StringAssert.Contains(page, "novels.read.chapterLabel");
        StringAssert.Contains(sharedSettings, "data-reader-genre-select");
        StringAssert.Contains(sharedSettings, "data-reader-background-select");
        StringAssert.Contains(sharedSettings, "data-setting-key=\"bookmarkStyle\"");
        Assert.IsFalse(page.Contains("<option value=\"horror\">", StringComparison.Ordinal));

        StringAssert.Contains(script, "captureLogicalAnchor");
        StringAssert.Contains(script, "sendPagedProgress");
        StringAssert.Contains(script, "prefers-reduced-motion");
        StringAssert.Contains(script, "google:");
        StringAssert.Contains(themeScript, "/api/reader-themes");
        StringAssert.Contains(script, "backgroundAssetId");
        Assert.IsFalse(script.Contains("/api/reader-backgrounds", StringComparison.Ordinal));
        Assert.IsFalse(script.Contains("canvas", StringComparison.OrdinalIgnoreCase));

        StringAssert.Contains(themeScript, "jularr:reader-settings");
        StringAssert.Contains(themeScript, "prefers-reduced-motion");
        StringAssert.Contains(themeScript, "parallaxStrength");

        StringAssert.Contains(bookPage, "data-reader-personalization");
        StringAssert.Contains(bookPage, "reader-themes.css");
        StringAssert.Contains(bookPage, "_ReaderSettingsPanel");
        StringAssert.Contains(bookScript, "jularr:reader-settings");
        StringAssert.Contains(bookScript, "themeParallaxStrength");

        StringAssert.Contains(css, "[data-reading-mode=\"paged\"]");
        StringAssert.Contains(css, "[data-paper-style=\"oled\"]");
        StringAssert.Contains(css, "[data-chapter-style=\"light-novel\"]");
        StringAssert.Contains(css, "[data-bookmark-style=\"fabric\"]");
    }

    [TestMethod]
    public void ReaderSettingsOptionLabelsComeFromTheCatalog()
    {
        var panel = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Jularr.Web", "Pages", "Shared", "_ReaderSettingsPanel.cshtml"));

        // Typeface names are proper nouns and stay verbatim; every other option label is UI text.
        string[] typefaceNames =
        [
            "Atkinson Hyperlegible", "Noto Serif JP", "Noto Sans JP", "Literata", "Lora", "Merriweather"
        ];

        var literals = System.Text.RegularExpressions.Regex
            .Matches(panel, @"<option\b[^>]*>(?<text>[^<]*)</option>")
            .Select(match => match.Groups["text"].Value.Trim())
            .Where(text => text.Length > 0 && !text.StartsWith('@') && !typefaceNames.Contains(text))
            .ToArray();

        Assert.AreEqual(0, literals.Length, "Hard-coded option labels: " + string.Join(", ", literals));
        StringAssert.Contains(panel, "@ui[\"reader.settings.chapterStyle.classic\"]");
        StringAssert.Contains(panel, "@ui[\"reader.frame.paper.oled\"]");
        StringAssert.Contains(panel, "@ui[\"reader.settings.bookmarkStyle.fabric\"]");
    }

    private static ReaderSettingsInput Defaults(string font, string paper) =>
        new()
        {
            ReadingMode = "continuous",
            PageTransition = "curl",
            TwoPageSpread = true,
            AutoScrollSpeed = 42,
            FontFamily = font,
            FontSizeRem = 1.1,
            LineHeight = 1.9,
            ParagraphSpacingEm = .8,
            TextWidthPx = 760,
            TextAlignment = "start",
            ChapterStyle = "light-novel",
            PaperStyle = paper,
            GenreArtworkEnabled = true,
            GenreTheme = "auto",
            BackgroundAssetId = "auto",
            BackgroundIntensity = .05,
            BookmarkStyle = "fabric",
            BookmarkColor = "#b04455"
        };

    private static NovelWork NewWork(string key) =>
        new()
        {
            SourceProvider = "fake",
            SourceKey = key,
            SourceUrl = $"https://example.invalid/{key}",
            Title = key
        };

    private static string TempDatabasePath() =>
        Path.Combine(
            Path.GetTempPath(),
            $"jularr-reader-{Guid.NewGuid():N}.db");

    private static async Task<AppDbContext> CreateDatabaseAsync(string path)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Foreign Keys=True")
            .Options;
        var db = new AppDbContext(options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        return db;
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

        throw new DirectoryNotFoundException("Could not locate Jularr repository root.");
    }
}
