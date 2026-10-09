using Jularr.Web.Data;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Features.Progress;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class VideoProgressTests
{
    [TestMethod]
    public async Task MovieProgressIsProfileScopedStickyAndIdempotent()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var movie = new Work { MediaType = WorkMediaType.Movie, CanonicalTitle = "Movie" };
        db.Works.Add(movie);
        await db.SaveChangesAsync();

        var service = new VideoProgressService(db);
        var target = MediaProgressTarget.Movie(movie.Id);

        var started = await service.UpdateAsync(
            "reader",
            target,
            new MediaProgressUpdate(60_000, 100_000, Completed: false));
        Assert.IsNotNull(started);
        Assert.AreEqual(60_000L, started.PositionMs);
        Assert.IsFalse(started.IsCompleted);

        var other = await service.GetAsync("other", target);
        Assert.IsNotNull(other);
        Assert.AreEqual(0L, other.PositionMs);
        Assert.IsNull(other.UpdatedAt);

        var seeked = await service.UpdateAsync(
            "reader",
            target,
            new MediaProgressUpdate(96_000, 100_000, Completed: false));
        Assert.IsNotNull(seeked);
        Assert.IsFalse(seeked.IsCompleted, "Seeking, pausing or resuming past the threshold must not complete the item.");
        Assert.AreEqual(96_000L, seeked.PositionMs, "The position stays the exact resume point.");

        var completed = await service.UpdateAsync(
            "reader",
            target,
            new MediaProgressUpdate(96_000, 100_000, Completed: true));
        Assert.IsNotNull(completed);
        Assert.IsTrue(completed.IsCompleted);
        Assert.AreEqual(0L, completed.PositionMs);

        var rewatch = await service.UpdateAsync(
            "reader",
            target,
            new MediaProgressUpdate(45_000, 100_000, Completed: false));
        Assert.IsNotNull(rewatch);
        Assert.IsTrue(rewatch.IsCompleted, "A rewatch checkpoint must not silently unwatch completed media.");
        Assert.AreEqual(45_000L, rewatch.PositionMs);

        var rows = await db.Database.SqlQueryRaw<int>(
                """
                SELECT COUNT(*) AS "Value"
                FROM "MediaProgress"
                WHERE "ProfileId" = {0} AND "WorkId" = {1} AND "WorkEpisodeId" IS NULL
                """,
                "reader",
                movie.Id)
            .SingleAsync();
        Assert.AreEqual(1, rows);
    }

    [TestMethod]
    public async Task ConcurrentMovieUpdatesStaySingleAndIdempotent()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var movie = new Work { MediaType = WorkMediaType.Movie, CanonicalTitle = "Concurrent Movie" };
        db.Works.Add(movie);
        await db.SaveChangesAsync();

        var connectionString = db.Database.GetConnectionString()
            ?? throw new AssertFailedException("Expected a PostgreSQL test connection.");
        await using var secondDb = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(connectionString)
                .Options);

        var target = MediaProgressTarget.Movie(movie.Id);
        var first = new VideoProgressService(db);
        var second = new VideoProgressService(secondDb);

        await Task.WhenAll(
            first.UpdateAsync("reader", target, new MediaProgressUpdate(60_000, 120_000, Completed: false)),
            second.UpdateAsync("reader", target, new MediaProgressUpdate(60_000, 120_000, Completed: false)));

        var count = await db.Database.SqlQueryRaw<int>(
                """
                SELECT COUNT(*) AS "Value"
                FROM "MediaProgress"
                WHERE "ProfileId" = {0} AND "WorkId" = {1} AND "WorkEpisodeId" IS NULL
                """,
                "reader",
                movie.Id)
            .SingleAsync();
        Assert.AreEqual(1, count);

        var snapshot = await first.GetAsync("reader", target);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(60_000L, snapshot.PositionMs);
        Assert.IsFalse(snapshot.IsCompleted);
    }

    [TestMethod]
    public async Task ResumeDemand_AggregatesRecentProfilesWithoutExposingIdentities()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var root = new LibraryRoot { Name = "Video", Path = $"/tmp/jularr-demand-{Guid.NewGuid():N}" };
        var movie = new Work { MediaType = WorkMediaType.Movie, CanonicalTitle = "Movie" };
        var series = new Work { MediaType = WorkMediaType.Series, CanonicalTitle = "Series" };
        db.AddRange(root, movie, series);
        await db.SaveChangesAsync();

        var episode = new WorkEpisode { WorkId = series.Id, SeasonNumber = 1, EpisodeNumber = 1 };
        db.WorkEpisodes.Add(episode);
        await db.SaveChangesAsync();
        await AddPlayableAsync(db, root, movie, null, "movie.mkv");
        await AddPlayableAsync(db, root, series, episode, "episode.mkv");

        var progress = new VideoProgressService(db);
        var film = MediaProgressTarget.Movie(movie.Id);
        var tv = MediaProgressTarget.Episode(series.Id, episode.Id);
        foreach (var viewer in new[] { "first", "second", "old" })
        {
            await progress.UpdateAsync(viewer, film, new MediaProgressUpdate(60_000, 120_000, false));
        }

        await progress.UpdateAsync("short", film, new MediaProgressUpdate(15_000, 120_000, false));
        await progress.UpdateAsync("finished", film, new MediaProgressUpdate(120_000, 120_000, true));
        await progress.UpdateAsync("rewatch", film, new MediaProgressUpdate(120_000, 120_000, true));
        await progress.UpdateAsync("rewatch", film, new MediaProgressUpdate(50_000, 120_000, false));
        await progress.UpdateAsync("first", tv, new MediaProgressUpdate(60_000, 120_000, false));
        await progress.UpdateAsync("second", tv, new MediaProgressUpdate(70_000, 120_000, false));

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"MediaProgress\" SET \"UpdatedAt\" = {DateTime.UtcNow.AddDays(-40)} WHERE \"ProfileId\" = {"old"} AND \"WorkId\" = {movie.Id}");

        var counts = await progress.GetRecentResumeDemandAsync(
            [movie.Id, movie.Id, series.Id],
            DateTime.UtcNow.AddDays(-14));

        Assert.AreEqual(2, counts.Count);
        Assert.AreEqual(3L, counts.Single(item => item.WorkId == movie.Id).ResumeProfiles);
        var episodeCount = counts.Single(item => item.WorkId == series.Id);
        Assert.AreEqual(episode.Id, episodeCount.WorkEpisodeId);
        Assert.AreEqual(2L, episodeCount.ResumeProfiles);

        var evaluator = new PlaybackPreparationDemandService(progress);
        var cost = new PlaybackPreparationCost(
            Measured: true,
            PreparationMinutes: 10,
            SavedLiveEncodingMinutesPerView: 20,
            OutputBytes: 200_000_000);
        var capacity = new PlaybackPreparationCapacity(
            AdminEnabled: true,
            InIdleWindow: true,
            InteractiveLoad: false,
            SourceAvailableWithoutWake: true,
            AlreadyHasCompatibleRendition: false,
            NeedsVideoConversion: true,
            CacheBytesAvailable: 2_000_000_000,
            DiskBytesAboveReserve: 2_000_000_000);
        var assessments = await evaluator.AssessAsync(
            [
                new PlaybackPreparationCandidate(movie.Id, null, cost, capacity),
                new PlaybackPreparationCandidate(series.Id, episode.Id, cost, capacity),
                new PlaybackPreparationCandidate(movie.Id + 999, null, cost, capacity),
            ],
            DateTime.UtcNow.AddDays(-14));
        Assert.AreEqual(3, assessments.Count);
        Assert.AreEqual(
            PlaybackPreparationVerdict.Eligible,
            assessments.Single(value => value.WorkId == movie.Id).Assessment.Verdict);
        Assert.AreEqual(
            PlaybackPreparationVerdict.Eligible,
            assessments.Single(value => value.WorkId == series.Id).Assessment.Verdict);
        Assert.AreEqual(
            PlaybackPreparationVerdict.DemandTooWeak,
            assessments.Single(value => value.WorkId == movie.Id + 999).Assessment.Verdict);

        var disabled = await evaluator.AssessAsync(
            [new PlaybackPreparationCandidate(movie.Id, null, cost, capacity with { AdminEnabled = false })],
            DateTime.UtcNow.AddDays(-14));
        Assert.AreEqual(PlaybackPreparationVerdict.Disabled, disabled.Single().Assessment.Verdict);
    }

    [TestMethod]
    public async Task ResumeDemand_BoundsWorkBatchAndRequiresUtcCutoff()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var progress = new VideoProgressService(db);

        Assert.AreEqual(0, (await progress.GetRecentResumeDemandAsync([], DateTime.UtcNow)).Count);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => progress.GetRecentResumeDemandAsync(Enumerable.Range(1, 101).Select(i => (long)i).ToArray(), DateTime.UtcNow));
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => progress.GetRecentResumeDemandAsync([1L], DateTime.Now));
    }

    [TestMethod]
    public async Task ContinueWatchingProjectsMovieAndTvFromOneOwner()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var root = new LibraryRoot { Name = "Video", Path = $"/tmp/jularr-progress-{Guid.NewGuid():N}" };
        var movie = new Work { MediaType = WorkMediaType.Movie, CanonicalTitle = "Movie" };
        db.Works.Add(movie);
        await db.SaveChangesAsync();
        var series = new Work { MediaType = WorkMediaType.Series, CanonicalTitle = "Series" };
        db.Works.Add(series);
        await db.SaveChangesAsync();
        var episode1 = new WorkEpisode
        {
            WorkId = series.Id,
            SeasonNumber = 1,
            EpisodeNumber = 1,
            Title = "Episode 1"
        };
        var episode2 = new WorkEpisode
        {
            WorkId = series.Id,
            SeasonNumber = 1,
            EpisodeNumber = 2,
            Title = "Episode 2"
        };
        db.AddRange(root, episode1, episode2);
        await db.SaveChangesAsync();

        await AddPlayableAsync(db, root, movie, null, "movie.mkv");
        await AddPlayableAsync(db, root, series, episode1, "s01e01.mkv");
        await AddPlayableAsync(db, root, series, episode2, "s01e02.mkv");

        var service = new VideoProgressService(db);
        await service.UpdateAsync(
            "reader",
            MediaProgressTarget.Movie(movie.Id),
            new MediaProgressUpdate(60_000, 120_000, Completed: false));
        await service.UpdateAsync(
            "reader",
            MediaProgressTarget.Episode(series.Id, episode1.Id),
            new MediaProgressUpdate(100_000, 100_000, Completed: true));

        var items = await service.GetContinueWatchingAsync("reader");

        var movieItem = items.Single(x => x.WorkId == movie.Id);
        Assert.AreEqual(VideoContinueWatchingKind.Resume, movieItem.Kind);
        Assert.AreEqual(60_000L, movieItem.ResumePositionMs);

        var tvItem = items.Single(x => x.WorkId == series.Id);
        Assert.AreEqual(VideoContinueWatchingKind.UpNext, tvItem.Kind);
        Assert.AreEqual(episode2.Id, tvItem.WorkEpisodeId);
        Assert.AreEqual(WorkMediaType.Series, tvItem.MediaType);

        var flow = await service.GetEpisodeFlowAsync(
            MediaProgressTarget.Episode(series.Id, episode1.Id));
        Assert.IsNotNull(flow);
        Assert.IsNull(flow.PreviousWorkEpisodeId);
        Assert.AreEqual(episode2.Id, flow.NextWorkEpisodeId);

        Assert.IsNull(
            await service.GetEpisodeFlowAsync(MediaProgressTarget.Movie(movie.Id)),
            "Movies never synthesize an episode-style Next target.");
    }

    [TestMethod]
    public async Task ActiveSessionDoesNotCreateConsumptionProgress()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var root = new LibraryRoot { Name = "Video", Path = $"/tmp/jularr-session-{Guid.NewGuid():N}" };
        var movie = new Work { MediaType = WorkMediaType.Movie, CanonicalTitle = "Movie" };
        db.Works.Add(movie);
        await db.SaveChangesAsync();
        db.AddRange(root);
        await db.SaveChangesAsync();
        var file = await AddPlayableAsync(db, root, movie, null, "movie.mkv");

        var sessions = new ActiveSessionService(db, TimeProvider.System);
        var sessionId = Guid.NewGuid();
        var active = await sessions.OpenAsync(
            sessionId,
            "reader",
            file.Id,
            "direct_play",
            "web");

        Assert.IsNotNull(active);
        Assert.IsTrue(active.IsActive);
        Assert.AreEqual(movie.Id, active.WorkId);

        var progress = await new VideoProgressService(db).GetAsync(
            "reader",
            MediaProgressTarget.Movie(movie.Id));
        Assert.IsNotNull(progress);
        Assert.IsFalse(progress.IsCompleted);
        Assert.AreEqual(0L, progress.PositionMs);
        Assert.IsNull(progress.UpdatedAt, "Opening playback must not create consumption progress.");

        Assert.IsTrue(await sessions.EndAsync(sessionId, "reader"));
        Assert.IsFalse((await sessions.GetAsync(sessionId, "reader"))!.IsActive);
    }

    [TestMethod]
    public async Task LegacyAnimeProgressAndHistoryBackfillToCanonicalTargets()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var anime = new Anime { Key = "legacy", Title = "Legacy Anime" };
        var episode = new Episode
        {
            AnimeId = anime.Id,
            SeasonNumber = 1,
            Number = 3,
            Title = "Episode 3"
        };
        var work = new Work { MediaType = WorkMediaType.Anime, CanonicalTitle = anime.Title };
        db.Works.Add(work);
        await db.SaveChangesAsync();
        var workEpisode = new WorkEpisode
        {
            WorkId = work.Id,
            SeasonNumber = 1,
            EpisodeNumber = 3,
            Title = episode.Title
        };
        db.AddRange(
            anime,
            episode,
            workEpisode,
            new WorkSourceLink
            {
                WorkId = work.Id,
                SourceKind = WorkSourceKind.Anime,
                SourceId = anime.Id
            },
            new WorkSourceLink
            {
                WorkId = work.Id,
                SourceKind = WorkSourceKind.Episode,
                SourceId = episode.Id
            },
            new EpisodeProgress
            {
                ProfileId = "reader",
                EpisodeId = episode.Id,
                PositionMs = 42_000,
                DurationMs = 100_000,
                IsCompleted = false,
                UpdatedAt = DateTime.UtcNow.AddMinutes(-1)
            },
            new EpisodePlaybackHistoryEntry
            {
                ProfileId = "reader",
                EpisodeId = episode.Id,
                StartedAt = DateTime.UtcNow.AddMinutes(-2),
                LastPlayedAt = DateTime.UtcNow.AddMinutes(-1),
                PositionMs = 42_000,
                DurationMs = 100_000,
                ReachedEnd = false
            });
        await db.SaveChangesAsync();

        var videoProgress = new VideoProgressService(db);
        var resolver = new CanonicalVideoTargetResolver(
            db,
            new LegacyWorkBridge(db, new WorkService(db), new WorkStructureService(db)));
        var backfill = new CanonicalVideoProgressBackfillService(db, resolver, videoProgress);

        Assert.AreEqual(1, await backfill.BackfillLegacyAnimeAsync());

        var canonical = await videoProgress.GetAsync(
            "reader",
            MediaProgressTarget.Episode(work.Id, workEpisode.Id));
        Assert.IsNotNull(canonical);
        Assert.AreEqual(42_000L, canonical.PositionMs);
        Assert.IsFalse(canonical.IsCompleted);

        var history = await videoProgress.GetHistoryAsync("reader");
        Assert.AreEqual(1, history.Count);
        Assert.AreEqual(workEpisode.Id, history[0].WorkEpisodeId);

        Assert.AreEqual(0, await db.EpisodeProgress.CountAsync(), "Migrated progress rows are consumed.");
        Assert.AreEqual(0, await db.EpisodePlaybackHistory.CountAsync(), "Migrated history rows are consumed.");
        Assert.AreEqual(0, await backfill.BackfillLegacyAnimeAsync(), "The backfill is one-time: a second run imports nothing.");
        await videoProgress.ClearHistoryAsync("reader");
        await backfill.BackfillLegacyAnimeAsync();
        Assert.AreEqual(0, (await videoProgress.GetHistoryAsync("reader")).Count, "Cleared history is not resurrected by a later backfill run.");
    }

    [TestMethod]
    public async Task CompletedThroughStopsAtTheFirstGapAndIgnoresSpecials()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var series = new Work { MediaType = WorkMediaType.Anime, CanonicalTitle = "Gap Anime" };
        db.Works.Add(series);
        await db.SaveChangesAsync();
        var special = new WorkEpisode { WorkId = series.Id, SeasonNumber = 0, EpisodeNumber = 1 };
        var episodes = Enumerable.Range(1, 4)
            .Select(number => new WorkEpisode { WorkId = series.Id, SeasonNumber = 1, EpisodeNumber = number })
            .ToArray();
        db.AddRange(special);
        db.AddRange(episodes);
        await db.SaveChangesAsync();

        var service = new VideoProgressService(db);
        async Task CompleteAsync(string profile, WorkEpisode episode) =>
            await service.SetCompletedAsync(profile, MediaProgressTarget.Episode(series.Id, episode.Id), true);

        await CompleteAsync("reader", episodes[0]);
        await CompleteAsync("reader", episodes[2]);
        await CompleteAsync("reader", special);

        var gap = Assert.ContainsSingle(await service.GetCompletedThroughAsync("reader"));
        Assert.AreEqual(1, gap.CompletedThrough?.EpisodeNumber, "E1 and E3 completed with E2 open must give 1, never 3.");

        await service.UpdateAsync("reader", MediaProgressTarget.Episode(series.Id, episodes[1].Id), new MediaProgressUpdate(600_000, 1_000_000, Completed: false));
        Assert.AreEqual(1, (await service.GetCompletedThroughAsync("reader", series.Id)).Single().CompletedThrough?.EpisodeNumber, "An in-progress episode never advances CompletedThrough.");

        await CompleteAsync("reader", episodes[1]);
        var through = (await service.GetCompletedThroughAsync("reader", series.Id)).Single().CompletedThrough;
        Assert.AreEqual(3, through?.EpisodeNumber, "Closing the gap extends the contiguous prefix up to the next open episode.");
        Assert.AreEqual(episodes[2].Id, through?.WorkEpisodeId);

        await service.SetCompletedAsync("reader", MediaProgressTarget.Episode(series.Id, episodes[0].Id), false);
        Assert.IsNull((await service.GetCompletedThroughAsync("reader", series.Id)).Single().CompletedThrough, "Without E1 nothing is contiguously completed.");

        Assert.AreEqual(0, (await service.GetCompletedThroughAsync("other")).Count, "Profiles are isolated.");
    }

    [TestMethod]
    public async Task UntouchedEpisodeBetweenTwoCompletedOnesStopsCompletedThrough()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var series = new Work { MediaType = WorkMediaType.Anime, CanonicalTitle = "Open Episode Anime" };
        db.Works.Add(series);
        await db.SaveChangesAsync();
        var episodes = Enumerable.Range(1, 3)
            .Select(number => new WorkEpisode { WorkId = series.Id, SeasonNumber = 1, EpisodeNumber = number })
            .ToArray();
        db.AddRange(episodes);
        await db.SaveChangesAsync();

        var service = new VideoProgressService(db);
        await service.SetCompletedAsync("reader", MediaProgressTarget.Episode(series.Id, episodes[0].Id), true);
        await service.SetCompletedAsync("reader", MediaProgressTarget.Episode(series.Id, episodes[2].Id), true);

        var through = (await service.GetCompletedThroughAsync("reader", series.Id)).Single().CompletedThrough;
        Assert.AreEqual(1, through?.EpisodeNumber, "E1 and E3 completed with an untouched E2 row present gives 1.");
    }

    [TestMethod]
    public async Task RewatchKeepsCompletedStateAndOffersResumeInContinueWatching()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var root = new LibraryRoot { Name = "Video", Path = $"/tmp/jularr-rewatch-{Guid.NewGuid():N}" };
        var series = new Work { MediaType = WorkMediaType.Anime, CanonicalTitle = "Rewatch Anime" };
        db.Works.Add(series);
        await db.SaveChangesAsync();
        var first = new WorkEpisode { WorkId = series.Id, SeasonNumber = 1, EpisodeNumber = 1 };
        var second = new WorkEpisode { WorkId = series.Id, SeasonNumber = 1, EpisodeNumber = 2 };
        db.AddRange(root, first, second);
        await db.SaveChangesAsync();
        await AddPlayableAsync(db, root, series, first, "s01e01.mkv");
        await AddPlayableAsync(db, root, series, second, "s01e02.mkv");

        var service = new VideoProgressService(db);
        var target = MediaProgressTarget.Episode(series.Id, first.Id);
        await service.UpdateAsync("reader", target, new MediaProgressUpdate(1_000_000, 1_000_000, Completed: true));

        var upNext = Assert.ContainsSingle(await service.GetContinueWatchingAsync("reader"));
        Assert.AreEqual(VideoContinueWatchingKind.UpNext, upNext.Kind);
        Assert.AreEqual(second.Id, upNext.WorkEpisodeId);

        await service.UpdateAsync("reader", target, new MediaProgressUpdate(420_000, 1_000_000, Completed: false));

        var rewatch = Assert.ContainsSingle(await service.GetContinueWatchingAsync("reader"));
        Assert.AreEqual(VideoContinueWatchingKind.Resume, rewatch.Kind, "A rewatch resume position is offered even though the episode is completed.");
        Assert.AreEqual(first.Id, rewatch.WorkEpisodeId);
        Assert.AreEqual(420_000L, rewatch.ResumePositionMs);

        var snapshot = await service.GetAsync("reader", target);
        Assert.IsTrue(snapshot!.IsCompleted, "The rewatch must not un-complete the episode.");
        Assert.AreEqual(420_000L, snapshot.ResumePositionMs);
        Assert.AreEqual(first.Id, (await service.GetCompletedThroughAsync("reader", series.Id)).Single().CompletedThrough?.WorkEpisodeId);

        await service.UpdateAsync("reader", target, new MediaProgressUpdate(1_000_000, 1_000_000, Completed: true));
        Assert.AreEqual(VideoContinueWatchingKind.UpNext, Assert.ContainsSingle(await service.GetContinueWatchingAsync("reader")).Kind, "Finishing the rewatch clears the resume position.");
    }

    [TestMethod]
    public async Task ContinueWatchingNarrowsToOneMediaTypeFromTheSharedOwner()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var root = new LibraryRoot { Name = "Video", Path = $"/tmp/jularr-types-{Guid.NewGuid():N}" };
        var movie = new Work { MediaType = WorkMediaType.Movie, CanonicalTitle = "Movie" };
        db.Works.Add(movie);
        await db.SaveChangesAsync();
        var anime = new Work { MediaType = WorkMediaType.Anime, CanonicalTitle = "Anime" };
        db.Works.Add(anime);
        await db.SaveChangesAsync();
        var animeEpisode = new WorkEpisode { WorkId = anime.Id, SeasonNumber = 1, EpisodeNumber = 1 };
        db.AddRange(root, animeEpisode);
        await db.SaveChangesAsync();
        await AddPlayableAsync(db, root, movie, null, "movie.mkv");
        await AddPlayableAsync(db, root, anime, animeEpisode, "anime.mkv");

        var service = new VideoProgressService(db);
        await service.UpdateAsync("reader", MediaProgressTarget.Movie(movie.Id), new MediaProgressUpdate(60_000, 120_000, Completed: false));
        await service.UpdateAsync("reader", MediaProgressTarget.Episode(anime.Id, animeEpisode.Id), new MediaProgressUpdate(60_000, 120_000, Completed: false));

        Assert.AreEqual(2, (await service.GetContinueWatchingAsync("reader")).Count);
        Assert.AreEqual(movie.Id, Assert.ContainsSingle(await service.GetContinueWatchingAsync("reader", mediaTypes: [WorkMediaType.Movie])).WorkId);
        Assert.AreEqual(anime.Id, Assert.ContainsSingle(await service.GetContinueWatchingAsync("reader", mediaTypes: [WorkMediaType.Anime])).WorkId);
    }

    private static async Task<StoredFile> AddPlayableAsync(
        AppDbContext db,
        LibraryRoot root,
        Work work,
        WorkEpisode? episode,
        string fileName)
    {
        var version = new WorkVersion
        {
            WorkId = work.Id,
            VersionKey = $"test:{Guid.NewGuid():N}",
            UnitKey = episode is null ? null : $"S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2}",
            Source = "test"
        };
        var asset = new MediaAsset
        {
            WorkId = work.Id,
            WorkEpisodeId = episode?.Id,
            WorkVersionId = version.Id,
            Kind = MediaAssetKind.Video
        };
        var file = new StoredFile
        {
            MediaAssetId = asset.Id,
            LibraryRootId = root.Id,
            Path = Path.Combine(root.Path, fileName),
            SizeBytes = 1_024,
            LastWriteTimeUtc = DateTime.UtcNow
        };
        db.AddRange(version, asset, file);
        await db.SaveChangesAsync();
        return file;
    }
}
