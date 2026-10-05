using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Operations;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>
/// An anime request follows the shared lifecycle but is searched, downloaded and imported by the monitoring pipeline: it is Completed
/// only when its episodes are in the library, the state it shows is read back from that pipeline by the shared Wanted pass, and
/// repeating any step (a retry, a second pass, a restart) never searches or grabs twice.
/// </summary>
[TestClass]
public sealed class AnimeRequestLifecycleTests
{
    private const string FrierenId = "154587";
    private const string Episode1 = "Frieren.S01E01.1080p.WEB-DL.AAC.H.264-GRP";
    private const string Episode2 = "Frieren.S01E02.1080p.WEB-DL.AAC.H.264-GRP";

    private static async Task<AnimeAcquisitionEnvironment> CreateAsync(int episodeCount = 2)
    {
        var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        environment.AniListMetadata.Add(FrierenId, "Frieren", episodeCount);
        environment.Prowlarr.Releases.Add(AnimeAcquisitionEnvironment.Release(Episode1, "e1"));
        return environment;
    }

    /// <summary>SABnzbd finishes the latest acquisition's download into a real folder; the import is not run.</summary>
    private static async Task<(OperationSnapshot Download, string Folder)> FinishDownloadAsync(AnimeAcquisitionEnvironment environment, string release)
    {
        var folder = environment.AddCompletedDownload(release, $"{release}.mkv");
        var latest = (await environment.Acquisitions.LoadAsync()).Acquisitions.MaxBy(acquisition => acquisition.CreatedAtUtc);
        return (await environment.CompleteLatestDownloadAsync(folder, latest), folder);
    }

    [TestMethod]
    public async Task ARequestStaysInProgressUntilEveryRequestedEpisodeIsImportedAndThenCompletesWithTheLibraryAddress()
    {
        await using var environment = await CreateAsync();

        // Requested: the series exists and is monitored, but nothing is downloaded, so the request is not done.
        var request = await environment.SubmitRequestAsync(FrierenId);
        var anime = await environment.Db.Anime.AsNoTracking().SingleAsync();
        var detail = $"/Library/Anime/{anime.Id}";
        Assert.AreEqual(AcquisitionRequestStatus.Approved, request.Status, request.StatusMessage);
        Assert.AreEqual(detail, request.ResultUrl);
        Assert.AreEqual(1, environment.Scheduler.QueuedRequests);
        Assert.AreEqual(0, await environment.RequestPassAsync(), "A request that is where its pipeline is stays untouched.");
        Assert.AreEqual(1, environment.Scheduler.QueuedRequests, "Following a request never queues a search.");

        // The pipeline grabs the first episode: the request is Downloading and linked to that download. Passes and a restart change nothing.
        Assert.AreEqual(1, (await environment.Scheduler.RunNowAsync(anime.Key, AnimeSearchTrigger.SearchOnAdd, CancellationToken.None)).Grabs);
        Assert.AreEqual(1, await environment.RequestPassAsync());
        var first = (await environment.Acquisitions.LoadAsync()).Acquisitions.Single();
        request = await environment.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status, request.StatusMessage);
        Assert.AreEqual(first.LatestAttempt!.OperationId, request.OperationId);
        Assert.AreEqual(0, await environment.RequestPassAsync());
        await environment.RestartAsync();
        Assert.AreEqual(0, await environment.RequestPassAsync());
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, (await environment.GetRequestAsync(request.Id)).Status);
        Assert.AreEqual(1, environment.Sabnzbd.Grabs.Count);

        // The download finished and waits for its import: Importing, still not available.
        var (download, folder) = await FinishDownloadAsync(environment, Episode1);
        await environment.RequestPassAsync();
        Assert.AreEqual(AcquisitionRequestStatus.Importing, (await environment.GetRequestAsync(request.Id)).Status);

        // The first episode is imported; the second is still missing, so the request goes back to looking and is not Completed.
        Assert.AreEqual(AnimeImportStatus.Imported, (await environment.ImportCompletedAsync(download, folder))!.Status);
        Assert.AreEqual(1, await environment.Db.MediaFiles.CountAsync(), "The first episode is a library file.");
        await environment.RequestPassAsync();
        request = await environment.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, request.Status, request.StatusMessage);

        // The second episode follows the same way and only its import completes the request, with the canonical Library address.
        environment.Prowlarr.Releases.Add(AnimeAcquisitionEnvironment.Release(Episode2, "e2"));
        Assert.AreEqual(1, (await environment.Scheduler.RunNowAsync(anime.Key, AnimeSearchTrigger.Manual, CancellationToken.None)).Grabs);
        await environment.RequestPassAsync();
        var second = (await environment.Acquisitions.LoadAsync()).Acquisitions.MaxBy(acquisition => acquisition.CreatedAtUtc)!;
        request = await environment.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status);
        Assert.AreEqual(second.LatestAttempt!.OperationId, request.OperationId);
        (download, folder) = await FinishDownloadAsync(environment, Episode2);
        await environment.RequestPassAsync();
        Assert.AreEqual(AcquisitionRequestStatus.Importing, (await environment.GetRequestAsync(request.Id)).Status, "A finished download is not an imported episode.");
        Assert.AreEqual(AnimeImportStatus.Imported, (await environment.ImportCompletedAsync(download, folder))!.Status);
        await environment.RequestPassAsync();
        request = await environment.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, request.Status, request.StatusMessage);
        Assert.AreEqual(detail, request.ResultUrl);
        Assert.AreEqual(0, await environment.RequestPassAsync(), "A completed request is not followed again.");
        Assert.AreEqual(2, environment.Sabnzbd.Grabs.Count);
    }

    [TestMethod]
    public async Task ARequestForOneEpisodeCompletesWhenThatEpisodeIsImportedWhileTheRestOfTheSeriesIsNotRequested()
    {
        await using var environment = await CreateAsync();
        var options = new AcquisitionRequestOptions { Scope = RequestScope.Episodes, Episodes = [new RequestEpisode(1, 1)] };

        var request = await environment.SubmitRequestAsync(FrierenId, options);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, request.Status, request.StatusMessage);
        var anime = await environment.Db.Anime.AsNoTracking().SingleAsync();
        await environment.Scheduler.RunNowAsync(anime.Key, AnimeSearchTrigger.SearchOnAdd, CancellationToken.None);
        var (download, folder) = await FinishDownloadAsync(environment, Episode1);
        await environment.ImportCompletedAsync(download, folder);
        await environment.RequestPassAsync();

        request = await environment.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, request.Status, request.StatusMessage);
        Assert.AreEqual(1, await environment.Db.MediaFiles.CountAsync(), "Only the requested episode was acquired.");
    }

    [TestMethod]
    public async Task ARequestForATitleThatIsCompleteInTheLibraryIsCompletedAtOnceWithoutASearch()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(episodeCount: 1);

        var request = await environment.SubmitRequestAsync(FrierenId);

        Assert.AreEqual(AcquisitionRequestStatus.Completed, request.Status, request.StatusMessage);
        Assert.AreEqual($"/Library/Anime/{environment.AnimeId}", request.ResultUrl);
        Assert.AreEqual(0, environment.Scheduler.QueuedRequests);
    }

    [TestMethod]
    public async Task RunningARequestAgainNeverSearchesOrGrabsTwiceAndKeepsTheMonitoringSettings()
    {
        await using var environment = await CreateAsync();
        var request = await environment.SubmitRequestAsync(FrierenId);
        var anime = await environment.Db.Anime.AsNoTracking().SingleAsync();
        await environment.Scheduler.RunNowAsync(anime.Key, AnimeSearchTrigger.SearchOnAdd, CancellationToken.None);
        await environment.RequestPassAsync();
        var settings = (await environment.MonitoringStateAsync()).Anime[anime.Key];

        // A second request for the same title is the open one; a retry or a background continue runs the executor again.
        Assert.AreEqual(request.Id, (await environment.SubmitRequestAsync(FrierenId)).Id);
        var continued = await environment.RunRequestAsync(request.Id, continued: true);
        await environment.Scheduler.RunNowAsync(anime.Key, AnimeSearchTrigger.Manual, CancellationToken.None);
        await environment.RestartAsync();
        await environment.Scheduler.RunNowAsync(anime.Key, AnimeSearchTrigger.Manual, CancellationToken.None);

        Assert.AreEqual(AcquisitionRequestStatus.Downloading, continued.Status, continued.StatusMessage);
        Assert.AreEqual(1, environment.Sabnzbd.Grabs.Count, "The release is submitted once, whatever the number of runs and restarts.");
        Assert.AreEqual(1, await environment.Db.Anime.CountAsync());
        var after = (await environment.MonitoringStateAsync()).Anime[anime.Key];
        Assert.AreEqual(settings.Monitored, after.Monitored);
        CollectionAssert.AreEquivalent(settings.SeasonOverrides.ToArray(), after.SeasonOverrides.ToArray());
    }

    [TestMethod]
    public async Task ADownloadTheImporterCannotPlaceFailsTheRequestWithItsReasonAndTheOwnerRetryCompletesIt()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(seasonFolders: false);
        environment.Prowlarr.Releases.Add(AnimeAcquisitionEnvironment.Release(Episode2, "e2"));
        var request = await environment.SubmitRequestAsync(FrierenId);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, request.Status, request.StatusMessage);
        await environment.Scheduler.RunNowAsync(null, AnimeSearchTrigger.PeriodicMissing, CancellationToken.None);

        // A file where the season folder must be created makes the import need the owner.
        var blocker = Path.Combine(environment.SeriesFolder, "Season 01");
        await File.WriteAllTextAsync(blocker, "not a folder");
        var (download, folder) = await FinishDownloadAsync(environment, Episode2);
        var record = (await environment.ImportCompletedAsync(download, folder))!;
        Assert.AreEqual(AnimeImportStatus.ManualRequired, record.Status);
        await environment.RequestPassAsync();

        request = await environment.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Failed, request.Status);
        Assert.AreEqual(record.Message, request.StatusMessage, "The importer's own reason is the request's reason.");
        Assert.AreEqual(0, await environment.RequestPassAsync(), "A failed request waits for the owner.");

        File.Delete(blocker);
        var manual = await environment.ImportManuallyAsync(record.Id, record.Files.Single(file => file.SourcePath.EndsWith(".mkv", StringComparison.Ordinal)).SourcePath, 1, 2);
        Assert.IsTrue(manual.Success, manual.Message);
        request = await environment.RunRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, request.Status, request.StatusMessage);
    }

    [TestMethod]
    public async Task ARequestWhoseSearchWasInterruptedIsFollowedAgainAfterTheStaleSearchTimeout()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        var store = new AcquisitionAccessStore(environment.Db);
        var draft = new AcquisitionRequestDraft(MediaAcquisitionKind.Anime, "anilist", FrierenId, "Frieren", null, null);
        var stuck = await store.CreateAsync(draft, "owner", AcquisitionRequestStatus.Searching, "owner", CancellationToken.None);

        Assert.AreEqual(0, await environment.RequestPassAsync(DateTime.UtcNow), "A fresh Searching request still has its worker.");
        Assert.AreEqual(AcquisitionRequestStatus.Searching, (await environment.GetRequestAsync(stuck.Id)).Status);
        await environment.RequestPassAsync(DateTime.UtcNow + WantedAcquisitionService.StaleSearchingAfter + TimeSpan.FromMinutes(1));

        Assert.AreEqual(AcquisitionRequestStatus.Approved, (await environment.GetRequestAsync(stuck.Id)).Status);
    }
}
