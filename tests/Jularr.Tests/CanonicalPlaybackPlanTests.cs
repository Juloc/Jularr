using System.Net;
using Jularr.Web.Features.ClientApi;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Playback;
using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Features.Progress;

namespace Jularr.Tests;

[TestClass]
public sealed class CanonicalPlaybackPlanTests
{
    private const string ChromeAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

    [TestMethod]
    public async Task MovieAnimeAndTvUseOneCanonicalPlanner()
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var storage = new CanonicalMediaStorageService(fixture.Db);
        var progress = new VideoProgressService(fixture.Db);
        var sessions = new PlaybackStreamSessionStore(TimeProvider.System);
        var planner = new PlaybackPlanService(
            fixture.Db,
            fixture.Inventory,
            sessions,
            new PlaybackServerCapabilityProvider(new PlaybackTranscodeSlots()),
            canonicalStorage: storage,
            videoProgress: progress);

        var movie = new Work
        {
            MediaType = WorkMediaType.Movie,
            CanonicalTitle = "Canonical Movie"
        };
        var anime = new Work
        {
            MediaType = WorkMediaType.Anime,
            CanonicalTitle = "Canonical Anime"
        };
        var animeEpisode = new WorkEpisode
        {
            WorkId = anime.Id,
            SeasonNumber = 1,
            EpisodeNumber = 1,
            Title = "Anime 1"
        };
        var series = new Work
        {
            MediaType = WorkMediaType.Series,
            CanonicalTitle = "Canonical Series"
        };
        var tvEpisode = new WorkEpisode
        {
            WorkId = series.Id,
            SeasonNumber = 1,
            EpisodeNumber = 2,
            Title = "TV 2"
        };
        fixture.Db.AddRange(movie, anime, animeEpisode, series, tvEpisode);
        await fixture.Db.SaveChangesAsync();

        var movieFile = await AttachAsync(fixture, storage, movie.Id, null, "movie.mp4", MediaProbeFixtures.H264Stereo);
        var animeFile = await AttachAsync(fixture, storage, anime.Id, animeEpisode.Id, "anime.mp4", MediaProbeFixtures.H264Stereo);
        var tvFile = await AttachAsync(fixture, storage, series.Id, tvEpisode.Id, "tv.mkv", MediaProbeFixtures.HevcTenBitHdrMultiAudio);

        await progress.UpdateAsync(
            "reader",
            MediaProgressTarget.Movie(movie.Id),
            new MediaProgressUpdate(61_000, 120_000, Completed: false));

        var input = new PlaybackPlanInput(null, ClientKinds.Web, ChromeAgent, IPAddress.Loopback);

        var moviePlan = await planner.PlanAsync(
            PlaybackVideoTarget.Movie(movie.Id),
            "reader",
            input,
            CancellationToken.None);
        var animePlan = await planner.PlanAsync(
            PlaybackVideoTarget.Episode(anime.Id, animeEpisode.Id),
            "reader",
            input,
            CancellationToken.None);
        var tvPlan = await planner.PlanAsync(
            PlaybackVideoTarget.Episode(series.Id, tvEpisode.Id),
            "reader",
            input,
            CancellationToken.None);

        Assert.IsNotNull(moviePlan);
        Assert.IsNotNull(animePlan);
        Assert.IsNotNull(tvPlan);

        Assert.AreEqual(movie.Id, moviePlan.Target.WorkId);
        Assert.IsNull(moviePlan.Target.WorkEpisodeId);
        Assert.AreEqual(61_000L, moviePlan.ResumePositionMs);
        Assert.AreEqual(movieFile.StoredFileId, moviePlan.MediaFileId);
        Assert.AreEqual(moviePlan.Target, moviePlan.Session!.Target);

        Assert.AreEqual(animeEpisode.Id, animePlan.Target.WorkEpisodeId);
        Assert.AreEqual(animeFile.StoredFileId, animePlan.MediaFileId);
        Assert.AreEqual(animePlan.Target, animePlan.Session!.Target);

        Assert.AreEqual(tvEpisode.Id, tvPlan.Target.WorkEpisodeId);
        Assert.AreEqual(tvFile.StoredFileId, tvPlan.MediaFileId);
        Assert.AreEqual(tvPlan.Target, tvPlan.Session!.Target);

        Assert.AreEqual(
            PlaybackDeliveryMode.DirectPlay,
            moviePlan.Plan.Mode,
            "An H.264/AAC MP4 uses the shared direct-play path.");
        Assert.AreEqual(
            PlaybackDeliveryMode.DirectPlay,
            animePlan.Plan.Mode,
            "Anime uses the same decision engine as every other canonical target.");
        Assert.AreEqual(
            PlaybackDeliveryMode.Transcode,
            tvPlan.Plan.Mode,
            "The same planner sends an incompatible HEVC/HDR MKV through the existing transcode fallback.");
    }

    [TestMethod]
    public async Task LegacyAnimePlanRouteResolvesCanonicalTarget()
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var media = await fixture.AddMediaAsync("legacy-anime.mp4", new byte[4096]);
        fixture.Runner.Returns(media.Path, MediaProbeFixtures.H264Stereo);

        var work = new Work
        {
            MediaType = WorkMediaType.Anime,
            CanonicalTitle = "Legacy Anime"
        };
        var workEpisode = new WorkEpisode
        {
            WorkId = work.Id,
            SeasonNumber = 1,
            EpisodeNumber = 1,
            Title = "Episode 1"
        };
        fixture.Db.AddRange(work, workEpisode);
        await fixture.Db.SaveChangesAsync();

        var storage = new CanonicalMediaStorageService(fixture.Db);
        await storage.AttachVideoAsync(
            work.Id,
            workEpisode.Id,
            media.Path,
            fixture.Root.Path,
            CancellationToken.None);

        var sessions = new PlaybackStreamSessionStore(TimeProvider.System);
        var planner = new PlaybackPlanService(
            fixture.Db,
            fixture.Inventory,
            sessions,
            new PlaybackServerCapabilityProvider(new PlaybackTranscodeSlots()),
            canonicalStorage: storage);

        var outcome = await planner.PlanAsync(
            media.EpisodeId!.Value,
            "reader",
            new PlaybackPlanInput(null, ClientKinds.Web, ChromeAgent, IPAddress.Loopback),
            CancellationToken.None);

        Assert.IsNotNull(outcome);
        Assert.AreEqual(work.Id, outcome.Target.WorkId);
        Assert.AreEqual(workEpisode.Id, outcome.Target.WorkEpisodeId);
        Assert.AreEqual(media.EpisodeId, outcome.Session!.LegacyEpisodeId);
        Assert.AreEqual(outcome.Target, outcome.Session.Target);
    }

    [TestMethod]
    public void ClientContractExposesCanonicalVideoPlanRoute()
    {
        Assert.AreEqual(
            "/api/client/v1/video/playback-plan",
            ClientApiRoutes.VideoPlaybackPlan);
        Assert.AreEqual(
            "/api/client/v1/video/progress",
            ClientApiRoutes.VideoProgress);
    }

    private static async Task<CanonicalPlayableFile> AttachAsync(
        MediaInventoryFixture fixture,
        CanonicalMediaStorageService storage,
        Guid workId,
        Guid? workEpisodeId,
        string fileName,
        string probe)
    {
        var path = Path.Combine(fixture.Root.Path, fileName);
        await File.WriteAllBytesAsync(path, new byte[4096]);
        var playable = await storage.AttachVideoAsync(
            workId,
            workEpisodeId,
            path,
            fixture.Root.Path,
            CancellationToken.None);
        fixture.Runner.Returns(playable.Path, probe);
        return playable;
    }
}
