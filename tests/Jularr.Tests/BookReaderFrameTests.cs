using System.Text.RegularExpressions;
using Jularr.Web.Data;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.ReaderCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>
/// The Books reader renders the shared reader frame (docs/UNIFIED_READER.md
/// "Reader frame"): shared chrome from reader-shell.js/css and the reader icon
/// partial, the book-specific paper layer in book-reader.css/books-reader.js.
/// </summary>
[TestClass]
public sealed class BookReaderFrameTests
{
    [TestMethod]
    public void BookReaderRendersTheSharedFrameWithWorkingControls()
    {
        var page = Read("src", "Jularr.Web", "Pages", "Books", "Read.cshtml");
        var sheet = Read("src", "Jularr.Web", "Pages", "Shared", "_ReaderAppearanceSheet.cshtml");

        StringAssert.Contains(page, "data-reader-frame");
        StringAssert.Contains(page, "data-reader-chrome-primary");
        StringAssert.Contains(page, "_ReaderIcon");
        StringAssert.Contains(page, "~/css/book-reader.css");
        StringAssert.Contains(page, "data-reader-progress-slider");
        StringAssert.Contains(page, "data-reader-progress-text");
        StringAssert.Contains(page, "data-reader-contents");
        StringAssert.Contains(page, "data-reader-contents-tab=\"chapters\"");
        StringAssert.Contains(page, "data-reader-contents-tab=\"bookmarks\"");
        StringAssert.Contains(page, "data-reader-contents-tab=\"notes\"");
        StringAssert.Contains(page, "data-reader-contents-tab=\"details\"");
        StringAssert.Contains(page, "data-reader-settings-open=\"tts\"");
        StringAssert.Contains(page, "data-reader-fullscreen-toggle");
        StringAssert.Contains(page, "data-bookmark-button");
        StringAssert.Contains(page, "data-book-search-form");
        StringAssert.Contains(page, "data-offline-save=\"@work.Id\"");

        // Every menu toggle points at a rendered menu.
        var toggles = Regex.Matches(page, "data-reader-menu-toggle=\"(?<name>[a-z]+)\"")
            .Select(x => x.Groups["name"].Value)
            .Distinct();
        foreach (var name in toggles)
        {
            StringAssert.Contains(page + sheet, $"data-reader-menu=\"{name}\"", $"Menu '{name}' has no panel.");
        }

        // Read-aloud entry points exist only behind the TTS capability.
        var ttsBlocks = Regex.Matches(page, @"@if \(supportsTts\)\s*\{(?<body>.*?)\n            \}", RegexOptions.Singleline)
            .Select(x => x.Groups["body"].Value)
            .ToArray();
        Assert.IsTrue(ttsBlocks.Count(x => x.Contains("data-reader-tts-toggle", StringComparison.Ordinal)) >= 2);
        var ungated = Regex.Replace(page, @"@if \(supportsTts\)\s*\{.*?\n            \}", "", RegexOptions.Singleline);
        Assert.IsFalse(ungated.Contains("data-reader-tts-toggle", StringComparison.Ordinal));
        Assert.IsFalse(page.Contains("<iframe", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void BookReaderKeepsOfflineWritesAndChapterNavigation()
    {
        var script = Read("src", "Jularr.Web", "wwwroot", "js", "books-reader.js");

        StringAssert.Contains(script, "repository.queueProgress(");
        StringAssert.Contains(script, "repository.queueBookmarkUpsert(");
        StringAssert.Contains(script, "repository.queueBookmarkRemove(");
        StringAssert.Contains(script, "initializeOfflineChapterNavigation");
        StringAssert.Contains(script, "linkSelector: \"a[data-book-chapter-link]\"");
        StringAssert.Contains(script, "jularr:reader-page-edge");
        StringAssert.Contains(script, "jularr:reader-seek");
        StringAssert.Contains(script, "jularr:reader-location");
        StringAssert.Contains(script, "prefers-reduced-motion");
        StringAssert.Contains(script, "document.fonts");
        Assert.IsFalse(script.Contains("handler=Progress", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SharedShellOwnsTheFrameChromeAndLeavesClassicReadersAlone()
    {
        var shell = Read("src", "Jularr.Web", "wwwroot", "js", "reader-shell.js");
        var css = Read("src", "Jularr.Web", "wwwroot", "css", "reader-shell.css");

        StringAssert.Contains(shell, "const frame = root.hasAttribute(\"data-reader-frame\")");
        StringAssert.Contains(shell, "if (frame || root.querySelector(\"[data-reader-mobile-actions]\")) return;");
        StringAssert.Contains(shell, "if (frame) return root.querySelector('[data-reader-menu=\"more\"]');");
        StringAssert.Contains(shell, "jularr:reader-location");
        StringAssert.Contains(shell, "jularr:reader-seek");
        StringAssert.Contains(shell, "openSettings");
        StringAssert.Contains(css, ".reader-frame");
        StringAssert.Contains(css, "[data-reader-frame] > .reader-frame-top");
        // No backdrop-filter on the bars: it would trap the fixed mobile sheets.
        var frameCss = css[css.IndexOf("/* Reader frame ---", StringComparison.Ordinal)..];
        Assert.IsFalse(frameCss.Contains("backdrop-filter", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ReadAloudTextComesFromTheCatalog()
    {
        var tts = Read("src", "Jularr.Web", "wwwroot", "js", "reader-tts.js");
        var partial = Read("src", "Jularr.Web", "Pages", "Shared", "_ReaderSettingsPanel.cshtml");

        StringAssert.Contains(partial, "ui.WithPrefix(\"reader.tts.\")");
        StringAssert.Contains(tts, "[data-reader-tts-text]");
        foreach (var german in new[] { "Vorlesen pausieren", "Absatz vorlesen", "Seite vorlesen", "Liest vor", "Stopp" })
        {
            Assert.IsFalse(tts.Contains(german, StringComparison.Ordinal), $"reader-tts.js still hard-codes '{german}'.");
        }

        foreach (Match key in Regex.Matches(tts, "tt\\(\"(?<key>[A-Za-z]+)\""))
        {
            Assert.IsTrue(
                UiTranslationResources.TryGet("reader.tts." + key.Groups["key"].Value, out _),
                $"Missing catalog entry reader.tts.{key.Groups["key"].Value}.");
        }
    }

    [TestMethod]
    public void FrameAndBookTextKeysUsedByScriptsExist()
    {
        foreach (var file in new[] { "books-reader.js", "reader-shell.js" })
        {
            var script = Read("src", "Jularr.Web", "wwwroot", "js", file);
            foreach (Match key in Regex.Matches(script, "t\\(\"(?<key>(?:books\\.read|reader\\.frame|books\\.chapter)\\.[A-Za-z.]+)\""))
            {
                Assert.IsTrue(
                    UiTranslationResources.TryGet(key.Groups["key"].Value, out _),
                    $"{file}: missing catalog entry {key.Groups["key"].Value}.");
            }

            foreach (Match key in Regex.Matches(script, "ft\\(\"(?<key>[A-Za-z.]+)\""))
            {
                Assert.IsTrue(
                    UiTranslationResources.TryGet("reader.frame." + key.Groups["key"].Value, out _),
                    $"{file}: missing catalog entry reader.frame.{key.Groups["key"].Value}.");
            }
        }
    }

    [TestMethod]
    public async Task InBookSearchFindsOriginalAndCurrentTranslationParagraphs()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jularr-book-search-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var (work, first, second) = await SeedAsync(db);
            db.NovelTranslations.AddRange(
                new NovelTranslation
                {
                    ChapterId = second.Id,
                    TargetLanguage = "de",
                    SourceHash = second.SourceHash,
                    Text = "Ein Leuchtturm im Nebel."
                },
                new NovelTranslation
                {
                    ChapterId = first.Id,
                    TargetLanguage = "de",
                    SourceHash = "outdated",
                    Text = "Leuchtturm veraltet."
                });
            await db.SaveChangesAsync();

            var hits = await ReaderTextSearch.SearchWorkAsync(db, work.Id, "LIGHTHOUSE", "de", CancellationToken.None);
            Assert.AreEqual(2, hits.Count);
            Assert.AreEqual(first.Id, hits[0].ChapterId);
            Assert.AreEqual(1, hits[0].ParagraphIndex);
            Assert.AreEqual("original", hits[0].Language);
            Assert.AreEqual(
                "lighthouse",
                hits[0].Snippet.Substring(hits[0].MatchStart, hits[0].MatchLength),
                ignoreCase: true);

            var translated = await ReaderTextSearch.SearchWorkAsync(db, work.Id, "Leuchtturm", "de", CancellationToken.None);
            Assert.AreEqual(1, translated.Count, "Outdated translations are not searched.");
            Assert.AreEqual(second.Id, translated[0].ChapterId);
            Assert.AreEqual("de", translated[0].Language);

            Assert.AreEqual(0, (await ReaderTextSearch.SearchWorkAsync(db, work.Id, "l", "de", CancellationToken.None)).Count);
            Assert.AreEqual(0, (await ReaderTextSearch.SearchWorkAsync(db, work.Id, "%", "de", CancellationToken.None)).Count);
            Assert.AreEqual(1, (await ReaderTextSearch.SearchWorkAsync(db, work.Id, "50%", null, CancellationToken.None)).Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<(NovelWork Work, NovelChapter First, NovelChapter Second)> SeedAsync(AppDbContext db)
    {
        var work = new NovelWork
        {
            SourceProvider = "books-upload",
            SourceKey = "search-test",
            SourceUrl = "upload://search-test.epub",
            Title = "Search Test",
            Format = "EPUB:en"
        };
        var volume = new NovelVolume { WorkId = work.Id, Number = 1, Kind = NovelVolumeKinds.Book, SourceKey = "book" };
        var first = new NovelChapter
        {
            WorkId = work.Id,
            VolumeId = volume.Id,
            Number = 1,
            Title = "Harbour",
            SourceUrl = "book://search-test/1",
            OriginalText = "The harbour at dusk.\n\nThe old Lighthouse keeper waited.\n\nHalf of it, 50% at most.",
            SourceHash = "hash-1"
        };
        var second = new NovelChapter
        {
            WorkId = work.Id,
            VolumeId = volume.Id,
            Number = 2,
            Title = "Fog",
            SourceUrl = "book://search-test/2",
            OriginalText = "A lighthouse in the fog.",
            SourceHash = "hash-2"
        };
        db.NovelWorks.Add(work);
        db.NovelVolumes.Add(volume);
        db.NovelChapters.AddRange(first, second);
        await db.SaveChangesAsync();
        return (work, first, second);
    }

    private static async Task<AppDbContext> CreateDatabaseAsync(string path)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Foreign Keys=True")
            .Options;
        var db = new AppDbContext(options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        return db;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepositoryRoot(), .. parts]));

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
