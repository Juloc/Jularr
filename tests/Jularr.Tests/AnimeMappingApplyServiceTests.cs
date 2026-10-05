using Jularr.Web.Data;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Mapping;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Tracking;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

[TestClass]
public sealed class AnimeMappingApplyServiceTests
{
    private const string ProfileId = "reader-1";

    [TestMethod]
    public async Task Apply_PersistsRangesAndWritesAudit()
    {
        await using var fixture = await Fixture.CreateAsync(episodeCount: 12);

        var result = await fixture.Service.ApplyAsync(
            fixture.AnimeId,
            [new AnimeMappingRange(1, 1, 12, "anilist", "100", 1, RemoteEpisodeCount: 12)],
            ProfileId,
            CancellationToken.None);

        Assert.IsTrue(result.Applied);
        var applied = await fixture.Service.LoadAppliedRangesAsync(fixture.AnimeId, CancellationToken.None);
        Assert.AreEqual(1, applied.Count);
        Assert.AreEqual("anilist", applied[0].Provider);
        Assert.AreEqual("100", applied[0].ExternalId);

        var audit = await fixture.Audit.ListForAnimeAsync(fixture.AnimeId, CancellationToken.None);
        Assert.AreEqual(1, audit.Count);
        Assert.AreEqual(MappingAuditStore.ActionApply, audit[0].Action);
    }

    [TestMethod]
    public async Task Apply_RefusesConflictingRanges()
    {
        await using var fixture = await Fixture.CreateAsync(episodeCount: 4);

        var result = await fixture.Service.ApplyAsync(
            fixture.AnimeId,
            [
                new AnimeMappingRange(1, 1, 3, "anilist", "100", 1),
                new AnimeMappingRange(1, 2, 4, "anilist", "200", 1)
            ],
            ProfileId,
            CancellationToken.None);

        Assert.IsFalse(result.Applied);
        Assert.AreEqual(0, (await fixture.Service.LoadAppliedRangesAsync(fixture.AnimeId, CancellationToken.None)).Count);
        Assert.AreEqual(0, (await fixture.Audit.ListForAnimeAsync(fixture.AnimeId, CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task Remap_ChangesProviderCoordinatesWithoutLosingProgress()
    {
        await using var fixture = await Fixture.CreateAsync(episodeCount: 12);

        // A viewer has watched local episode 3.
        var episode3 = fixture.EpisodeIds[3];
        await CanonicalProgressSeed.SetAsync(fixture.Db, ProfileId, episode3, 123_456, 1_440_000, true);

        // Apply mapping A (AniList 100), then remap to mapping B (AniList 200).
        await fixture.Service.ApplyAsync(
            fixture.AnimeId,
            [new AnimeMappingRange(1, 1, 12, "anilist", "100", 1, RemoteEpisodeCount: 12)],
            ProfileId, CancellationToken.None);

        var afterFirst = await ReadProgressAsync(fixture, episode3);
        Assert.IsNotNull(afterFirst);

        var remap = await fixture.Service.ApplyAsync(
            fixture.AnimeId,
            [new AnimeMappingRange(1, 1, 12, "anilist", "200", 5, RemoteEpisodeCount: 24)],
            ProfileId, CancellationToken.None);
        Assert.IsTrue(remap.Applied);

        // Provider coordinates changed: episode 3 now points at AniList 200 (remote ep 7).
        var applied = await fixture.Service.LoadAppliedRangesAsync(fixture.AnimeId, CancellationToken.None);
        Assert.AreEqual("200", applied[0].ExternalId);
        Assert.AreEqual(7, applied[0].ResolveRemoteEpisode(3));

        // Progress survives the remap unchanged — it keys on the stable EpisodeId, not provider coords.
        var afterRemap = await ReadProgressAsync(fixture, episode3);
        Assert.IsNotNull(afterRemap);
        Assert.AreEqual(episode3, afterRemap!.EpisodeId);
        Assert.AreEqual(123_456, afterRemap.PositionMs);
        Assert.IsTrue(afterRemap.IsCompleted);
        Assert.AreEqual(1, (await CanonicalProgressSeed.RowsAsync(fixture.Db, fixture.AnimeId)).Count);
    }

    [TestMethod]
    public async Task MarkUnmapped_ClearsRangesAndRecordsAudit()
    {
        await using var fixture = await Fixture.CreateAsync(episodeCount: 6);

        await fixture.Service.ApplyAsync(
            fixture.AnimeId,
            [new AnimeMappingRange(1, 1, 6, "anilist", "100", 1)],
            ProfileId, CancellationToken.None);

        await fixture.Service.MarkUnmappedAsync(fixture.AnimeId, ProfileId, CancellationToken.None);

        Assert.AreEqual(0, (await fixture.Service.LoadAppliedRangesAsync(fixture.AnimeId, CancellationToken.None)).Count);
        var audit = await fixture.Audit.ListForAnimeAsync(fixture.AnimeId, CancellationToken.None);
        Assert.IsTrue(audit.Any(x => x.Action == MappingAuditStore.ActionUnmapped));
    }

    private static async Task<LegacyEpisodeProgress?> ReadProgressAsync(Fixture fixture, Guid episodeId) =>
        (await CanonicalProgressSeed.RowsAsync(fixture.Db, fixture.AnimeId)).SingleOrDefault(x => x.EpisodeId == episodeId);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;

        private Fixture(string root, AppDbContext db, Guid animeId, IReadOnlyDictionary<int, Guid> episodeIds, AnimeMappingApplyService service, MappingAuditStore audit)
        {
            _root = root;
            Db = db;
            AnimeId = animeId;
            EpisodeIds = episodeIds;
            Service = service;
            Audit = audit;
        }

        public AppDbContext Db { get; }
        public Guid AnimeId { get; }
        public IReadOnlyDictionary<int, Guid> EpisodeIds { get; }
        public AnimeMappingApplyService Service { get; }
        public MappingAuditStore Audit { get; }

        public static async Task<Fixture> CreateAsync(int episodeCount)
        {
            var root = Path.Combine(Path.GetTempPath(), $"jularr-mapapply-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite($"Data Source={Path.Combine(root, "jularr.db")};Foreign Keys=True")
                    .Options);
            await DatabaseMigrationBridge.UpgradeAsync(db);

            var anime = new Anime { Title = "Test Anime", Key = "test" };
            db.Anime.Add(anime);
            var episodeIds = new Dictionary<int, Guid>();
            for (var n = 1; n <= episodeCount; n++)
            {
                var episode = new Episode { AnimeId = anime.Id, SeasonNumber = 1, Number = n, Title = $"Episode {n}" };
                db.Episodes.Add(episode);
                episodeIds[n] = episode.Id;
            }

            await db.SaveChangesAsync();

            var mappingStore = new AniListAccountStore(
                new EphemeralDataProtectionProvider(),
                NullLogger<AniListAccountStore>.Instance,
                new DirectoryInfo(Path.Combine(root, "integrations")));
            var audit = new MappingAuditStore(db);
            var service = new AnimeMappingApplyService(db, mappingStore, audit);

            return new Fixture(root, db, anime.Id, episodeIds, service, audit);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
