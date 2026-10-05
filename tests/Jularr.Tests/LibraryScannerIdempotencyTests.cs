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
public sealed class LibraryScannerIdempotencyTests
{
    [TestMethod]
    public async Task ScanningSameLibraryTwiceDoesNotDuplicateCoreRows()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"jularr-scan-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(tempRoot, "jularr.db");
        var dictionaryPath = Path.Combine(tempRoot, "dictionary");

        Directory.CreateDirectory(dictionaryPath);
        await File.WriteAllTextAsync(
            Path.Combine(dictionaryPath, "jmdict-ger.tsv"),
            "猫\tねこ\t1\tKatze\n走る\tはしる\t1\tlaufen\n");
        await File.WriteAllTextAsync(
            Path.Combine(dictionaryPath, "jmdict-eng-common.tsv"),
            "");

        var episodeDirectory = Path.Combine(
            tempRoot,
            "anime",
            "Sousou no Frieren",
            "Season 01");
        Directory.CreateDirectory(episodeDirectory);

        var mediaPath = Path.Combine(
            episodeDirectory,
            "Sousou no Frieren - S01E03.mkv");
        var subtitlePath = Path.Combine(
            episodeDirectory,
            "Sousou no Frieren - S01E03.ja.srt");

        await File.WriteAllBytesAsync(mediaPath, [0x00]);
        await File.WriteAllTextAsync(
            subtitlePath,
            """
            1
            00:00:01,000 --> 00:00:03,000
            猫が走る
            """);

        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath};Foreign Keys=True")
                .Options;

            await using var db = new AppDbContext(options);
            await DatabaseMigrationBridge.UpgradeAsync(db);

            var root = new LibraryRoot
            {
                Name = "Anime",
                Path = Path.Combine(tempRoot, "anime")
            };
            db.LibraryRoots.Add(root);
            db.LibraryRootContentAssignments.Add(new LibraryRootContentAssignment { LibraryRootId = root.Id, ContentType = LibraryContentType.Anime });
            await db.SaveChangesAsync();

            var extractor = new JapaneseTermExtractor(
                new StubMorphology(new Dictionary<string, JapaneseMorphToken[]>
                {
                    ["猫が走る"] =
                    [
                        new("猫", "猫", "ネコ", "名詞"),
                        new("が", "が", "ガ", "助詞"),
                        new("走る", "走る", "ハシル", "動詞")
                    ]
                }));
            var vocabulary = new VocabularyService(
                db,
                extractor,
                new JapaneseDictionary(dictionaryPath));
            var subtitleImport = new SubtitleImportService(db, vocabulary);
            var processRunner = new MediaProcessRunner(
                NullLogger<MediaProcessRunner>.Instance);
            var inventory = MediaInventoryTestSupport.Create(options);
            var embedded = new EmbeddedSubtitleExtractor(
                processRunner,
                inventory,
                NullLogger<EmbeddedSubtitleExtractor>.Instance);
            var sonarrStore = new SonarrConnectionStore(
                DataProtectionProvider.Create(
                    new DirectoryInfo(Path.Combine(tempRoot, "keys"))));
            var sonarrImport = new SonarrArtworkImportService(
                db,
                new TestHttpClientFactory(),
                NullLogger<SonarrArtworkImportService>.Instance);
            var sonarrSync = new SonarrArtworkSyncService(
                sonarrStore,
                sonarrImport,
                NullLogger<SonarrArtworkSyncService>.Instance);
            var scanner = new LibraryScanner(
                db,
                subtitleImport,
                embedded,
                inventory,
                sonarrSync,
                NullLogger<LibraryScanner>.Instance);

            var first = await scanner.ScanAsync(root.Id, CancellationToken.None);
            var second = await scanner.ScanAsync(root.Id, CancellationToken.None);

            Assert.AreEqual(1, first.Discovered);
            Assert.AreEqual(0, second.Discovered);
            Assert.AreEqual(1, await db.Anime.CountAsync());
            Assert.AreEqual(1, await db.Episodes.CountAsync());
            Assert.AreEqual(1, await db.MediaFiles.CountAsync());
            Assert.AreEqual(1, await db.SubtitleTracks.CountAsync());
            Assert.AreEqual(1, await db.SubtitleCues.CountAsync());
            Assert.AreEqual(2, await db.EpisodeTerms.CountAsync());

            var media = await db.MediaFiles.AsNoTracking().SingleAsync();
            Assert.AreEqual(Path.GetFullPath(mediaPath), media.Path);

            var episode = await db.Episodes.AsNoTracking().SingleAsync();
            Assert.AreEqual(1, episode.SeasonNumber);
            Assert.AreEqual(3, episode.Number);
        }
        finally
        {

            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class StubMorphology(
        IReadOnlyDictionary<string, JapaneseMorphToken[]> tokens)
        : IJapaneseMorphology
    {
        public IReadOnlyList<JapaneseMorphToken> Analyze(string text) =>
            tokens.GetValueOrDefault(text) ?? [];
    }
}
