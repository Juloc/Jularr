using Jularr.Web.Data;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Sonarr;
using Jularr.Web.Features.Subtitles;
using Jularr.Web.Features.Vocabulary;
using Jularr.Web.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

[TestClass]
public sealed class LibraryScannerMediaInventoryTests
{
    [TestMethod]
    public async Task ReconciliationProbesOnlyNewOrChangedMedia()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"jularr-scan-inventory-{Guid.NewGuid():N}");
        var dictionaryPath = Path.Combine(tempRoot, "dictionary");
        var seasonPath = Path.Combine(tempRoot, "anime", "Sousou no Frieren", "Season 01");
        Directory.CreateDirectory(dictionaryPath);
        Directory.CreateDirectory(seasonPath);
        await File.WriteAllTextAsync(Path.Combine(dictionaryPath, "jmdict-ger.tsv"), "");
        await File.WriteAllTextAsync(Path.Combine(dictionaryPath, "jmdict-eng-common.tsv"), "");

        var first = Path.Combine(seasonPath, "Sousou no Frieren - S01E01.mkv");
        var second = Path.Combine(seasonPath, "Sousou no Frieren - S01E02.mkv");
        var broken = Path.Combine(seasonPath, "Sousou no Frieren - S01E03.mkv");
        await File.WriteAllBytesAsync(first, [1, 2, 3]);
        await File.WriteAllBytesAsync(second, [4, 5, 6]);
        await File.WriteAllBytesAsync(broken, [0]);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(tempRoot, "jularr.db")};Foreign Keys=True")
            .Options;

        try
        {
            await using var db = new AppDbContext(options);
            await DatabaseMigrationBridge.UpgradeAsync(db);
            var root = new LibraryRoot { Name = "Anime", Path = Path.Combine(tempRoot, "anime") };
            db.LibraryRoots.Add(root);
            db.LibraryRootContentAssignments.Add(new LibraryRootContentAssignment { LibraryRootId = root.Id, ContentType = LibraryContentType.Anime });
            await db.SaveChangesAsync();

            var runner = new FakeMediaProbeRunner();
            runner.Returns(first, MediaProbeFixtures.H264Stereo);
            runner.Returns(second, MediaProbeFixtures.H264Stereo);
            var scanner = CreateScanner(db, options, runner, tempRoot, dictionaryPath);

            var initial = await scanner.ScanAsync(root.Id, CancellationToken.None);

            Assert.AreEqual(3, initial.Discovered, "Invalid media still becomes a library file.");
            Assert.AreEqual(new MediaInventoryReconciliation(0, 2, 1, 0), initial.MediaInventory);
            Assert.AreEqual(3, runner.Calls.Count);
            var diagnostic = await db.MediaAnalyses.AsNoTracking()
                .Where(x => x.Status == MediaAnalysisStatus.Failed)
                .Select(x => x.Diagnostic)
                .SingleAsync();
            StringAssert.Contains(diagnostic, "Invalid data");

            runner.ClearCalls();
            var unchanged = await scanner.ScanAsync(root.Id, CancellationToken.None);

            Assert.AreEqual(0, runner.Calls.Count, "An unchanged library must not run ffprobe.");
            Assert.AreEqual(new MediaInventoryReconciliation(3, 0, 0, 0), unchanged.MediaInventory);

            await File.AppendAllTextAsync(second, "new release");
            var changed = await scanner.ScanAsync(root.Id, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { Path.GetFullPath(second) }, runner.Calls.ToArray());
            Assert.AreEqual(new MediaInventoryReconciliation(2, 1, 0, 0), changed.MediaInventory);

            File.Delete(broken);
            await scanner.ScanAsync(root.Id, CancellationToken.None);

            Assert.AreEqual(2, await db.MediaAnalyses.CountAsync(), "Removed media drops its analysis.");
            Assert.AreEqual(4, await db.MediaAnalysisStreams.CountAsync(), "Each surviving video stores one video and one audio track.");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static LibraryScanner CreateScanner(
        AppDbContext db,
        DbContextOptions<AppDbContext> options,
        FakeMediaProbeRunner runner,
        string tempRoot,
        string dictionaryPath)
    {
        var inventory = MediaInventoryTestSupport.Create(options, runner);
        var vocabulary = new VocabularyService(
            db,
            new JapaneseTermExtractor(new EmptyMorphology()),
            new JapaneseDictionary(dictionaryPath));
        var sonarrSync = new SonarrArtworkSyncService(
            new SonarrConnectionStore(
                DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(tempRoot, "keys")))),
            new SonarrArtworkImportService(
                db,
                new TestHttpClientFactory(),
                NullLogger<SonarrArtworkImportService>.Instance),
            NullLogger<SonarrArtworkSyncService>.Instance);

        return new LibraryScanner(
            db,
            new SubtitleImportService(db, vocabulary),
            new EmbeddedSubtitleExtractor(
                new MediaProcessRunner(NullLogger<MediaProcessRunner>.Instance),
                inventory,
                NullLogger<EmbeddedSubtitleExtractor>.Instance),
            inventory,
            sonarrSync,
            NullLogger<LibraryScanner>.Instance);
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class EmptyMorphology : IJapaneseMorphology
    {
        public IReadOnlyList<JapaneseMorphToken> Analyze(string text) => [];
    }
}
