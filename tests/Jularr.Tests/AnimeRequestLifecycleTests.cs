using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Events;
using Jularr.Web.Features.Operations;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>
/// An anime request runs the shared request lifecycle: it is Completed only when its episodes are in the library, the shared Wanted pass
/// follows its download and imports it, and repeating any step (a retry, a second pass, a restart) never searches or grabs twice.
/// </summary>
[TestClass]
public sealed class AnimeRequestLifecycleTests
{
    private const string FrierenId = "154587";
    private const string Episode1 = "Frieren.S01E01.1080p.WEB-DL.AAC.H.264-GRP";
    private const string Episode2 = "Frieren.S01E02.1080p.WEB-DL.AAC.H.264-GRP";

    /// <summary>A finished series: every one of its episodes has aired, so every one is part of the request.</summary>
    private static async Task<AnimeAcquisitionEnvironment> CreateAsync(int episodeCount = 2)
    {
        var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        environment.AniListMetadata.Add(FrierenId, "Frieren", episodeCount, status: "FINISHED");
        environment.Prowlarr.Releases.Add(AnimeAcquisitionEnvironment.Release(Episode1, "e1"));
        return environment;
    }

    /// <summary>SABnzbd finishes the latest anime download into a real folder; the import is left to the Wanted pass.</summary>
    private static async Task<OperationSnapshot> FinishDownloadAsync(AnimeAcquisitionEnvironment environment, string release) =>
        await environment.CompleteLatestDownloadAsync(environment.AddCompletedDownload(release, $"{release}.mkv"));

    [TestMethod]
    public async Task ARequestStaysInProgressUntilEveryRequestedEpisodeIsImportedAndThenCompletesWithTheLibraryAddress()
    {
        await using var environment = await CreateAsync();

        // The series exists and is monitored; the first episode is searched and grabbed at once, and passes and a restart change nothing.
        var request = await environment.SubmitRequestAsync(FrierenId);
        var anime = await environment.Db.Anime.AsNoTracking().SingleAsync();
        var detail = $"/Library/Anime/{anime.Id}";
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status, request.StatusMessage);
        Assert.AreEqual(detail, request.ResultUrl);
        Assert.IsNotNull(request.OperationId);
        Assert.AreEqual(0, await environment.RequestPassAsync(), "A download in flight is left alone.");
        await environment.RestartAsync();
        Assert.AreEqual(0, await environment.RequestPassAsync());
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, (await environment.GetRequestAsync(request.Id)).Status);
        Assert.AreEqual(1, environment.Sabnzbd.Grabs.Count);

        // The pass imports the finished download; the second episode is still missing, so the request goes back to looking and is not Completed.
        await FinishDownloadAsync(environment, Episode1);
        await environment.RequestPassAsync();
        Assert.AreEqual(1, await environment.Db.MediaFiles.CountAsync(), "The first episode is a library file.");
        request = await environment.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, request.Status, request.StatusMessage);

        // The second episode follows the same way and only its import completes the request, with the canonical Library address.
        environment.Prowlarr.Releases.Add(AnimeAcquisitionEnvironment.Release(Episode2, "e2"));
        request = (await environment.SearchNowAsync())!;
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status, request.StatusMessage);
        await FinishDownloadAsync(environment, Episode2);
        await environment.RequestPassAsync();
        request = await environment.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, request.Status, request.StatusMessage);
        Assert.AreEqual(detail, request.ResultUrl);
        Assert.AreEqual(2, await environment.Db.MediaFiles.CountAsync());
        Assert.AreEqual(0, await environment.RequestPassAsync(), "A completed request is not followed again.");
        Assert.AreEqual(2, environment.Sabnzbd.Grabs.Count);
    }

    [TestMethod]
    public async Task ARequestForOneEpisodeCompletesWhenThatEpisodeIsImportedWhileTheRestOfTheSeriesIsNotRequested()
    {
        await using var environment = await CreateAsync();
        var options = new AcquisitionRequestOptions { Scope = RequestScope.Episodes, Episodes = [new RequestEpisode(1, 1)] };

        var request = await environment.SubmitRequestAsync(FrierenId, options);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status, request.StatusMessage);
        await FinishDownloadAsync(environment, Episode1);
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
        Assert.AreEqual(0, environment.Prowlarr.Queries.Count);
    }

    [TestMethod]
    public async Task RunningARequestAgainNeverSearchesOrGrabsTwiceAndKeepsTheMonitoringSettings()
    {
        await using var environment = await CreateAsync();
        var request = await environment.SubmitRequestAsync(FrierenId);
        var anime = await environment.Db.Anime.AsNoTracking().SingleAsync();
        await environment.RequestPassAsync();
        var before = await environment.AnimeMonitoringAsync(anime.Key);

        // A second request for the same title is the open one; a retry or a background continue runs the executor again.
        Assert.AreEqual(request.Id, (await environment.SubmitRequestAsync(FrierenId)).Id);
        var continued = await environment.RunRequestAsync(request.Id, continued: true);
        await environment.Scheduler.RunNowAsync(anime.Key, AnimeSearchTrigger.Manual, CancellationToken.None);
        await environment.RestartAsync();
        await environment.SearchNowAsync();

        Assert.AreEqual(AcquisitionRequestStatus.Downloading, continued.Status, continued.StatusMessage);
        Assert.AreEqual(1, environment.Sabnzbd.Grabs.Count, "The release is submitted once, whatever the number of runs and restarts.");
        Assert.AreEqual(1, await environment.Db.Anime.CountAsync());
        var after = await environment.AnimeMonitoringAsync(anime.Key);
        Assert.AreEqual(before.IsWorkMonitored, after.IsWorkMonitored);
        Assert.AreEqual(before.HasNodeDecisions, after.HasNodeDecisions);
    }

    [TestMethod]
    public async Task ADownloadTheImporterCannotPlaceFailsTheRequestWithItsReasonAndResolvingItCompletesTheRequest()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(seasonFolders: false, status: "FINISHED");
        environment.Prowlarr.Releases.Add(AnimeAcquisitionEnvironment.Release(Episode2, "e2"));
        var request = await environment.SubmitRequestAsync(FrierenId);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status, request.StatusMessage);

        // A file where the season folder must be created makes the import need the owner.
        var blocker = Path.Combine(environment.SeriesFolder, "Season 01");
        await File.WriteAllTextAsync(blocker, "not a folder");
        var download = await FinishDownloadAsync(environment, Episode2);
        await environment.RequestPassAsync();
        var record = (await environment.Imports.FindByDownloadAsync(download.Id, CancellationToken.None))!;
        Assert.AreEqual(AnimeImportStatus.ManualRequired, record.Status);

        request = await environment.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Failed, request.Status);
        Assert.AreEqual(record.Message, request.StatusMessage, "The importer's own reason is the request's reason.");
        Assert.AreEqual(0, await environment.RequestPassAsync(), "A failed request waits for the owner.");

        // The owner resolves the import (here as the Imports page does) and runs the request again: it is complete without another search.
        File.Delete(blocker);
        var manual = await environment.ImportManuallyAsync(record.Id, record.Files.Single(file => file.SourcePath.EndsWith(".mkv", StringComparison.Ordinal)).SourcePath, 1, 2);
        Assert.IsTrue(manual.Success, manual.Message);
        request = await environment.RunRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, request.Status, request.StatusMessage);
        Assert.AreEqual(1, environment.Sabnzbd.Grabs.Count);
    }

    [TestMethod]
    public async Task ARequestWhoseSearchWasInterruptedIsFollowedAgainAfterTheStaleSearchTimeout()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(status: "FINISHED");
        var store = new AcquisitionAccessStore(environment.Db);
        var draft = new AcquisitionRequestDraft(MediaAcquisitionKind.Anime, "anilist", FrierenId, "Frieren", null, null);
        var stuck = await store.CreateAsync(draft, "owner", AcquisitionRequestStatus.Searching, "owner", CancellationToken.None);

        Assert.AreEqual(0, await environment.RequestPassAsync(DateTime.UtcNow), "A fresh Searching request still has its worker.");
        Assert.AreEqual(AcquisitionRequestStatus.Searching, (await environment.GetRequestAsync(stuck.Id)).Status);
        await environment.RequestPassAsync(DateTime.UtcNow + WantedAcquisitionService.StaleSearchingAfter + TimeSpan.FromMinutes(1));

        Assert.AreEqual(AcquisitionRequestStatus.Approved, (await environment.GetRequestAsync(stuck.Id)).Status);
    }

    private static int ReleaseNotices(AnimeAcquisitionEnvironment environment) =>
        environment.PublishedEvents.Count(published => published.Category == JularrEventCategory.ReleaseAvailable);

    [TestMethod]
    public async Task TheRequesterIsToldOncePerDownloadAndNotWhenTheSameDownloadIsOnlySeenAgain()
    {
        await using var environment = await CreateAsync();
        var request = await environment.SubmitRequestAsync(FrierenId);
        Assert.AreEqual(1, ReleaseNotices(environment), "The first download starts: one notice.");

        // However often the request is followed, retried or continued while it runs, that is not a new release.
        await environment.RequestPassAsync();
        await environment.RequestPassAsync();
        await environment.RunRequestAsync(request.Id, continued: true);
        await environment.RequestPassAsync();
        Assert.AreEqual(1, ReleaseNotices(environment));

        await FinishDownloadAsync(environment, Episode1);
        await environment.RequestPassAsync();
        Assert.AreEqual(AcquisitionRequestStatus.Approved, (await environment.GetRequestAsync(request.Id)).Status);
        Assert.AreEqual(1, ReleaseNotices(environment));

        // The next episode's download is a new release.
        environment.Prowlarr.Releases.Add(AnimeAcquisitionEnvironment.Release(Episode2, "e2"));
        request = (await environment.SearchNowAsync())!;
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status);
        Assert.AreEqual(2, ReleaseNotices(environment));
    }

    [TestMethod]
    public async Task ASeriesThatSonarrManagesStaysApprovedWithoutASearchAndCompletesWhenItsFilesAreInTheLibrary()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(mode: AnimeManagementMode.ReadOnlyCoexistence, status: "FINISHED");

        var request = await environment.SubmitRequestAsync(FrierenId);

        Assert.AreEqual(AcquisitionRequestStatus.Approved, request.Status);
        StringAssert.Contains(request.StatusMessage, "Sonarr manages this series");
        await environment.RequestPassAsync();
        Assert.AreEqual(0, environment.Prowlarr.Queries.Count, "A read-only series is never searched.");

        environment.AddLibraryFile("Frieren", "Season 01", "Frieren - S01E02 - Episode 2.mkv");
        await environment.ScanAsync();
        await environment.RequestPassAsync();

        Assert.AreEqual(AcquisitionRequestStatus.Completed, (await environment.GetRequestAsync(request.Id)).Status);
    }

    [TestMethod]
    public async Task AnApprovedRequestNobodyExecutedYetIsExecutedByTheWantedPass()
    {
        await using var environment = await CreateAsync();
        var draft = new AcquisitionRequestDraft(MediaAcquisitionKind.Anime, "anilist", FrierenId, "Frieren", null, null);
        var approved = await new AcquisitionAccessStore(environment.Db).CreateAsync(draft, "owner", AcquisitionRequestStatus.Approved, "owner", CancellationToken.None);
        Assert.AreEqual(0, await environment.Db.Anime.CountAsync());

        await environment.RequestPassAsync();

        Assert.AreEqual(1, await environment.Db.Anime.CountAsync(), "The approval was executed: the series exists and is searched.");
        var request = await environment.GetRequestAsync(approved.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status, request.StatusMessage);
        await environment.RequestPassAsync();
        Assert.AreEqual(1, environment.Sabnzbd.Grabs.Count, "The request is not executed again.");
    }

    [TestMethod]
    public async Task ARequestTheExecutorFailsForIsNotOverwrittenByTheWantedPass()
    {
        await using var environment = await CreateAsync();
        await environment.Db.LibraryRoots.ExecuteUpdateAsync(setters => setters.SetProperty(root => root.IsEnabled, false));
        var draft = new AcquisitionRequestDraft(MediaAcquisitionKind.Anime, "anilist", FrierenId, "Frieren", null, null);
        var request = await new AcquisitionAccessStore(environment.Db).CreateAsync(draft, "owner", AcquisitionRequestStatus.Approved, "owner", CancellationToken.None);

        var failed = await environment.RunRequestAsync(request.Id);

        Assert.AreEqual(AcquisitionRequestStatus.Failed, failed.Status);
        Assert.AreEqual(0, await environment.RequestPassAsync(), "The reason the executor gave stays.");
        StringAssert.Contains((await environment.GetRequestAsync(request.Id)).StatusMessage, "Admin → System");
    }

    [TestMethod]
    public async Task ApprovingAFailedRequestKeepsItsDownloadLinkWhenTheExecutorThrows()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(status: "FINISHED");
        var draft = new AcquisitionRequestDraft(MediaAcquisitionKind.Anime, "anilist", FrierenId, "Frieren", null, null);
        var store = new AcquisitionAccessStore(environment.Db);
        var request = await store.CreateAsync(draft, "owner", AcquisitionRequestStatus.Failed, "owner", CancellationToken.None);
        var download = Guid.NewGuid();
        await store.UpdateStatusAsync(request.Id, AcquisitionRequestStatus.Failed, "The download needs a decision.", download, null, null, CancellationToken.None);
        await environment.CorruptMonitoringStateAsync();

        var retried = await environment.RunRequestAsync(request.Id);

        Assert.AreEqual(AcquisitionRequestStatus.Failed, retried.Status);
        Assert.AreEqual(download, retried.OperationId, "The monitoring state could not be read this time; the owner's pending decision is still there.");
    }
}
