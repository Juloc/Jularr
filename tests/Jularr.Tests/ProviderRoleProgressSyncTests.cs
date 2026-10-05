using Jularr.Web.Data;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Mapping;
using Jularr.Web.Features.MediaMapping;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Tracking;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

// Issue #568: AniListAccountService used to sync progress for any anime whose episode metadata
// happened to resolve to an AniList entry, regardless of any owner preference. It now only
// contacts AniList when the anime's resolved ProgressTracking role is AniList. These tests prove
// (a) an anime with nothing configured still syncs through AniList exactly as before, (b) an
// anime whose ProgressTracking role was explicitly reassigned away from AniList is never
// contacted, and (c) that reassignment leaves the anime's other roles untouched.
[TestClass]
public sealed class ProviderRoleProgressSyncTests
{
    private const string Owner = "owner";

    private const string OwnerToken = "owner-token";

    [TestMethod]
    public async Task SyncWithNoRoleConfigured_ContactsAniListLikeToday()
    {
        await using var fixture = await Fixture.CreateAsync();
        var anime = await fixture.AddAnimeAsync("Example Anime", "555", episodes: 4);
        await fixture.MarkWatchedAsync(anime, 1, 2);
        await fixture.ConnectAsync(viewerId: 42, OwnerToken);

        var remote = new AniListSyncTests.SyncRemote();
        remote.Put(OwnerToken, mediaId: 555, progress: 1);
        var result = await fixture.Service(remote).SyncAnimeProgressAsync(anime.Id, CancellationToken.None);

        Assert.IsTrue(result.Success, result.Message);
        Assert.IsTrue(remote.Calls > 0, "Nothing configured must still reach AniList, as before #568.");
    }

    [TestMethod]
    public async Task SyncWithProgressTrackingReassigned_NeverContactsAniList()
    {
        await using var fixture = await Fixture.CreateAsync();
        var anime = await fixture.AddAnimeAsync("Example Anime", "555", episodes: 4);
        await fixture.MarkWatchedAsync(anime, 1, 2);
        await fixture.ConnectAsync(viewerId: 42, OwnerToken);
        await fixture.Roles.SetWorkOverrideAsync(
            anime.Id, MappingProviderRole.ProgressTracking, MappingProviders.Mal, CancellationToken.None);

        var remote = new AniListSyncTests.SyncRemote();
        remote.Put(OwnerToken, mediaId: 555, progress: 1);
        var result = await fixture.Service(remote).SyncAnimeProgressAsync(anime.Id, CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message, "No AniList episode mapping exists");
        Assert.AreEqual(0, remote.Calls, "AniList must never be contacted once ProgressTracking points elsewhere.");
    }

    [TestMethod]
    public async Task ReassigningProgressTracking_LeavesOtherRolesForTheSameAnimeUntouched()
    {
        await using var fixture = await Fixture.CreateAsync();
        var anime = await fixture.AddAnimeAsync("Example Anime", "555", episodes: 4);

        await fixture.Roles.SetWorkOverrideAsync(
            anime.Id, MappingProviderRole.ProgressTracking, MappingProviders.Mal, CancellationToken.None);

        var display = await fixture.Roles.ResolveRoleForWorkAsync(
            anime.Id, MappingProviderRole.DisplayMetadata, CancellationToken.None);
        var artwork = await fixture.Roles.ResolveRoleForWorkAsync(
            anime.Id, MappingProviderRole.Artwork, CancellationToken.None);
        var structure = await fixture.Roles.ResolveRoleForWorkAsync(
            anime.Id, MappingProviderRole.EpisodeStructure, CancellationToken.None);

        Assert.AreEqual(MappingProviders.AniList, display.Provider);
        Assert.AreEqual(ProviderRoleSource.BuiltIn, display.Source);
        Assert.AreEqual(MappingProviders.AniList, artwork.Provider);
        Assert.AreEqual(ProviderRoleSource.BuiltIn, artwork.Source);
        Assert.AreEqual(MappingProviders.Local, structure.Provider);
        Assert.AreEqual(ProviderRoleSource.BuiltIn, structure.Source);
    }

    private sealed record TestAnime(Guid Id, IReadOnlyList<Guid> Episodes);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root;
        private readonly AniListAccountStore accountStore;
        private readonly MediaMappingReviewStore reviewStore;
        private readonly ReadingSegmentMappingStore segmentStore;

        private Fixture(string root, AppDbContext db)
        {
            this.root = root;
            Db = db;
            Roles = new ProviderRoleAssignmentStore(db);
            accountStore = new AniListAccountStore(
                new EphemeralDataProtectionProvider(),
                NullLogger<AniListAccountStore>.Instance,
                new DirectoryInfo(root));
            var mappingDirectory = new DirectoryInfo(Path.Combine(root, "anilist"));
            reviewStore = new MediaMappingReviewStore(NullLogger<MediaMappingReviewStore>.Instance, mappingDirectory);
            segmentStore = new ReadingSegmentMappingStore(NullLogger<ReadingSegmentMappingStore>.Instance, mappingDirectory);
        }

        public AppDbContext Db { get; }
        public ProviderRoleAssignmentStore Roles { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "jularr-tests",
                $"progress-roles-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(root, "anilist"));
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "jularr.db")};Foreign Keys=True")
                .Options);
            await DatabaseMigrationBridge.UpgradeAsync(db);
            return new Fixture(root, db);
        }

        public AniListAccountService Service(AniListSyncTests.SyncRemote remote) =>
            new(
                new HttpClient(remote) { BaseAddress = new Uri("https://graphql.anilist.co/") },
                accountStore,
                Db,
                new AnimeMetadataService(Db, [], accountStore, reviewStore),
                segmentStore,
                reviewStore,
                EpisodeFlowFixture.Account(Owner),
                NullLogger<AniListAccountService>.Instance);

        public Task ConnectAsync(int viewerId, string token) =>
            accountStore.SaveAsync(
                Owner,
                new StoredAniListAccount(
                    12345,
                    viewerId,
                    $"viewer-{viewerId}",
                    null,
                    token,
                    DateTimeOffset.UtcNow,
                    null),
                CancellationToken.None);

        public async Task<TestAnime> AddAnimeAsync(string title, string externalId, int episodes)
        {
            var anime = new Anime { Key = Guid.NewGuid().ToString("N"), Title = title };
            Db.Add(anime);
            var episodeIds = new List<Guid>();
            for (var number = 1; number <= episodes; number++)
            {
                var episode = new Episode
                {
                    AnimeId = anime.Id,
                    SeasonNumber = 1,
                    Number = number,
                    Title = $"Episode {number}"
                };
                episodeIds.Add(episode.Id);
                Db.Add(episode);
            }

            Db.Add(new AnimeMetadata
            {
                AnimeId = anime.Id,
                Provider = AniListMetadataProvider.ProviderKey,
                ExternalId = externalId,
                PreferredTitle = title,
                EpisodeCount = episodes
            });

            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return new TestAnime(anime.Id, episodeIds);
        }

        public async Task MarkWatchedAsync(TestAnime anime, params int[] numbers)
        {
            foreach (var number in numbers)
            {
                await CanonicalProgressSeed.SetAsync(Db, Owner, anime.Episodes[number - 1], 0, null, true);
            }

            Db.ChangeTracker.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
