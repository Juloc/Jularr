using Jularr.Web.Data;
using Jularr.Web.Features.ClientApi;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Progress;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Jularr.Tests;

[TestClass]
public sealed class EpisodeFlowTests
{

    [TestMethod]
    public void ResolverFollowsLocalSeasonAndEpisodeOrder()
    {
        var s1e1 = Key(1, 1);
        var s1e2 = Key(1, 2);
        var s1e3 = Key(1, 3);
        var s2e1 = Key(2, 1);
        var s2e2 = Key(2, 2);
        EpisodeOrderKey[] episodes = [s2e2, s1e3, s1e1, s2e1, s1e2];

        Assert.AreEqual(new EpisodeNeighborIds(null, s1e2.Id), EpisodeSequence.Resolve(episodes, s1e1.Id));
        Assert.AreEqual(new EpisodeNeighborIds(s1e1.Id, s1e3.Id), EpisodeSequence.Resolve(episodes, s1e2.Id));
        Assert.AreEqual(new EpisodeNeighborIds(s1e2.Id, s2e1.Id), EpisodeSequence.Resolve(episodes, s1e3.Id));
        Assert.AreEqual(new EpisodeNeighborIds(s1e3.Id, s2e2.Id), EpisodeSequence.Resolve(episodes, s2e1.Id));
        Assert.AreEqual(new EpisodeNeighborIds(s2e1.Id, null), EpisodeSequence.Resolve(episodes, s2e2.Id));
        Assert.AreEqual(EpisodeNeighborIds.None, EpisodeSequence.Resolve(episodes, Guid.NewGuid()));
    }

    [TestMethod]
    public void ResolverReturnsNoNeighborForAmbiguousNumbering()
    {
        var current = Key(1, 1);
        var duplicateCurrent = Key(1, 1);
        var next = Key(1, 2);

        Assert.AreEqual(
            EpisodeNeighborIds.None,
            EpisodeSequence.Resolve([current, duplicateCurrent, next], current.Id));

        var duplicateNextA = Key(1, 2);
        var duplicateNextB = Key(1, 2);
        var resolved = EpisodeSequence.Resolve([current, duplicateNextA, duplicateNextB], current.Id);
        Assert.IsNull(resolved.NextEpisodeId);

        var last = Key(1, 12);
        var nextSeasonA = Key(2, 1);
        var nextSeasonB = Key(2, 1);
        Assert.IsNull(EpisodeSequence.Resolve([last, nextSeasonA, nextSeasonB], last.Id).NextEpisodeId);
    }

    [TestMethod]
    public void ResolverDoesNotGuessAcrossGapsOrSpecials()
    {
        var e1 = Key(1, 1);
        var e3 = Key(1, 3);
        var gap = EpisodeSequence.Resolve([e1, e3], e1.Id);
        Assert.IsNull(gap.NextEpisodeId, "A missing local episode must not be skipped.");
        Assert.IsNull(EpisodeSequence.Resolve([e1, e3], e3.Id).PreviousEpisodeId);

        var s1Last = Key(1, 12);
        var s2Late = Key(2, 3);
        Assert.IsNull(
            EpisodeSequence.Resolve([s1Last, s2Late], s1Last.Id).NextEpisodeId,
            "The next season must start at episode 1 locally.");
        Assert.IsNull(EpisodeSequence.Resolve([s1Last, s2Late], s2Late.Id).PreviousEpisodeId);

        var s1Twelve = Key(1, 12);
        var s3First = Key(3, 1);
        Assert.IsNull(
            EpisodeSequence.Resolve([s1Twelve, s3First], s1Twelve.Id).NextEpisodeId,
            "Seasons must be consecutive.");

        var special = Key(0, 1);
        var specialTwo = Key(0, 2);
        var regular = Key(1, 1);
        EpisodeOrderKey[] withSpecials = [special, specialTwo, regular];
        Assert.AreEqual(
            new EpisodeNeighborIds(null, specialTwo.Id),
            EpisodeSequence.Resolve(withSpecials, special.Id));
        Assert.IsNull(EpisodeSequence.Resolve(withSpecials, specialTwo.Id).NextEpisodeId);
        Assert.IsNull(EpisodeSequence.Resolve(withSpecials, regular.Id).PreviousEpisodeId);
    }

    [TestMethod]
    public async Task FlowUsesOnlyLocalEpisodesAndTheProfileAutoplayPreference()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var anime = await fixture.AddAnimeAsync("flow");
        var first = await fixture.AddEpisodeAsync(anime, 1, 1);
        var second = await fixture.AddEpisodeAsync(anime, 1, 2);
        var withoutMedia = await fixture.AddEpisodeAsync(anime, 1, 3, withMedia: false);
        var other = await fixture.AddAnimeAsync("other-series");
        await fixture.AddEpisodeAsync(other, 1, 3);

        var reader = fixture.Service("reader");
        await reader.UpdatePreferencesAsync(new PlaybackPreferencesUpdate(AutoplayNext: true));

        var flow = await reader.GetFlowAsync(first.Id);
        Assert.IsNotNull(flow);
        Assert.AreEqual(anime.Id, flow.AnimeId);
        Assert.IsNull(flow.Previous);
        Assert.AreEqual(second.Id, flow.Next?.Id);
        Assert.AreEqual(2, flow.Next?.Number);
        Assert.IsTrue(flow.AutoplayNext);

        var secondFlow = await reader.GetFlowAsync(second.Id);
        Assert.IsNotNull(secondFlow);
        Assert.AreEqual(first.Id, secondFlow.Previous?.Id);
        Assert.IsNull(secondFlow.Next, "Episodes without a local media file are not playable neighbors.");

        var orphanFlow = await reader.GetFlowAsync(withoutMedia.Id);
        Assert.AreEqual(second.Id, orphanFlow?.Previous?.Id);

        Assert.IsFalse((await fixture.Service("other").GetFlowAsync(first.Id))!.AutoplayNext);
        Assert.IsNull(await reader.GetFlowAsync(Guid.NewGuid()));

        var client = ClientApiMappings.ToClientEpisodeFlow(flow);
        Assert.AreEqual(second.Id, client.Next?.Id);
        Assert.IsTrue(client.AutoplayNext);
    }

    [TestMethod]
    public void ClientContractAdvertisesAdditiveEpisodeFlowFeatures()
    {
        var capabilities = ClientApiContract.Capabilities();

        Assert.AreEqual(2, capabilities.ApiVersion);
        Assert.IsTrue(capabilities.Features.PlaybackProgress);
        Assert.IsTrue(capabilities.Features.EpisodeFlow);
        Assert.IsTrue(capabilities.Features.ContinueWatching);
        Assert.IsTrue(capabilities.Features.PlaybackHistory);

        var episodeId = Guid.NewGuid();
        Assert.AreEqual($"/api/client/v1/episodes/{episodeId:D}/progress", ClientApiRoutes.Progress(episodeId));
        Assert.AreEqual($"/api/client/v1/episodes/{episodeId:D}/watched", ClientApiRoutes.Watched(episodeId));
        Assert.AreEqual($"/api/client/v1/episodes/{episodeId:D}/flow", ClientApiRoutes.Flow(episodeId));
        Assert.AreEqual("/api/client/v1/continue-watching", ClientApiRoutes.ContinueWatching);
        Assert.AreEqual("/api/client/v1/me/playback-preferences", ClientApiRoutes.PlaybackPreferences);
        Assert.AreEqual("/api/client/v1/me/playback-history", ClientApiRoutes.PlaybackHistory);

        var progress = ClientApiMappings.ToClientEpisodeProgress(
            new EpisodeProgressSnapshot(episodeId, 45_000, 90_000, false, DateTime.UtcNow));
        Assert.AreEqual(45_000, progress.PositionMs);
        Assert.AreEqual(45_000, progress.ResumePositionMs);
        Assert.AreEqual(50, progress.Percent);
    }


    private static EpisodeOrderKey Key(int season, int number) =>
        new(Guid.NewGuid(), season, number);
}
