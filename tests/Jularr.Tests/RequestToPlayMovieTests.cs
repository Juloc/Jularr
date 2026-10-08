using System.Net;
using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.ClientApi;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>
/// #813 Movie gate: a TMDB title requested from Discover is searched by the Wanted pass, downloaded, imported by the shared Movie
/// importer into the default Movie root, listed in the Library, offered by the Movie detail page as Play, planned by the canonical
/// player API and resumed/completed through the canonical progress owner. Each stage consumes what the previous one produced
/// (request id, Work id, result URL, file ids); only TMDB, the indexer, SABnzbd, ffprobe and the files are faked.
/// </summary>
[TestClass]
public sealed class RequestToPlayMovieTests
{
    private const string DuneTmdb = "438631";
    private const string DuneRelease = "Dune.2021.1080p.WEB-DL.x264-GROUP";
    private const string Viewer = VideoDetailPageTestHost.Profile;
    private const string Api = "/api/client/v1";
    private static readonly WorkMediaType[] s_videoTypes = [WorkMediaType.Anime, WorkMediaType.Series, WorkMediaType.Movie];

    private static FakeTmdb Tmdb()
    {
        var tmdb = new FakeTmdb();
        tmdb.AddMovie(438631, "Dune", new DateTime(2021, 9, 15));
        tmdb.AddMovie(550, "Fight Club", new DateTime(1999, 10, 15));
        return tmdb;
    }

    /// <summary>Request, Wanted pass, download, import: the standard chain the scenarios below start from.</summary>
    private static async Task<(AcquisitionRequest Request, Work Work)> ImportDuneAsync(VideoRequestToPlayWorld world)
    {
        world.Indexer.Publish(DuneRelease);
        var request = await world.RequestAsync(DuneTmdb, "Dune");
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status, request.StatusMessage);
        await world.CompleteDownloadAsync(request, DuneRelease, $"{DuneRelease}.mkv");
        await world.WantedPassAsync();
        var completed = await world.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, completed.Status, completed.StatusMessage);
        return (completed, await world.Db.Works.AsNoTracking().SingleAsync());
    }

    private static async Task<JsonElement> PostPlanAsync(VideoRequestToPlayWorld world, Guid workId, string? profile = null)
    {
        var response = await world.Pages.SendAsync(HttpMethod.Post, $"{Api}/video/playback-plan", new { target = new { workId } }, profile: profile);
        Assert.AreEqual(HttpStatusCode.OK, response.Status, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.Clone();
    }

    private static async Task<JsonElement> PutProgressAsync(VideoRequestToPlayWorld world, Guid workId, long positionMs, bool completed, string? profile = null)
    {
        var response = await world.Pages.SendAsync(HttpMethod.Put, $"{Api}/video/progress", new { target = new { workId }, positionMs, durationMs = 1_440_000, completed }, profile: profile);
        Assert.AreEqual(HttpStatusCode.OK, response.Status, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.Clone();
    }

    [TestMethod]
    public async Task ADiscoverRequestBecomesOnePlayableMovieThroughWantedDownloadImportLibraryAndDetail()
    {
        await using var world = await VideoRequestToPlayWorld.CreateAsync(MediaAcquisitionKind.Movie, Tmdb());

        // Discover -> Request: the card posts only the TMDB identity; no release exists yet, so the approved request waits.
        var request = await world.RequestAsync(DuneTmdb, "Dune");
        Assert.AreEqual(AcquisitionRequestStatus.Approved, request.Status, request.StatusMessage);
        Assert.AreEqual(0, world.Sabnzbd.Grabs.Count);
        var work = await world.Db.Works.AsNoTracking().SingleAsync();
        Assert.AreEqual(WorkMediaType.Movie, work.MediaType);
        Assert.IsTrue(await world.Db.WorkExternalIdentities.AnyAsync(x => x.WorkId == work.Id && x.Provider == "tmdb" && x.ExternalId == DuneTmdb));
        Assert.AreEqual(1, world.Tmdb.Requests.Count, "TMDB is asked once, to resolve the card into its canonical Work.");
        RequestToPlayAssert.AdminQueueProjectsTheRequest(request);

        // Library and Detail show the request from the shared lifecycle, with nothing to play.
        var waiting = Assert.ContainsSingle((await new LibraryMediaCardQuery(world.Db).GetEntriesAsync(Viewer, s_videoTypes, CancellationToken.None)).Entries);
        Assert.AreEqual(work.Id, waiting.WorkId);
        Assert.AreEqual(LibraryAvailabilityState.Requested, LibraryBrowse.IndicatorOf(waiting));
        var pending = await world.Pages.GetOkAsync($"/Library/Movie/{work.Id}");
        RequestToPlayAssert.Contains(pending, $"href=\"/Requests/{request.Id}\"");
        Assert.IsFalse(pending.Contains($"/Library/Watch/{work.Id}", StringComparison.Ordinal), "Nothing is playable before the import.");

        // Wanted: once the search is due and a release exists, the pass searches and grabs exactly once.
        world.Indexer.Publish(DuneRelease);
        world.Clock.Advance(TimeSpan.FromHours(7));
        await world.WantedPassAsync();
        await world.WantedPassAsync();
        var grabbed = await world.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, grabbed.Status, grabbed.StatusMessage);
        Assert.AreEqual(1, world.Sabnzbd.Grabs.Count, "A second Wanted pass never submits the same request again.");
        Assert.AreEqual(DuneRelease, world.Sabnzbd.Grabs.Single().NzbName);
        Assert.AreEqual(MediaAcquisitionKind.Movie, DownloadDetails(world, grabbed).MediaKind);
        var searchesAfterGrab = world.Indexer.Searches;
        RequestToPlayAssert.AdminQueueProjectsTheRequest(grabbed);

        // Download: the consumer's live card reads the percent from the download operation, not from a timer.
        await world.ReportDownloadProgressAsync(grabbed, 40);
        var status = await RequestToPlayAssert.ConsumerStatusAsync(world.DiscoverPage(), request.Id);
        Assert.AreEqual("downloading", status.GetProperty("status").GetString());
        Assert.AreEqual(40, status.GetProperty("progress").GetInt32());
        Assert.IsFalse(status.GetProperty("done").GetBoolean());

        // A restart mid-download neither loses nor repeats the download.
        await world.RestartAsync();
        await world.WantedPassAsync();
        Assert.AreEqual(1, world.Sabnzbd.Grabs.Count);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, (await world.GetRequestAsync(request.Id)).Status);
        Assert.AreEqual(searchesAfterGrab, world.Indexer.Searches, "A request that is downloading is not searched again.");

        // Import: the finished download goes through the shared Movie importer into the default Movie root.
        await world.CompleteDownloadAsync(grabbed, DuneRelease, $"{DuneRelease}.mkv", $"{DuneRelease}.en.srt");
        await world.WantedPassAsync();
        var completed = await world.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, completed.Status, completed.StatusMessage);
        RequestToPlayAssert.AdminQueueProjectsTheRequest(completed);
        var placed = Path.Combine(world.LibraryRoot, "Dune (2021)", "Dune (2021).mkv");
        Assert.IsTrue(File.Exists(placed), "The video is placed into the default Movie root.");
        Assert.IsTrue(File.Exists(Path.Combine(world.LibraryRoot, "Dune (2021)", "Dune (2021).en.srt")), "The subtitle sidecar follows the video.");

        // One Work from provider result through request to import, and one canonical Asset/File on it.
        Assert.AreEqual(work.Id, (await world.Db.Works.AsNoTracking().SingleAsync()).Id, "The import must attach to the Work the request is about, not create a second one.");
        var asset = await world.Db.MediaAssets.AsNoTracking().SingleAsync();
        Assert.AreEqual(work.Id, asset.WorkId);
        Assert.IsNull(asset.WorkEpisodeId);
        var file = await world.Db.StoredFiles.AsNoTracking().SingleAsync();
        Assert.AreEqual(asset.Id, file.MediaAssetId);
        Assert.AreEqual(Path.GetFullPath(placed), Path.GetFullPath(file.Path));

        // The request's result destination is the canonical detail page of that Work.
        Assert.AreEqual($"/Library/Movie/{work.Id}", completed.ResultUrl);
        var done = await RequestToPlayAssert.ConsumerStatusAsync(world.DiscoverPage(), request.Id);
        Assert.AreEqual("completed", done.GetProperty("status").GetString());
        Assert.AreEqual(completed.ResultUrl, done.GetProperty("resultUrl").GetString());
        Assert.IsTrue(done.GetProperty("done").GetBoolean());

        // Library: the same Work, now complete.
        var entry = Assert.ContainsSingle((await new LibraryMediaCardQuery(world.Db).GetEntriesAsync(Viewer, s_videoTypes, CancellationToken.None)).Entries);
        Assert.AreEqual(work.Id, entry.WorkId);
        Assert.AreEqual(1, entry.PlayableUnits);
        Assert.AreEqual(completed.ResultUrl, entry.Card.Href);
        RequestToPlayAssert.NoHostPathIn(JsonSerializer.Serialize(entry), world.LibraryRoot, world.Downloads);
        Assert.IsTrue(LibraryBrowse.Matches(entry, LibraryAvailabilityState.Complete));

        // Detail: following the result URL, the page changed from request state to Play on the canonical Watch target.
        var detail = await world.Pages.GetOkAsync(completed.ResultUrl!);
        RequestToPlayAssert.Contains(detail, $"href=\"/Library/Watch/{work.Id}\"");
        RequestToPlayAssert.Contains(detail, ">Play<");
        Assert.IsFalse(detail.Contains("data-dc-card-request", StringComparison.Ordinal), "Something playable offers Play, not Request.");
        RequestToPlayAssert.NoHostPathIn(detail, world.LibraryRoot, world.Downloads);
        Assert.IsFalse(detail.Contains(".mkv", StringComparison.Ordinal));

        // Player: the plan and bootstrap resolve the imported canonical File and its probed tracks, never a path.
        var watch = await world.Pages.GetOkAsync($"/Library/Watch/{work.Id}");
        RequestToPlayAssert.Contains(watch, $"data-video-target-work=\"{work.Id}\"");
        RequestToPlayAssert.Contains(watch, "data-storage-state=\"available\"");
        RequestToPlayAssert.NoHostPathIn(watch, world.LibraryRoot, world.Downloads);
        var plan = await PostPlanAsync(world, work.Id);
        Assert.AreEqual(work.Id, plan.GetProperty("target").GetProperty("workId").GetGuid());
        Assert.AreNotEqual(JsonValueKind.Null, plan.GetProperty("delivery").ValueKind, "A reachable file has a delivery route.");
        Assert.AreEqual(0, plan.GetProperty("resumePositionMs").GetInt64());
        var bootstrap = await world.Pages.SendAsync(HttpMethod.Post, $"{Api}/video/player", new { target = new { workId = work.Id } });
        Assert.AreEqual(HttpStatusCode.OK, bootstrap.Status, bootstrap.Body);
        var player = JsonDocument.Parse(bootstrap.Body).RootElement;
        Assert.AreEqual(file.Id, player.GetProperty("media").GetProperty("mediaFileId").GetGuid());
        Assert.IsTrue(player.GetProperty("audioTracks").GetArrayLength() > 0, "The tracks come from the probed canonical file.");
        RequestToPlayAssert.NoHostPathIn(bootstrap.Body, world.LibraryRoot, world.Downloads);
        Assert.IsTrue(await world.Db.MediaTracks.AnyAsync(x => x.MediaFileId == file.Id), "Playing analysed the imported file into canonical tracks.");
        Assert.AreEqual(1, world.Tmdb.Requests.Count, "Import, Library, detail and player never go back to the provider.");
    }

    [TestMethod]
    public async Task AFinishedImportIsPlayedResumedExactlyAndCompletedThroughTheCanonicalProgressOwner()
    {
        await using var world = await VideoRequestToPlayWorld.CreateAsync(MediaAcquisitionKind.Movie, Tmdb());
        var (request, work) = await ImportDuneAsync(world);
        var progress = new VideoProgressService(world.Db);
        var detailUrl = request.ResultUrl!;
        RequestToPlayAssert.Contains(await world.Pages.GetOkAsync(detailUrl), ">Play<");

        // A checkpoint resumes exactly, in the plan, the player stage, the detail page and Continue Watching.
        var saved = await PutProgressAsync(world, work.Id, 600_000, completed: false);
        Assert.AreEqual(600_000, saved.GetProperty("positionMs").GetInt64());
        Assert.IsFalse(saved.GetProperty("isCompleted").GetBoolean());
        Assert.AreEqual(600_000, (await PostPlanAsync(world, work.Id)).GetProperty("resumePositionMs").GetInt64());
        RequestToPlayAssert.Contains(await world.Pages.GetOkAsync($"/Library/Watch/{work.Id}"), "data-resume-seconds=\"600\"");
        RequestToPlayAssert.Contains(await world.Pages.GetOkAsync(detailUrl), "Continue watching");
        var resume = Assert.ContainsSingle(await progress.GetContinueWatchingAsync(Viewer));
        Assert.AreEqual(work.Id, resume.WorkId);
        Assert.AreEqual(VideoContinueWatchingKind.Resume, resume.Kind);
        Assert.AreEqual(600_000, resume.ResumePositionMs);

        // A seek or pause near the end is only a resume point; the client's declared completion is what finishes the movie.
        var seeked = await PutProgressAsync(world, work.Id, 1_420_000, completed: false);
        Assert.IsFalse(seeked.GetProperty("isCompleted").GetBoolean(), "Seeking past 95% must not complete the movie.");
        Assert.AreEqual(1_420_000, (await PostPlanAsync(world, work.Id)).GetProperty("resumePositionMs").GetInt64());

        var finished = await PutProgressAsync(world, work.Id, 1_440_000, completed: true);
        Assert.IsTrue(finished.GetProperty("isCompleted").GetBoolean());
        RequestToPlayAssert.Contains(await world.Pages.GetOkAsync(detailUrl), "Watch again");
        Assert.AreEqual(0, (await progress.GetContinueWatchingAsync(Viewer)).Count, "A finished movie has no next item to continue with.");
        var history = Assert.ContainsSingle(await progress.GetHistoryAsync(Viewer));
        Assert.AreEqual(work.Id, history.WorkId);
        Assert.IsTrue(history.ReachedEnd);

        // Progress is the profile's own: another profile still sees an unwatched, playable movie.
        var other = await world.Pages.GetOkAsync(detailUrl, profile: "someone-else");
        RequestToPlayAssert.Contains(other, ">Play<");
        Assert.IsFalse(other.Contains("Watch again", StringComparison.Ordinal));
        Assert.AreEqual(0, (await PostPlanAsync(world, work.Id, profile: "someone-else")).GetProperty("resumePositionMs").GetInt64());
        Assert.AreEqual(0, (await progress.GetContinueWatchingAsync("someone-else")).Count);
    }

    [TestMethod]
    public async Task RepeatedWantedPassesAndRestartsNeverDuplicateTheDownloadTheWorkOrTheFile()
    {
        await using var world = await VideoRequestToPlayWorld.CreateAsync(MediaAcquisitionKind.Movie, Tmdb());
        world.Indexer.Publish(DuneRelease);
        var request = await world.RequestAsync(DuneTmdb, "Dune");
        await world.WantedPassAsync();
        await world.RestartAsync();
        await world.WantedPassAsync();
        Assert.AreEqual(1, world.Sabnzbd.Grabs.Count, "Neither a Wanted pass nor a restart submits the request's release again.");
        Assert.AreEqual(request.Id, Assert.ContainsSingle(await world.Requests.ListAsync(null, null, openOnly: false, 10, CancellationToken.None)).Id);

        // The same TMDB title requested again joins the open request instead of creating another one.
        var again = await world.RequestAsync(DuneTmdb, "Dune");
        Assert.AreEqual(request.Id, again.Id);
        Assert.AreEqual(1, await world.Db.Works.CountAsync());

        // The importer runs, the process restarts, and the next pass finds the request finished instead of importing again.
        await world.CompleteDownloadAsync(again, DuneRelease, $"{DuneRelease}.mkv");
        await world.WantedPassAsync();
        await world.RestartAsync();
        await world.WantedPassAsync();
        await world.WantedPassAsync();

        Assert.AreEqual(AcquisitionRequestStatus.Completed, (await world.GetRequestAsync(request.Id)).Status);
        Assert.AreEqual(1, await world.Db.Works.CountAsync());
        Assert.AreEqual(1, await world.Db.Movies.CountAsync());
        Assert.AreEqual(1, await world.Db.MediaAssets.CountAsync());
        Assert.AreEqual(1, await world.Db.StoredFiles.CountAsync());
        Assert.AreEqual(1, Directory.GetFiles(world.LibraryRoot, "*.mkv", SearchOption.AllDirectories).Length);
        Assert.AreEqual(1, world.Sabnzbd.Grabs.Count);
    }

    [TestMethod]
    public async Task AFailedDownloadStaysRecoverableAndTheNextReleaseStillReachesPlay()
    {
        await using var world = await VideoRequestToPlayWorld.CreateAsync(MediaAcquisitionKind.Movie, Tmdb());
        const string Second = "Dune.2021.1080p.BluRay.x264-SECOND";
        world.Indexer.Publish(DuneRelease);
        world.Indexer.Publish(Second);
        var request = await world.RequestAsync(DuneTmdb, "Dune");
        var first = world.Sabnzbd.Grabs.Single().NzbName;

        await world.Operations.MarkFailedAsync(request.OperationId!.Value, "Out of retention");
        await world.RestartAsync();
        await world.WantedPassAsync();

        var retried = await world.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, retried.Status, retried.StatusMessage);
        Assert.AreEqual(2, world.Sabnzbd.Grabs.Count);
        Assert.AreNotEqual(first, world.Sabnzbd.Grabs[1].NzbName, "The retry takes the next untried release.");

        await world.CompleteDownloadAsync(retried, Second, $"{Second}.mkv");
        await world.WantedPassAsync();
        var completed = await world.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, completed.Status, completed.StatusMessage);
        RequestToPlayAssert.Contains(await world.Pages.GetOkAsync(completed.ResultUrl!), $"href=\"/Library/Watch/{(await world.Db.Works.SingleAsync()).Id}\"");
    }

    [TestMethod]
    public async Task ACompletedDownloadWaitsForStorageAndImportsOnceStorageIsBack()
    {
        await using var world = await VideoRequestToPlayWorld.CreateAsync(MediaAcquisitionKind.Movie, Tmdb());
        world.Indexer.Publish(DuneRelease);
        var request = await world.RequestAsync(DuneTmdb, "Dune");
        var folder = await world.CompleteDownloadAsync(request, DuneRelease, $"{DuneRelease}.mkv");
        Directory.Move(folder, folder + "-away");

        await world.WantedPassAsync();
        var waiting = await world.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Importing, waiting.Status, "A missing download folder is not the release's fault.");
        Assert.AreEqual(0, await world.Db.StoredFiles.CountAsync());
        Assert.AreEqual(1, world.Sabnzbd.Grabs.Count, "No other release is grabbed while the files are unreachable.");

        Directory.Move(folder + "-away", folder);
        await world.RestartAsync();
        await world.WantedPassAsync();

        Assert.AreEqual(AcquisitionRequestStatus.Completed, (await world.GetRequestAsync(request.Id)).Status);
        Assert.AreEqual(1, await world.Db.StoredFiles.CountAsync());
    }

    [TestMethod]
    public async Task AManuallyApprovedRequestRunsThroughTheSameExecutorAndEndsAtTheSameResult()
    {
        await using var world = await VideoRequestToPlayWorld.CreateAsync(MediaAcquisitionKind.Movie, Tmdb());
        await world.Pages.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Movie, MediaCapability.Request);
        world.Indexer.Publish(DuneRelease);

        var posted = await world.PostRequestAsync(DuneTmdb, "Dune", profileId: "alice", role: AccountRole.User);
        var pending = ((DiscoverRequestResultView)((PartialViewResult)posted).ViewData.Model!).Request;

        Assert.AreEqual(AcquisitionRequestStatus.Pending, pending.Status, "A requester without Instant waits for the owner.");
        Assert.AreEqual(0, world.Sabnzbd.Grabs.Count);
        Assert.AreEqual(0, world.Indexer.Searches, "Nothing is searched before the decision.");
        RequestToPlayAssert.AdminQueueProjectsTheRequest(pending);
        await world.WantedPassAsync();
        Assert.AreEqual(0, world.Sabnzbd.Grabs.Count, "The Wanted pass does not pick up a request nobody approved.");

        var approved = await world.ApproveAsync(pending.Id);

        Assert.AreEqual(AcquisitionRequestStatus.Downloading, approved.Status, approved.StatusMessage);
        Assert.AreEqual(DuneRelease, Assert.ContainsSingle(world.Sabnzbd.Grabs).NzbName);
        await world.CompleteDownloadAsync(approved, DuneRelease, $"{DuneRelease}.mkv");
        await world.WantedPassAsync();
        var completed = await world.GetRequestAsync(pending.Id);
        var work = await world.Db.Works.AsNoTracking().SingleAsync();
        Assert.AreEqual(AcquisitionRequestStatus.Completed, completed.Status, completed.StatusMessage);
        Assert.AreEqual($"/Library/Movie/{work.Id}", completed.ResultUrl, "The same result as an auto-approved request.");
        Assert.AreEqual("alice", completed.RequestedByProfileId);
        RequestToPlayAssert.Contains(await world.Pages.GetOkAsync(completed.ResultUrl!), $"href=\"/Library/Watch/{work.Id}\"");
    }

    [TestMethod]
    public async Task ThePlayerAndDetailAreRefusedServerSideForAProfileWithoutTheMovieTypeAndItsRequestsAreDenied()
    {
        await using var world = await VideoRequestToPlayWorld.CreateAsync(MediaAcquisitionKind.Movie, Tmdb());
        var (request, work) = await ImportDuneAsync(world);
        await world.Pages.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Movie, MediaCapability.Hidden);

        Assert.AreEqual(HttpStatusCode.NotFound, (await world.Pages.GetAsync(request.ResultUrl!)).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await world.Pages.GetAsync($"/Library/Watch/{work.Id}")).Status);
        foreach (var call in new[]
        {
            await world.Pages.SendAsync(HttpMethod.Post, $"{Api}/video/player", new { target = new { workId = work.Id } }),
            await world.Pages.SendAsync(HttpMethod.Post, $"{Api}/video/playback-plan", new { target = new { workId = work.Id } }),
            await world.Pages.SendAsync(HttpMethod.Put, $"{Api}/video/progress", new { target = new { workId = work.Id }, positionMs = 90_000, durationMs = 1_440_000, completed = false })
        })
        {
            Assert.AreEqual(HttpStatusCode.NotFound, call.Status, call.Body);
        }

        Assert.IsNull((await new VideoProgressService(world.Db).GetAsync(Viewer, MediaProgressTarget.Movie(work.Id)))!.UpdatedAt, "A hidden type's progress is never written.");
        Assert.AreEqual(0, (await new LibraryMediaCardQuery(world.Db).GetEntriesAsync(Viewer, [], CancellationToken.None)).Entries.Count, "No media type in scope reads no titles.");
        Assert.IsNotInstanceOfType<PartialViewResult>(await world.PostRequestAsync("550", "Fight Club", profileId: Viewer, role: AccountRole.User));
        Assert.AreEqual(0, (await world.Requests.ListAsync(null, null, openOnly: false, 10, CancellationToken.None)).Count(x => x.ExternalId == "550"), "A profile that may not request creates no request.");

        // Browsing without the right to request keeps the page and the player but offers no acquisition.
        await world.Pages.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Movie, MediaCapability.Browse);
        Assert.AreEqual(HttpStatusCode.OK, (await world.Pages.GetAsync(request.ResultUrl!)).Status);
        Assert.IsNotInstanceOfType<PartialViewResult>(await world.PostRequestAsync("550", "Fight Club", profileId: Viewer, role: AccountRole.User));
        Assert.AreEqual(HttpStatusCode.OK, (await world.Pages.GetAsync(request.ResultUrl!, asOwner: true)).Status, "The owner is unrestricted.");
    }

    [TestMethod]
    public async Task OfflineStorageKeepsTheDetailPageAndMakesPlayUnavailableWithARetryableState()
    {
        await using var world = await VideoRequestToPlayWorld.CreateAsync(MediaAcquisitionKind.Movie, Tmdb());
        var (request, work) = await ImportDuneAsync(world);
        await world.AnalyzeLibraryFilesAsync();
        Directory.Move(world.LibraryRoot, world.LibraryRoot + "-offline");

        var detail = await world.Pages.GetOkAsync(request.ResultUrl!);
        RequestToPlayAssert.Contains(detail, ">Play<", "The detail page is derived from the database and renders while storage is away.");
        var watch = await world.Pages.GetOkAsync($"/Library/Watch/{work.Id}");
        Assert.AreEqual(ClientApiMappings.AvailabilityStateName(StorageAvailabilityState.Offline), System.Text.RegularExpressions.Regex.Match(watch, "data-storage-state=\"([^\"]*)\"").Groups[1].Value);
        var plan = await PostPlanAsync(world, work.Id);
        Assert.AreEqual(JsonValueKind.Null, plan.GetProperty("delivery").ValueKind, "No delivery is promised for unreachable storage.");
        var availability = plan.GetProperty("availability");
        Assert.AreEqual(ClientApiMappings.AvailabilityStateName(StorageAvailabilityState.Offline), availability.GetProperty("state").GetString());
        Assert.IsTrue(availability.GetProperty("retryable").GetBoolean());
        Assert.AreEqual(work.Id, (await world.Db.Works.AsNoTracking().SingleAsync()).Id);
        Assert.AreEqual(1, await world.Db.StoredFiles.CountAsync(), "Offline storage never deletes the canonical file record.");

        var saved = await PutProgressAsync(world, work.Id, 120_000, completed: false);
        Assert.AreEqual(120_000, saved.GetProperty("positionMs").GetInt64(), "Progress is the profile's own data and keeps working.");
    }

    private static DownloadOperationDetails DownloadDetails(VideoRequestToPlayWorld world, AcquisitionRequest request)
    {
        var operation = world.Operations.GetAsync(request.OperationId!.Value).GetAwaiter().GetResult()!;
        Assert.IsTrue(DownloadOperationDetails.TryParse(operation.Details, out var details));
        return details!;
    }
}
