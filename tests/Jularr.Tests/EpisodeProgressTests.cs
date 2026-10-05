using Jularr.Web.Data;
using Jularr.Web.Features.Progress;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class EpisodeProgressTests
{
    [TestMethod]
    public async Task ProgressIsProfileScopedAndWatchedStateIsSticky()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var anime = await fixture.AddAnimeAsync("progress-anime");
        var episode = await fixture.AddEpisodeAsync(anime, 1, 3);

        var readerA = fixture.Service("reader-a");
        var readerB = fixture.Service("reader-b");

        var a = await readerA.UpdateAsync(
            episode.Id,
            new EpisodeProgressUpdate(40_000, 200_000, false));
        var b = await readerB.UpdateAsync(
            episode.Id,
            new EpisodeProgressUpdate(120_000, 200_000, false));

        Assert.IsNotNull(a);
        Assert.IsNotNull(b);
        Assert.AreEqual(20, a.Percent);
        Assert.AreEqual(60, b.Percent);
        Assert.AreEqual(2, await CountAsync(fixture.Db, "MediaProgress"));

        var completed = await readerA.UpdateAsync(
            episode.Id,
            new EpisodeProgressUpdate(192_000, 200_000, true));

        Assert.IsNotNull(completed);
        Assert.IsTrue(completed.IsCompleted);
        Assert.AreEqual(100, completed.Percent);
        Assert.AreEqual(0, completed.ResumePositionMs);

        var rewatch = await readerA.UpdateAsync(
            episode.Id,
            new EpisodeProgressUpdate(50_000, 200_000, false));

        Assert.IsNotNull(rewatch);
        Assert.IsTrue(rewatch.IsCompleted, "Rewatching must not silently flip watched state.");
        Assert.AreEqual(50_000, rewatch.ResumePositionMs);

        var readerBAfter = await readerB.GetAsync(episode.Id);
        Assert.IsNotNull(readerBAfter);
        Assert.IsFalse(readerBAfter.IsCompleted);
        Assert.AreEqual(120_000, readerBAfter.ResumePositionMs);
    }

    [TestMethod]
    public async Task ExistingEpisodeWithoutProgressReturnsEmptySnapshot()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var anime = await fixture.AddAnimeAsync("empty-progress");
        var episode = await fixture.AddEpisodeAsync(anime, 1, 1);

        var service = fixture.Service("reader");
        var progress = await service.GetAsync(episode.Id);

        Assert.IsNotNull(progress);
        Assert.AreEqual(0, progress.PositionMs);
        Assert.AreEqual(0, progress.Percent);
        Assert.IsNull(progress.UpdatedAt);

        Assert.IsNull(await service.GetAsync(Guid.NewGuid()));
        Assert.IsNull(await service.UpdateAsync(
            Guid.NewGuid(),
            new EpisodeProgressUpdate(60_000, null, false)));
        Assert.IsNull(await service.SetWatchedAsync(Guid.NewGuid(), true));
    }

    [TestMethod]
    public async Task TinyAccidentalStartCreatesNoProgressOrHistory()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var anime = await fixture.AddAnimeAsync("tiny-start");
        var episode = await fixture.AddEpisodeAsync(anime, 1, 1);
        var service = fixture.Service("reader");

        var snapshot = await service.UpdateAsync(
            episode.Id,
            new EpisodeProgressUpdate(
                EpisodeProgressService.MinimumResumeMs - 1,
                1_400_000,
                false));

        Assert.IsNotNull(snapshot);
        Assert.IsNull(snapshot.UpdatedAt);
        Assert.AreEqual(0, snapshot.ResumePositionMs);
        Assert.AreEqual(0, await CountAsync(fixture.Db, "MediaProgress"));
        Assert.AreEqual(0, await CountAsync(fixture.Db, "MediaPlaybackHistory"));
        Assert.AreEqual(0, (await service.GetContinueWatchingAsync()).Count);
    }

    [TestMethod]
    public async Task OnlyDeclaredCompletionMarksWatchedAndClearsResume()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var anime = await fixture.AddAnimeAsync("threshold");
        var natural = await fixture.AddEpisodeAsync(anime, 1, 1);
        var ended = await fixture.AddEpisodeAsync(anime, 1, 2);
        var service = fixture.Service("reader");

        var belowThreshold = await service.UpdateAsync(
            natural.Id,
            new EpisodeProgressUpdate(949_000, 1_000_000, false));
        Assert.IsNotNull(belowThreshold);
        Assert.IsFalse(belowThreshold.IsCompleted);
        Assert.AreEqual(949_000, belowThreshold.ResumePositionMs);

        var seekedPastThreshold = await service.UpdateAsync(
            natural.Id,
            new EpisodeProgressUpdate(960_000, 1_000_000, false));
        Assert.IsNotNull(seekedPastThreshold);
        Assert.IsFalse(seekedPastThreshold.IsCompleted, "A position past the threshold without a declared completion is a seek, not a finish.");
        Assert.AreEqual(960_000, seekedPastThreshold.ResumePositionMs);

        var omittedDuration = await service.UpdateAsync(
            natural.Id,
            new EpisodeProgressUpdate(970_000, null, false));
        Assert.IsNotNull(omittedDuration);
        Assert.IsFalse(omittedDuration.IsCompleted);

        var naturalFinish = await service.UpdateAsync(
            natural.Id,
            new EpisodeProgressUpdate(960_000, null, true));
        Assert.IsNotNull(naturalFinish);
        Assert.IsTrue(naturalFinish.IsCompleted);
        Assert.AreEqual(0, naturalFinish.PositionMs);

        var explicitEnd = await service.UpdateAsync(
            ended.Id,
            new EpisodeProgressUpdate(10_000, null, true));
        Assert.IsNotNull(explicitEnd);
        Assert.IsTrue(explicitEnd.IsCompleted);
        Assert.AreEqual(100, explicitEnd.Percent);
    }

    [TestMethod]
    public async Task RestartFromBeginningClearsTheResumePosition()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var anime = await fixture.AddAnimeAsync("restart");
        var episode = await fixture.AddEpisodeAsync(anime, 1, 1);
        var service = fixture.Service("reader");

        await service.UpdateAsync(
            episode.Id,
            new EpisodeProgressUpdate(600_000, 1_400_000, false));

        var restarted = await service.UpdateAsync(
            episode.Id,
            new EpisodeProgressUpdate(0, 1_400_000, false));

        Assert.IsNotNull(restarted);
        Assert.IsFalse(restarted.IsCompleted);
        Assert.AreEqual(0, restarted.ResumePositionMs);

        var shortlyAfterRestart = await service.UpdateAsync(
            episode.Id,
            new EpisodeProgressUpdate(12_000, 1_400_000, false));

        Assert.IsNotNull(shortlyAfterRestart);
        Assert.AreEqual(
            0,
            shortlyAfterRestart.ResumePositionMs,
            "A few seconds after an explicit restart must not become a resume point.");
        Assert.AreEqual(0, (await service.GetContinueWatchingAsync()).Count);
    }

    [TestMethod]
    public async Task ManualWatchedStateIsExplicitAndClearsResume()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var anime = await fixture.AddAnimeAsync("manual");
        var partial = await fixture.AddEpisodeAsync(anime, 1, 1);
        var untouched = await fixture.AddEpisodeAsync(anime, 1, 2);
        var service = fixture.Service("reader");

        await service.UpdateAsync(
            partial.Id,
            new EpisodeProgressUpdate(300_000, 1_400_000, false));

        var watched = await service.SetWatchedAsync(partial.Id, true);
        Assert.IsNotNull(watched);
        Assert.IsTrue(watched.IsCompleted);
        Assert.AreEqual(0, watched.ResumePositionMs);

        var unwatched = await service.SetWatchedAsync(partial.Id, false);
        Assert.IsNotNull(unwatched);
        Assert.IsFalse(unwatched.IsCompleted);
        Assert.AreEqual(0, unwatched.ResumePositionMs);

        var noRow = await service.SetWatchedAsync(untouched.Id, false);
        Assert.IsNotNull(noRow);
        Assert.IsNull((await service.GetAsync(untouched.Id))!.UpdatedAt, "Marking an untouched episode unwatched creates no state.");

        await service.SetWatchedAsync(untouched.Id, true);
        Assert.IsTrue((await service.GetAsync(untouched.Id))!.IsCompleted);

        Assert.AreEqual(
            1,
            await CountAsync(fixture.Db, "MediaPlaybackHistory"),
            "Manual watched actions are state, not playback history.");
        Assert.IsFalse((await fixture.Service("other").GetAsync(untouched.Id))!.IsCompleted);
    }

    [TestMethod]
    public async Task WatchedStateAndResumeSurviveARestart()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var anime = await fixture.AddAnimeAsync("restart-server");
        var watched = await fixture.AddEpisodeAsync(anime, 1, 1);
        var partial = await fixture.AddEpisodeAsync(anime, 1, 2);

        await fixture.Service("reader").SetWatchedAsync(watched.Id, true);
        await fixture.Service("reader").UpdateAsync(
            partial.Id,
            new EpisodeProgressUpdate(420_000, 1_400_000, false));

        await fixture.ReopenAsync();
        var service = fixture.Service("reader");

        Assert.IsTrue((await service.GetAsync(watched.Id))!.IsCompleted);
        Assert.AreEqual(420_000, (await service.GetAsync(partial.Id))!.ResumePositionMs);
    }

    [TestMethod]
    public async Task HistoryMergesSessionsIsBoundedAndClearedPerProfile()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var anime = await fixture.AddAnimeAsync("history");
        var first = await fixture.AddEpisodeAsync(anime, 1, 1);
        var second = await fixture.AddEpisodeAsync(anime, 1, 2);
        var reader = fixture.Service("reader");
        var other = fixture.Service("other");

        await reader.UpdateAsync(first.Id, new EpisodeProgressUpdate(60_000, 1_400_000, false));
        await reader.UpdateAsync(first.Id, new EpisodeProgressUpdate(90_000, 1_400_000, false));

        var merged = await reader.GetHistoryAsync();
        Assert.AreEqual(1, merged.Count, "Checkpoints of one session extend one entry.");
        Assert.AreEqual(90_000, merged[0].PositionMs);

        var staleAt = DateTime.UtcNow - VideoProgressService.HistorySessionGap - TimeSpan.FromMinutes(1);
        await fixture.Db.Database.ExecuteSqlRawAsync("""UPDATE "MediaPlaybackHistory" SET "LastPlayedAt" = {0}""", staleAt);

        await reader.UpdateAsync(first.Id, new EpisodeProgressUpdate(120_000, 1_400_000, false));
        Assert.AreEqual(2, (await reader.GetHistoryAsync()).Count, "A later session starts a new entry.");

        for (var index = 0; index < EpisodeProgressService.HistoryLimit + 5; index++)
        {
            var episode = index % 2 == 0 ? second : first;
            await reader.UpdateAsync(
                episode.Id,
                new EpisodeProgressUpdate(60_000 + index * 1_000, 1_400_000, false));
        }

        await other.UpdateAsync(second.Id, new EpisodeProgressUpdate(60_000, 1_400_000, false));

        Assert.AreEqual(
            EpisodeProgressService.HistoryLimit,
            await CountAsync(fixture.Db, "MediaPlaybackHistory", "reader"));

        var history = await reader.GetHistoryAsync();
        Assert.AreEqual(EpisodeProgressService.HistoryLimit, history.Count);
        Assert.IsTrue(history.Zip(history.Skip(1)).All(pair => pair.First.LastPlayedAt >= pair.Second.LastPlayedAt));
        Assert.AreEqual(1, (await other.GetHistoryAsync()).Count);

        await reader.ClearHistoryAsync();

        Assert.AreEqual(0, (await reader.GetHistoryAsync()).Count);
        Assert.AreEqual(1, (await other.GetHistoryAsync()).Count, "Clearing is limited to the own profile.");
        Assert.IsNotNull(await reader.GetAsync(first.Id));
        Assert.IsTrue(
            (await reader.GetAsync(first.Id))!.ResumePositionMs > 0,
            "Clearing history keeps resume positions.");
    }

    [TestMethod]
    public async Task AdapterWritesOnlyCanonicalStateAndNeverTheLegacyTables()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var anime = await fixture.AddAnimeAsync("single-writer");
        var first = await fixture.AddEpisodeAsync(anime, 1, 1);
        var second = await fixture.AddEpisodeAsync(anime, 1, 2);
        var service = fixture.Service("reader");

        await service.UpdateAsync(first.Id, new EpisodeProgressUpdate(300_000, 1_400_000, false));
        await service.UpdateAsync(first.Id, new EpisodeProgressUpdate(1_400_000, 1_400_000, true));
        await service.SetWatchedAsync(second.Id, true);

        Assert.AreEqual(0, await fixture.Db.EpisodeProgress.CountAsync(), "The legacy progress table has no runtime writer.");
        Assert.AreEqual(0, await fixture.Db.EpisodePlaybackHistory.CountAsync(), "The legacy history table has no runtime writer.");
        Assert.AreEqual(2, await CountAsync(fixture.Db, "MediaProgress", "reader"));
        Assert.AreEqual(1, await CountAsync(fixture.Db, "MediaPlaybackHistory", "reader"));
        Assert.IsTrue((await service.GetAsync(first.Id))!.IsCompleted);
        Assert.IsTrue((await service.GetAsync(second.Id))!.IsCompleted);
    }

    [TestMethod]
    public async Task AutoplayNextDefaultsOffAndIsProfileScoped()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var reader = fixture.Service("reader");
        var other = fixture.Service("other");

        Assert.IsFalse((await reader.GetPreferencesAsync()).AutoplayNext);

        Assert.IsTrue((await reader.UpdatePreferencesAsync(new PlaybackPreferencesUpdate(AutoplayNext: true))).AutoplayNext);
        Assert.IsTrue((await reader.GetPreferencesAsync()).AutoplayNext);
        Assert.IsFalse((await other.GetPreferencesAsync()).AutoplayNext);

        Assert.IsFalse((await reader.UpdatePreferencesAsync(new PlaybackPreferencesUpdate(AutoplayNext: false))).AutoplayNext);
        Assert.AreEqual(1, await fixture.Db.ProfilePlaybackPreferences.CountAsync());
    }

    private static async Task<int> CountAsync(AppDbContext db, string table, string? profileId = null) =>
        await db.Database.SqlQueryRaw<int>(
                $$"""SELECT COUNT(*)::int AS "Value" FROM "{{table}}" WHERE {0}::text IS NULL OR "ProfileId" = {0}""",
                (object?)profileId ?? DBNull.Value)
            .SingleAsync();
}
