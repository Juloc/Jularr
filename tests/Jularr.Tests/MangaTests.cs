using System.IO.Compression;
using System.Security.Cryptography;
using Jularr.Web.Data;
using Jularr.Web.Features.Manga;
using Jularr.Web.Features.ReaderPreferences;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class MangaTests
{
    [TestMethod]
    public async Task CbzImportUsesCanonicalDatabaseAndNeverMutatesSource()
    {
        var root = TempDirectory();
        var database = Path.Combine(root, "jularr.db");
        var source = Path.Combine(root, "Example.Ch.001.cbz");
        var cache = Path.Combine(root, "cache");

        try
        {
            CreateArchive(source, 3);
            var beforeHash = Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(source)));
            var beforeWrite = File.GetLastWriteTimeUtc(source);

            await using var db = await CreateDatabaseAsync(database);
            var repository = new MangaRepository(db);
            var importer = new MangaImportService(repository, cache);

            var result = await importer.ImportAsync(
                source,
                CancellationToken.None);

            Assert.AreEqual(1, result.ChapterCount);
            Assert.AreEqual(3, result.PageCount);

            var library = await repository.GetLibraryAsync(
                "reader-a",
                CancellationToken.None);

            Assert.AreEqual(1, library.Count);
            Assert.AreEqual(1, library[0].ChapterCount);
            Assert.IsNotNull(library[0].PreviewChapterId);

            var series = await repository.GetSeriesAsync(
                result.SeriesId,
                CancellationToken.None);
            Assert.IsNotNull(series);
            Assert.AreEqual(1, series.Chapters.Count);
            Assert.AreEqual(3, series.Chapters[0].PageCount);

            var page = await repository.GetPageAsync(
                series.Chapters[0].Id,
                2,
                CancellationToken.None);
            Assert.IsNotNull(page);
            Assert.IsTrue(File.Exists(page.CachedPath));
            Assert.IsTrue(
                Path.GetFullPath(page.CachedPath).StartsWith(
                    Path.GetFullPath(cache),
                    StringComparison.Ordinal));

            var afterHash = Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(source)));
            Assert.AreEqual(beforeHash, afterHash);
            Assert.AreEqual(beforeWrite, File.GetLastWriteTimeUtc(source));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task ProgressAndBookmarksAreProfileScoped()
    {
        var root = TempDirectory();
        var database = Path.Combine(root, "jularr.db");
        var source = Path.Combine(root, "Series");
        var chapterDirectory = Path.Combine(source, "Vol.1", "Ch. 12");
        var cache = Path.Combine(root, "cache");

        try
        {
            Directory.CreateDirectory(chapterDirectory);
            await File.WriteAllBytesAsync(
                Path.Combine(chapterDirectory, "001.jpg"),
                [1, 2, 3]);
            await File.WriteAllBytesAsync(
                Path.Combine(chapterDirectory, "002.jpg"),
                [4, 5, 6]);

            await using var db = await CreateDatabaseAsync(database);
            var repository = new MangaRepository(db);
            var importer = new MangaImportService(repository, cache);
            var imported = await importer.ImportAsync(
                source,
                CancellationToken.None);
            var series = await repository.GetSeriesAsync(
                imported.SeriesId,
                CancellationToken.None);
            var chapter = await repository.GetChapterAsync(
                series!.Chapters.Single().Id,
                CancellationToken.None);

            Assert.IsNotNull(chapter);
            Assert.AreEqual(12d, chapter.Number);
            Assert.AreEqual(1, chapter.VolumeNumber);

            await repository.SaveProgressAsync(
                "reader-a",
                chapter,
                1,
                CancellationToken.None);
            await repository.SaveProgressAsync(
                "reader-b",
                chapter,
                0,
                CancellationToken.None);

            Assert.AreEqual(
                1,
                (await repository.GetProgressAsync(
                    "reader-a",
                    chapter.SeriesId,
                    CancellationToken.None))!.PageIndex);
            Assert.AreEqual(
                0,
                (await repository.GetProgressAsync(
                    "reader-b",
                    chapter.SeriesId,
                    CancellationToken.None))!.PageIndex);

            var bookmark = await repository.AddBookmarkAsync(
                "reader-a",
                chapter,
                1,
                "Important",
                CancellationToken.None);

            Assert.AreEqual(
                1,
                (await repository.GetBookmarksAsync(
                    "reader-a",
                    chapter.SeriesId,
                    CancellationToken.None)).Count);
            Assert.AreEqual(
                0,
                (await repository.GetBookmarksAsync(
                    "reader-b",
                    chapter.SeriesId,
                    CancellationToken.None)).Count);

            await repository.RemoveBookmarkAsync(
                "reader-b",
                bookmark.Id,
                CancellationToken.None);
            Assert.AreEqual(
                1,
                (await repository.GetBookmarksAsync(
                    "reader-a",
                    chapter.SeriesId,
                    CancellationToken.None)).Count);

            await repository.RemoveBookmarkAsync(
                "reader-a",
                bookmark.Id,
                CancellationToken.None);
            Assert.AreEqual(
                0,
                (await repository.GetBookmarksAsync(
                    "reader-a",
                    chapter.SeriesId,
                    CancellationToken.None)).Count);
        }
        finally
        {
            TryDelete(root);
        }
    }


    [TestMethod]
    public async Task RenamingChapterFilePreservesIdentityAndProgressOnRescan()
    {
        var root = TempDirectory();
        var database = Path.Combine(root, "jularr.db");
        var source = Path.Combine(root, "Series");
        var cache = Path.Combine(root, "cache");
        var originalChapterPath = Path.Combine(source, "Ch. 5.cbz");

        try
        {
            Directory.CreateDirectory(source);
            CreateArchive(originalChapterPath, 4);

            await using var db = await CreateDatabaseAsync(database);
            var repository = new MangaRepository(db);
            var importer = new MangaImportService(repository, cache);

            var imported = await importer.ImportAsync(source, CancellationToken.None);
            var seriesBefore = await repository.GetSeriesAsync(imported.SeriesId, CancellationToken.None);
            var chapterBefore = seriesBefore!.Chapters.Single();
            Assert.AreEqual(5d, chapterBefore.Number);

            var chapterRead = await repository.GetChapterAsync(chapterBefore.Id, CancellationToken.None);
            await repository.SaveProgressAsync("reader-a", chapterRead!, 2, CancellationToken.None);

            // Rename the chapter file on disk within the same series folder: same chapter number,
            // different path/name - the way a #529 naming placement or a manual rename would leave
            // it, and exactly what MangaChapters used to have no durable identity to survive (#563).
            var renamedChapterPath = Path.Combine(source, "Chapter 05 (moved).cbz");
            File.Move(originalChapterPath, renamedChapterPath);

            var rescanned = await importer.ImportAsync(source, CancellationToken.None);

            Assert.AreEqual(imported.SeriesId, rescanned.SeriesId);
            Assert.AreEqual(1, rescanned.ChapterCount);

            var seriesAfter = await repository.GetSeriesAsync(rescanned.SeriesId, CancellationToken.None);
            var chapterAfter = seriesAfter!.Chapters.Single();
            Assert.AreEqual(chapterBefore.Id, chapterAfter.Id);

            var progress = await repository.GetProgressAsync(
                "reader-a",
                rescanned.SeriesId,
                CancellationToken.None);
            Assert.IsNotNull(progress);
            Assert.AreEqual(chapterBefore.Id, progress!.ChapterId);
            Assert.AreEqual(2, progress.PageIndex);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task RescanningUnchangedMangaSeriesIsIdempotentAndDoesNotDuplicateChaptersOrVolumes()
    {
        var root = TempDirectory();
        var database = Path.Combine(root, "jularr.db");
        var source = Path.Combine(root, "Series");
        var chapterDirectory = Path.Combine(source, "Vol.1", "Ch. 3");
        var cache = Path.Combine(root, "cache");

        try
        {
            Directory.CreateDirectory(chapterDirectory);
            await File.WriteAllBytesAsync(Path.Combine(chapterDirectory, "001.jpg"), [1, 2, 3]);

            await using var db = await CreateDatabaseAsync(database);
            var repository = new MangaRepository(db);
            var importer = new MangaImportService(repository, cache);

            var first = await importer.ImportAsync(source, CancellationToken.None);
            var seriesAfterFirst = await repository.GetSeriesAsync(first.SeriesId, CancellationToken.None);
            var chapterIdAfterFirst = seriesAfterFirst!.Chapters.Single().Id;
            var volumesAfterFirst = await repository.GetVolumesAsync(first.SeriesId, CancellationToken.None);
            var volumeIdAfterFirst = volumesAfterFirst.Single().Id;

            var second = await importer.ImportAsync(source, CancellationToken.None);
            var seriesAfterSecond = await repository.GetSeriesAsync(second.SeriesId, CancellationToken.None);
            var volumesAfterSecond = await repository.GetVolumesAsync(second.SeriesId, CancellationToken.None);

            Assert.AreEqual(first.SeriesId, second.SeriesId);
            Assert.AreEqual(1, seriesAfterSecond!.Chapters.Count);
            Assert.AreEqual(chapterIdAfterFirst, seriesAfterSecond.Chapters.Single().Id);
            Assert.AreEqual(1, volumesAfterSecond.Count);
            Assert.AreEqual(volumeIdAfterFirst, volumesAfterSecond.Single().Id);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task SeriesCoverBesideChapterFoldersIsNotImportedAsChapter()
    {
        var root = TempDirectory();
        var database = Path.Combine(root, "jularr.db");
        var source = Path.Combine(root, "Series");
        var chapter = Path.Combine(source, "Ch. 1");
        var cache = Path.Combine(root, "cache");

        try
        {
            Directory.CreateDirectory(chapter);
            await File.WriteAllBytesAsync(Path.Combine(source, "cover.jpg"), [9, 9, 9]);
            await File.WriteAllBytesAsync(Path.Combine(chapter, "001.jpg"), [1, 2, 3]);

            await using var db = await CreateDatabaseAsync(database);
            var repository = new MangaRepository(db);
            var importer = new MangaImportService(repository, cache);

            var result = await importer.ImportAsync(source, CancellationToken.None);
            var series = await repository.GetSeriesAsync(result.SeriesId, CancellationToken.None);

            Assert.AreEqual(1, result.ChapterCount);
            Assert.IsNotNull(series);
            Assert.AreEqual(1, series.Chapters.Count);
            Assert.AreEqual(1d, series.Chapters[0].Number);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task ReaderPreferencesResolveGlobalTypeAndWorkScopes()
    {
        var root = TempDirectory();
        var database = Path.Combine(root, "jularr.db");
        var workId = Guid.NewGuid();

        try
        {
            await using var db = await CreateDatabaseAsync(database);
            db.ReaderPreferences.Add(new ReaderPreference
            {
                ProfileId = "reader-a",
                ScopeKey = ReaderPreferenceRules.UserDefaultScope,
                ReadingMode = "continuous",
                TwoPageSpread = false,
                PageTransition = "fade",
                BookmarkColor = "#123456"
            });
            await db.SaveChangesAsync();

            var global = await MangaReaderPreferences.GetAsync(
                db,
                "reader-a",
                workId,
                CancellationToken.None);
            Assert.AreEqual("continuous", global.ReadingMode);
            Assert.IsFalse(global.TwoPageSpread);
            Assert.AreEqual("#123456", global.BookmarkColor);
            Assert.IsFalse(global.HasSeriesOverride);

            await MangaReaderPreferences.SaveAsync(
                db,
                "reader-a",
                workId: null,
                new MangaReaderPreferenceInput { Mode = "double" },
                "mode",
                CancellationToken.None);

            var type = await MangaReaderPreferences.GetAsync(
                db,
                "reader-a",
                workId,
                CancellationToken.None);
            Assert.AreEqual("paged", type.ReadingMode);
            Assert.IsTrue(type.TwoPageSpread);
            Assert.AreEqual("double", type.UiMode);

            await MangaReaderPreferences.SaveAsync(
                db,
                "reader-a",
                workId,
                new MangaReaderPreferenceInput { Mode = "continuous" },
                "mode",
                CancellationToken.None);

            var work = await MangaReaderPreferences.GetAsync(
                db,
                "reader-a",
                workId,
                CancellationToken.None);
            Assert.AreEqual("continuous", work.ReadingMode);
            Assert.IsFalse(work.TwoPageSpread);
            Assert.AreEqual("continuous", work.UiMode);
            Assert.IsTrue(work.HasSeriesOverride);

            await MangaReaderPreferences.ResetWorkAsync(
                db,
                "reader-a",
                workId,
                CancellationToken.None);

            var reset = await MangaReaderPreferences.GetAsync(
                db,
                "reader-a",
                workId,
                CancellationToken.None);
            Assert.AreEqual("paged", reset.ReadingMode);
            Assert.IsTrue(reset.TwoPageSpread);
            Assert.AreEqual("double", reset.UiMode);
            Assert.IsFalse(reset.HasSeriesOverride);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public void MangaReaderSupportsPageModesDirectionAndSharedReadingSwitch()
    {
        var root = FindRepositoryRoot();
        var reader = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Jularr.Web",
            "Pages",
            "Manga",
            "Read.cshtml"));
        var script = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Jularr.Web",
            "wwwroot",
            "js",
            "manga-reader.js"));
        var library = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Jularr.Web",
            "Pages",
            "Manga",
            "Index.cshtml"));

        StringAssert.Contains(reader, "(\"single\", \"reader.frame.singlePage\")");
        StringAssert.Contains(reader, "(\"double\", \"reader.frame.twoPages\")");
        StringAssert.Contains(reader, "(\"continuous\", \"manga.reader.mode.vertical\")");
        StringAssert.Contains(reader, "data-manga-setting=\"rightToLeft\"");
        StringAssert.Contains(reader, "data-reader-progress-slider");
        StringAssert.Contains(script, "direction === \"rtl\"");
        StringAssert.Contains(script, "prefetch");
        StringAssert.Contains(script, "toggleBookmark");
        StringAssert.Contains(library, "href=\"/Novels\"");
        StringAssert.Contains(library, "href=\"/Manga\"");
    }

    [TestMethod]
    public async Task MangaMigrationStaysInsideJularrDatabase()
    {
        // Manga data lives in the canonical Jularr (PostgreSQL) database, not a separate store:
        // the reading tables are part of the applied schema.
        await using var db = await CreateDatabaseAsync(
            Path.Combine(Path.GetTempPath(), $"jularr-manga-schema-{Guid.NewGuid():N}.db"));

        var tables = await db.Database
            .SqlQueryRaw<string>(
                """
                SELECT tablename FROM pg_tables
                WHERE schemaname = 'public'
                  AND tablename IN ('MangaSeries', 'MangaProgress', 'MangaBookmarks', 'MangaChapters')
                """)
            .ToListAsync();

        CollectionAssert.AreEquivalent(
            new[] { "MangaSeries", "MangaProgress", "MangaBookmarks", "MangaChapters" },
            tables.ToArray());
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

    private static void CreateArchive(string path, int pages)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        for (var index = 0; index < pages; index++)
        {
            var entry = archive.CreateEntry(
                $"{index + 1:D3}.jpg",
                CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.WriteByte((byte)(index + 1));
            stream.WriteByte(2);
            stream.WriteByte(3);
        }
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"jularr-manga-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
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
