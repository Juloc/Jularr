using System.Text.Json;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;

namespace Jularr.Tests;

// Proves the acquisition engine (release parser + scorer + quality-profile store) is
// media-type-agnostic: anime is one registration, and a second media type registers its own parser
// and default profile without engine changes. MediaAcquisitionKind.Book stands in for any future
// video media type (Movie/TV land in #593/#594) purely to exercise kind-keying.
[TestClass]
public sealed class MediaAcquisitionEngineTests
{
    private const string MoviesProfileId = "movies-web-1080p";

    private static QualityProfile MoviesProfile() =>
        new(
            MoviesProfileId,
            "Movies WEB 1080p",
            ["WEB-1080p", "BLURAY-1080p", "WEB-720p"],
            ["WEB-1080p", "BLURAY-1080p", "WEB-720p"],
            UpgradeAllowed: true,
            UpgradeCutoffQuality: "WEB-1080p",
            MinimumSizeBytes: null,
            MaximumSizeBytes: null,
            MustContain: [],
            MustNotContain: [],
            RequiredRegex: [],
            RejectedRegex: [],
            ScoreRules: [new("Prefer x265", ReleaseRuleField.VideoCodec, ReleaseRuleMatch.Equals, "Hevc", 10)]);

    private static MediaAcquisitionRegistry TwoKindRegistry(IReleaseParser? secondParser = null) =>
        new(
        [
            new AnimeAcquisitionRegistration(),
            new TestRegistration(MediaAcquisitionKind.Book, MoviesProfile(), secondParser)
        ]);

    [TestMethod]
    public void RegistryResolvesParserAndSeedProfilePerKind()
    {
        var custom = new MarkerParser();
        var registry = TwoKindRegistry(custom);

        Assert.IsInstanceOfType<SceneReleaseParser>(registry.ParserFor(MediaAcquisitionKind.Anime));
        Assert.AreSame(custom, registry.ParserFor(MediaAcquisitionKind.Book));

        Assert.AreEqual(AnimeQualityProfiles.DefaultAnime1080pId, registry.DefaultProfileFor(MediaAcquisitionKind.Anime).Id);
        Assert.AreEqual(MoviesProfileId, registry.DefaultProfileFor(MediaAcquisitionKind.Book).Id);

        // The custom parser is actually used, not the scene default.
        Assert.AreEqual("MARKER", registry.ParserFor(MediaAcquisitionKind.Book).Parse("[G] Show - 01 1080p").SeriesTitle);
    }

    [TestMethod]
    public void RegistryFailsFastForUnregisteredKind()
    {
        var registry = new MediaAcquisitionRegistry([new AnimeAcquisitionRegistration()]);

        Assert.IsFalse(registry.Supports(MediaAcquisitionKind.Book));
        Assert.ThrowsExactly<InvalidOperationException>(() => registry.DefaultProfileFor(MediaAcquisitionKind.Book));
    }

    [TestMethod]
    public async Task StoreSeedsAndResolvesDefaultsPerMediaType()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new QualityProfileStore(directory, TwoKindRegistry());

            var anime = await store.ResolveAsync(MediaAcquisitionKind.Anime, null);
            var book = await store.ResolveAsync(MediaAcquisitionKind.Book, null);

            Assert.AreEqual(AnimeQualityProfiles.DefaultAnime1080pId, anime.Id);
            Assert.AreEqual(MoviesProfileId, book.Id);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task WorkAssignmentOverridesKindDefault()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new QualityProfileStore(directory, TwoKindRegistry());
            var workId = Guid.NewGuid();

            await store.AssignWorkAsync(workId, MoviesProfileId);

            // The per-work override wins over the kind default even when resolved for the anime kind.
            var resolved = await store.ResolveAsync(MediaAcquisitionKind.Anime, workId);
            Assert.AreEqual(MoviesProfileId, resolved.Id);

            // Clearing the override falls back to the kind default.
            await store.AssignWorkAsync(workId, null);
            Assert.AreEqual(AnimeQualityProfiles.DefaultAnime1080pId, (await store.ResolveAsync(MediaAcquisitionKind.Anime, workId)).Id);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void ScorerRanksSameReleasesDifferentlyPerProfile()
    {
        var web = new ReleaseCandidate(ReleaseParser.Parse("[G] Show - 01 WEB-DL 1080p HEVC AAC"));
        var bluray = new ReleaseCandidate(ReleaseParser.Parse("[G] Show - 01 BluRay 1080p HEVC AAC"));

        var animeRanked = ReleaseScorer.Rank(AnimeQualityProfiles.CreateDefaultAnime1080p(), [web, bluray]);
        var moviesRanked = ReleaseScorer.Rank(MoviesProfile(), [web, bluray]);

        // Same two releases, opposite winners because each media type's profile orders quality differently.
        Assert.AreEqual("BLURAY-1080p", animeRanked[0].QualityKey);
        Assert.AreEqual("WEB-1080p", moviesRanked[0].QualityKey);
    }

    [TestMethod]
    public async Task LegacyVersion1FileUpgradesToGenericShape()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var anime = AnimeQualityProfiles.CreateDefaultAnime1080p();
            var workId = Guid.NewGuid();
            var webOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
            var legacy = new
            {
                version = 1,
                defaultProfileId = anime.Id,
                profiles = new[] { anime },
                animeProfileAssignments = new Dictionary<string, string> { [workId.ToString("D")] = anime.Id }
            };
            var path = Path.Combine(directory.FullName, "quality-profiles.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(legacy, webOptions));

            var store = new QualityProfileStore(directory);
            var state = await store.LoadAsync();

            Assert.AreEqual(QualityProfileState.CurrentVersion, state.Version);
            Assert.AreEqual(anime.Id, state.DefaultProfileIdFor(MediaAcquisitionKind.Anime));
            Assert.AreEqual(anime.Id, (await store.ResolveAsync(MediaAcquisitionKind.Anime, workId)).Id);

            // The migration is persisted once: the file on disk is now the generic version.
            using var reloaded = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.AreEqual(QualityProfileState.CurrentVersion, reloaded.RootElement.GetProperty("version").GetInt32());
            Assert.IsTrue(reloaded.RootElement.TryGetProperty("kindDefaults", out _));
            Assert.IsTrue(reloaded.RootElement.TryGetProperty("workAssignments", out _));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static DirectoryInfo CreateTemporaryDirectory() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"jularr-engine-{Guid.NewGuid():N}"));

    private sealed class TestRegistration(
        MediaAcquisitionKind kind,
        QualityProfile profile,
        IReleaseParser? parser) : IMediaAcquisitionRegistration
    {
        public MediaAcquisitionKind Kind => kind;

        public IReleaseParser CreateReleaseParser() => parser ?? SceneReleaseParser.Instance;

        public QualityProfile CreateDefaultQualityProfile() => profile;
    }

    private sealed class MarkerParser : IReleaseParser
    {
        public ReleaseInfo Parse(string releaseName) => ReleaseParser.Parse(releaseName) with { SeriesTitle = "MARKER" };

        public bool TryParse(string? releaseName, out ReleaseInfo release)
        {
            if (string.IsNullOrWhiteSpace(releaseName))
            {
                release = default!;
                return false;
            }

            release = Parse(releaseName);
            return true;
        }
    }
}
