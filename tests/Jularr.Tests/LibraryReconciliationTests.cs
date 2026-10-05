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
public sealed class LibraryReconciliationTests
{
    [TestMethod]
    public async Task ReconciliationRemovesMissingMediaAndOrphanEpisodeButPreservesAnime()
    {
        await using var fixture = await ReconciliationFixture.CreateAsync();

        var anime = new Anime
        {
            Key = "frieren",
            Title = "Frieren"
        };
        var episode = new Episode
        {
            AnimeId = anime.Id,
            SeasonNumber = 1,
            Number = 1,
            Title = "Episode 1"
        };
        fixture.Db.Anime.Add(anime);
        fixture.Db.Episodes.Add(episode);
        fixture.Db.MediaFiles.Add(new MediaFile
        {
            LibraryRootId = fixture.Root.Id,
            EpisodeId = episode.Id,
            Path = Path.Combine(fixture.Root.Path, "Frieren", "Season 01", "missing.mkv"),
            SizeBytes = 123,
            LastWriteTimeUtc = DateTime.UtcNow.AddHours(-1)
        });

        var retainedEpisode = new Episode
        {
            AnimeId = anime.Id,
            SeasonNumber = 1,
            Number = 2,
            Title = "Episode 2"
        };
        var retainedPath = Path.Combine(
            fixture.Root.Path,
            "Frieren",
            "Season 01",
            "Frieren - S01E02.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(retainedPath)!);
        await File.WriteAllBytesAsync(retainedPath, [0]);

        fixture.Db.Episodes.Add(retainedEpisode);
        fixture.Db.MediaFiles.Add(new MediaFile
        {
            LibraryRootId = fixture.Root.Id,
            EpisodeId = retainedEpisode.Id,
            Path = retainedPath,
            SizeBytes = 1,
            LastWriteTimeUtc = File.GetLastWriteTimeUtc(retainedPath)
        });
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Scanner.ScanAsync(
            fixture.Root.Id,
            CancellationToken.None);

        Assert.AreEqual(1, result.Removed);
        Assert.AreEqual(1, await fixture.Db.MediaFiles.CountAsync());
        Assert.AreEqual(1, await fixture.Db.Episodes.CountAsync());
        Assert.AreEqual(1, await fixture.Db.Anime.CountAsync());
    }

    [TestMethod]
    public async Task UnexpectedlyEmptyReadableRootNeverDeletesEstablishedLibraryState()
    {
        await using var fixture = await ReconciliationFixture.CreateAsync();

        var anime = new Anime
        {
            Key = "frieren",
            Title = "Frieren"
        };
        var episode = new Episode
        {
            AnimeId = anime.Id,
            SeasonNumber = 1,
            Number = 1,
            Title = "Episode 1"
        };
        fixture.Db.Anime.Add(anime);
        fixture.Db.Episodes.Add(episode);
        fixture.Db.MediaFiles.Add(new MediaFile
        {
            LibraryRootId = fixture.Root.Id,
            EpisodeId = episode.Id,
            Path = Path.Combine(
                fixture.Root.Path,
                "Frieren",
                "Season 01",
                "Frieren - S01E01.mkv"),
            SizeBytes = 123,
            LastWriteTimeUtc = DateTime.UtcNow.AddHours(-1)
        });
        await fixture.Db.SaveChangesAsync();

        var exception = await Assert.ThrowsExactlyAsync<IOException>(
            () => fixture.Scanner.ScanAsync(
                fixture.Root.Id,
                CancellationToken.None));

        StringAssert.Contains(exception.Message, "mass deletion");
        Assert.AreEqual(1, await fixture.Db.MediaFiles.CountAsync());
        Assert.AreEqual(1, await fixture.Db.Episodes.CountAsync());

        var root = await fixture.Db.LibraryRoots
            .AsNoTracking()
            .SingleAsync(x => x.Id == fixture.Root.Id);
        Assert.IsNull(root.LastScannedAt);
    }

    [TestMethod]
    public async Task UnavailableRootNeverDeletesExistingLibraryState()
    {
        await using var fixture = await ReconciliationFixture.CreateAsync();

        var anime = new Anime
        {
            Key = "frieren",
            Title = "Frieren"
        };
        var episode = new Episode
        {
            AnimeId = anime.Id,
            SeasonNumber = 1,
            Number = 1,
            Title = "Episode 1"
        };
        fixture.Db.Anime.Add(anime);
        fixture.Db.Episodes.Add(episode);
        fixture.Db.MediaFiles.Add(new MediaFile
        {
            LibraryRootId = fixture.Root.Id,
            EpisodeId = episode.Id,
            Path = Path.Combine(fixture.Root.Path, "Frieren", "Season 01", "episode.mkv"),
            SizeBytes = 123,
            LastWriteTimeUtc = DateTime.UtcNow.AddHours(-1)
        });
        await fixture.Db.SaveChangesAsync();

        Directory.Delete(fixture.Root.Path, recursive: true);

        await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(
            () => fixture.Scanner.ScanAsync(
                fixture.Root.Id,
                CancellationToken.None));

        Assert.AreEqual(1, await fixture.Db.MediaFiles.CountAsync());
        Assert.AreEqual(1, await fixture.Db.Episodes.CountAsync());
        Assert.AreEqual(1, await fixture.Db.Anime.CountAsync());

        var root = await fixture.Db.LibraryRoots
            .AsNoTracking()
            .SingleAsync(x => x.Id == fixture.Root.Id);
        Assert.IsNull(root.LastScannedAt);
    }

    private sealed class ReconciliationFixture : IAsyncDisposable
    {
        private ReconciliationFixture(
            string tempRoot,
            AppDbContext db,
            LibraryRoot root,
            LibraryScanner scanner)
        {
            TempRoot = tempRoot;
            Db = db;
            Root = root;
            Scanner = scanner;
        }

        public string TempRoot { get; }
        public AppDbContext Db { get; }
        public LibraryRoot Root { get; }
        public LibraryScanner Scanner { get; }

        public static async Task<ReconciliationFixture> CreateAsync()
        {
            var tempRoot = Path.Combine(
                Path.GetTempPath(),
                $"jularr-reconcile-{Guid.NewGuid():N}");
            var libraryPath = Path.Combine(tempRoot, "anime");
            var dictionaryPath = Path.Combine(tempRoot, "dictionary");
            var databasePath = Path.Combine(tempRoot, "jularr.db");

            Directory.CreateDirectory(libraryPath);
            Directory.CreateDirectory(dictionaryPath);
            await File.WriteAllTextAsync(
                Path.Combine(dictionaryPath, "jmdict-ger.tsv"),
                "");
            await File.WriteAllTextAsync(
                Path.Combine(dictionaryPath, "jmdict-eng-common.tsv"),
                "");

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath};Foreign Keys=True")
                .Options;

            var db = new AppDbContext(options);
            await DatabaseMigrationBridge.UpgradeAsync(db);

            var root = new LibraryRoot
            {
                Name = "Anime",
                Path = libraryPath
            };
            db.LibraryRoots.Add(root);
            db.LibraryRootContentAssignments.Add(new LibraryRootContentAssignment { LibraryRootId = root.Id, ContentType = LibraryContentType.Anime });
            await db.SaveChangesAsync();

            var vocabulary = new VocabularyService(
                db,
                new JapaneseTermExtractor(new EmptyMorphology()),
                new JapaneseDictionary(dictionaryPath));
            var subtitleImport = new SubtitleImportService(db, vocabulary);
            var inventory = MediaInventoryTestSupport.Create(options);
            var embedded = new EmbeddedSubtitleExtractor(
                new MediaProcessRunner(NullLogger<MediaProcessRunner>.Instance),
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

            return new ReconciliationFixture(tempRoot, db, root, scanner);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();

            if (Directory.Exists(TempRoot))
            {
                Directory.Delete(TempRoot, recursive: true);
            }
        }
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
