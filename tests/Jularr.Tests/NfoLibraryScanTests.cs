using Jularr.Web.Data;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaMapping;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Sonarr;
using Jularr.Web.Features.Subtitles;
using Jularr.Web.Features.Tracking;
using Jularr.Web.Features.Vocabulary;
using Jularr.Web.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

[TestClass]
public sealed class NfoLibraryScanTests
{
    private const string ShowNfo =
        """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <tvshow>
          <title>Frieren: Beyond Journey's End</title>
          <originaltitle>葬送のフリーレン</originaltitle>
          <uniqueid type="anilist">154587</uniqueid>
          <uniqueid type="tvdb" default="true">424536</uniqueid>
        </tvshow>
        """;

    [TestMethod]
    public async Task ShowAndEpisodeNfoTitlesAreAppliedIdempotentlyWithoutWritingToTheLibrary()
    {
        await using var fixture = await NfoScanFixture.CreateAsync();
        var episodePath = fixture.AddMedia("Sousou no Frieren", "Season 01", "Sousou no Frieren - S01E01.mkv");
        fixture.AddMedia("Sousou no Frieren", "Season 01", "Sousou no Frieren - S01E02.mkv");
        fixture.WriteNfo(Path.Combine("Sousou no Frieren", "tvshow.nfo"), ShowNfo);
        fixture.WriteNfo(
            Path.ChangeExtension(episodePath, ".nfo"),
            "<episodedetails><title>The Journey's End</title><season>1</season><episode>1</episode></episodedetails>");
        var libraryBefore = fixture.SnapshotLibrary();

        var first = await fixture.Scanner.ScanAsync(fixture.Root.Id, CancellationToken.None);
        var second = await fixture.Scanner.ScanAsync(fixture.Root.Id, CancellationToken.None);

        Assert.AreEqual(2, first.Discovered);
        Assert.AreEqual(0, first.MetadataWarnings);
        Assert.AreEqual(0, second.Discovered);
        Assert.AreEqual(0, second.Updated);
        var anime = await fixture.Db.Anime.AsNoTracking().SingleAsync();
        Assert.AreEqual("sousou no frieren", anime.Key);
        Assert.AreEqual("Frieren: Beyond Journey's End", anime.Title);
        var titles = await fixture.Db.Episodes.AsNoTracking()
            .OrderBy(x => x.Number)
            .Select(x => x.Title)
            .ToArrayAsync();
        CollectionAssert.AreEqual(new[] { "The Journey's End", "Episode 2" }, titles);
        CollectionAssert.AreEqual(libraryBefore, fixture.SnapshotLibrary());
    }

    [TestMethod]
    public async Task ShowNfoAniListIdMatchesNewAnimeWithoutTitleSearch()
    {
        await using var fixture = await NfoScanFixture.CreateAsync();
        fixture.AddMedia("Sousou no Frieren", "Season 01", "Sousou no Frieren - S01E01.mkv");
        fixture.WriteNfo(Path.Combine("Sousou no Frieren", "tvshow.nfo"), ShowNfo);
        fixture.Provider.Add("154587", "Frieren: Beyond Journey's End");

        await fixture.Scanner.ScanAsync(fixture.Root.Id, CancellationToken.None);

        var metadata = await fixture.Db.AnimeMetadata.AsNoTracking().SingleAsync();
        Assert.AreEqual(AniListMetadataProvider.ProviderKey, metadata.Provider);
        Assert.AreEqual("154587", metadata.ExternalId);
        Assert.AreEqual(0, fixture.Provider.Searches.Count);
    }

    [TestMethod]
    public async Task UnknownShowNfoAniListIdFallsThroughToAutomaticTitleMatching()
    {
        await using var fixture = await NfoScanFixture.CreateAsync();
        fixture.AddMedia("Sousou no Frieren", "Season 01", "Sousou no Frieren - S01E01.mkv");
        fixture.WriteNfo(Path.Combine("Sousou no Frieren", "tvshow.nfo"), ShowNfo);

        await fixture.Scanner.ScanAsync(fixture.Root.Id, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "154587" }, fixture.Provider.Requested);
        Assert.IsNotEmpty(fixture.Provider.Searches);
        Assert.IsTrue(fixture.Provider.Searches.All(query => query == "Frieren: Beyond Journey's End"));
        Assert.AreEqual(0, await fixture.Db.AnimeMetadata.CountAsync());
    }

    [TestMethod]
    public async Task ShowNfoAniListIdNeverReplacesAnExistingMatch()
    {
        await using var fixture = await NfoScanFixture.CreateAsync();
        var anime = new Anime { Key = "sousou no frieren", Title = "Sousou no Frieren" };
        fixture.Db.Anime.Add(anime);
        fixture.Db.AnimeMetadata.Add(new AnimeMetadata
        {
            AnimeId = anime.Id,
            Provider = AniListMetadataProvider.ProviderKey,
            ExternalId = "111",
            PreferredTitle = "Manually chosen entry"
        });
        await fixture.Db.SaveChangesAsync();
        fixture.AddMedia("Sousou no Frieren", "Season 01", "Sousou no Frieren - S01E01.mkv");
        fixture.WriteNfo(
            Path.Combine("Sousou no Frieren", "tvshow.nfo"),
            "<tvshow><title>Frieren</title><uniqueid type=\"anilist\">222</uniqueid></tvshow>");
        fixture.Provider.Add("222", "Other entry");

        await fixture.Scanner.ScanAsync(fixture.Root.Id, CancellationToken.None);

        var metadata = await fixture.Db.AnimeMetadata.AsNoTracking().SingleAsync();
        Assert.AreEqual("111", metadata.ExternalId);
        Assert.AreEqual("Manually chosen entry", metadata.PreferredTitle);
        Assert.AreEqual(0, fixture.Provider.Requested.Count);
        Assert.AreEqual(0, fixture.Provider.Searches.Count);
        Assert.AreEqual("Frieren", (await fixture.Db.Anime.AsNoTracking().SingleAsync()).Title);
    }

    [TestMethod]
    public async Task MalformedNfoIsIsolatedAsWarningAndKeepsLastKnownTitles()
    {
        await using var fixture = await NfoScanFixture.CreateAsync();
        var episodePath = fixture.AddMedia("Sousou no Frieren", "Season 01", "Sousou no Frieren - S01E01.mkv");
        fixture.AddMedia("Sousou no Frieren", "Season 01", "Sousou no Frieren - S01E02.mkv");
        var showNfo = fixture.WriteNfo(Path.Combine("Sousou no Frieren", "tvshow.nfo"), ShowNfo);
        var episodeNfo = fixture.WriteNfo(
            Path.ChangeExtension(episodePath, ".nfo"),
            "<episodedetails><title>The Journey's End</title></episodedetails>");
        await fixture.Scanner.ScanAsync(fixture.Root.Id, CancellationToken.None);

        File.WriteAllText(showNfo, "<tvshow><title>Broken</tvshow>");
        File.WriteAllText(
            episodeNfo,
            "<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><episodedetails><title>&e;</title></episodedetails>");
        var rescan = await fixture.Scanner.ScanAsync(fixture.Root.Id, CancellationToken.None);

        Assert.AreEqual(2, rescan.MetadataWarnings);
        Assert.AreEqual(0, rescan.Updated);
        Assert.AreEqual(2, await fixture.Db.MediaFiles.CountAsync());
        Assert.AreEqual("Frieren: Beyond Journey's End", (await fixture.Db.Anime.AsNoTracking().SingleAsync()).Title);
        Assert.AreEqual(
            "The Journey's End",
            await fixture.Db.Episodes.AsNoTracking().Where(x => x.Number == 1).Select(x => x.Title).SingleAsync());
    }

    [TestMethod]
    public async Task MalformedNfoOnFirstScanFallsBackToFileNameTitles()
    {
        await using var fixture = await NfoScanFixture.CreateAsync();
        var episodePath = fixture.AddMedia("Sousou no Frieren", "Season 01", "Sousou no Frieren - S01E01.mkv");
        fixture.WriteNfo(Path.Combine("Sousou no Frieren", "tvshow.nfo"), new string('<', 64));
        fixture.WriteNfo(Path.ChangeExtension(episodePath, ".nfo"), "<movie><title>Film</title></movie>");

        var result = await fixture.Scanner.ScanAsync(fixture.Root.Id, CancellationToken.None);

        Assert.AreEqual(1, result.Discovered);
        Assert.AreEqual(2, result.MetadataWarnings);
        Assert.AreEqual("Sousou no Frieren", (await fixture.Db.Anime.AsNoTracking().SingleAsync()).Title);
        Assert.AreEqual("Episode 1", (await fixture.Db.Episodes.AsNoTracking().SingleAsync()).Title);
        Assert.IsNotEmpty(fixture.Provider.Searches);
        Assert.IsTrue(fixture.Provider.Searches.All(query => query == "Sousou no Frieren"));
    }

    [TestMethod]
    public async Task EpisodeNfoWithDifferentNumberingNeverRenamesTheFileNameEpisode()
    {
        await using var fixture = await NfoScanFixture.CreateAsync();
        var episodePath = fixture.AddMedia("Sousou no Frieren", "Season 01", "Sousou no Frieren - S01E13.mkv");
        fixture.WriteNfo(
            Path.ChangeExtension(episodePath, ".nfo"),
            "<episodedetails><title>Season Two Opener</title><season>2</season><episode>1</episode></episodedetails>");

        var result = await fixture.Scanner.ScanAsync(fixture.Root.Id, CancellationToken.None);

        var episode = await fixture.Db.Episodes.AsNoTracking().SingleAsync();
        Assert.AreEqual(0, result.MetadataWarnings);
        Assert.AreEqual(1, episode.SeasonNumber);
        Assert.AreEqual(13, episode.Number);
        Assert.AreEqual("Episode 13", episode.Title);
    }

    [TestMethod]
    public async Task MultiEpisodeNfoTitlesTheMatchingEpisodeOnly()
    {
        await using var fixture = await NfoScanFixture.CreateAsync();
        var episodePath = fixture.AddMedia("Sousou no Frieren", "Season 01", "Sousou no Frieren - S01E02.mkv");
        fixture.WriteNfo(
            Path.ChangeExtension(episodePath, ".nfo"),
            """
            <episodedetails><title>Part A</title><season>1</season><episode>1</episode></episodedetails>
            <episodedetails><title>Part B</title><season>1</season><episode>2</episode></episodedetails>
            """);

        await fixture.Scanner.ScanAsync(fixture.Root.Id, CancellationToken.None);

        Assert.AreEqual("Part B", (await fixture.Db.Episodes.AsNoTracking().SingleAsync()).Title);
    }

    [TestMethod]
    public async Task SeasonNfoAniListIdMapsThatSeasonsEpisodeRangeOnly()
    {
        await using var fixture = await NfoScanFixture.CreateAsync();
        fixture.AddMedia("Sousou no Frieren", "Season 01", "Sousou no Frieren - S01E01.mkv");
        fixture.AddMedia("Sousou no Frieren", "Season 02", "Sousou no Frieren - S02E01.mkv");
        fixture.WriteNfo(
            Path.Combine("Sousou no Frieren", "Season 02", "season.nfo"),
            "<season><uniqueid type=\"anilist\">154595</uniqueid></season>");
        fixture.Provider.Add("154595", "Frieren Season Two", episodeCount: 1);

        var result = await fixture.Scanner.ScanAsync(fixture.Root.Id, CancellationToken.None);

        Assert.AreEqual(0, result.MetadataWarnings);
        var anime = await fixture.Db.Anime.AsNoTracking().SingleAsync();
        var mappings = await fixture.Metadata.GetEpisodeMappingsAsync(anime.Id, CancellationToken.None);
        Assert.AreEqual(1, mappings.Count);
        Assert.AreEqual(2, mappings[0].SeasonNumber);
        Assert.AreEqual(1, mappings[0].LocalEpisodeStart);
        Assert.AreEqual(1, mappings[0].LocalEpisodeEnd);
        Assert.AreEqual("154595", mappings[0].ExternalId);
    }

    [TestMethod]
    public async Task LocalMetadataIsPersistedFromShowNfoAndSkippedWhenUnchanged()
    {
        await using var fixture = await NfoScanFixture.CreateAsync();
        fixture.AddMedia("Sousou no Frieren", "Season 01", "Sousou no Frieren - S01E01.mkv");
        var showNfoPath = fixture.WriteNfo(
            Path.Combine("Sousou no Frieren", "tvshow.nfo"),
            """
            <tvshow>
              <title>Frieren: Beyond Journey's End</title>
              <originaltitle>葬送のフリーレン</originaltitle>
              <plot>An elf mage outlives her party.</plot>
              <year>2023</year>
              <premiered>2023-09-29</premiered>
              <uniqueid type="tvdb">424536</uniqueid>
            </tvshow>
            """);

        await fixture.Scanner.ScanAsync(fixture.Root.Id, CancellationToken.None);

        var anime = await fixture.Db.Anime.AsNoTracking().SingleAsync();
        var local = await fixture.Db.AnimeLocalMetadata.AsNoTracking().SingleAsync(x => x.AnimeId == anime.Id);
        Assert.AreEqual("nfo", local.Source);
        Assert.AreEqual("葬送のフリーレン", local.OriginalTitle);
        Assert.AreEqual("An elf mage outlives her party.", local.Plot);
        Assert.AreEqual(2023, local.Year);
        Assert.AreEqual(new DateOnly(2023, 9, 29), local.Premiered);
        Assert.AreEqual("424536", local.TvdbId);
        var firstUpdatedAt = local.UpdatedAt;

        // Rewriting the file with different content but the exact same size and last-write time
        // must not be picked up: an unchanged NFO is skipped rather than re-parsed.
        var originalLength = new FileInfo(showNfoPath).Length;
        var originalWriteTime = File.GetLastWriteTimeUtc(showNfoPath);
        var replacement = "<tvshow><title>Different Title Entirely Here</title></tvshow>";
        File.WriteAllText(showNfoPath, replacement.PadRight((int)originalLength));
        File.SetLastWriteTimeUtc(showNfoPath, originalWriteTime);

        await fixture.Scanner.ScanAsync(fixture.Root.Id, CancellationToken.None);

        var localAfterRescan = await fixture.Db.AnimeLocalMetadata.AsNoTracking().SingleAsync(x => x.AnimeId == anime.Id);
        Assert.AreEqual("An elf mage outlives her party.", localAfterRescan.Plot);
        Assert.AreEqual(firstUpdatedAt, localAfterRescan.UpdatedAt);
        Assert.AreEqual(
            "Frieren: Beyond Journey's End",
            (await fixture.Db.Anime.AsNoTracking().SingleAsync()).Title);
    }

    private sealed class NfoScanFixture : IAsyncDisposable
    {
        private NfoScanFixture(
            string tempRoot,
            AppDbContext db,
            LibraryRoot root,
            LibraryScanner scanner,
            FakeAniListProvider provider,
            AnimeMetadataService metadata)
        {
            TempRoot = tempRoot;
            Db = db;
            Root = root;
            Scanner = scanner;
            Provider = provider;
            Metadata = metadata;
        }

        public string TempRoot { get; }
        public AppDbContext Db { get; }
        public LibraryRoot Root { get; }
        public LibraryScanner Scanner { get; }
        public FakeAniListProvider Provider { get; }
        public AnimeMetadataService Metadata { get; }

        public static async Task<NfoScanFixture> CreateAsync()
        {
            var tempRoot = Path.Combine(
                Path.GetTempPath(),
                $"jularr-nfo-scan-{Guid.NewGuid():N}");
            var libraryPath = Path.Combine(tempRoot, "anime");
            var dictionaryPath = Path.Combine(tempRoot, "dictionary");
            var integrationPath = Path.Combine(tempRoot, "integrations");

            Directory.CreateDirectory(libraryPath);
            Directory.CreateDirectory(dictionaryPath);
            Directory.CreateDirectory(integrationPath);
            await File.WriteAllTextAsync(Path.Combine(dictionaryPath, "jmdict-ger.tsv"), "");
            await File.WriteAllTextAsync(Path.Combine(dictionaryPath, "jmdict-eng-common.tsv"), "");

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(tempRoot, "jularr.db")};Foreign Keys=True")
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

            var dataProtection = DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(tempRoot, "keys")));
            var vocabulary = new VocabularyService(
                db,
                new JapaneseTermExtractor(new EmptyMorphology()),
                new JapaneseDictionary(dictionaryPath));
            var sonarrSync = new SonarrArtworkSyncService(
                new SonarrConnectionStore(dataProtection),
                new SonarrArtworkImportService(
                    db,
                    new TestHttpClientFactory(),
                    NullLogger<SonarrArtworkImportService>.Instance),
                NullLogger<SonarrArtworkSyncService>.Instance);
            var provider = new FakeAniListProvider();
            var metadata = new AnimeMetadataService(
                db,
                [provider],
                new AniListAccountStore(
                    dataProtection,
                    NullLogger<AniListAccountStore>.Instance,
                    new DirectoryInfo(integrationPath)),
                new MediaMappingReviewStore(
                    NullLogger<MediaMappingReviewStore>.Instance,
                    new DirectoryInfo(integrationPath)));
            var inventory = MediaInventoryTestSupport.Create(options);
            var scanner = new LibraryScanner(
                db,
                new SubtitleImportService(db, vocabulary),
                new EmbeddedSubtitleExtractor(
                    new MediaProcessRunner(NullLogger<MediaProcessRunner>.Instance),
                    inventory,
                    NullLogger<EmbeddedSubtitleExtractor>.Instance),
                inventory,
                sonarrSync,
                NullLogger<LibraryScanner>.Instance,
                metadata);

            return new NfoScanFixture(tempRoot, db, root, scanner, provider, metadata);
        }

        public string AddMedia(params string[] relativeParts)
        {
            var path = Path.Combine([Root.Path, .. relativeParts]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [0]);
            return path;
        }

        public string WriteNfo(string relativeOrFullPath, string content)
        {
            var path = Path.Combine(Root.Path, relativeOrFullPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public string[] SnapshotLibrary() =>
            Directory.EnumerateFileSystemEntries(Root.Path, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .Select(path => $"{path}|{File.GetLastWriteTimeUtc(path):O}")
                .ToArray();

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();

            if (Directory.Exists(TempRoot))
            {
                Directory.Delete(TempRoot, recursive: true);
            }
        }
    }

    private sealed class FakeAniListProvider : IAnimeMetadataProvider
    {
        private readonly Dictionary<string, AnimeMetadataCandidate> entries = new(StringComparer.Ordinal);

        public string Key => AniListMetadataProvider.ProviderKey;
        public List<string> Requested { get; } = [];
        public List<string> Searches { get; } = [];

        public void Add(string externalId, string title, int? episodeCount = 28) =>
            entries[externalId] = new AnimeMetadataCandidate(
                AniListMetadataProvider.ProviderKey,
                externalId,
                title,
                null,
                title,
                null,
                null,
                null,
                null,
                "TV",
                "FINISHED",
                "FALL",
                2023,
                episodeCount,
                24);

        public Task<IReadOnlyList<AnimeMetadataCandidate>> SearchAsync(
            string query,
            int limit,
            CancellationToken cancellationToken)
        {
            Searches.Add(query);
            return Task.FromResult<IReadOnlyList<AnimeMetadataCandidate>>([]);
        }

        public Task<AnimeMetadataCandidate?> GetAsync(
            string externalId,
            CancellationToken cancellationToken)
        {
            Requested.Add(externalId);
            return Task.FromResult(entries.GetValueOrDefault(externalId));
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
