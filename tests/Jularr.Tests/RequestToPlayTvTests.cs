using System.Net;
using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.ClientApi;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Storage;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>
/// #813 TV gate: a TMDB series requested from Discover with the All, Future or Custom scope has exactly the selected canonical
/// episodes searched, downloaded and imported by the shared TV importer; the Series enters the Library with its canonical
/// seasons and episodes, the Series detail page shows partial availability, the player follows WorkEpisode order, future
/// monitoring survives a restart and progress is the profile's own. Each stage consumes what the previous one produced.
/// </summary>
[TestClass]
public sealed class RequestToPlayTvTests
{
    private const string SeveranceTmdb = "95396";
    private const string Viewer = VideoDetailPageTestHost.Profile;
    private const string Api = "/api/client/v1";
    private static readonly WorkMediaType[] s_videoTypes = [WorkMediaType.Anime, WorkMediaType.Series, WorkMediaType.Movie];

    private static string Release(int episode) => $"Severance.S01E{episode:00}.1080p.WEB-DL.x264-GROUP";

    /// <summary>Season 1 with two aired episodes and a third that airs ten days from now.</summary>
    private static FakeTmdb Tmdb(int daysToThirdEpisode = 10)
    {
        var now = DateTime.UtcNow;
        var tmdb = new FakeTmdb();
        tmdb.AddSeries(95396, "Severance", now.AddDays(-30), (1, [(1, now.AddDays(-30)), (2, now.AddDays(-23)), (3, now.AddDays(daysToThirdEpisode))]));
        tmdb.AddSeries(1396, "Breaking Bad", now.AddDays(-400), (1, [(1, now.AddDays(-400))]));
        return tmdb;
    }

    private static async Task<VideoRequestToPlayWorld> CreateWorldAsync(int daysToThirdEpisode = 10)
    {
        var world = await VideoRequestToPlayWorld.CreateAsync(MediaAcquisitionKind.Tv, Tmdb(daysToThirdEpisode));
        foreach (var episode in new[] { 1, 2, 3 })
        {
            world.Indexer.Publish(Release(episode));
        }

        return world;
    }

    /// <summary>The season of the resolved series as the Request dialog offers it: the canonical episode ids a Custom scope selects.</summary>
    private static async Task<Guid[]> EpisodeIdsAsync(VideoRequestToPlayWorld world)
    {
        var settings = await world.ResolveAsync(SeveranceTmdb);
        Assert.IsTrue(settings.OffersScope, "A series offers its own scope.");
        var season = Assert.ContainsSingle(settings.Seasons);
        Assert.AreEqual(3, season.Episodes.Count);
        return [.. season.Episodes.OrderBy(x => x.Number).Select(x => x.Id)];
    }

    /// <summary>SABnzbd finishes the request's current download of one episode and the Wanted pass imports it.</summary>
    private static async Task<AcquisitionRequest> ImportAsync(VideoRequestToPlayWorld world, Guid requestId, int episode)
    {
        var current = await world.GetRequestAsync(requestId);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, current.Status, current.StatusMessage);
        await world.CompleteDownloadAsync(current, Release(episode), $"{Release(episode)}.mkv");
        await world.WantedPassAsync();
        return await world.GetRequestAsync(requestId);
    }

    private static async Task<JsonElement> PostAsync(VideoRequestToPlayWorld world, string route, Guid workId, Guid episodeId, string? profile = null)
    {
        var response = await world.Pages.SendAsync(HttpMethod.Post, $"{Api}/video/{route}", new { target = new { workId, workEpisodeId = episodeId } }, profile: profile);
        Assert.AreEqual(HttpStatusCode.OK, response.Status, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.Clone();
    }

    private static async Task<JsonElement> PutProgressAsync(VideoRequestToPlayWorld world, Guid workId, Guid episodeId, long positionMs, bool completed, string? profile = null)
    {
        var response = await world.Pages.SendAsync(HttpMethod.Put, $"{Api}/video/progress", new { target = new { workId, workEpisodeId = episodeId }, positionMs, durationMs = 1_440_000, completed }, profile: profile);
        Assert.AreEqual(HttpStatusCode.OK, response.Status, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.Clone();
    }

    /// <summary>The player offers exactly these episodes before and after one, in canonical season/episode order.</summary>
    private static async Task AssertNavigationAsync(VideoRequestToPlayWorld world, Guid workId, Guid episodeId, Guid? previous, Guid? next)
    {
        var navigation = (await PostAsync(world, "player", workId, episodeId)).GetProperty("navigation");
        Guid? Neighbour(string name) => navigation.GetProperty(name) is { ValueKind: JsonValueKind.Object } item ? item.GetProperty("target").GetProperty("workEpisodeId").GetGuid() : null;
        Assert.AreEqual(previous, Neighbour("previous"), "previous");
        Assert.AreEqual(next, Neighbour("next"), "next");
    }

    private static async Task<int> CommandsAsync(VideoRequestToPlayWorld world, Func<AppDbContext, Task> read)
    {
        var counter = new LibraryCanonicalReadTests.CommandCounter();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={world.DatabasePath};Foreign Keys=True").AddInterceptors(counter).Options);
        await read(db);
        return counter.Count;
    }

    private static Task<int> LibraryReadCommandsAsync(VideoRequestToPlayWorld world) =>
        CommandsAsync(world, async db => await new LibraryMediaCardQuery(db).GetEntriesAsync(Viewer, s_videoTypes, CancellationToken.None));

    private static string Section(string html, string start, string end)
    {
        var from = html.IndexOf(start, StringComparison.Ordinal);
        Assert.IsTrue(from >= 0, $"'{start}' not found.");
        var to = html.IndexOf(end, from, StringComparison.Ordinal);
        Assert.IsTrue(to > from, $"'{end}' not found after '{start}'.");
        return html[from..to];
    }

    [TestMethod]
    public async Task AnAllScopeRequestImportsEpisodeByEpisodeIntoOneSeriesAndKeepsFutureMonitoringAcrossARestart()
    {
        await using var world = await CreateWorldAsync();
        var episodeIds = await EpisodeIdsAsync(world);
        var work = await world.Db.Works.AsNoTracking().SingleAsync();
        Assert.AreEqual(WorkMediaType.Series, work.MediaType);

        // Request (All): the canonical Work and its structure come from the dialog's resolve step, the request carries only ids of it.
        var request = await world.RequestAsync(SeveranceTmdb, "Severance", scope: "all");
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status, request.StatusMessage);
        var payload = VideoRequestPayload.Parse(request.PayloadJson)!;
        Assert.AreEqual(VideoRequestScope.AllCurrentAndFuture, payload.Scope);
        Assert.IsTrue(payload.MonitorFuture);
        Assert.AreEqual(work.Id, payload.WorkId);
        Assert.AreEqual(episodeIds[0], payload.ActiveWorkEpisodeId, "The first missing aired episode is searched first.");
        Assert.AreEqual(Release(1), world.Sabnzbd.Grabs.Single().NzbName);
        RequestToPlayAssert.AdminQueueProjectsTheRequest(request);
        var status = await RequestToPlayAssert.ConsumerStatusAsync(world.DiscoverPage(), request.Id);
        Assert.AreEqual("downloading", status.GetProperty("status").GetString());

        // Detail while nothing is imported: only the episode the download is on reads Downloading, the others Requested; nothing plays.
        var pending = await world.Pages.GetOkAsync($"/Library/Series/{work.Id}");
        RequestToPlayAssert.Contains(Section(pending, "S01 E01", "</article>"), "ad-state-downloading");
        RequestToPlayAssert.Contains(Section(pending, "S01 E02", "</article>"), "ad-state-requested");
        RequestToPlayAssert.Contains(Section(pending, "<section class=\"ad-hero", "</section>"), "href=\"/Requests\"");
        Assert.IsFalse(pending.Contains("/Library/Watch/", StringComparison.Ordinal));

        // Import E1: the next pass also searches and grabs E2 for the same open request.
        await ImportAsync(world, request.Id, 1);
        var secondGrab = await world.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, secondGrab.Status, secondGrab.StatusMessage);
        Assert.AreEqual(2, world.Sabnzbd.Grabs.Count);
        Assert.AreEqual(Release(2), world.Sabnzbd.Grabs[1].NzbName);
        Assert.AreEqual(episodeIds[1], VideoRequestPayload.Parse(secondGrab.PayloadJson)!.ActiveWorkEpisodeId);

        // One Work and one canonical structure from provider result through request to import; the file sits in a season folder.
        Assert.AreEqual(work.Id, (await world.Db.Works.AsNoTracking().SingleAsync()).Id, "The import must attach to the Work the request is about.");
        Assert.AreEqual(1, await world.Db.TvSeries.CountAsync());
        var episodes = await world.Db.WorkEpisodes.AsNoTracking().Where(x => x.WorkId == work.Id).OrderBy(x => x.EpisodeNumber).ToListAsync();
        CollectionAssert.AreEqual(episodeIds, episodes.Select(x => x.Id).ToArray(), "The import reuses the structure the provider materialized.");
        Assert.IsTrue(episodes.All(x => x.AiredAt is not null && !string.IsNullOrEmpty(x.Title)), "The import must not erase the provider's air dates and titles.");
        var asset = await world.Db.MediaAssets.AsNoTracking().SingleAsync();
        Assert.AreEqual(work.Id, asset.WorkId);
        Assert.AreEqual(episodeIds[0], asset.WorkEpisodeId);
        var placed = Assert.ContainsSingle(Directory.GetFiles(world.LibraryRoot, "*.mkv", SearchOption.AllDirectories));
        Assert.AreEqual("Season 01", Path.GetFileName(Path.GetDirectoryName(placed)));
        Assert.AreEqual(Path.GetFullPath(placed), Path.GetFullPath((await world.Db.StoredFiles.AsNoTracking().SingleAsync()).Path));

        // Library and Detail show partial availability of the same Work.
        var entry = Assert.ContainsSingle((await new LibraryMediaCardQuery(world.Db).GetEntriesAsync(Viewer, s_videoTypes, CancellationToken.None)).Entries);
        Assert.AreEqual(work.Id, entry.WorkId);
        Assert.AreEqual(1, entry.PlayableUnits);
        Assert.AreEqual(2, entry.MissingUnits);
        Assert.IsTrue(LibraryBrowse.Matches(entry, LibraryAvailabilityState.Partial));
        RequestToPlayAssert.NoHostPathIn(JsonSerializer.Serialize(entry), world.LibraryRoot, world.Downloads);
        var libraryCommands = await LibraryReadCommandsAsync(world);
        Assert.IsTrue(libraryCommands <= 12, $"The Library read must stay bounded, saw {libraryCommands} commands.");
        var detail = await world.Pages.GetOkAsync($"/Library/Series/{work.Id}");
        var hero = Section(detail, "<section class=\"ad-hero", "</section>");
        RequestToPlayAssert.Contains(hero, "1 of 3 episodes available");
        RequestToPlayAssert.Contains(hero, $"href=\"/Library/Watch/{work.Id}/{episodeIds[0]}\"");
        var episodeList = Section(detail, "<section class=\"ad-episodes\"", "</section>");
        RequestToPlayAssert.Contains(Section(episodeList, "S01 E01", "</article>"), $"/Library/Watch/{work.Id}/{episodeIds[0]}");
        RequestToPlayAssert.Contains(Section(episodeList, "S01 E02", "</article>"), "ad-state-downloading");
        RequestToPlayAssert.NoHostPathIn(detail, world.LibraryRoot, world.Downloads);

        // Play E1: the canonical target resolves the imported file, a checkpoint resumes exactly and a declared completion advances CompletedThrough.
        await world.AnalyzeLibraryFilesAsync();
        var watch = await world.Pages.GetOkAsync($"/Library/Watch/{work.Id}/{episodeIds[0]}");
        RequestToPlayAssert.Contains(watch, $"data-video-target-episode=\"{episodeIds[0]}\"");
        RequestToPlayAssert.NoHostPathIn(watch, world.LibraryRoot, world.Downloads);
        var plan = await PostAsync(world, "playback-plan", work.Id, episodeIds[0]);
        Assert.AreEqual(episodeIds[0], plan.GetProperty("target").GetProperty("workEpisodeId").GetGuid());
        Assert.AreEqual(0, plan.GetProperty("resumePositionMs").GetInt64());
        await PutProgressAsync(world, work.Id, episodeIds[0], 600_000, completed: false);
        Assert.AreEqual(600_000, (await PostAsync(world, "playback-plan", work.Id, episodeIds[0])).GetProperty("resumePositionMs").GetInt64());
        RequestToPlayAssert.Contains(Section(await world.Pages.GetOkAsync($"/Library/Series/{work.Id}"), "<section class=\"ad-hero", "</section>"), "Continue watching");
        var progress = new VideoProgressService(world.Db);
        Assert.IsNull((await progress.GetCompletedThroughAsync(Viewer, work.Id)).Single().CompletedThrough, "A checkpoint is not a completion.");
        Assert.IsTrue((await PutProgressAsync(world, work.Id, episodeIds[0], 1_440_000, completed: true)).GetProperty("isCompleted").GetBoolean());
        Assert.AreEqual(episodeIds[0], (await progress.GetCompletedThroughAsync(Viewer, work.Id)).Single().CompletedThrough!.WorkEpisodeId);
        Assert.AreEqual(0, (await progress.GetContinueWatchingAsync(Viewer)).Count, "E2 is not playable yet, so there is nothing to continue with.");

        // Import E2: the aired episodes are done; E3 airs later, so the same request now waits as the durable future-monitor owner.
        var waiting = await ImportAsync(world, request.Id, 2);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, waiting.Status, waiting.StatusMessage);
        Assert.AreEqual(2, world.Sabnzbd.Grabs.Count, "The unaired episode is not searched.");
        var waitingPayload = VideoRequestPayload.Parse(waiting.PayloadJson)!;
        Assert.IsNull(waitingPayload.ActiveWorkEpisodeId);
        Assert.IsNotNull(waitingPayload.NextSearchUtc);
        Assert.AreEqual(libraryCommands, await LibraryReadCommandsAsync(world), "Importing more episodes adds no per-episode Library commands.");

        // Next/Previous and Continue Watching follow canonical WorkEpisode order and the profile's progress.
        await AssertNavigationAsync(world, work.Id, episodeIds[0], null, episodeIds[1]);
        await AssertNavigationAsync(world, work.Id, episodeIds[1], episodeIds[0], null);
        var upNext = Assert.ContainsSingle(await progress.GetContinueWatchingAsync(Viewer));
        Assert.AreEqual(VideoContinueWatchingKind.UpNext, upNext.Kind);
        Assert.AreEqual(episodeIds[1], upNext.WorkEpisodeId);
        var middle = await world.Pages.GetOkAsync($"/Library/Watch/{work.Id}/{episodeIds[0]}");
        RequestToPlayAssert.Contains(middle, $"data-next-url=\"/Library/Watch/{work.Id}/{episodeIds[1]}\"");

        // Future monitoring survives a restart: the persisted request still waits for the unaired episode and searches nothing early.
        await world.RestartAsync();
        await world.WantedPassAsync();
        var afterRestart = await world.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, afterRestart.Status);
        Assert.AreEqual(waitingPayload.NextSearchUtc, VideoRequestPayload.Parse(afterRestart.PayloadJson)!.NextSearchUtc);
        Assert.AreEqual(2, world.Sabnzbd.Grabs.Count);

        world.Clock.Advance(TimeSpan.FromDays(11));
        await world.WantedPassAsync();
        await world.WantedPassAsync();
        var third = await world.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, third.Status, third.StatusMessage);
        Assert.AreEqual(3, world.Sabnzbd.Grabs.Count, "The episode that aired is searched once.");
        Assert.AreEqual(Release(3), world.Sabnzbd.Grabs[2].NzbName);

        var monitoring = await ImportAsync(world, request.Id, 3);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, monitoring.Status, "An All scope keeps monitoring future episodes.");
        Assert.IsTrue(monitoring.IsOpen);
        Assert.AreEqual($"/Library/Series/{work.Id}", monitoring.ResultUrl);
        Assert.AreEqual(3, await world.Db.StoredFiles.CountAsync());
        Assert.AreEqual(1, await world.Db.Works.CountAsync());
        var complete = await world.Pages.GetOkAsync(monitoring.ResultUrl!);
        var completeHero = Section(complete, "<section class=\"ad-hero", "</section>");
        RequestToPlayAssert.Contains(completeHero, "3 episodes");
        Assert.IsFalse(completeHero.Contains("episodes available", StringComparison.Ordinal), "Nothing is missing any more.");
        RequestToPlayAssert.Contains(completeHero, $"href=\"/Library/Watch/{work.Id}/{episodeIds[1]}\"");
        await AssertNavigationAsync(world, work.Id, episodeIds[2], episodeIds[1], null);
    }

    [TestMethod]
    public async Task ACustomScopeSearchesOnlyTheSelectedEpisodesAndANewRequestFillsTheGapInCanonicalOrder()
    {
        await using var world = await CreateWorldAsync(daysToThirdEpisode: -16);
        var episodeIds = await EpisodeIdsAsync(world);
        var work = await world.Db.Works.AsNoTracking().SingleAsync();

        var request = await world.RequestAsync(SeveranceTmdb, "Severance", scope: "custom", episodeIds: [episodeIds[0], episodeIds[2]]);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status, request.StatusMessage);
        var payload = VideoRequestPayload.Parse(request.PayloadJson)!;
        Assert.AreEqual(VideoRequestScope.Custom, payload.Scope);
        CollectionAssert.AreEquivalent(new[] { episodeIds[0], episodeIds[2] }, payload.SelectedEpisodeIds);
        Assert.IsFalse(payload.MonitorFuture);

        await ImportAsync(world, request.Id, 1);
        var second = await world.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, second.Status, second.StatusMessage);
        var finished = await ImportAsync(world, request.Id, 3);

        Assert.AreEqual(AcquisitionRequestStatus.Completed, finished.Status, "Without future monitoring a Custom request ends when its episodes are in.");
        CollectionAssert.AreEqual(new[] { Release(1), Release(3) }, world.Sabnzbd.Grabs.Select(x => x.NzbName).ToArray(), "The unselected episode is never searched.");
        Assert.AreEqual($"/Library/Series/{work.Id}", finished.ResultUrl);

        // Detail: the gap is Not available and offers its own Request; the two imported episodes play.
        await world.AnalyzeLibraryFilesAsync();
        var detail = await world.Pages.GetOkAsync(finished.ResultUrl!);
        RequestToPlayAssert.Contains(Section(detail, "<section class=\"ad-hero", "</section>"), "2 of 3 episodes available");
        var gap = Section(Section(detail, "<section class=\"ad-episodes\"", "</section>"), "S01 E02", "</article>");
        RequestToPlayAssert.Contains(gap, "Not available");
        RequestToPlayAssert.Contains(gap, $"data-dc-preselect=\"{episodeIds[1]}\"");
        await AssertNavigationAsync(world, work.Id, episodeIds[0], null, episodeIds[2]);

        // The missing episode is requested on its own and imported; Next/Previous now follow the full canonical order.
        var fill = await world.RequestAsync(SeveranceTmdb, "Severance", scope: "custom", episodeIds: [episodeIds[1]]);
        Assert.AreNotEqual(request.Id, fill.Id, "A completed request does not block a new one for the same title.");
        Assert.AreEqual(Release(2), world.Sabnzbd.Grabs[2].NzbName);
        var filled = await ImportAsync(world, fill.Id, 2);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, filled.Status, filled.StatusMessage);
        await world.AnalyzeLibraryFilesAsync();
        await AssertNavigationAsync(world, work.Id, episodeIds[0], null, episodeIds[1]);
        await AssertNavigationAsync(world, work.Id, episodeIds[1], episodeIds[0], episodeIds[2]);
        await AssertNavigationAsync(world, work.Id, episodeIds[2], episodeIds[1], null);
        RequestToPlayAssert.Contains(await world.Pages.GetOkAsync($"/Library/Watch/{work.Id}/{episodeIds[0]}"), $"data-next-url=\"/Library/Watch/{work.Id}/{episodeIds[1]}\"");

        // Completed-through is the contiguous prefix: E1 and E3 watched with E2 still open stops at E1, and closing the gap reaches E3.
        var progress = new VideoProgressService(world.Db);
        await PutProgressAsync(world, work.Id, episodeIds[0], 1_440_000, completed: true);
        await PutProgressAsync(world, work.Id, episodeIds[2], 1_440_000, completed: true);
        Assert.AreEqual(episodeIds[0], (await progress.GetCompletedThroughAsync(Viewer, work.Id)).Single().CompletedThrough!.WorkEpisodeId, "A gap stops CompletedThrough.");
        await PutProgressAsync(world, work.Id, episodeIds[1], 1_440_000, completed: true);
        Assert.AreEqual(episodeIds[2], (await progress.GetCompletedThroughAsync(Viewer, work.Id)).Single().CompletedThrough!.WorkEpisodeId);

        Assert.AreEqual(1, await world.Db.Works.CountAsync());
        Assert.AreEqual(1, await world.Db.TvSeries.CountAsync());
        Assert.AreEqual(3, await world.Db.WorkEpisodes.CountAsync());
        Assert.AreEqual(3, await world.Db.MediaAssets.CountAsync());
        Assert.AreEqual(3, await world.Db.StoredFiles.CountAsync());
    }

    [TestMethod]
    public async Task AFutureScopeSearchesNothingEarlyAndGrabsTheEpisodeThatAiresAfterARestart()
    {
        await using var world = await CreateWorldAsync();
        var episodeIds = await EpisodeIdsAsync(world);
        var work = await world.Db.Works.AsNoTracking().SingleAsync();

        var request = await world.RequestAsync(SeveranceTmdb, "Severance", scope: "future");

        Assert.AreEqual(AcquisitionRequestStatus.Approved, request.Status, "Aired episodes are not part of a Future request, so there is nothing to grab yet.");
        Assert.AreEqual(VideoRequestScope.FutureOnly, VideoRequestPayload.Parse(request.PayloadJson)!.Scope);
        Assert.AreEqual(0, world.Sabnzbd.Grabs.Count);
        var waiting = await world.Pages.GetOkAsync($"/Library/Series/{work.Id}");
        RequestToPlayAssert.Contains(Section(Section(waiting, "<section class=\"ad-episodes\"", "</section>"), "S01 E03", "</article>"), "ad-state-requested");
        RequestToPlayAssert.Contains(Section(Section(waiting, "<section class=\"ad-episodes\"", "</section>"), "S01 E01", "</article>"), "ad-state-unavailable");

        await world.RestartAsync();
        await world.WantedPassAsync();
        Assert.AreEqual(0, world.Sabnzbd.Grabs.Count, "A restart before the episode airs searches nothing.");

        world.Clock.Advance(TimeSpan.FromDays(11));
        await world.WantedPassAsync();
        var grabbed = await world.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, grabbed.Status, grabbed.StatusMessage);
        Assert.AreEqual(Release(3), Assert.ContainsSingle(world.Sabnzbd.Grabs).NzbName);
        Assert.AreEqual(episodeIds[2], VideoRequestPayload.Parse(grabbed.PayloadJson)!.ActiveWorkEpisodeId);

        var imported = await ImportAsync(world, request.Id, 3);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, imported.Status, "Future monitoring keeps the request open for the next episodes.");
        var asset = await world.Db.MediaAssets.AsNoTracking().SingleAsync();
        Assert.AreEqual(episodeIds[2], asset.WorkEpisodeId);
        Assert.AreEqual(1, await world.Db.Works.CountAsync());
        var detail = await world.Pages.GetOkAsync($"/Library/Series/{work.Id}");
        RequestToPlayAssert.Contains(Section(detail, "<section class=\"ad-hero", "</section>"), "1 of 3 episodes available");
    }

    [TestMethod]
    public async Task OfflineStorageKeepsTheSeriesPageAndMakesEpisodePlayUnavailableWithARetryableState()
    {
        await using var world = await CreateWorldAsync();
        var episodeIds = await EpisodeIdsAsync(world);
        var work = await world.Db.Works.AsNoTracking().SingleAsync();
        var request = await world.RequestAsync(SeveranceTmdb, "Severance", scope: "all");
        await ImportAsync(world, request.Id, 1);
        await world.AnalyzeLibraryFilesAsync();
        Directory.Move(world.LibraryRoot, world.LibraryRoot + "-offline");

        var detail = await world.Pages.GetOkAsync($"/Library/Series/{work.Id}");
        RequestToPlayAssert.Contains(Section(detail, "<section class=\"ad-hero", "</section>"), $"href=\"/Library/Watch/{work.Id}/{episodeIds[0]}\"");
        var watch = await world.Pages.GetOkAsync($"/Library/Watch/{work.Id}/{episodeIds[0]}");
        var offline = ClientApiMappings.AvailabilityStateName(StorageAvailabilityState.Offline);
        Assert.AreEqual(offline, System.Text.RegularExpressions.Regex.Match(watch, "data-storage-state=\"([^\"]*)\"").Groups[1].Value);
        var plan = await PostAsync(world, "playback-plan", work.Id, episodeIds[0]);
        Assert.AreEqual(JsonValueKind.Null, plan.GetProperty("delivery").ValueKind, "No delivery is promised for unreachable storage.");
        Assert.AreEqual(offline, plan.GetProperty("availability").GetProperty("state").GetString());
        Assert.IsTrue(plan.GetProperty("availability").GetProperty("retryable").GetBoolean());
        Assert.AreEqual(1, await world.Db.StoredFiles.CountAsync(), "Offline storage never deletes the canonical file record.");
        Assert.AreEqual(120_000, (await PutProgressAsync(world, work.Id, episodeIds[0], 120_000, completed: false)).GetProperty("positionMs").GetInt64(), "Progress keeps working while storage is away.");
    }

    [TestMethod]
    public async Task SeriesProgressIsTheProfilesOwnAndAProfileWithoutTheSeriesTypeIsRefusedServerSide()
    {
        await using var world = await CreateWorldAsync();
        var episodeIds = await EpisodeIdsAsync(world);
        var work = await world.Db.Works.AsNoTracking().SingleAsync();
        var request = await world.RequestAsync(SeveranceTmdb, "Severance", scope: "all");
        await ImportAsync(world, request.Id, 1);
        await ImportAsync(world, request.Id, 2);
        await world.AnalyzeLibraryFilesAsync();
        const string Other = "someone-else";
        var progress = new VideoProgressService(world.Db);

        // One profile watches E1 to the end and starts E2; the other profile is untouched.
        await PutProgressAsync(world, work.Id, episodeIds[0], 1_440_000, completed: true);
        await PutProgressAsync(world, work.Id, episodeIds[1], 300_000, completed: false);
        Assert.AreEqual(300_000, (await PostAsync(world, "playback-plan", work.Id, episodeIds[1])).GetProperty("resumePositionMs").GetInt64());
        Assert.AreEqual(0, (await PostAsync(world, "playback-plan", work.Id, episodeIds[1], Other)).GetProperty("resumePositionMs").GetInt64());
        Assert.AreEqual(0, (await progress.GetContinueWatchingAsync(Other)).Count);
        Assert.IsEmpty(await progress.GetCompletedThroughAsync(Other, work.Id));
        var mine = await world.Pages.GetOkAsync($"/Library/Series/{work.Id}");
        RequestToPlayAssert.Contains(Section(mine, "<section class=\"ad-hero", "</section>"), "Continue watching");
        var theirs = Section(await world.Pages.GetOkAsync($"/Library/Series/{work.Id}", profile: Other), "<section class=\"ad-hero", "</section>");
        Assert.IsFalse(theirs.Contains("Continue watching", StringComparison.Ordinal), "Another profile's progress is not mine.");
        RequestToPlayAssert.Contains(theirs, $"href=\"/Library/Watch/{work.Id}/{episodeIds[0]}\"");

        // Without the Series type the profile has no pages, no player API and no way to request; progress is never written.
        await world.Pages.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Series, MediaCapability.Hidden);
        Assert.AreEqual(HttpStatusCode.NotFound, (await world.Pages.GetAsync($"/Library/Series/{work.Id}", profile: Other)).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await world.Pages.GetAsync($"/Library/Watch/{work.Id}/{episodeIds[0]}", profile: Other)).Status);
        var hiddenTarget = new { target = new { workId = work.Id, workEpisodeId = episodeIds[0] } };
        var checkpoint = new { hiddenTarget.target, positionMs = 90_000, durationMs = 1_440_000, completed = false };
        foreach (var call in new[]
        {
            await world.Pages.SendAsync(HttpMethod.Post, $"{Api}/video/player", hiddenTarget, profile: Other),
            await world.Pages.SendAsync(HttpMethod.Post, $"{Api}/video/playback-plan", hiddenTarget, profile: Other),
            await world.Pages.SendAsync(HttpMethod.Put, $"{Api}/video/progress", checkpoint, profile: Other)
        })
        {
            Assert.AreEqual(HttpStatusCode.NotFound, call.Status, call.Body);
        }

        Assert.IsNull((await progress.GetAsync(Other, MediaProgressTarget.Episode(work.Id, episodeIds[0])))!.UpdatedAt);
        Assert.IsInstanceOfType<Microsoft.AspNetCore.Mvc.ForbidResult>(await world.PostRequestAsync("1396", "Breaking Bad", scope: "all", profileId: Other, role: AccountRole.User));
        Assert.AreEqual(0, (await world.Requests.ListAsync(null, null, openOnly: false, 10, CancellationToken.None)).Count(x => x.ExternalId == "1396"), "A profile that may not request creates no request.");
    }
}
