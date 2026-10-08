using Jularr.Web.Features.Acquisition.Pipeline;
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

    [TestMethod]
    public async Task AnAnimeWithoutKnownSlotsGetsNoInventedEpisodes()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(episodeCount: null);

        await environment.WithScopeAsync(async services => await services.GetRequiredService<AnimeCanonicalEpisodes>().EnsureAsync(AnimeAcquisitionEnvironment.AnimeKey, CancellationToken.None));

        CollectionAssert.AreEqual(new[] { (1, 1) }, (await EpisodesAsync(environment)).ToArray(), "Only the episode on disk exists; nothing is expected without an episode count.");
    }
}
