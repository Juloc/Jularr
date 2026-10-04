using Jularr.Web.Data;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
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

        var completed = await service.UpdateAsync(
            "reader",
            target,
            new MediaProgressUpdate(95_000, 100_000, Completed: false));
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
    public async Task ContinueWatchingProjectsMovieAndTvFromOneOwner()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var root = new LibraryRoot { Name = "Video", Path = $"/tmp/jularr-progress-{Guid.NewGuid():N}" };
        var movie = new Work { MediaType = WorkMediaType.Movie, CanonicalTitle = "Movie" };
        var series = new Work { MediaType = WorkMediaType.Series, CanonicalTitle = "Series" };
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
        db.AddRange(root, movie, series, episode1, episode2);
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
        db.AddRange(root, movie);
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
            work,
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
