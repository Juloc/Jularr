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
            PlaybackServerTestKit.Create().Capabilities,
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
        fixture.Db.AddRange(movie, anime, series);
        await fixture.Db.SaveChangesAsync();
        animeEpisode.WorkId = anime.Id;
        tvEpisode.WorkId = series.Id;
        fixture.Db.AddRange(animeEpisode, tvEpisode);
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
    public async Task OnlyProvenanceLinkedRenditionsCanReplaceTheOriginal()
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var storage = new CanonicalMediaStorageService(fixture.Db);
        var movie = new Work { MediaType = WorkMediaType.Movie, CanonicalTitle = "Safe Video Renditions" };
        fixture.Db.Works.Add(movie);
        await fixture.Db.SaveChangesAsync();

        const string originalCodec = "\"codec_name\": \"h264\"";
        const string originalContainer = "\"format_name\": \"mov,mp4,m4a,3gp,3g2,mj2\"";
        var hevcStereo = MediaProbeFixtures.H264Stereo
            .Replace(originalCodec, "\"codec_name\": \"hevc\"", StringComparison.Ordinal)
            .Replace("\"profile\": \"High\"", "\"profile\": \"Main\"", StringComparison.Ordinal)
            .Replace(originalContainer, "\"format_name\": \"matroska,webm\"", StringComparison.Ordinal);
        var original = await AttachAsync(fixture, storage, movie.Id, null, "a-hevc.mkv", hevcStereo);
        var candidate = await AttachAsync(fixture, storage, movie.Id, null, "b-h264.mp4", MediaProbeFixtures.H264Stereo);
        await fixture.Inventory.EnsureAnalyzedAsync(original.StoredFileId, CancellationToken.None);
        await fixture.Inventory.EnsureAnalyzedAsync(candidate.StoredFileId, CancellationToken.None);

        var planner = new PlaybackPlanService(
            fixture.Db,
            fixture.Inventory,
            new PlaybackStreamSessionStore(TimeProvider.System),
            PlaybackServerTestKit.Create().Capabilities,
            canonicalStorage: storage);
        var input = new PlaybackPlanInput(
            null, ClientKinds.Web, ChromeAgent, IPAddress.Parse("203.0.113.9"),
            Network: new PlaybackNetworkReport(ThroughputKbps: 24_000));

        var unproven = (await planner.PlanAsync(PlaybackVideoTarget.Movie(movie.Id), "reader", input, CancellationToken.None))!;
        Assert.AreEqual(original.StoredFileId, unproven.MediaFileId,
            "A separate imported edition is not evidence of the same cut, default language or timeline.");

        var sourceAnalysis = (await fixture.Inventory.GetManyAsync(
            [original.StoredFileId], CancellationToken.None))[original.StoredFileId];
        Assert.IsNotNull(sourceAnalysis.SourceFingerprint);
        var externallySerialized = System.Text.Json.JsonSerializer.Serialize(sourceAnalysis);
        Assert.IsFalse(externallySerialized.Contains("SourceFingerprint", StringComparison.Ordinal));
        Assert.IsFalse(externallySerialized.Contains("SourceSizeBytes", StringComparison.Ordinal));
        Assert.IsFalse(externallySerialized.Contains("SourceLastWriteTimeUtc", StringComparison.Ordinal));
        var version = await fixture.Db.WorkVersions.FindAsync(candidate.WorkVersionId);
        Assert.IsNotNull(version);
        version.Source = "jularr-prepared:v1";
        version.Notes = System.Text.Json.JsonSerializer.Serialize(new
        {
            sourceStoredFileId = original.StoredFileId,
            sourceFingerprint = sourceAnalysis.SourceFingerprint,
            recipeVersion = 1,
            verifiedOutput = true
        });
        await fixture.Db.SaveChangesAsync();

        var installed = await storage.ListVideoFilesAsync(movie.Id, CancellationToken.None);
        Assert.AreEqual(1, installed.Count, "A disposable prepared cache entry is not an installed acquisition version.");
        Assert.AreEqual(original.StoredFileId, installed[0].StoredFileId);
        Assert.AreEqual(2, (await storage.ResolveVideoCandidatesAsync(movie.Id, null, CancellationToken.None)).Count,
            "Playback can still consider the verified derivative without exposing it as an installed release.");

        var verified = (await planner.PlanAsync(PlaybackVideoTarget.Movie(movie.Id), "reader", input, CancellationToken.None))!;
        Assert.AreEqual(candidate.StoredFileId, verified.MediaFileId);
        Assert.AreEqual(PlaybackDeliveryMode.DirectPlay, verified.Plan.Mode,
            "A verified identical-timeline rendition avoids video re-encoding.");
        Assert.AreEqual(candidate.StoredFileId, verified.Session!.MediaFileId);

        // An automatically prepared file may sort before its source by filename.
        // It can never become the canonical original just because the filename changed.
        var preparedStored = await fixture.Db.StoredFiles.FindAsync(candidate.StoredFileId);
        Assert.IsNotNull(preparedStored);
        var earlierPath = Path.Combine(Path.GetDirectoryName(preparedStored.Path)!, "0-prepared-video.mp4");
        File.Move(preparedStored.Path, earlierPath);
        preparedStored.Path = earlierPath;
        await fixture.Db.SaveChangesAsync();
        var candidatesByOrigin = await storage.ResolveVideoCandidatesAsync(movie.Id, null, CancellationToken.None);
        Assert.AreEqual(original.StoredFileId, candidatesByOrigin[0].StoredFileId);
        var sortedPlan = (await planner.PlanAsync(
            PlaybackVideoTarget.Movie(movie.Id), "reader", input, CancellationToken.None))!;
        Assert.AreEqual(candidate.StoredFileId, sortedPlan.MediaFileId,
            "The verified rendition remains selectable even when its path sorts ahead of the source.");

        var local = (await planner.PlanAsync(
            PlaybackVideoTarget.Movie(movie.Id), "reader",
            input with { RemoteAddress = IPAddress.Loopback, Network = null }, CancellationToken.None))!;
        Assert.AreEqual(original.StoredFileId, local.MediaFileId,
            "LAN Auto means Original, so it must not prefer a smaller pre-encoded version.");

        var selectedAudio = (await planner.PlanAsync(
            PlaybackVideoTarget.Movie(movie.Id), "reader",
            input with { AudioStreamIndex = 1 }, CancellationToken.None))!;
        Assert.AreEqual(original.StoredFileId, selectedAudio.MediaFileId,
            "Track indices belong to a particular file, even when a prepared version has the same language.");

        var requestedOriginal = (await planner.PlanAsync(
            PlaybackVideoTarget.Movie(movie.Id), "reader",
            input with
            {
                Quality = PlaybackQualityPreset.Original,
                ReplacesSessionId = verified.Session.Id
            }, CancellationToken.None))!;
        Assert.AreEqual(original.StoredFileId, requestedOriginal.MediaFileId,
            "Explicit Original never silently switches to a lossy prepared file.");

        var continued = (await planner.PlanAsync(
            PlaybackVideoTarget.Movie(movie.Id), "reader",
            input with { ReplacesSessionId = unproven.Session!.Id }, CancellationToken.None))!;
        Assert.AreEqual(original.StoredFileId, continued.MediaFileId,
            "An in-progress seek/re-plan retains its current physical file and timeline.");

        version.Notes = System.Text.Json.JsonSerializer.Serialize(new
        {
            sourceStoredFileId = original.StoredFileId,
            sourceFingerprint = new string('0', 64),
            recipeVersion = 1,
            verifiedOutput = true
        });
        await fixture.Db.SaveChangesAsync();
        var stale = (await planner.PlanAsync(PlaybackVideoTarget.Movie(movie.Id), "reader", input, CancellationToken.None))!;
        Assert.AreEqual(original.StoredFileId, stale.MediaFileId,
            "A changed source fingerprint invalidates an otherwise compatible prepared file.");
        var staleSeek = (await planner.PlanAsync(
            PlaybackVideoTarget.Movie(movie.Id), "reader",
            input with { ReplacesSessionId = verified.Session.Id },
            CancellationToken.None))!;
        Assert.AreEqual(original.StoredFileId, staleSeek.MediaFileId,
            "A now revoked derivative cannot survive simply because an older session used it.");

        version.Notes = System.Text.Json.JsonSerializer.Serialize(new
        {
            sourceStoredFileId = original.StoredFileId,
            sourceFingerprint = sourceAnalysis.SourceFingerprint,
            recipeVersion = 2,
            verifiedOutput = true
        });
        await fixture.Db.SaveChangesAsync();
        var unknownRecipe = (await planner.PlanAsync(
            PlaybackVideoTarget.Movie(movie.Id), "reader", input, CancellationToken.None))!;
        Assert.AreEqual(original.StoredFileId, unknownRecipe.MediaFileId,
            "Future preparation recipes require explicit compatibility review before selecting their output.");

        await storage.RemoveFilesAsync([original.StoredFileId], CancellationToken.None);
        var orphaned = await planner.PlanAsync(
            PlaybackVideoTarget.Movie(movie.Id), "reader", input, CancellationToken.None);
        Assert.IsNull(orphaned,
            "A surviving prepared rendition is not allowed to masquerade as the missing source file.");
    }

    [DataTestMethod]
    [DataRow("shifted-timeline")]
    [DataRow("different-audio-language")]
    [DataRow("different-soundtrack-title")]
    [DataRow("distorted-picture")]
    public async Task PreparedRenditionsCannotChangeTimelineOrTrackIdentity(string mismatch)
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var storage = new CanonicalMediaStorageService(fixture.Db);
        var movie = new Work { MediaType = WorkMediaType.Movie, CanonicalTitle = "Different Edition" };
        fixture.Db.Works.Add(movie);
        await fixture.Db.SaveChangesAsync();

        var originalJson = MediaProbeFixtures.H264Stereo
            .Replace("\"codec_name\": \"h264\"", "\"codec_name\": \"hevc\"", StringComparison.Ordinal)
            .Replace("\"format_name\": \"mov,mp4,m4a,3gp,3g2,mj2\"", "\"format_name\": \"matroska,webm\"", StringComparison.Ordinal);
        var derivativeJson = mismatch switch
        {
            "shifted-timeline" => MediaProbeFixtures.H264Stereo.Replace(
                "\"duration\": \"1440.0\"", "\"duration\": \"1420.0\"", StringComparison.Ordinal),
            "different-audio-language" => MediaProbeFixtures.H264Stereo.Replace(
                "\"language\": \"jpn\"", "\"language\": \"eng\"", StringComparison.Ordinal),
            "different-soundtrack-title" => MediaProbeFixtures.H264Stereo.Replace(
                "\"language\": \"jpn\"", "\"language\": \"jpn\", \"title\": \"Commentary\"", StringComparison.Ordinal),
            "distorted-picture" => MediaProbeFixtures.H264Stereo.Replace(
                "\"width\": 1920", "\"width\": 1280", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };

        var original = await AttachAsync(fixture, storage, movie.Id, null, "a-hevc.mkv", originalJson);
        var other = await AttachAsync(fixture, storage, movie.Id, null, "b-h264.mp4", derivativeJson);
        var originalAnalysis = await fixture.Inventory.EnsureAnalyzedAsync(original.StoredFileId, CancellationToken.None);
        await fixture.Inventory.EnsureAnalyzedAsync(other.StoredFileId, CancellationToken.None);
        Assert.IsNotNull(originalAnalysis!.SourceFingerprint);

        var version = await fixture.Db.WorkVersions.FindAsync(other.WorkVersionId);
        Assert.IsNotNull(version);
        version.Source = "jularr-prepared:v1";
        version.Notes = System.Text.Json.JsonSerializer.Serialize(new
        {
            sourceStoredFileId = original.StoredFileId,
            sourceFingerprint = originalAnalysis.SourceFingerprint,
            recipeVersion = 1,
            verifiedOutput = true
        });
        await fixture.Db.SaveChangesAsync();

        var planner = new PlaybackPlanService(
            fixture.Db, fixture.Inventory,
            new PlaybackStreamSessionStore(TimeProvider.System),
            PlaybackServerTestKit.Create().Capabilities,
            canonicalStorage: storage);
        var result = await planner.PlanAsync(
            PlaybackVideoTarget.Movie(movie.Id), "reader",
            new PlaybackPlanInput(null, ClientKinds.Web, ChromeAgent, IPAddress.Parse("203.0.113.9"),
                Network: new PlaybackNetworkReport(ThroughputKbps: 24_000)),
            CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(original.StoredFileId, result.MediaFileId,
            "A marked prepared file is not enough: seek positions and audio/forced-subtitle selection must be equivalent.");
    }

    [TestMethod]
    public async Task CanonicalBootstrapUsesTracksAndWorkEpisodeNavigation()
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var storage = new CanonicalMediaStorageService(fixture.Db);
        var progress = new VideoProgressService(fixture.Db);
        var player = new CanonicalVideoPlayerService(
            fixture.Db,
            storage,
            fixture.Inventory,
            progress);

        var movie = new Work
        {
            MediaType = WorkMediaType.Movie,
            CanonicalTitle = "Movie Without Fake Next"
        };
        var series = new Work
        {
            MediaType = WorkMediaType.Series,
            CanonicalTitle = "Ordered Series"
        };
        var episode1 = new WorkEpisode { WorkId = series.Id, SeasonNumber = 1, EpisodeNumber = 1, Title = "One" };
        var episode2 = new WorkEpisode { WorkId = series.Id, SeasonNumber = 1, EpisodeNumber = 2, Title = "Two" };
        var episode3 = new WorkEpisode { WorkId = series.Id, SeasonNumber = 2, EpisodeNumber = 1, Title = "Three" };
        fixture.Db.AddRange(movie, series);
        await fixture.Db.SaveChangesAsync();
        foreach (var episode in new[] { episode1, episode2, episode3 })
        {
            episode.WorkId = series.Id;
        }

        fixture.Db.AddRange(episode1, episode2, episode3);
        await fixture.Db.SaveChangesAsync();

        await AttachAsync(fixture, storage, movie.Id, null, "bootstrap-movie.mp4", MediaProbeFixtures.H264Stereo);
        await AttachAsync(fixture, storage, series.Id, episode1.Id, "bootstrap-s01e01.mp4", MediaProbeFixtures.H264Stereo);
        await AttachAsync(fixture, storage, series.Id, episode2.Id, "bootstrap-s01e02.mkv", MediaProbeFixtures.HevcTenBitHdrMultiAudio);
        await AttachAsync(fixture, storage, series.Id, episode3.Id, "bootstrap-s02e01.mp4", MediaProbeFixtures.H264Stereo);

        var movieBootstrap = (await player.GetAsync(
            "reader",
            PlaybackVideoTarget.Movie(movie.Id),
            CancellationToken.None)).Snapshot;
        Assert.IsNotNull(movieBootstrap);
        Assert.IsNull(movieBootstrap.Navigation.Previous);
        Assert.IsNull(movieBootstrap.Navigation.Next, "A movie never fabricates episode navigation.");

        var tvBootstrap = (await player.GetAsync(
            "reader",
            PlaybackVideoTarget.Episode(series.Id, episode2.Id),
            CancellationToken.None)).Snapshot;
        Assert.IsNotNull(tvBootstrap);
        Assert.AreEqual(episode1.Id, tvBootstrap.Navigation.Previous!.Target.WorkEpisodeId);
        Assert.AreEqual(episode3.Id, tvBootstrap.Navigation.Next!.Target.WorkEpisodeId);
        Assert.AreEqual(2, tvBootstrap.Inventory.Technical!.AudioStreams.Count);
        Assert.AreEqual(2, tvBootstrap.Inventory.Technical.SubtitleStreams.Count);
        Assert.AreEqual(series.Id, tvBootstrap.Target.WorkId);
        Assert.AreEqual(episode2.Id, tvBootstrap.Target.WorkEpisodeId);
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
        fixture.Db.Add(work);
        await fixture.Db.SaveChangesAsync();
        workEpisode.WorkId = work.Id;
        fixture.Db.Add(workEpisode);
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
            PlaybackServerTestKit.Create().Capabilities,
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
            "/api/client/v1/video/player",
            ClientApiRoutes.VideoPlayer);
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
        long workId,
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
