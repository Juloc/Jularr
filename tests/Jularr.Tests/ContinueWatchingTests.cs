using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.ClientApi;
using Jularr.Web.Features.Progress;
using Jularr.Web.Pages;

namespace Jularr.Tests;

[TestClass]
public sealed class ContinueWatchingTests
{
    private static readonly DateTime BaseTime = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public async Task OnlyMeaningfulUnfinishedEpisodesAppearNewestFirstWithStableTieBreak()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var alpha = await fixture.AddAnimeAsync("alpha");
        var beta = await fixture.AddAnimeAsync("beta");
        var gamma = await fixture.AddAnimeAsync("gamma");
        var delta = await fixture.AddAnimeAsync("delta");

        var alphaEpisode = await fixture.AddEpisodeAsync(alpha, 1, 1,
            id: Guid.Parse("00000000-0000-0000-0000-0000000000b1"));
        var betaEpisode = await fixture.AddEpisodeAsync(beta, 1, 1,
            id: Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
        var gammaEpisode = await fixture.AddEpisodeAsync(gamma, 1, 1);
        var deltaEpisode = await fixture.AddEpisodeAsync(delta, 1, 1, withMedia: false);

        var reader = fixture.Service("reader");
        await reader.UpdateAsync(alphaEpisode.Id, new EpisodeProgressUpdate(300_000, 1_400_000, false));
        await reader.UpdateAsync(betaEpisode.Id, new EpisodeProgressUpdate(600_000, 1_400_000, false));
        await reader.UpdateAsync(gammaEpisode.Id, new EpisodeProgressUpdate(900_000, 1_400_000, false));
        await reader.UpdateAsync(deltaEpisode.Id, new EpisodeProgressUpdate(900_000, 1_400_000, false));
        await fixture.Service("other").UpdateAsync(
            alphaEpisode.Id,
            new EpisodeProgressUpdate(700_000, 1_400_000, false));

        await fixture.SetUpdatedAtAsync("reader", alphaEpisode.Id, BaseTime);
        await fixture.SetUpdatedAtAsync("reader", betaEpisode.Id, BaseTime);
        await fixture.SetUpdatedAtAsync("reader", gammaEpisode.Id, BaseTime.AddMinutes(5));
        await fixture.SetUpdatedAtAsync("reader", deltaEpisode.Id, BaseTime.AddMinutes(10));

        var items = await reader.GetContinueWatchingAsync();

        Assert.AreEqual(gammaEpisode.Id, items[0].EpisodeId, "Newest first; episodes without media are skipped.");
        CollectionAssert.AreEquivalent(
            new[] { gammaEpisode.Id, betaEpisode.Id, alphaEpisode.Id },
            items.Select(x => x.EpisodeId).ToArray());
        Assert.IsTrue(items.All(x => x.Kind == ContinueWatchingKind.Resume));
        var alphaItem = items.Single(x => x.EpisodeId == alphaEpisode.Id);
        Assert.AreEqual(300_000, alphaItem.ResumePositionMs, "Another profile's progress must not leak.");
        Assert.AreEqual(21, alphaItem.Percent);
        Assert.AreEqual(1_100_000, alphaItem.RemainingMs);

        await reader.UpdateAsync(gammaEpisode.Id, new EpisodeProgressUpdate(1_390_000, 1_400_000, true));

        var remaining = (await reader.GetContinueWatchingAsync()).Select(x => x.EpisodeId).ToArray();
        CollectionAssert.AreEquivalent(
            new[] { betaEpisode.Id, alphaEpisode.Id },
            remaining,
            "Completed episodes of a series without a next local episode disappear.");

        CollectionAssert.AreEqual(
            remaining,
            (await reader.GetContinueWatchingAsync()).Select(x => x.EpisodeId).ToArray(),
            "Equal timestamps order stably.");
    }

    [TestMethod]
    public async Task WatchedEpisodeSurfacesTheCanonicalNextLocalEpisode()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var anime = await fixture.AddAnimeAsync("series");
        var first = await fixture.AddEpisodeAsync(anime, 1, 1);
        var second = await fixture.AddEpisodeAsync(anime, 1, 2);
        var third = await fixture.AddEpisodeAsync(anime, 1, 3);
        var reader = fixture.Service("reader");

        await reader.UpdateAsync(first.Id, new EpisodeProgressUpdate(400_000, 1_400_000, false));
        await fixture.SetUpdatedAtAsync("reader", first.Id, BaseTime);

        var resume = Assert.ContainsSingle(await reader.GetContinueWatchingAsync());
        Assert.AreEqual(ContinueWatchingKind.Resume, resume.Kind);
        Assert.AreEqual(first.Id, resume.EpisodeId);

        await reader.UpdateAsync(first.Id, new EpisodeProgressUpdate(1_400_000, 1_400_000, true));

        var upNext = Assert.ContainsSingle(await reader.GetContinueWatchingAsync());
        Assert.AreEqual(ContinueWatchingKind.UpNext, upNext.Kind, "One item per series.");
        Assert.AreEqual(second.Id, upNext.EpisodeId);
        Assert.AreEqual(0, upNext.ResumePositionMs);
        Assert.AreEqual("up_next", ClientApiMappings.ToClientContinueWatchingItem(upNext).Kind);

        await reader.UpdateAsync(second.Id, new EpisodeProgressUpdate(200_000, 1_400_000, false));
        await fixture.SetUpdatedAtAsync("reader", second.Id, BaseTime.AddDays(-1));

        var upNextWithProgress = Assert.ContainsSingle(await reader.GetContinueWatchingAsync());
        Assert.AreEqual(second.Id, upNextWithProgress.EpisodeId);
        Assert.AreEqual(200_000, upNextWithProgress.ResumePositionMs);

        await reader.SetWatchedAsync(second.Id, true);

        var afterSecond = Assert.ContainsSingle(await reader.GetContinueWatchingAsync());
        Assert.AreEqual(third.Id, afterSecond.EpisodeId);
        Assert.AreEqual(ContinueWatchingKind.UpNext, afterSecond.Kind);
        Assert.AreEqual(0, (await fixture.Service("other").GetContinueWatchingAsync()).Count);

        await reader.SetWatchedAsync(third.Id, true);

        Assert.AreEqual(
            0,
            (await reader.GetContinueWatchingAsync()).Count,
            "A finished series without a next local episode leaves Continue Watching.");
    }

    [TestMethod]
    public async Task NextEpisodeThatIsAlreadyWatchedIsNotSuggested()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var anime = await fixture.AddAnimeAsync("out-of-order");
        var first = await fixture.AddEpisodeAsync(anime, 1, 1);
        var second = await fixture.AddEpisodeAsync(anime, 1, 2);
        var reader = fixture.Service("reader");

        await reader.SetWatchedAsync(second.Id, true);
        await fixture.SetUpdatedAtAsync("reader", second.Id, BaseTime);
        await reader.SetWatchedAsync(first.Id, true);

        Assert.AreEqual(0, (await reader.GetContinueWatchingAsync()).Count);
    }

    [TestMethod]
    public async Task HomeShowsContinueWatchingAndPersonalHistory()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var anime = await fixture.AddAnimeAsync("home");
        var episode = await fixture.AddEpisodeAsync(anime, 1, 1);
        await fixture.Service("reader").UpdateAsync(
            episode.Id,
            new EpisodeProgressUpdate(500_000, 1_400_000, false));

        var model = EpisodeFlowFixture.Home(fixture.Db, EpisodeFlowFixture.Account("reader"));
        await model.LoadHomeAsync(DiscoveryCategory.All, CancellationToken.None);

        var item = Assert.ContainsSingle(model.ContinueWatching);
        Assert.AreEqual($"/Library/Episode/{episode.Id}", item.PlayHref);
        Assert.AreEqual($"/Library/Episode/{episode.Id}", Assert.ContainsSingle(model.PlaybackHistory).PlayHref);

        var otherModel = EpisodeFlowFixture.Home(fixture.Db, EpisodeFlowFixture.Account("other"));
        await otherModel.LoadHomeAsync(DiscoveryCategory.All, CancellationToken.None);
        Assert.AreEqual(0, otherModel.ContinueWatching.Count);
        Assert.AreEqual(0, otherModel.PlaybackHistory.Count);
    }
}
