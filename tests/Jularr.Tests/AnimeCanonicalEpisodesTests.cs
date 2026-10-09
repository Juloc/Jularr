using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Tests;

[TestClass]
public sealed class AnimeCanonicalEpisodesTests
{
    private static async Task<List<(int Season, int Episode)>> EpisodesAsync(AnimeAcquisitionEnvironment environment) =>
        (await environment.Db.WorkEpisodes.AsNoTracking().ToListAsync()).Select(episode => (episode.SeasonNumber, episode.EpisodeNumber)).OrderBy(pair => pair).ToList();

    [TestMethod]
    public async Task ExpectedSlotsBecomeCanonicalEpisodesOnceAndNeverOverwriteWhatExists()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(episodeCount: 3);
        await environment.Db.Database.ExecuteSqlRawAsync("""UPDATE "WorkEpisodes" SET "Title" = 'Kept', "AbsoluteNumber" = 7""");

        var created = await environment.WithScopeAsync(async services => await services.GetRequiredService<AnimeCanonicalEpisodes>().EnsureAsync(AnimeAcquisitionEnvironment.AnimeKey, CancellationToken.None));
        var again = await environment.WithScopeAsync(async services => await services.GetRequiredService<AnimeCanonicalEpisodes>().EnsureAsync(AnimeAcquisitionEnvironment.AnimeKey, CancellationToken.None));

        CollectionAssert.AreEqual(new[] { (1, 1), (1, 2), (1, 3) }, (await EpisodesAsync(environment)).ToArray());
        Assert.AreEqual(2, created, "The local episode already had its canonical row.");
        Assert.AreEqual(0, again);
        var local = await environment.Db.WorkEpisodes.AsNoTracking().SingleAsync(episode => episode.EpisodeNumber == 1);
        Assert.AreEqual("Kept", local.Title);
        Assert.AreEqual(7, local.AbsoluteNumber);
        Assert.IsNotNull(local.SeasonId);
    }

    private static Task EnsureAsync(AnimeAcquisitionEnvironment environment) =>
        environment.WithScopeAsync(async services => await services.GetRequiredService<AnimeCanonicalEpisodes>().EnsureAsync(AnimeAcquisitionEnvironment.AnimeKey, CancellationToken.None));

    private static async Task<Dictionary<(int Season, int Episode), int?>> AbsolutesAsync(AnimeAcquisitionEnvironment environment) =>
        (await environment.Db.WorkEpisodes.AsNoTracking().ToListAsync()).ToDictionary(episode => (episode.SeasonNumber, episode.EpisodeNumber), episode => episode.AbsoluteNumber);

    [TestMethod]
    public async Task KnownAbsoluteNumbersOfTheSlotsAreKeptOnNewAndExistingEpisodes()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(episodeCount: 3);

        await EnsureAsync(environment);

        var absolutes = await AbsolutesAsync(environment);
        CollectionAssert.AreEqual(new int?[] { 1, 2, 3 }, new[] { absolutes[(1, 1)], absolutes[(1, 2)], absolutes[(1, 3)] }, "The local episode gets its number too.");
    }

    [TestMethod]
    public async Task AnAbsoluteNumberAnotherEpisodeHoldsIsNeverClaimedTwice()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(episodeCount: 3);
        await environment.Db.Database.ExecuteSqlRawAsync("""UPDATE "WorkEpisodes" SET "AbsoluteNumber" = 3""");

        await EnsureAsync(environment);

        var absolutes = await AbsolutesAsync(environment);
        CollectionAssert.AreEquivalent(new[] { (1, 1), (1, 2) }, absolutes.Keys.ToArray(), "The slot whose number the corrected episode holds is not made a second episode.");
        Assert.AreEqual(3, absolutes[(1, 1)]);
        Assert.AreEqual(2, absolutes[(1, 2)]);
    }

    [TestMethod]
    public async Task SlotsWhoseEntriesAllCountFromOneGetNoAbsoluteNumber()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(episodeCount: 2);
        var now = DateTimeOffset.UtcNow;
        foreach (var (season, id) in new[] { (1, "100"), (2, "200") })
        {
            await environment.AniListAccounts.TryAddEpisodeMappingAsync(new AnimeEpisodeMetadataMapping(Guid.NewGuid(), environment.AnimeId, season, 1, 2, 1, "anilist", id, "Frieren", 2, now), CancellationToken.None);
        }

        await EnsureAsync(environment);

        var absolutes = await AbsolutesAsync(environment);
        Assert.AreEqual(4, absolutes.Count);
        Assert.IsTrue(absolutes.Values.All(value => value is null), "Two entries that each start at 1 do not name one absolute number.");
    }

    [TestMethod]
    public async Task AnAnimeWithoutKnownSlotsGetsNoInventedEpisodes()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(episodeCount: null);

        await environment.WithScopeAsync(async services => await services.GetRequiredService<AnimeCanonicalEpisodes>().EnsureAsync(AnimeAcquisitionEnvironment.AnimeKey, CancellationToken.None));

        CollectionAssert.AreEqual(new[] { (1, 1) }, (await EpisodesAsync(environment)).ToArray(), "Only the episode on disk exists; nothing is expected without an episode count.");
    }

    [TestMethod]
    public async Task MonitoredAnimeEpisodesWithoutAFileAreWantedThroughTheSharedQueue()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(episodeCount: 3);
        await environment.WithScopeAsync(async services => await services.GetRequiredService<AnimeCanonicalEpisodes>().EnsureAsync(AnimeAcquisitionEnvironment.AnimeKey, CancellationToken.None));

        await new WantedReconciler(environment.Db, TimeProvider.System).ReconcileAsync(null, CancellationToken.None);

        var wanted = await environment.Db.WantedItems.AsNoTracking().ToListAsync();
        var episodes = await environment.Db.WorkEpisodes.AsNoTracking().ToDictionaryAsync(episode => episode.Id);
        CollectionAssert.AreEqual(new[] { 2, 3 }, wanted.Select(item => episodes[item.TargetId].EpisodeNumber).Order().ToArray(), "The episode on disk is covered; the other two are wanted.");
        Assert.IsTrue(wanted.All(item => item.TargetKind == WantedTargetKind.Episode));
    }
}
