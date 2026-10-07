using System.Net;
using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.MediaMapping;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Tracking;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using DiscoverIndexModel = Jularr.Web.Pages.IndexModel;

namespace Jularr.Tests;

/// <summary>
/// #813 Anime regression gate: an AniList title requested from Discover is added, searched by the Anime scheduler, downloaded,
/// imported, listed in the Library, shown on the Anime detail page, planned and resumed through the canonical player API, and its
/// completion advances CompletedThrough and AniList, which is only ever written from a completed episode. Each stage consumes what the
/// previous one produced; only AniList, the indexer, SABnzbd, ffprobe and the files are faked.
/// </summary>
[TestClass]
public sealed class RequestToPlayAnimeTests
{
    private const string FrierenAniList = "154587";
    private const string Episode1 = "Frieren.S01E01.1080p.WEB-DL.AAC.H.264-GRP";
    private const string Viewer = VideoDetailPageTestHost.Profile;
    private const string Other = "someone-else";
    private const string Api = "/api/client/v1";
    private const string Token = "viewer-token";
    private const int AniListViewerId = 42;
    private static readonly int s_aniListMediaId = int.Parse(FrierenAniList);
    private static readonly WorkMediaType[] s_videoTypes = [WorkMediaType.Anime, WorkMediaType.Series, WorkMediaType.Movie];

    /// <summary>The Anime acquisition pipeline plus, over the same database, the real detail pages and client player API.</summary>
    private sealed class AnimeWorld : IAsyncDisposable
    {
        private AnimeWorld(AnimeAcquisitionEnvironment environment, VideoDetailPageTestHost pages, ManageSheetPageTestHost animePages)
        {
            Environment = environment;
            Pages = pages;
            AnimePages = animePages;
        }

        public AnimeAcquisitionEnvironment Environment { get; }
        public VideoDetailPageTestHost Pages { get; }
        public ManageSheetPageTestHost AnimePages { get; }
        public AniListSyncTests.SyncRemote AniList { get; } = new();

        public static async Task<AnimeWorld> CreateAsync()
        {
            var environment = await AnimeAcquisitionEnvironment.CreateAsync();
            var databasePath = Path.Combine(environment.TempRoot, "jularr.db");
            var pages = await VideoDetailPageTestHost.CreateAsync(databasePath);
            var animePages = await ManageSheetPageTestHost.CreateAsync(databasePath);
            var h264 = new MediaProbeRun(MediaProbeRunStatus.Completed, MediaProbeFixtures.H264Stereo);
            pages.Probe.DefaultResult = h264;
            environment.Probe.DefaultResult = h264;
            environment.AniListMetadata.Add(FrierenAniList, "Frieren", episodeCount: 2, status: "FINISHED");
            environment.Prowlarr.Releases.Add(AnimeAcquisitionEnvironment.Release(Episode1, "e1"));
            return new AnimeWorld(environment, pages, animePages);
        }

        /// <summary>The Request dialog of the Discover page: the one entry point of every request, here through the real Anime executor.</summary>
        public async Task<IActionResult> PostRequestAsync(string profileId = "owner", AccountRole role = AccountRole.Owner)
        {
            await using var scope = Environment.CreateScope();
            var page = DiscoverPage(scope, profileId, role);
            var form = new DiscoverRequestForm { Category = "anime", Provider = "anilist", ExternalId = FrierenAniList, Title = "Frieren" };
            return await page.OnPostRequestAsync(form, CancellationToken.None);
        }

        public async Task<AcquisitionRequest> RequestAsync() =>
            ((DiscoverRequestResultView)((PartialViewResult)await PostRequestAsync()).ViewData.Model!).Request;

        public DiscoverIndexModel DiscoverPage(IServiceScope scope, string profileId = "owner", AccountRole role = AccountRole.Owner) =>
            DiscoverPageFactory.Create(Environment.Db, null, [scope.ServiceProvider.GetRequiredService<AnimeAcquisitionRequestExecutor>()], Pages.Capabilities, Environment.DataRoot, profileId, role);

        public async Task<AniListAccountService> ConnectAniListAsync()
        {
            await using var scope = Environment.CreateScope();
            var provider = scope.ServiceProvider;
            var accounts = provider.GetRequiredService<AniListAccountStore>();
            await accounts.SaveAsync(Viewer, new StoredAniListAccount(12345, AniListViewerId, "viewer", null, Token, DateTimeOffset.UtcNow, null), CancellationToken.None);
            AniList.Viewer(Token, AniListViewerId);
            var reviews = provider.GetRequiredService<MediaMappingReviewStore>();
            var http = new HttpClient(AniList, disposeHandler: false) { BaseAddress = new Uri("https://graphql.anilist.co/") };
            var metadata = new AnimeMetadataService(Environment.Db, [], accounts, reviews);
            var segments = provider.GetRequiredService<ReadingSegmentMappingStore>();
            return new AniListAccountService(http, accounts, Environment.Db, metadata, segments, reviews, EpisodeFlowFixture.Account(Viewer), NullLogger<AniListAccountService>.Instance);
        }

        /// <summary>The scheduler's search-on-add run for the requested anime, as the Wanted pass of this media type.</summary>
        public Task<AnimeAcquisitionRunSummary> WantedPassAsync(Anime anime) =>
            Environment.Scheduler.RunNowAsync(anime.Key, AnimeSearchTrigger.SearchOnAdd, CancellationToken.None);

        /// <summary>One pass of the shared Wanted scheduler, which brings the request to the state of the Anime pipeline.</summary>
        public Task<int> RequestPassAsync() => Environment.RequestPassAsync();

        /// <summary>What the consumer's live request card reads for the request.</summary>
        public async Task<JsonElement> ConsumerStatusAsync(Guid requestId)
        {
            using var scope = Environment.CreateScope();
            return await RequestToPlayAssert.ConsumerStatusAsync(DiscoverPage(scope), requestId);
        }

        /// <summary>SABnzbd finishes the latest grab into a real folder and the shared completed-download spine imports it.</summary>
        public async Task<AnimeImportRecord> DownloadAndImportAsync()
        {
            var folder = Environment.AddCompletedDownload(Episode1, $"{Episode1}.mkv");
            var record = await Environment.ImportCompletedAsync(await Environment.CompleteLatestDownloadAsync(folder), folder);
            Assert.AreEqual(AnimeImportStatus.Imported, record!.Status, record.Message);
            return record;
        }

        public async ValueTask DisposeAsync()
        {
            await AnimePages.DisposeAsync();
            await Pages.DisposeAsync();
            await Environment.DisposeAsync();
        }
    }

    private static async Task<JsonElement> SendAsync(AnimeWorld world, HttpMethod method, string path, object body, string? profile = null)
    {
        var response = await world.Pages.SendAsync(method, path, body, profile: profile);
        Assert.AreEqual(HttpStatusCode.OK, response.Status, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.Clone();
    }

    private static object TargetOf(MediaProgressTarget target) => new { target = new { workId = target.WorkId, workEpisodeId = target.WorkEpisodeId } };

    private static Task<JsonElement> PlanAsync(AnimeWorld world, MediaProgressTarget target, string? profile = null) =>
        SendAsync(world, HttpMethod.Post, $"{Api}/video/playback-plan", TargetOf(target), profile);

    private static Task<JsonElement> LegacyPlanAsync(AnimeWorld world, Guid episodeId, string? profile = null) =>
        SendAsync(world, HttpMethod.Post, $"{Api}/episodes/{episodeId}/playback-plan", new { }, profile);

    private static Task<JsonElement> CheckpointAsync(AnimeWorld world, MediaProgressTarget target, long positionMs, bool completed)
    {
        var body = new { target = new { workId = target.WorkId, workEpisodeId = target.WorkEpisodeId }, positionMs, durationMs = 1_440_000, completed };
        return SendAsync(world, HttpMethod.Put, $"{Api}/video/progress", body);
    }

    [TestMethod]
    public async Task ADiscoverRequestBecomesAPlayableResumableCompletableEpisodeAndAniListIsWrittenOnlyFromTheCompletedOne()
    {
        await using var world = await AnimeWorld.CreateAsync();
        var environment = world.Environment;

        // Discover -> Request: the Anime executor adds the series like Sonarr, creates its canonical Work and queues the search.
        // Nothing is downloaded yet, so the request is in progress and never reads as available.
        var request = await world.RequestAsync();
        Assert.AreEqual(AcquisitionRequestStatus.Approved, request.Status, request.StatusMessage);
        var anime = await environment.Db.Anime.AsNoTracking().SingleAsync();
        Assert.AreEqual($"/Library/Anime/{anime.Id}", request.ResultUrl);
        RequestToPlayAssert.AdminQueueProjectsTheRequest(request);
        var requestedStatus = await world.ConsumerStatusAsync(request.Id);
        Assert.AreEqual("approved", requestedStatus.GetProperty("status").GetString());
        Assert.IsFalse(requestedStatus.GetProperty("done").GetBoolean());

        var requested = Assert.ContainsSingle((await new LibraryMediaCardQuery(environment.Db).GetEntriesAsync(Viewer, s_videoTypes, CancellationToken.None)).Entries);
        Assert.AreEqual(request.ResultUrl, requested.Card.Href, "A requested anime is in the Library before it has a file.");
        Assert.AreEqual(0, requested.PlayableUnits);

        // Wanted: the scheduler searches and grabs once; a second pass and a restart never submit the release again.
        var run = await world.WantedPassAsync(anime);
        Assert.AreEqual(1, run.Grabs, run.ToString());
        await world.WantedPassAsync(anime);
        await environment.RestartAsync();
        await world.WantedPassAsync(anime);
        Assert.AreEqual(Episode1, Assert.ContainsSingle(environment.Sabnzbd.Grabs).NzbName, "The release is submitted once, whatever the number of passes and restarts.");

        // The request follows the grab: Downloading, linked to the download Operation the consumer's progress reads.
        await world.RequestPassAsync();
        var downloading = await world.ConsumerStatusAsync(request.Id);
        Assert.AreEqual("downloading", downloading.GetProperty("status").GetString());
        Assert.AreEqual(request.ResultUrl, downloading.GetProperty("resultUrl").GetString());
        Assert.IsFalse(downloading.GetProperty("done").GetBoolean());

        // Download -> Import: the finished job goes through the shared completed-download spine into the Anime importer.
        await world.DownloadAndImportAsync();

        // The first episode is in the library and playable below, but the request also asks for episode 2, which has no release yet:
        // it keeps looking for it instead of reporting the title as done (AnimeRequestLifecycleTests covers the Completed transition).
        await world.RequestPassAsync();
        var remaining = await world.ConsumerStatusAsync(request.Id);
        Assert.AreEqual("approved", remaining.GetProperty("status").GetString());
        Assert.AreEqual(request.ResultUrl, remaining.GetProperty("resultUrl").GetString());
        Assert.IsFalse(remaining.GetProperty("done").GetBoolean());
        Assert.AreEqual(anime.Id, (await environment.Db.Anime.AsNoTracking().SingleAsync()).Id, "The import attaches to the requested entry.");
        var legacyFile = await environment.Db.MediaFiles.AsNoTracking().SingleAsync();
        var legacyEpisode = await environment.Db.Episodes.AsNoTracking().SingleAsync(x => x.Id == legacyFile.EpisodeId);
        Assert.AreEqual(1, legacyEpisode.Number);

        // One canonical Work, WorkEpisode, Asset and File: the same identity from request to import.
        var work = await environment.Db.Works.AsNoTracking().SingleAsync();
        Assert.AreEqual(WorkMediaType.Anime, work.MediaType);
        var workEpisode = await environment.Db.WorkEpisodes.AsNoTracking().SingleAsync(x => x.WorkId == work.Id && x.EpisodeNumber == 1);
        var asset = await environment.Db.MediaAssets.AsNoTracking().SingleAsync();
        Assert.AreEqual(work.Id, asset.WorkId);
        Assert.AreEqual(workEpisode.Id, asset.WorkEpisodeId);
        Assert.AreEqual(legacyFile.Id, (await environment.Db.StoredFiles.AsNoTracking().SingleAsync()).Id);

        // Library: the same entry, now with one playable episode, read in a bounded number of commands and without any path.
        var entry = Assert.ContainsSingle((await new LibraryMediaCardQuery(environment.Db).GetEntriesAsync(Viewer, s_videoTypes, CancellationToken.None)).Entries);
        Assert.AreEqual(work.Id, entry.WorkId);
        Assert.AreEqual(1, entry.PlayableUnits);
        Assert.AreEqual(request.ResultUrl, entry.Card.Href);
        RequestToPlayAssert.NoHostPathIn(JsonSerializer.Serialize(entry), environment.Root.Path, environment.Downloads);
        var counter = new LibraryCanonicalReadTests.CommandCounter();
        var countedOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(environment.TempRoot, "jularr.db")};Foreign Keys=True").AddInterceptors(counter).Options;
        await using (var counted = new AppDbContext(countedOptions))
        {
            await new LibraryMediaCardQuery(counted).GetEntriesAsync(Viewer, s_videoTypes, CancellationToken.None);
        }

        Assert.IsTrue(counter.Count <= 12, $"The Library read must stay bounded, saw {counter.Count} commands.");

        // Detail: following the request's result URL, the episode is available and links to the episode player.
        var detail = await world.AnimePages.GetHtmlAsync(request.ResultUrl!, asOwner: false);
        RequestToPlayAssert.Contains(detail, $"/Library/Episode/{legacyEpisode.Id}");
        RequestToPlayAssert.Contains(detail, "ad-state-available");
        RequestToPlayAssert.NoHostPathIn(detail, environment.Root.Path, environment.Downloads);

        // Player: the legacy episode route resolves to the same canonical target as the canonical route and the same file.
        var bridge = new LegacyWorkBridge(environment.Db, new WorkService(environment.Db), new WorkStructureService(environment.Db));
        var target = (await new CanonicalVideoTargetResolver(environment.Db, bridge).ResolveLegacyEpisodeAsync(legacyEpisode.Id))!;
        Assert.AreEqual(work.Id, target.WorkId);
        Assert.AreEqual(workEpisode.Id, target.WorkEpisodeId);
        var canonicalPlan = await PlanAsync(world, target);
        var legacyPlan = await LegacyPlanAsync(world, legacyEpisode.Id);
        Assert.AreEqual(workEpisode.Id, canonicalPlan.GetProperty("target").GetProperty("workEpisodeId").GetGuid());
        Assert.AreEqual(workEpisode.Id, legacyPlan.GetProperty("target").GetProperty("workEpisodeId").GetGuid(), "The legacy route is only an adapter onto the canonical target.");
        Assert.AreNotEqual(JsonValueKind.Null, canonicalPlan.GetProperty("delivery").ValueKind, canonicalPlan.GetProperty("plan").ToString());
        var bootstrap = await world.Pages.SendAsync(HttpMethod.Post, $"{Api}/video/player", TargetOf(target));
        Assert.AreEqual(HttpStatusCode.OK, bootstrap.Status, bootstrap.Body);
        Assert.AreEqual(legacyFile.Id, JsonDocument.Parse(bootstrap.Body).RootElement.GetProperty("media").GetProperty("mediaFileId").GetGuid());
        RequestToPlayAssert.NoHostPathIn(bootstrap.Body + canonicalPlan + legacyPlan, environment.Root.Path, environment.Downloads);

        // Resume: a checkpoint resumes exactly in both routes, and a seek past 95% neither completes nor reaches AniList.
        var anilist = await world.ConnectAniListAsync();
        world.AniList.Put(Token, s_aniListMediaId, progress: 0);
        Assert.AreEqual(600_000, (await CheckpointAsync(world, target, 600_000, completed: false)).GetProperty("positionMs").GetInt64());
        Assert.AreEqual(600_000, (await PlanAsync(world, target)).GetProperty("resumePositionMs").GetInt64());
        Assert.AreEqual(600_000, (await LegacyPlanAsync(world, legacyEpisode.Id)).GetProperty("resumePositionMs").GetInt64());
        Assert.IsFalse((await CheckpointAsync(world, target, 1_420_000, completed: false)).GetProperty("isCompleted").GetBoolean(), "Seeking near the end is only a resume point.");
        var progress = new VideoProgressService(environment.Db);
        Assert.IsTrue((await progress.GetCompletedThroughAsync(Viewer, work.Id)).All(x => x.CompletedThrough is null));
        var beforeCompletion = await anilist.SyncEpisodeProgressAsync(legacyEpisode.Id, CancellationToken.None);
        Assert.IsFalse(beforeCompletion.Changed, beforeCompletion.Message);
        Assert.AreEqual(0, world.AniList.Mutations.Count, "An episode that is merely in progress never reaches AniList.");

        // Completion (declared by the player) advances CompletedThrough and only then AniList, with exactly that episode.
        Assert.IsTrue((await CheckpointAsync(world, target, 1_440_000, completed: true)).GetProperty("isCompleted").GetBoolean());
        Assert.AreEqual(workEpisode.Id, (await progress.GetCompletedThroughAsync(Viewer, work.Id)).Single().CompletedThrough!.WorkEpisodeId);
        Assert.IsTrue(Assert.ContainsSingle(await CanonicalProgressSeed.RowsAsync(environment.Db, anime.Id, Viewer)).IsCompleted, "The legacy-addressed projection reads the same canonical row.");
        var synced = await anilist.SyncEpisodeProgressAsync(legacyEpisode.Id, CancellationToken.None);
        Assert.IsTrue(synced.Changed, synced.Message);
        Assert.AreEqual(1, world.AniList.Progress(Token, s_aniListMediaId));
        StringAssert.Contains(Assert.ContainsSingle(world.AniList.Mutations), "\"progress\":1");

        // A rewatch keeps the completion and a new resume point, and never writes AniList again.
        await CheckpointAsync(world, target, 120_000, completed: false);
        var rewatch = (await progress.GetAsync(Viewer, target))!;
        Assert.IsTrue(rewatch.IsCompleted);
        Assert.AreEqual(120_000, rewatch.ResumePositionMs);
        Assert.IsFalse((await anilist.SyncEpisodeProgressAsync(legacyEpisode.Id, CancellationToken.None)).Changed);
        Assert.AreEqual(1, world.AniList.Mutations.Count);

        // AniList ahead of the local completion is never lowered.
        world.AniList.Put(Token, s_aniListMediaId, progress: 5);
        var ahead = await anilist.SyncEpisodeProgressAsync(legacyEpisode.Id, CancellationToken.None);
        Assert.IsFalse(ahead.Changed, ahead.Message);
        Assert.AreEqual(5, world.AniList.Progress(Token, s_aniListMediaId));

        // Continue Watching and history read the same canonical rows.
        Assert.AreEqual(work.Id, Assert.ContainsSingle(await progress.GetContinueWatchingAsync(Viewer)).WorkId);
        Assert.AreEqual(work.Id, Assert.ContainsSingle(await progress.GetHistoryAsync(Viewer)).WorkId);
    }

    [TestMethod]
    public async Task ARequestedAnimeIsRefusedServerSideForAProfileWithoutTheAnimeTypeAndProgressStaysPerProfile()
    {
        await using var world = await AnimeWorld.CreateAsync();
        var environment = world.Environment;
        var request = await world.RequestAsync();
        await world.WantedPassAsync(await environment.Db.Anime.AsNoTracking().SingleAsync());
        await world.DownloadAndImportAsync();
        var legacyEpisode = await environment.Db.Episodes.AsNoTracking().SingleAsync();
        var work = await environment.Db.Works.AsNoTracking().SingleAsync();
        var target = new MediaProgressTarget(work.Id, (await environment.Db.WorkEpisodes.AsNoTracking().SingleAsync(x => x.WorkId == work.Id && x.EpisodeNumber == 1)).Id);
        var progress = new VideoProgressService(environment.Db);

        await CheckpointAsync(world, target, 600_000, completed: false);
        Assert.AreEqual(600_000, (await LegacyPlanAsync(world, legacyEpisode.Id)).GetProperty("resumePositionMs").GetInt64());
        Assert.AreEqual(0, (await LegacyPlanAsync(world, legacyEpisode.Id, Other)).GetProperty("resumePositionMs").GetInt64(), "Another profile starts from the beginning.");
        Assert.AreEqual(0, (await progress.GetContinueWatchingAsync(Other)).Count);

        await world.Pages.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Anime, MediaCapability.Hidden);
        await world.AnimePages.Services.GetRequiredService<MediaCapabilityStore>().SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Anime, MediaCapability.Hidden);
        var hiddenBody = TargetOf(target);
        var checkpoint = new { target = new { workId = target.WorkId, workEpisodeId = target.WorkEpisodeId }, positionMs = 90_000, durationMs = 1_440_000, completed = false };
        foreach (var call in new[]
        {
            await world.Pages.SendAsync(HttpMethod.Post, $"{Api}/episodes/{legacyEpisode.Id}/playback-plan", new { }, profile: Other),
            await world.Pages.SendAsync(HttpMethod.Post, $"{Api}/video/playback-plan", hiddenBody, profile: Other),
            await world.Pages.SendAsync(HttpMethod.Post, $"{Api}/video/player", hiddenBody, profile: Other),
            await world.Pages.SendAsync(HttpMethod.Put, $"{Api}/video/progress", checkpoint, profile: Other)
        })
        {
            Assert.AreEqual(HttpStatusCode.NotFound, call.Status, call.Body);
        }

        Assert.IsNull((await progress.GetAsync(Other, target))!.UpdatedAt, "A hidden type's progress is never written.");
        Assert.AreEqual(HttpStatusCode.OK, (await world.Pages.SendAsync(HttpMethod.Post, $"{Api}/episodes/{legacyEpisode.Id}/playback-plan", new { }, asOwner: true)).Status, "The owner is unrestricted.");
        Assert.IsNotInstanceOfType<PartialViewResult>(await world.PostRequestAsync(Other, AccountRole.User), "A profile that may not request creates no request.");
        Assert.AreEqual(request.Id, Assert.ContainsSingle(await new AcquisitionAccessStore(environment.Db).ListAsync(null, null, openOnly: false, 10, CancellationToken.None)).Id, "Only the owner's request exists.");
    }
}
