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
public sealed class SubtitleSidecarReconciliationTests
{
    private const string BaseName = "Frieren - S01E01";

    [TestMethod]
    public async Task ImportsTaggedSidecarFromSubsDirectory()
    {
        await using var fixture = await SidecarFixture.CreateAsync();
        var subtitle = fixture.WriteSubtitle(
            Path.Combine("Subs", $"{BaseName}.jpn.default.srt"),
            "猫が走る");

        var result = await fixture.ScanAsync();

        Assert.AreEqual(1, result.SubtitleFiles);
        var track = await fixture.Db.SubtitleTracks.AsNoTracking().SingleAsync();
        Assert.AreEqual(subtitle, track.Path);
        Assert.AreEqual("srt", track.Format);
        Assert.AreEqual("猫が走る", (await fixture.Db.SubtitleCues.AsNoTracking().SingleAsync()).Text);
    }

    [TestMethod]
    public async Task ImportsJapaneseTrackFromPerEpisodeSubsFolder()
    {
        await using var fixture = await SidecarFixture.CreateAsync();
        fixture.WriteSubtitle(Path.Combine("Subs", BaseName, "2_English.srt"), "The cat runs");
        var japanese = fixture.WriteVtt(Path.Combine("Subs", BaseName, "3_Japanese.vtt"), "猫が走る");

        await fixture.ScanAsync();

        var track = await fixture.Db.SubtitleTracks.AsNoTracking().SingleAsync();
        Assert.AreEqual(japanese, track.Path);
        Assert.AreEqual("vtt", track.Format);
    }

    [TestMethod]
    public async Task PrefersTaggedFullSubtitleOverForcedAndUntagged()
    {
        await using var fixture = await SidecarFixture.CreateAsync();
        fixture.WriteSubtitle($"{BaseName}.srt", "猫が走る");
        fixture.WriteSubtitle($"{BaseName}.ja.forced.srt", "看板");
        var full = fixture.WriteSubtitle(Path.Combine("Subtitles", $"{BaseName}.japanese.srt"), "犬が走る");

        await fixture.ScanAsync();

        Assert.AreEqual(full, (await fixture.Db.SubtitleTracks.AsNoTracking().SingleAsync()).Path);
    }

    [TestMethod]
    public async Task UntaggedSidecarIsUsedOnlyWhenItContainsJapaneseText()
    {
        await using var fixture = await SidecarFixture.CreateAsync();
        var untagged = fixture.WriteSubtitle($"{BaseName}.srt", "The cat runs");

        await fixture.ScanAsync();
        Assert.AreEqual(0, await fixture.Db.SubtitleTracks.CountAsync());

        fixture.WriteSubtitle($"{BaseName}.srt", "猫が走る", DateTime.UtcNow.AddMinutes(1));
        await fixture.ScanAsync();

        Assert.AreEqual(untagged, (await fixture.Db.SubtitleTracks.AsNoTracking().SingleAsync()).Path);
    }

    [TestMethod]
    public async Task ChangedSidecarReplacesCanonicalCues()
    {
        await using var fixture = await SidecarFixture.CreateAsync();
        fixture.WriteSubtitle($"{BaseName}.ja.srt", "猫が走る");
        await fixture.ScanAsync();

        fixture.WriteSubtitle($"{BaseName}.ja.srt", "犬が歩く", DateTime.UtcNow.AddMinutes(1));
        await fixture.ScanAsync();

        Assert.AreEqual(1, await fixture.Db.SubtitleTracks.CountAsync());
        Assert.AreEqual("犬が歩く", (await fixture.Db.SubtitleCues.AsNoTracking().SingleAsync()).Text);
    }

    [TestMethod]
    public async Task SidecarChangedToUnusableContentRemovesTrack()
    {
        await using var fixture = await SidecarFixture.CreateAsync();
        var subtitle = fixture.WriteSubtitle($"{BaseName}.ja.srt", "猫が走る");
        await fixture.ScanAsync();

        File.WriteAllText(subtitle, "");
        File.SetLastWriteTimeUtc(subtitle, DateTime.UtcNow.AddMinutes(1));
        await fixture.ScanAsync();

        Assert.AreEqual(0, await fixture.Db.SubtitleTracks.CountAsync());
    }

    [TestMethod]
    public async Task DeletedPreferredSidecarFallsBackToNextCandidate()
    {
        await using var fixture = await SidecarFixture.CreateAsync();
        var preferred = fixture.WriteSubtitle($"{BaseName}.ja.srt", "猫が走る");
        var fallback = fixture.WriteAss($"{BaseName}.ja.forced.ass", "看板");
        await fixture.ScanAsync();
        Assert.AreEqual(preferred, (await fixture.Db.SubtitleTracks.AsNoTracking().SingleAsync()).Path);

        File.Delete(preferred);
        await fixture.ScanAsync();

        var track = await fixture.Db.SubtitleTracks.AsNoTracking().SingleAsync();
        Assert.AreEqual(fallback, track.Path);
        Assert.AreEqual("ass", track.Format);
    }

    [TestMethod]
    public async Task DeletedOnlySidecarRemovesTrackButKeepsRemoteSources()
    {
        await using var fixture = await SidecarFixture.CreateAsync();
        var subtitle = fixture.WriteSubtitle($"{BaseName}.ja.srt", "猫が走る");
        await fixture.ScanAsync();
        Assert.AreEqual(1, await fixture.Db.SubtitleCues.CountAsync());

        File.Delete(subtitle);
        await fixture.ScanAsync();

        Assert.AreEqual(0, await fixture.Db.SubtitleTracks.CountAsync());
        Assert.AreEqual(0, await fixture.Db.SubtitleCues.CountAsync());

        var episode = await fixture.Db.Episodes.AsNoTracking().SingleAsync();
        var jimakuTrack = new SubtitleTrack
        {
            EpisodeId = episode.Id,
            Path = $"{SubtitleImportService.JimakuSourcePrefix}1:abc",
            Format = "srt",
            SourceUpdatedAt = DateTime.UtcNow
        };
        fixture.Db.SubtitleTracks.Add(jimakuTrack);
        fixture.Db.SubtitleCues.Add(new SubtitleCue
        {
            SubtitleTrackId = jimakuTrack.Id,
            StartMs = 0,
            EndMs = 1000,
            Text = "猫"
        });
        await fixture.Db.SaveChangesAsync();

        await fixture.ScanAsync();

        Assert.AreEqual(jimakuTrack.Path, (await fixture.Db.SubtitleTracks.AsNoTracking().SingleAsync()).Path);
    }

    [TestMethod]
    public async Task NewHigherPrioritySidecarReplacesCurrentTrack()
    {
        await using var fixture = await SidecarFixture.CreateAsync();
        fixture.WriteSubtitle($"{BaseName}.srt", "猫が走る");
        await fixture.ScanAsync();

        var tagged = fixture.WriteAss($"{BaseName}.ja.ass", "犬が歩く");
        await fixture.ScanAsync();

        Assert.AreEqual(tagged, (await fixture.Db.SubtitleTracks.AsNoTracking().SingleAsync()).Path);
        Assert.AreEqual("犬が歩く", (await fixture.Db.SubtitleCues.AsNoTracking().SingleAsync()).Text);
    }

    private sealed class SidecarFixture : IAsyncDisposable
    {
        private readonly string tempRoot;
        private readonly LibraryRoot root;
        private readonly LibraryScanner scanner;

        private SidecarFixture(
            string tempRoot,
            string seasonDirectory,
            AppDbContext db,
            LibraryRoot root,
            LibraryScanner scanner)
        {
            this.tempRoot = tempRoot;
            SeasonDirectory = seasonDirectory;
            Db = db;
            this.root = root;
            this.scanner = scanner;
        }

        public string SeasonDirectory { get; }
        public AppDbContext Db { get; }

        public static async Task<SidecarFixture> CreateAsync()
        {
            var tempRoot = Path.Combine(
                Path.GetTempPath(),
                $"jularr-sidecar-{Guid.NewGuid():N}");
            var libraryPath = Path.Combine(tempRoot, "anime");
            var seasonDirectory = Path.Combine(libraryPath, "Frieren", "Season 01");
            var dictionaryPath = Path.Combine(tempRoot, "dictionary");

            Directory.CreateDirectory(seasonDirectory);
            Directory.CreateDirectory(dictionaryPath);
            await File.WriteAllTextAsync(Path.Combine(dictionaryPath, "jmdict-ger.tsv"), "");
            await File.WriteAllTextAsync(Path.Combine(dictionaryPath, "jmdict-eng-common.tsv"), "");
            await File.WriteAllBytesAsync(Path.Combine(seasonDirectory, $"{BaseName}.mkv"), [0]);

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(tempRoot, "jularr.db")};Foreign Keys=True")
                .Options;
            var db = new AppDbContext(options);
            await DatabaseMigrationBridge.UpgradeAsync(db);

            var root = new LibraryRoot { Name = "Anime", Path = libraryPath };
            db.LibraryRoots.Add(root);
            db.LibraryRootContentAssignments.Add(new LibraryRootContentAssignment { LibraryRootId = root.Id, ContentType = LibraryContentType.Anime });
            await db.SaveChangesAsync();

            var vocabulary = new VocabularyService(
                db,
                new JapaneseTermExtractor(new EmptyMorphology()),
                new JapaneseDictionary(dictionaryPath));
            var sonarrSync = new SonarrArtworkSyncService(
                new SonarrConnectionStore(
                    DataProtectionProvider.Create(
                        new DirectoryInfo(Path.Combine(tempRoot, "keys")))),
                new SonarrArtworkImportService(
                    db,
                    new TestHttpClientFactory(),
                    NullLogger<SonarrArtworkImportService>.Instance),
                NullLogger<SonarrArtworkSyncService>.Instance);
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
                NullLogger<LibraryScanner>.Instance);

            return new SidecarFixture(tempRoot, seasonDirectory, db, root, scanner);
        }

        public Task<ScanResult> ScanAsync() =>
            scanner.ScanAsync(root.Id, CancellationToken.None);

        public string WriteSubtitle(string relativePath, string text, DateTime? lastWriteUtc = null) =>
            Write(relativePath, $"1\n00:00:01,000 --> 00:00:03,000\n{text}\n", lastWriteUtc);

        public string WriteAss(string relativePath, string text) =>
            Write(
                relativePath,
                "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n" +
                $"Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,{text}\n",
                null);

        public string WriteVtt(string relativePath, string text) =>
            Write(relativePath, $"WEBVTT\n\n00:01.000 --> 00:03.000\n{text}\n", null);

        private string Write(string relativePath, string content, DateTime? lastWriteUtc)
        {
            var path = Path.GetFullPath(Path.Combine(SeasonDirectory, relativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            if (lastWriteUtc is not null)
            {
                File.SetLastWriteTimeUtc(path, lastWriteUtc.Value);
            }

            return path;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();

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

    private sealed class EmptyMorphology : IJapaneseMorphology
    {
        public IReadOnlyList<JapaneseMorphToken> Analyze(string text) => [];
    }
}
