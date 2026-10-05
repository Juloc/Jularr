using System.Data.Common;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Tests;

/// <summary>
/// The shared Admin surfaces for Movie and TV (#814): Requests actions, the Wanted rows, monitoring and the Activity links, all
/// driven through the same services the Admin pages call, against a real database and the shared Wanted pass.
/// </summary>
[TestClass]
public sealed class VideoAdminSurfaceTests
{
    private const string DuneRelease = "Dune.2021.1080p.WEB-DL.x264-GROUP";
    private const string SeveranceFirst = "Severance.S01E01.1080p.WEB-DL.x264-GROUP";
    private const string SeveranceSecond = "Severance.S01E02.1080p.WEB-DL.x264-GROUP";

    private static Task<VideoAcquisitionTestHost> MovieHostAsync(string? secondRelease = null) =>
        VideoAcquisitionTestHost.CreateAsync(MediaAcquisitionKind.Movie, "Dune", 2021, "438631", DuneRelease, secondRelease);

    private static Task<VideoAcquisitionTestHost> SeriesHostAsync(string first = SeveranceFirst, string? second = SeveranceSecond, bool episodes = true) =>
        VideoAcquisitionTestHost.CreateAsync(MediaAcquisitionKind.Tv, "Severance", 2022, "95396", first, second, addEpisode: episodes, addSecondEpisode: episodes);

    [TestMethod]
    public async Task MovieMonitoringOffEndsTheRequestAndOnReopensTheSameRequestWithItsRequester()
    {
        await using var host = await MovieHostAsync();
        var monitoring = host.Get<VideoMonitoringService>();
        var request = await host.Requests.CreateAsync(
            new AcquisitionRequestDraft(MediaAcquisitionKind.Movie, "tmdb", host.TmdbId, "Dune", null, null),
            "the-requester",
            AcquisitionRequestStatus.Approved,
            "owner",
            CancellationToken.None);

        Assert.AreEqual(VideoMonitoringOutcome.Saved, await monitoring.SetMovieMonitoredAsync(host.Work.Id, false, CancellationToken.None));
        Assert.AreEqual(VideoMonitoringOutcome.Unchanged, await monitoring.SetMovieMonitoredAsync(host.Work.Id, false, CancellationToken.None), "Nothing is acquiring it any more.");
        await host.ProcessAsync(DateTime.UtcNow);

        var stopped = await host.GetAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Rejected, stopped.Status, "Nothing was acquired, so the requester must not see it as available.");
        StringAssert.Contains(stopped.StatusMessage ?? "", "Monitoring was turned off");
        Assert.AreEqual(0, host.Environment.Client.Grabs.Count);

        Assert.AreEqual(VideoMonitoringOutcome.Saved, await monitoring.SetMovieMonitoredAsync(host.Work.Id, true, CancellationToken.None));
        Assert.AreEqual(VideoMonitoringOutcome.Saved, await monitoring.SetMovieMonitoredAsync(host.Work.Id, true, CancellationToken.None));
        await host.ProcessAsync(DateTime.UtcNow.AddMinutes(10));

        var reopened = await host.GetAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, reopened.Status);
        Assert.AreEqual("the-requester", reopened.RequestedByProfileId, "The original requester stays; the request is not recreated for the admin.");
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count);
        Assert.AreEqual(1, (await host.Requests.ListAllAsync(100, CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task ANewUserRequestForAMovieSwitchedOffSearchesInsteadOfIdling()
    {
        await using var host = await MovieHostAsync();
        var monitoring = host.Get<VideoMonitoringService>();
        var first = await host.CreateApprovedAsync();
        await monitoring.SetMovieMonitoredAsync(host.Work.Id, false, CancellationToken.None);
        Assert.AreEqual(AcquisitionRequestStatus.Rejected, (await host.GetAsync(first.Id)).Status);

        var second = await host.StartAsync();

        Assert.AreNotEqual(first.Id, second.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, second.Status, "An explicit request is monitored from the start.");
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count);
    }

    [TestMethod]
    public async Task MonitoringAMovieThatHasNoRequestCreatesItThroughTheSharedRequestPath()
    {
        await using var host = await MovieHostAsync();
        var monitoring = host.Get<VideoMonitoringService>();

        Assert.AreEqual(VideoMonitoringOutcome.NotFound, await monitoring.SetMovieMonitoredAsync(Guid.NewGuid(), true, CancellationToken.None));
        Assert.AreEqual(VideoMonitoringOutcome.Saved, await monitoring.SetMovieMonitoredAsync(host.Work.Id, true, CancellationToken.None));
        Assert.AreEqual(VideoMonitoringOutcome.Saved, await monitoring.SetMovieMonitoredAsync(host.Work.Id, true, CancellationToken.None));

        var requests = await host.Requests.ListAllAsync(100, CancellationToken.None);
        Assert.AreEqual(1, requests.Count);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, requests[0].Status);
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count);
        Assert.AreEqual($"/Library/Movie/{host.Work.Id:D}", requests[0].ResultUrl);
    }

    [TestMethod]
    public async Task MonitoringAMovieThatIsInTheLibraryAlreadyOpensNoRequest()
    {
        await using var host = await MovieHostAsync();
        await host.AttachFileAsync(null);

        Assert.AreEqual(VideoMonitoringOutcome.InLibrary, await host.Get<VideoMonitoringService>().SetMovieMonitoredAsync(host.Work.Id, true, CancellationToken.None));

        Assert.AreEqual(0, (await host.Requests.ListAllAsync(100, CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task SeriesCustomSelectionMakesTheNextWantedPassSearchOnlyTheChosenEpisode()
    {
        await using var host = await SeriesHostAsync();
        var monitoring = host.Get<VideoMonitoringService>();
        var request = await host.CreateApprovedAsync();

        Assert.AreEqual(VideoMonitoringOutcome.Saved, await monitoring.SetSeriesAsync(host.Work.Id, "custom", [], [host.SecondEpisodeId!.Value], false, CancellationToken.None));
        Assert.AreEqual(VideoMonitoringOutcome.Saved, await monitoring.SetSeriesAsync(host.Work.Id, "custom", [], [host.SecondEpisodeId!.Value], false, CancellationToken.None));
        await host.ProcessAsync(DateTime.UtcNow);

        var downloading = await host.GetAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, downloading.Status);
        Assert.AreEqual(host.SecondEpisodeId, VideoRequestPayload.Parse(downloading.PayloadJson)!.ActiveWorkEpisodeId);
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count);
        Assert.AreEqual(1, (await host.Requests.ListAllAsync(100, CancellationToken.None)).Count);

        var detail = (await host.Get<AdminVideoMediaService>().LoadAsync(MediaAcquisitionKind.Tv, host.Work.Id, CancellationToken.None))!;
        Assert.AreEqual("custom", detail.Monitoring.Scope);
        Assert.IsFalse(detail.Episodes.Single(episode => episode.Id == host.EpisodeId).Monitored);
        Assert.IsTrue(detail.Episodes.Single(episode => episode.Id == host.SecondEpisodeId).Monitored);
        Assert.AreEqual(AdminMediaState.Downloading, detail.Episodes.Single(episode => episode.Id == host.SecondEpisodeId).State);
    }

    [TestMethod]
    public async Task SeriesAllAndOffAndFutureOnlyFeedTheSameWantedPass()
    {
        await using var off = await SeriesHostAsync();
        var offRequest = await off.CreateApprovedAsync();
        Assert.AreEqual(VideoMonitoringOutcome.Saved, await off.Get<VideoMonitoringService>().SetSeriesAsync(off.Work.Id, VideoMonitoringService.OffScope, [], [], false, CancellationToken.None));
        await off.ProcessAsync(DateTime.UtcNow);
        var stopped = await off.GetAsync(offRequest.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Rejected, stopped.Status);
        StringAssert.Contains(stopped.StatusMessage ?? "", "Monitoring was turned off");
        Assert.AreEqual(0, off.Environment.Client.Grabs.Count);

        await using var future = await SeriesHostAsync();
        await future.AddEpisodeAsync(1, 3, DateTime.UtcNow.AddDays(2));
        var futureRequest = await future.CreateApprovedAsync();
        Assert.AreEqual(VideoMonitoringOutcome.Saved, await future.Get<VideoMonitoringService>().SetSeriesAsync(future.Work.Id, "future", [], [], false, CancellationToken.None));
        await future.ProcessAsync(DateTime.UtcNow);
        var waiting = await future.GetAsync(futureRequest.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, waiting.Status);
        Assert.AreEqual(0, future.Environment.Client.Grabs.Count, "Future only does not search episodes that already aired.");
        Assert.IsNotNull(VideoRequestPayload.Parse(waiting.PayloadJson)!.NextSearchUtc);

        await using var all = await SeriesHostAsync();
        var allRequest = await all.CreateApprovedAsync(new VideoRequestPayload(all.Work.Id, all.Work.CanonicalTitle, all.Work.Year, VideoRequestScope.Custom, [all.SecondEpisodeId!.Value], MonitorFuture: false));
        Assert.AreEqual(VideoMonitoringOutcome.Saved, await all.Get<VideoMonitoringService>().SetSeriesAsync(all.Work.Id, "all", [], [], false, CancellationToken.None));
        await all.ProcessAsync(DateTime.UtcNow);
        var searching = VideoRequestPayload.Parse((await all.GetAsync(allRequest.Id)).PayloadJson)!;
        Assert.AreEqual(all.EpisodeId, searching.ActiveWorkEpisodeId, "All starts with the first missing episode again.");
        Assert.AreEqual(1, all.Environment.Client.Grabs.Count);
    }

    [TestMethod]
    public async Task SeriesMonitoringRejectsAnUnknownScopeAndASelectionOfAnotherSeries()
    {
        await using var host = await SeriesHostAsync();
        var other = await host.AddWorkAsync("Other Show", "1");
        var foreign = new WorkEpisode { WorkId = other.Id, SeasonNumber = 1, EpisodeNumber = 1 };
        host.Environment.Db.WorkEpisodes.Add(foreign);
        await host.Environment.Db.SaveChangesAsync();
        var request = await host.CreateApprovedAsync();
        var before = (await host.GetAsync(request.Id)).PayloadJson;
        var monitoring = host.Get<VideoMonitoringService>();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => monitoring.SetSeriesAsync(host.Work.Id, "sideways", [], [], false, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => monitoring.SetSeriesAsync(host.Work.Id, "custom", [], [foreign.Id], false, CancellationToken.None));
        Assert.AreEqual(VideoMonitoringOutcome.NotFound, await monitoring.SetSeriesAsync(Guid.NewGuid(), "all", [], [], false, CancellationToken.None));

        Assert.AreEqual(before, (await host.GetAsync(request.Id)).PayloadJson, "A rejected change leaves the monitoring as it was.");
    }

    [TestMethod]
    public async Task RetryAndSearchNowNeverGrabTheSameReleaseTwiceForMovieOrSeries()
    {
        foreach (var kind in new[] { MediaAcquisitionKind.Movie, MediaAcquisitionKind.Tv })
        {
            await using var host = kind == MediaAcquisitionKind.Movie
                ? await MovieHostAsync("Dune.2021.1080p.BluRay.x264-SECOND")
                : await SeriesHostAsync(SeveranceFirst, "Severance.S01E01.1080p.BluRay.x264-SECOND");
            var requests = host.Get<AcquisitionRequestService>();
            var request = await host.StartAsync();
            Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status, kind.ToString());

            await requests.ApproveAsync(request.Id, CancellationToken.None);
            await requests.ApproveAsync(request.Id, CancellationToken.None);
            Assert.AreEqual(1, host.Environment.Client.Grabs.Count, $"{kind}: search now on a request that is downloading starts nothing.");

            await host.Operations.MarkCancelledAsync(request.OperationId!.Value, "Cancelled by owner.");
            await host.ProcessAsync(DateTime.UtcNow);
            Assert.AreEqual(AcquisitionRequestStatus.Failed, (await host.GetAsync(request.Id)).Status, kind.ToString());

            await requests.ApproveAsync(request.Id, CancellationToken.None);
            await requests.ApproveAsync(request.Id, CancellationToken.None);
            Assert.AreEqual(AcquisitionRequestStatus.Downloading, (await host.GetAsync(request.Id)).Status, kind.ToString());
            Assert.AreEqual(2, host.Environment.Client.Grabs.Count, $"{kind}: retry takes the next untried release exactly once, however often it is pressed.");
        }
    }

    [TestMethod]
    public async Task ActivityRetryOfADownloadThatWantedAlreadyReplacedIsRefusedForVideo()
    {
        await using var host = await MovieHostAsync("Dune.2021.1080p.BluRay.x264-SECOND");
        var request = await host.StartAsync();
        var firstOperation = request.OperationId!.Value;
        await host.Operations.MarkFailedAsync(firstOperation, "Out of retention");
        await host.ProcessAsync(DateTime.UtcNow);
        Assert.AreNotEqual(firstOperation, (await host.GetAsync(request.Id)).OperationId, "Wanted moved on to the next release.");

        var retried = await host.Environment.NewDownloadService(host.Environment.NewAcquisitionStore()).RetryAsync(firstOperation, CancellationToken.None);

        Assert.IsFalse(retried.Success);
        StringAssert.Contains(retried.Message, "newer release");
        Assert.AreEqual(0, host.Environment.Client.Retried.Count, "Nothing is sent to the download client.");
    }

    [TestMethod]
    public async Task RequestsResolveViewMediaToTheCanonicalWorkOfMovieAndSeriesOnly()
    {
        await using var movie = await MovieHostAsync();
        var request = await movie.CreateApprovedAsync();
        var unknown = request with { Id = Guid.NewGuid(), ExternalId = "does-not-exist" };
        var book = request with { Id = Guid.NewGuid(), Kind = MediaAcquisitionKind.Book };

        var works = await movie.Get<VideoRequestWorkResolver>().ResolveAsync([request, unknown, book], CancellationToken.None);

        Assert.AreEqual(1, works.Count);
        Assert.AreEqual(movie.Work.Id, works[request.Id]);
        Assert.AreEqual($"/Library/Movie/{movie.Work.Id:D}", VideoWorkLinks.DetailPath(MediaAcquisitionKind.Movie, works[request.Id]));
        Assert.AreEqual($"/Library/Series/{movie.Work.Id:D}", VideoWorkLinks.DetailPath(MediaAcquisitionKind.Tv, works[request.Id]));

        await using var series = await SeriesHostAsync();
        var tv = await series.CreateApprovedAsync();
        var seriesWorks = await series.Get<VideoRequestWorkResolver>().ResolveAsync([tv], CancellationToken.None);
        Assert.AreEqual(series.Work.Id, seriesWorks[tv.Id]);
    }

    [TestMethod]
    public async Task WantedShowsAMovieAsOneRowPerWorkAndOffersSearchOnlyWhereTheRequestCanSearch()
    {
        await using var host = await MovieHostAsync();
        var request = await host.CreateApprovedAsync();

        var waiting = (await host.Get<WantedListService>().LoadAsync(CancellationToken.None)).Single();
        Assert.AreEqual(MediaAcquisitionKind.Movie, waiting.Kind);
        Assert.AreEqual(WantedStatus.Requested, waiting.Status);
        Assert.AreEqual(host.Work.Id, waiting.WorkId);
        Assert.AreEqual($"/Library/Movie/{host.Work.Id:D}", waiting.DetailUrl);
        Assert.IsTrue(waiting.CanSearch);

        await host.ProcessAsync(DateTime.UtcNow);
        var downloading = (await host.Get<WantedListService>().LoadAsync(CancellationToken.None)).Single();
        Assert.AreEqual(WantedStatus.Downloading, downloading.Status);
        Assert.AreEqual(request.Id, downloading.RequestId);
        Assert.IsFalse(downloading.CanSearch, "A request that is downloading has nothing to search again.");
    }

    [TestMethod]
    public async Task WantedGroupsASeriesPerSeasonFromCanonicalEpisodesAndSkipsWhatIsInTheLibrary()
    {
        await using var host = await SeriesHostAsync(episodes: false);
        var s1e1 = await host.AddEpisodeAsync(1, 1);
        await host.AddEpisodeAsync(1, 2);
        await host.AddEpisodeAsync(1, 3);
        await host.AddEpisodeAsync(2, 1);
        await host.AddEpisodeAsync(2, 2);
        await host.AddEpisodeAsync(2, 3, DateTime.UtcNow.AddDays(9));
        await host.AttachFileAsync(s1e1.Id);
        await host.CreateApprovedAsync();

        var rows = (await host.Get<WantedListService>().LoadAsync(CancellationToken.None)).OrderBy(row => row.Season).ToArray();

        Assert.AreEqual(2, rows.Length);
        Assert.AreEqual("S01E02-03", rows[0].Selection);
        Assert.AreEqual("S02E01-02", rows[1].Selection, "An episode that has not aired yet is not wanted yet.");
        Assert.IsTrue(rows.All(row => row.Kind == MediaAcquisitionKind.Tv && row.Status == WantedStatus.Requested && row.WorkId == host.Work.Id));
        Assert.AreEqual(2, rows.Select(row => row.Id).Distinct().Count());
        Assert.AreEqual($"/Library/Series/{host.Work.Id:D}", rows[0].DetailUrl);

        await host.ProcessAsync(DateTime.UtcNow);
        var inFlight = (await host.Get<WantedListService>().LoadAsync(CancellationToken.None)).OrderBy(row => row.Season).ToArray();
        Assert.AreEqual(WantedStatus.Downloading, inFlight[0].Status, "The season of the episode being downloaded carries the download state.");
        Assert.AreEqual(WantedStatus.Requested, inFlight[1].Status, "Later seasons queue behind it.");
    }

    [TestMethod]
    public async Task WantedReadsVideoRowsWithAFixedNumberOfQueriesHoweverManySeriesAreRequested()
    {
        await using var host = await SeriesHostAsync(episodes: false);
        await host.AddEpisodeAsync(1, 1);
        await host.CreateApprovedAsync();
        var one = await CountQueriesAsync(host);

        for (var index = 0; index < 4; index++)
        {
            var work = await host.AddWorkAsync($"Show {index}", $"s{index}");
            host.Environment.Db.WorkEpisodes.Add(new WorkEpisode { WorkId = work.Id, SeasonNumber = 1, EpisodeNumber = 1, AiredAt = DateTime.UtcNow.AddDays(-3) });
            host.Environment.Db.WorkEpisodes.Add(new WorkEpisode { WorkId = work.Id, SeasonNumber = 2, EpisodeNumber = 1, AiredAt = DateTime.UtcNow.AddDays(-2) });
            await host.Environment.Db.SaveChangesAsync();
            await host.Requests.CreateAsync(
                new AcquisitionRequestDraft(MediaAcquisitionKind.Tv, "tmdb", $"s{index}", work.CanonicalTitle, null, null),
                "owner",
                AcquisitionRequestStatus.Approved,
                "owner",
                CancellationToken.None);
        }

        Assert.AreEqual(one, await CountQueriesAsync(host), "Five series cost the same queries as one.");
    }

    [TestMethod]
    public async Task MovieAndTvDownloadsAppearInActivityAsAcquisitionWorkWithCanonicalMediaLinks()
    {
        await using var movie = await MovieHostAsync();
        var movieRequest = await movie.StartAsync();
        var movieOperation = (await movie.Operations.GetAsync(movieRequest.OperationId!.Value))!;
        Assert.AreEqual("Download Movie", movieOperation.Title);
        Assert.IsTrue(movieOperation.IsDownload);
        Assert.AreEqual(AdminHistoryCategory.Acquisition, AdminHistoryQuery.CategoryOf(movieOperation.Kind, movieOperation.Category));
        var movieLinks = await movie.Get<VideoRequestWorkResolver>().ResolveOperationLinksAsync([movieOperation], CancellationToken.None);
        Assert.AreEqual($"/Admin/Media/movie/{movie.Work.Id:D}", movieLinks[movieOperation.Id]);

        await using var series = await SeriesHostAsync();
        var seriesRequest = await series.StartAsync();
        var seriesOperation = (await series.Operations.GetAsync(seriesRequest.OperationId!.Value))!;
        Assert.AreEqual("Download TV", seriesOperation.Title);
        Assert.AreEqual(AdminHistoryCategory.Acquisition, AdminHistoryQuery.CategoryOf(seriesOperation.Kind, seriesOperation.Category));
        var seriesLinks = await series.Get<VideoRequestWorkResolver>().ResolveOperationLinksAsync([seriesOperation], CancellationToken.None);
        Assert.AreEqual($"/Admin/Media/series/{series.Work.Id:D}", seriesLinks[seriesOperation.Id], "An episode download links to its Series.");
    }

    [TestMethod]
    public async Task AQualityProfileAssignedFromAdminChangesWhatTheWorkResolves()
    {
        await using var host = await MovieHostAsync();
        var store = host.Get<QualityProfileStore>();
        var profiles = (await store.LoadAsync(CancellationToken.None)).Profiles;
        Assert.IsTrue(profiles.Length > 1, "The registry seeds more than one profile.");
        var monitoring = host.Get<VideoMonitoringService>();

        Assert.AreEqual(VideoMonitoringOutcome.Saved, await monitoring.SetProfileAsync(MediaAcquisitionKind.Movie, host.Work.Id, profiles[1].Id, CancellationToken.None));
        Assert.AreEqual(profiles[1].Id, (await store.ResolveAsync(MediaAcquisitionKind.Movie, host.Work.Id, CancellationToken.None)).Id);
        var detail = (await host.Get<AdminVideoMediaService>().LoadAsync(MediaAcquisitionKind.Movie, host.Work.Id, CancellationToken.None))!;
        Assert.AreEqual(profiles[1].Id, detail.Acquisition.AssignedProfileId);

        Assert.AreEqual(VideoMonitoringOutcome.Saved, await monitoring.SetProfileAsync(MediaAcquisitionKind.Movie, host.Work.Id, "", CancellationToken.None));
        Assert.IsNull((await host.Get<AdminVideoMediaService>().LoadAsync(MediaAcquisitionKind.Movie, host.Work.Id, CancellationToken.None))!.Acquisition.AssignedProfileId);
        Assert.AreEqual(VideoMonitoringOutcome.NotFound, await monitoring.SetProfileAsync(MediaAcquisitionKind.Tv, host.Work.Id, profiles[1].Id, CancellationToken.None));
    }

    [TestMethod]
    public async Task FutureOnlyOnAnOldRequestCountsFromNowAndNotFromTheRequest()
    {
        await using var host = await SeriesHostAsync();
        var upcoming = await host.AddEpisodeAsync(1, 3, DateTime.UtcNow.AddDays(2));
        var request = await host.CreateApprovedAsync();
        await host.Environment.Db.Database.ExecuteSqlRawAsync(
            "UPDATE \"AcquisitionRequests\" SET \"CreatedAt\" = {0} WHERE \"Id\" = {1}",
            DateTime.UtcNow.AddDays(-60).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            request.Id.ToString());

        Assert.AreEqual(VideoMonitoringOutcome.Saved, await host.Get<VideoMonitoringService>().SetSeriesAsync(host.Work.Id, "future", [], [], false, CancellationToken.None));
        await host.ProcessAsync(DateTime.UtcNow);

        Assert.AreEqual(0, host.Environment.Client.Grabs.Count, "Episodes that aired since the request was made are not future.");
        var detail = (await host.Get<AdminVideoMediaService>().LoadAsync(MediaAcquisitionKind.Tv, host.Work.Id, CancellationToken.None))!;
        CollectionAssert.AreEquivalent(new[] { upcoming.Id }, detail.Episodes.Where(episode => episode.Monitored).Select(episode => episode.Id).ToArray());
    }

    [TestMethod]
    public async Task AnUncheckedEpisodeStaysUncheckedAndATickedSeasonKeepsCoveringItsLaterEpisodes()
    {
        await using var host = await SeriesHostAsync();
        var season = new WorkSeason { WorkId = host.Work.Id, SeasonNumber = 1 };
        host.Environment.Db.WorkSeasons.Add(season);
        foreach (var existing in host.Environment.Db.WorkEpisodes.Where(episode => episode.WorkId == host.Work.Id))
        {
            existing.SeasonId = season.Id;
        }

        await host.Environment.Db.SaveChangesAsync();
        var upcoming = await host.AddEpisodeAsync(1, 3, DateTime.UtcNow.AddDays(3), season.Id);
        await host.CreateApprovedAsync();
        var monitoring = host.Get<VideoMonitoringService>();
        var details = host.Get<AdminVideoMediaService>();

        async Task<Guid[]> MonitoredAsync() =>
            [.. (await details.LoadAsync(MediaAcquisitionKind.Tv, host.Work.Id, CancellationToken.None))!.Episodes.Where(episode => episode.Monitored).Select(episode => episode.Id).Order()];

        Assert.AreEqual(VideoMonitoringOutcome.Saved, await monitoring.SetSeriesAsync(host.Work.Id, "custom", [season.Id], [host.EpisodeId!.Value, upcoming.Id], true, CancellationToken.None));
        CollectionAssert.AreEqual(new[] { host.EpisodeId!.Value, upcoming.Id }.Order().ToArray(), await MonitoredAsync(), "The unchecked episode stays unchecked after saving and reloading.");

        var later = await host.AddEpisodeAsync(1, 4, DateTime.UtcNow.AddDays(10), season.Id);
        CollectionAssert.Contains(await MonitoredAsync(), later.Id, "A ticked season keeps covering the episodes that are added to it.");

        Assert.AreEqual(VideoMonitoringOutcome.Saved, await monitoring.SetSeriesAsync(host.Work.Id, "custom", [], [host.EpisodeId!.Value], true, CancellationToken.None));
        CollectionAssert.AreEqual(new[] { host.EpisodeId!.Value }, await MonitoredAsync(), "Episodes aired after the anchor can be unchecked while future episodes are on.");
    }

    [TestMethod]
    public async Task AnAdminEditDuringARunningPassIsKeptAndTheStaleSearchDoesNotGrab()
    {
        await using var host = await SeriesHostAsync();
        var request = await host.CreateApprovedAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Indexer.OnSearch = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };

        var pass = Task.Run(() => host.ProcessAsync(DateTime.UtcNow));
        await entered.Task;
        Assert.AreEqual(VideoMonitoringOutcome.Saved, await host.Get<VideoMonitoringService>().SetSeriesAsync(host.Work.Id, "custom", [], [host.SecondEpisodeId!.Value], false, CancellationToken.None));
        release.SetResult();
        await pass;

        var after = await host.GetAsync(request.Id);
        var payload = VideoRequestPayload.Parse(after.PayloadJson)!;
        Assert.AreEqual(0, host.Environment.Client.Grabs.Count, "The episode the search was for is no longer monitored.");
        Assert.AreEqual(AcquisitionRequestStatus.Approved, after.Status);
        Assert.AreEqual(VideoRequestScope.Custom, payload.Scope);
        CollectionAssert.AreEqual(new[] { host.SecondEpisodeId!.Value }, payload.SelectedEpisodeIds);
        Assert.AreEqual(1, payload.ScopeRevision);

        host.Indexer.OnSearch = null;
        await host.ProcessAsync(DateTime.UtcNow.AddMinutes(1));
        Assert.AreEqual(host.SecondEpisodeId, VideoRequestPayload.Parse((await host.GetAsync(request.Id)).PayloadJson)!.ActiveWorkEpisodeId);
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count);
    }

    [TestMethod]
    public async Task SwitchingAMovieOffWhileItIsBeingSearchedStopsTheGrab()
    {
        await using var host = await MovieHostAsync();
        var request = await host.CreateApprovedAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Indexer.OnSearch = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };

        var pass = Task.Run(() => host.ProcessAsync(DateTime.UtcNow));
        await entered.Task;
        Assert.AreEqual(VideoMonitoringOutcome.Saved, await host.Get<VideoMonitoringService>().SetMovieMonitoredAsync(host.Work.Id, false, CancellationToken.None));
        release.SetResult();
        await pass;

        Assert.AreEqual(0, host.Environment.Client.Grabs.Count);
        Assert.AreEqual(AcquisitionRequestStatus.Rejected, (await host.GetAsync(request.Id)).Status);
    }

    [TestMethod]
    public void AStalePayloadNeverOverwritesAdminOwnedFieldsAndAnEditKeepsItsWakeUp()
    {
        var work = Guid.NewGuid();
        var episode = Guid.NewGuid();
        var stale = new VideoRequestPayload(work, "Show", null, VideoRequestScope.AllCurrentAndFuture, [], MonitorFuture: true) { Searches = 3, NextSearchUtc = DateTime.UtcNow.AddHours(6), TriedReleases = ["tried"] };
        var stored = new VideoRequestPayload(work, "Show", null, VideoRequestScope.Custom, [episode], MonitorFuture: false) { Monitored = false, ScopeRevision = 1 }.Serialize();

        var merged = (VideoRequestPayload)stale.Reconcile(stored);

        Assert.AreEqual(VideoRequestScope.Custom, merged.Scope);
        CollectionAssert.AreEqual(new[] { episode }, merged.SelectedEpisodeIds);
        Assert.IsFalse(merged.Monitored);
        Assert.AreEqual(0, merged.Searches, "The edit's wake-up wins over the back-off the stale search computed.");
        Assert.IsNull(merged.NextSearchUtc);
        CollectionAssert.AreEqual(new[] { "tried" }, merged.TriedReleases!.ToArray(), "What the search did stays.");

        var current = (VideoRequestPayload)(stale with { ScopeRevision = 1 }).Reconcile(stored);
        Assert.AreEqual(3, current.Searches, "Without an edit in between the search state is the search's.");
    }

    [TestMethod]
    public async Task APayloadPatchRunsAgainOnTheNewerTextWhenSomeoneElseWroteFirst()
    {
        await using var host = await MovieHostAsync();
        var request = await host.CreateApprovedAsync(new VideoRequestPayload(host.Work.Id, "Dune", 2021, VideoRequestScope.WholeWork, [], MonitorFuture: false) { Searches = 1 });
        var attempts = 0;

        await host.Requests.PatchPayloadAsync(
            request.Id,
            stored =>
            {
                attempts++;
                if (attempts == 1)
                {
                    host.Requests.UpdatePayloadAsync(request.Id, (VideoRequestPayload.Parse(stored)! with { Searches = 7 }).Serialize(), CancellationToken.None).GetAwaiter().GetResult();
                }

                return (VideoRequestPayload.Parse(stored)! with { ScopeRevision = (VideoRequestPayload.Parse(stored)!.ScopeRevision) + 1 }).Serialize();
            },
            CancellationToken.None);

        var payload = VideoRequestPayload.Parse((await host.GetAsync(request.Id)).PayloadJson)!;
        Assert.AreEqual(2, attempts);
        Assert.AreEqual(7, payload.Searches, "The other writer's change is not lost.");
        Assert.AreEqual(1, payload.ScopeRevision);
    }

    [TestMethod]
    public async Task SwitchingASeriesOffWhenNothingIsOpenSaysSoInsteadOfPretendingToSave()
    {
        await using var host = await SeriesHostAsync();

        Assert.AreEqual(VideoMonitoringOutcome.Unchanged, await host.Get<VideoMonitoringService>().SetSeriesAsync(host.Work.Id, VideoMonitoringService.OffScope, [], [], false, CancellationToken.None));
        Assert.AreEqual(0, (await host.Requests.ListAllAsync(10, CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task TurningMonitoringOffCompletesTheRequestOnlyWhenTheTitleHasMedia()
    {
        await using var host = await SeriesHostAsync();
        await host.AttachFileAsync(host.EpisodeId);
        var request = await host.CreateApprovedAsync();

        await host.Get<VideoMonitoringService>().SetSeriesAsync(host.Work.Id, VideoMonitoringService.OffScope, [], [], false, CancellationToken.None);

        Assert.AreEqual(AcquisitionRequestStatus.Completed, (await host.GetAsync(request.Id)).Status);
    }

    [TestMethod]
    public async Task APayloadWrittenBeforeTheMonitoringFieldsExistedStillWorks()
    {
        await using var host = await MovieHostAsync();
        var current = new VideoRequestPayload(host.Work.Id, "Dune", 2021, VideoRequestScope.WholeWork, [], MonitorFuture: false).Serialize();
        var legacy = new System.Text.Json.Nodes.JsonObject(System.Text.Json.Nodes.JsonNode.Parse(current)!.AsObject()
            .Where(pair => pair.Key is not ("monitored" or "monitorFutureFromUtc" or "excludedEpisodeIds" or "scopeRevision"))
            .Select(pair => new KeyValuePair<string, System.Text.Json.Nodes.JsonNode?>(pair.Key, pair.Value?.DeepClone())));
        Assert.IsFalse(legacy.ContainsKey("monitored"));

        var parsed = VideoRequestPayload.Parse(legacy.ToJsonString())!;
        Assert.IsTrue(parsed.Monitored, "A request made before monitoring was a field is monitored.");
        Assert.IsNull(parsed.MonitorFutureFromUtc);
        Assert.AreEqual(0, parsed.ScopeRevision);

        var request = await host.Requests.CreateAsync(
            new AcquisitionRequestDraft(MediaAcquisitionKind.Movie, "tmdb", host.TmdbId, "Dune", null, null, legacy.ToJsonString()),
            "owner",
            AcquisitionRequestStatus.Approved,
            "owner",
            CancellationToken.None);
        await host.ProcessAsync(DateTime.UtcNow);

        Assert.AreEqual(AcquisitionRequestStatus.Downloading, (await host.GetAsync(request.Id)).Status);
        var selection = new VideoRequestSelection(parsed with { Scope = VideoRequestScope.FutureOnly }, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.IsTrue(selection.Includes(Guid.NewGuid(), null, new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc)), "Future counts from the request creation when no anchor was stored.");
    }

    [TestMethod]
    public async Task AStatusAndPayloadChangeLandsTogetherOrNotAtAll()
    {
        await using var host = await MovieHostAsync();
        var request = await host.CreateApprovedAsync(VideoRequestPayload.Default(MediaAcquisitionKind.Movie, host.Work.Id, "Dune", 2021));
        var interleaved = false;

        // Somebody switches monitoring on between the moment the Off is decided and the moment it is written.
        var offLanded = await host.Requests.PatchPayloadAsync(
            request.Id,
            stored =>
            {
                if (!interleaved)
                {
                    interleaved = true;
                    host.Requests.UpdatePayloadAsync(request.Id, (VideoRequestPayload.Parse(stored)! with { Monitored = true, ScopeRevision = 5 }).Serialize(), CancellationToken.None).GetAwaiter().GetResult();
                }

                return (VideoRequestPayload.Parse(stored)! with { Monitored = false }).Serialize();
            },
            AcquisitionRequestStatus.Approved,
            AcquisitionRequestStatus.Rejected,
            "off",
            CancellationToken.None);

        var after = await host.GetAsync(request.Id);
        var payload = VideoRequestPayload.Parse(after.PayloadJson);
        Assert.IsTrue(offLanded);
        Assert.AreEqual(AcquisitionRequestStatus.Rejected, after.Status);
        Assert.IsFalse(payload!.Monitored, "Status and payload agree: the write ran again on the newer payload.");
        Assert.AreEqual(5, payload.ScopeRevision);

        var stale = await host.Requests.PatchPayloadAsync(request.Id, stored => stored, AcquisitionRequestStatus.Approved, AcquisitionRequestStatus.Completed, null, CancellationToken.None);
        Assert.IsFalse(stale, "A request that is not in the expected status is left alone.");
        Assert.AreEqual(AcquisitionRequestStatus.Rejected, (await host.GetAsync(request.Id)).Status);
    }

    [TestMethod]
    public async Task TurningMonitoringOnJoinsTheOpenRequestInsteadOfFailingOnTheUniqueIndex()
    {
        await using var host = await MovieHostAsync();
        var monitoring = host.Get<VideoMonitoringService>();
        var first = await host.CreateApprovedAsync();
        await monitoring.SetMovieMonitoredAsync(host.Work.Id, false, CancellationToken.None);
        var second = await host.CreateApprovedAsync();

        Assert.AreEqual(VideoMonitoringOutcome.Saved, await monitoring.SetMovieMonitoredAsync(host.Work.Id, true, CancellationToken.None));
        Assert.AreEqual(VideoMonitoringOutcome.Saved, await monitoring.SetMovieMonitoredAsync(host.Work.Id, true, CancellationToken.None), "Pressing it twice is the same state.");

        Assert.AreEqual(AcquisitionRequestStatus.Rejected, (await host.GetAsync(first.Id)).Status, "The ended request stays ended.");
        Assert.AreEqual(AcquisitionRequestStatus.Approved, (await host.GetAsync(second.Id)).Status);
        Assert.AreEqual(2, (await host.Requests.ListAllAsync(10, CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task AReopenedRequestStartsItsSearchOverInsteadOfWaitingOutAnEarlierBackOff()
    {
        await using var host = await MovieHostAsync();
        var request = await host.StartAsync();
        await host.Operations.MarkFailedAsync(request.OperationId!.Value, "Out of retention");
        await host.ProcessAsync(DateTime.UtcNow);
        var tried = await host.GetAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, tried.Status);
        Assert.IsNotNull(VideoRequestPayload.Parse(tried.PayloadJson)!.NextSearchUtc, "Every release was tried, so the search backs off.");
        var monitoring = host.Get<VideoMonitoringService>();

        await monitoring.SetMovieMonitoredAsync(host.Work.Id, false, CancellationToken.None);
        await monitoring.SetMovieMonitoredAsync(host.Work.Id, true, CancellationToken.None);
        await host.ProcessAsync(DateTime.UtcNow.AddMinutes(1));

        Assert.AreEqual(AcquisitionRequestStatus.Downloading, (await host.GetAsync(request.Id)).Status);
        Assert.AreEqual(2, host.Environment.Client.Grabs.Count, "The release that failed before is tried again after monitoring was switched on.");
    }

    [TestMethod]
    public async Task AnEngineWriteKeepsAnExcludedEpisodeExcludedAndAdminWritesKeepWhatTheSearchDid()
    {
        await using var host = await SeriesHostAsync();
        var upcoming = await host.AddEpisodeAsync(1, 3, DateTime.UtcNow.AddDays(4));
        var request = await host.CreateApprovedAsync();
        await host.Get<VideoMonitoringService>().SetSeriesAsync(host.Work.Id, "custom", [], [host.EpisodeId!.Value], true, CancellationToken.None);

        await host.ProcessAsync(DateTime.UtcNow);

        var payload = VideoRequestPayload.Parse((await host.GetAsync(request.Id)).PayloadJson)!;
        Assert.AreEqual(host.EpisodeId, payload.ActiveWorkEpisodeId, "The search's own fields are written.");
        CollectionAssert.Contains(payload.ExcludedEpisodeIds!, upcoming.Id, "The unchecked future episode stays excluded after the engine wrote.");

        var edited = await host.Get<VideoMonitoringService>().SetSeriesAsync(host.Work.Id, "custom", [], [host.EpisodeId!.Value, host.SecondEpisodeId!.Value], true, CancellationToken.None);
        var afterEdit = VideoRequestPayload.Parse((await host.GetAsync(request.Id)).PayloadJson)!;
        Assert.AreEqual(VideoMonitoringOutcome.Saved, edited);
        Assert.AreEqual(host.EpisodeId, afterEdit.ActiveWorkEpisodeId, "An Admin edit does not forget the download that is in flight.");
        Assert.IsNotNull(afterEdit.TriedReleases);
    }

    [TestMethod]
    public async Task AnOffThatLandsAfterTheLastLookNeverResurrectsMonitoringAndTheHandedOverDownloadIsRecorded()
    {
        await using var host = await MovieHostAsync();
        var request = await host.CreateApprovedAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Environment.Client.BeforeGrab = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };

        var pass = Task.Run(() => host.ProcessAsync(DateTime.UtcNow));
        await entered.Task;
        Assert.AreEqual(VideoMonitoringOutcome.Saved, await host.Get<VideoMonitoringService>().SetMovieMonitoredAsync(host.Work.Id, false, CancellationToken.None));
        release.SetResult();
        await pass;

        var after = await host.GetAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, after.Status, "The download was handed over before the Off, so it is recorded and imported.");
        Assert.IsNotNull(after.OperationId);
        Assert.IsFalse(VideoRequestPayload.Parse(after.PayloadJson)!.Monitored, "Monitoring stays off.");

        host.Environment.Client.BeforeGrab = null;
        host.CompleteInSabnzbd(after, "/downloads/movies/Dune.2021");
        await host.Operations.MarkSucceededAsync(after.OperationId!.Value, "Downloaded.");
        await host.ProcessAsync(DateTime.UtcNow.AddMinutes(1));

        var done = await host.GetAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, done.Status);
        Assert.IsFalse(VideoRequestPayload.Parse(done.PayloadJson)!.Monitored);
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count);
    }

    private static async Task<int> CountQueriesAsync(VideoAcquisitionTestHost host)
    {
        var counter = new CommandCounter();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(host.Environment.Db.Database.GetConnectionString()!).AddInterceptors(counter).Options);
        var service = new WantedListService(
            new AcquisitionAccessStore(db),
            host.Get<AnimeMonitoringStore>(),
            host.Get<QualityProfileStore>(),
            db,
            new VideoRequestWorkResolver(db),
            host.Services.GetServices<IAcquisitionRequestExecutor>(),
            TimeProvider.System);

        await service.LoadAsync(CancellationToken.None);
        return counter.Count;
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
