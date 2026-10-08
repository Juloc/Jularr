using System.Data.Common;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Monitoring;
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

        await host.SetCustomScopeAsync([], [host.SecondEpisodeId!.Value], false);
        await host.SetCustomScopeAsync([], [host.SecondEpisodeId!.Value], false);
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
    public async Task SwitchingOneEpisodeOffDecidesOnlyThatEpisodeSoLaterEpisodesAreStillMonitored()
    {
        await using var host = await SeriesHostAsync();
        var request = await host.CreateApprovedAsync();
        var monitoring = host.Get<VideoMonitoringService>();
        var details = host.Get<AdminVideoMediaService>();
        var resolver = host.Get<MonitoringResolver>();

        Assert.AreEqual(VideoMonitoringOutcome.Saved, await monitoring.SetEpisodeMonitoredAsync(host.Work.Id, host.SecondEpisodeId!.Value, false, CancellationToken.None));

        var view = await resolver.LoadAsync(host.Work.Id, CancellationToken.None);
        Assert.IsFalse(view.IsMonitored(host.SecondEpisodeId!.Value), "The episode itself is off.");
        Assert.IsTrue(view.IsWorkMonitored, "One switch never rewrites the Work into a list of every episode.");
        Assert.AreEqual(1, view.DecidedIds(MonitoringTargetKind.Episode, monitored: false).Count());
        Assert.AreEqual(0, view.DecidedIds(MonitoringTargetKind.Episode, monitored: true).Count());

        var later = await host.AddEpisodeAsync(1, 9);
        var detail = (await details.LoadAsync(MediaAcquisitionKind.Tv, host.Work.Id, CancellationToken.None))!;
        Assert.AreEqual(AdminMediaMonitoring.Partial, detail.MediumMonitoring);
        Assert.IsFalse(detail.Episodes.Single(episode => episode.Id == host.SecondEpisodeId).Monitored);
        Assert.IsTrue(detail.Episodes.Single(episode => episode.Id == later.Id).Monitored, "An episode a metadata refresh adds later is monitored, as the Work says.");

        Assert.AreEqual(VideoMonitoringOutcome.Saved, await monitoring.SetEpisodeMonitoredAsync(host.Work.Id, host.SecondEpisodeId!.Value, true, CancellationToken.None));
        Assert.IsNull((await resolver.LoadAsync(host.Work.Id, CancellationToken.None)).DecisionOf(host.SecondEpisodeId!.Value), "Switching it back to what it inherits removes its decision.");
        Assert.AreEqual(VideoMonitoringOutcome.Unchanged, await monitoring.SetEpisodeMonitoredAsync(host.Work.Id, host.SecondEpisodeId!.Value, true, CancellationToken.None));
        Assert.AreEqual(VideoMonitoringOutcome.NotFound, await monitoring.SetSeasonMonitoredAsync(host.Work.Id, 9, true, CancellationToken.None));
        Assert.AreEqual(VideoMonitoringOutcome.NotFound, await monitoring.SetEpisodeMonitoredAsync(host.Work.Id, Guid.NewGuid(), true, CancellationToken.None));
        Assert.IsNotNull(request);
    }

    [TestMethod]
    public async Task ASeasonSwitchedOffStaysOffWhenEpisodesAreAddedToItAndOnMonitorsThemAgain()
    {
        await using var host = await SeriesHostAsync();
        var season = new WorkSeason { WorkId = host.Work.Id, SeasonNumber = 1, Title = "Season 1" };
        host.Environment.Db.WorkSeasons.Add(season);
        foreach (var existing in host.Environment.Db.WorkEpisodes.Where(episode => episode.WorkId == host.Work.Id))
        {
            existing.SeasonId = season.Id;
        }

        await host.Environment.Db.SaveChangesAsync();
        await host.CreateApprovedAsync();
        var monitoring = host.Get<VideoMonitoringService>();
        var details = host.Get<AdminVideoMediaService>();

        Assert.AreEqual(VideoMonitoringOutcome.Saved, await monitoring.SetSeasonMonitoredAsync(host.Work.Id, 1, false, CancellationToken.None));
        var later = await host.AddEpisodeAsync(1, 9, seasonId: season.Id);
        var detail = (await details.LoadAsync(MediaAcquisitionKind.Tv, host.Work.Id, CancellationToken.None))!;
        Assert.AreEqual(AdminMediaMonitoring.Off, detail.Seasons.Single().Monitoring, "An episode added to an off season does not flip it back to Partial.");
        Assert.IsFalse(detail.Episodes.Single(episode => episode.Id == later.Id).Monitored);

        Assert.AreEqual(VideoMonitoringOutcome.Saved, await monitoring.SetEpisodeMonitoredAsync(host.Work.Id, later.Id, true, CancellationToken.None));
        detail = (await details.LoadAsync(MediaAcquisitionKind.Tv, host.Work.Id, CancellationToken.None))!;
        Assert.AreEqual(AdminMediaMonitoring.Partial, detail.Seasons.Single().Monitoring, "One episode switched on inside an off season is monitored by itself.");

        Assert.AreEqual(VideoMonitoringOutcome.Saved, await monitoring.SetSeasonMonitoredAsync(host.Work.Id, 1, true, CancellationToken.None));
        detail = (await details.LoadAsync(MediaAcquisitionKind.Tv, host.Work.Id, CancellationToken.None))!;
        Assert.AreEqual(AdminMediaMonitoring.On, detail.Seasons.Single().Monitoring);
        var view = await host.Get<MonitoringResolver>().LoadAsync(host.Work.Id, CancellationToken.None);
        Assert.AreEqual(0, view.DecidedIds(MonitoringTargetKind.Episode, monitored: true).Count() + view.DecidedIds(MonitoringTargetKind.Episode, monitored: false).Count(), "The season's decision took the episode decisions with it.");
    }

    [TestMethod]
    public async Task AUnitSwitchAfterMonitoringWasOffStartsFromNothingInsteadOfAnOldSelection()
    {
        await using var host = await SeriesHostAsync();
        var season = new WorkSeason { WorkId = host.Work.Id, SeasonNumber = 1, Title = "Season 1" };
        host.Environment.Db.WorkSeasons.Add(season);
        foreach (var existing in host.Environment.Db.WorkEpisodes.Where(episode => episode.WorkId == host.Work.Id))
        {
            existing.SeasonId = season.Id;
        }

        await host.Environment.Db.SaveChangesAsync();
        await host.CreateApprovedAsync();
        var monitoring = host.Get<VideoMonitoringService>();
        var resolver = host.Get<MonitoringResolver>();
        await host.SetCustomScopeAsync([season.Id], [], false);
        await monitoring.SetSeriesAsync(host.Work.Id, VideoMonitoringService.OffScope, CancellationToken.None);
        Assert.IsFalse((await resolver.LoadAsync(host.Work.Id, CancellationToken.None)).IsAnyMonitored);

        Assert.AreEqual(VideoMonitoringOutcome.Saved, await monitoring.SetEpisodeMonitoredAsync(host.Work.Id, host.EpisodeId!.Value, true, CancellationToken.None));

        var view = await resolver.LoadAsync(host.Work.Id, CancellationToken.None);
        Assert.IsTrue(view.IsAnyMonitored);
        Assert.IsNull(view.DecisionOf(season.Id), "The season selected before monitoring went off is not brought back.");
        var detail = (await host.Get<AdminVideoMediaService>().LoadAsync(MediaAcquisitionKind.Tv, host.Work.Id, CancellationToken.None))!;
        Assert.AreEqual(1, detail.Episodes.Count(episode => episode.Monitored));
    }

    [TestMethod]
    public async Task SwitchingTheLastSelectedEpisodeOffEndsTheRequestAndAnEpisodeOnAgainReopensTheSameOne()
    {
        await using var host = await SeriesHostAsync(episodes: true);
        var request = await host.CreateApprovedAsync();
        var monitoring = host.Get<VideoMonitoringService>();
        var resolver = host.Get<MonitoringResolver>();
        await host.SetCustomScopeAsync([], [host.EpisodeId!.Value], false);

        Assert.AreEqual(VideoMonitoringOutcome.Saved, await monitoring.SetEpisodeMonitoredAsync(host.Work.Id, host.EpisodeId!.Value, false, CancellationToken.None));
        Assert.IsFalse((await resolver.LoadAsync(host.Work.Id, CancellationToken.None)).IsAnyMonitored, "Nothing is left to monitor, so the request ends like monitoring Off.");
        Assert.AreEqual(AcquisitionRequestStatus.Rejected, (await host.GetAsync(request.Id)).Status);
        Assert.AreEqual(VideoMonitoringOutcome.Unchanged, await monitoring.SetEpisodeMonitoredAsync(host.Work.Id, host.EpisodeId!.Value, false, CancellationToken.None));

        Assert.AreEqual(VideoMonitoringOutcome.Saved, await monitoring.SetEpisodeMonitoredAsync(host.Work.Id, host.SecondEpisodeId!.Value, true, CancellationToken.None));
        var view = await resolver.LoadAsync(host.Work.Id, CancellationToken.None);
        Assert.IsTrue(view.IsMonitored(host.SecondEpisodeId!.Value));
        Assert.AreEqual(AcquisitionRequestStatus.Approved, (await host.GetAsync(request.Id)).Status, "The request the Off ended is the one that reopens.");
        Assert.AreEqual(1, (await host.Requests.ListAllAsync(100, CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task AMovieListsEachVersionWithItsOwnFilesAndALocationInsideItsRoot()
    {
        await using var host = await MovieHostAsync();
        await host.AttachFileAsync(null, "Dune.2160p.mkv", 4096);
        await host.AttachFileAsync(null, "Dune.1080p.mkv", 1024);

        var detail = (await host.Get<AdminVideoMediaService>().LoadAsync(MediaAcquisitionKind.Movie, host.Work.Id, CancellationToken.None))!;

        Assert.AreEqual(2, detail.Versions.Count);
        Assert.AreEqual("Dune.2160p.mkv", detail.Versions[0].Files.Single().Name, "The largest version comes first.");
        Assert.AreEqual("Video test", detail.Versions[0].Files[0].Location);
        Assert.AreEqual(5120, detail.SizeBytes);
        Assert.AreEqual(1, detail.Available, "A Movie is available once any version is.");
    }

    [TestMethod]
    public async Task AnEpisodeSwitchedOnAndTheLastOneSwitchedOffLeaveTheRequestOpenInAnyOrder()
    {
        foreach (var onFirst in new[] { true, false })
        {
            await using var host = await SeriesHostAsync(episodes: true);
            var request = await host.CreateApprovedAsync();
            await host.SetCustomScopeAsync([], [host.EpisodeId!.Value], false);
            var monitoring = host.Get<VideoMonitoringService>();

            if (onFirst)
            {
                await monitoring.SetEpisodeMonitoredAsync(host.Work.Id, host.SecondEpisodeId!.Value, true, CancellationToken.None);
                await monitoring.SetEpisodeMonitoredAsync(host.Work.Id, host.EpisodeId!.Value, false, CancellationToken.None);
            }
            else
            {
                await monitoring.SetEpisodeMonitoredAsync(host.Work.Id, host.EpisodeId!.Value, false, CancellationToken.None);
                await monitoring.SetEpisodeMonitoredAsync(host.Work.Id, host.SecondEpisodeId!.Value, true, CancellationToken.None);
            }

            var view = await host.Get<MonitoringResolver>().LoadAsync(host.Work.Id, CancellationToken.None);
            Assert.IsTrue(view.IsMonitored(host.SecondEpisodeId!.Value), $"Both switches landed (on first: {onFirst}).");
            Assert.IsFalse(view.IsMonitored(host.EpisodeId!.Value));
            Assert.AreEqual(AcquisitionRequestStatus.Approved, (await host.GetAsync(request.Id)).Status, "A request is only closed while nothing is monitored.");
        }
    }

    [TestMethod]
    public async Task ASwitchThatChangesNothingIsNotSavedAndKeepsTheRevisionAndTheSearchState()
    {
        await using var host = await SeriesHostAsync();
        var request = await host.CreateApprovedAsync();
        var monitoring = host.Get<VideoMonitoringService>();
        Assert.AreEqual(VideoMonitoringOutcome.Saved, await monitoring.SetEpisodeMonitoredAsync(host.Work.Id, host.SecondEpisodeId!.Value, false, CancellationToken.None));
        await host.Requests.PatchPayloadAsync(request.Id, stored => (VideoRequestPayload.Parse(stored)! with { Searches = 2, LastProblem = "No release matched." }).Serialize(), CancellationToken.None);
        var before = VideoRequestPayload.Parse((await host.GetAsync(request.Id)).PayloadJson)!;

        Assert.AreEqual(VideoMonitoringOutcome.Unchanged, await monitoring.SetEpisodeMonitoredAsync(host.Work.Id, host.SecondEpisodeId!.Value, false, CancellationToken.None), "A double click.");

        var after = VideoRequestPayload.Parse((await host.GetAsync(request.Id)).PayloadJson)!;
        Assert.AreEqual(before.MonitoringRevision, after.MonitoringRevision);
        Assert.AreEqual(2, after.Searches);
        Assert.AreEqual("No release matched.", after.LastProblem);
    }

    [TestMethod]
    public async Task ASeasonSwitchedOffIsStillOffAfterTheEngineWroteItsOwnState()
    {
        await using var host = await SeriesHostAsync();
        var first = new WorkSeason { WorkId = host.Work.Id, SeasonNumber = 1, Title = "Season 1" };
        var second = new WorkSeason { WorkId = host.Work.Id, SeasonNumber = 2, Title = "Season 2" };
        host.Environment.Db.WorkSeasons.AddRange(first, second);
        foreach (var existing in host.Environment.Db.WorkEpisodes.Where(episode => episode.WorkId == host.Work.Id))
        {
            existing.SeasonId = first.Id;
        }

        await host.Environment.Db.SaveChangesAsync();
        await host.AddEpisodeAsync(2, 1, seasonId: second.Id);
        var request = await host.CreateApprovedAsync();
        Assert.AreEqual(VideoMonitoringOutcome.Saved, await host.Get<VideoMonitoringService>().SetSeasonMonitoredAsync(host.Work.Id, 1, false, CancellationToken.None));

        await host.ProcessAsync(DateTime.UtcNow);

        var payload = VideoRequestPayload.Parse((await host.GetAsync(request.Id)).PayloadJson)!;
        Assert.IsTrue(payload.Searches > 0 || payload.ActiveWorkEpisodeId is not null, "The engine searched and wrote its own fields.");
        Assert.AreEqual(false, (await host.Get<MonitoringResolver>().LoadAsync(host.Work.Id, CancellationToken.None)).DecisionOf(first.Id), "The season's decision is not the engine's to write and survives its writes.");
    }

    [TestMethod]
    public async Task SeriesAllAndOffAndFutureOnlyFeedTheSameWantedPass()
    {
        await using var off = await SeriesHostAsync();
        var offRequest = await off.CreateApprovedAsync();
        Assert.AreEqual(VideoMonitoringOutcome.Saved, await off.Get<VideoMonitoringService>().SetSeriesAsync(off.Work.Id, VideoMonitoringService.OffScope, CancellationToken.None));
        await off.ProcessAsync(DateTime.UtcNow);
        var stopped = await off.GetAsync(offRequest.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Rejected, stopped.Status);
        StringAssert.Contains(stopped.StatusMessage ?? "", "Monitoring was turned off");
        Assert.AreEqual(0, off.Environment.Client.Grabs.Count);

        await using var future = await SeriesHostAsync();
        await future.AddEpisodeAsync(1, 3, DateTime.UtcNow.AddDays(2));
        var futureRequest = await future.CreateApprovedAsync();
        Assert.AreEqual(VideoMonitoringOutcome.Saved, await future.Get<VideoMonitoringService>().SetSeriesAsync(future.Work.Id, "future", CancellationToken.None));
        await future.ProcessAsync(DateTime.UtcNow);
        var waiting = await future.GetAsync(futureRequest.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, waiting.Status);
        Assert.AreEqual(0, future.Environment.Client.Grabs.Count, "Future only does not search episodes that already aired.");
        Assert.IsNotNull(VideoRequestPayload.Parse(waiting.PayloadJson)!.NextSearchUtc);

        await using var all = await SeriesHostAsync();
        var allRequest = await all.CreateApprovedAsync(MonitoringTestSupport.Choosing(all.Work.Id, all.Work.CanonicalTitle, all.Work.Year, VideoRequestScope.Custom, [all.SecondEpisodeId!.Value]));
        Assert.AreEqual(VideoMonitoringOutcome.Saved, await all.Get<VideoMonitoringService>().SetSeriesAsync(all.Work.Id, "all", CancellationToken.None));
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

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => monitoring.SetSeriesAsync(host.Work.Id, "sideways", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => monitoring.SetSeriesAsync(host.Work.Id, "custom", CancellationToken.None));
        Assert.AreEqual(VideoMonitoringOutcome.NotFound, await monitoring.SetSeriesAsync(Guid.NewGuid(), "all", CancellationToken.None));
        Assert.AreEqual(VideoMonitoringOutcome.NotFound, await monitoring.SetEpisodeMonitoredAsync(host.Work.Id, foreign.Id, true, CancellationToken.None), "An episode of another title is not part of this Series.");

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
        Assert.AreEqual((movie.Work.Id, "Dune", (int?)2021), (works[request.Id].WorkId, works[request.Id].Title, works[request.Id].Year));
        Assert.AreEqual($"/Library/Movie/{movie.Work.Id:D}", VideoWorkLinks.DetailPath(MediaAcquisitionKind.Movie, works[request.Id].WorkId));
        Assert.AreEqual($"/Library/Series/{movie.Work.Id:D}", VideoWorkLinks.DetailPath(MediaAcquisitionKind.Tv, works[request.Id].WorkId));

        await using var series = await SeriesHostAsync();
        var tv = await series.CreateApprovedAsync();
        var seriesWorks = await series.Get<VideoRequestWorkResolver>().ResolveAsync([tv], CancellationToken.None);
        Assert.AreEqual(series.Work.Id, seriesWorks[tv.Id].WorkId);
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

        Assert.AreEqual(VideoMonitoringOutcome.Saved, await host.Get<VideoMonitoringService>().SetSeriesAsync(host.Work.Id, "future", CancellationToken.None));
        await host.ProcessAsync(DateTime.UtcNow);

        Assert.AreEqual(0, host.Environment.Client.Grabs.Count, "Episodes that aired since the request was made are not future.");
        var detail = (await host.Get<AdminVideoMediaService>().LoadAsync(MediaAcquisitionKind.Tv, host.Work.Id, CancellationToken.None))!;
        CollectionAssert.AreEquivalent(new[] { upcoming.Id }, detail.Episodes.Where(episode => episode.Monitored).Select(episode => episode.Id).ToArray());
    }

    [TestMethod]
    public async Task AnUncheckedEpisodeStaysUncheckedAndFutureKeepsCoveringLaterEpisodes()
    {
        await using var host = await SeriesHostAsync();
        var upcoming = await host.AddEpisodeAsync(1, 3, DateTime.UtcNow.AddDays(3));
        await host.CreateApprovedAsync();
        var details = host.Get<AdminVideoMediaService>();

        async Task<Guid[]> MonitoredAsync() =>
            [.. (await details.LoadAsync(MediaAcquisitionKind.Tv, host.Work.Id, CancellationToken.None))!.Episodes.Where(episode => episode.Monitored).Select(episode => episode.Id).Order()];

        await host.SetCustomScopeAsync([], [host.EpisodeId!.Value, upcoming.Id], true);
        CollectionAssert.AreEqual(new[] { host.EpisodeId!.Value, upcoming.Id }.Order().ToArray(), await MonitoredAsync(), "The unchecked episode stays unchecked after saving and reloading.");

        var later = await host.AddEpisodeAsync(1, 4, DateTime.UtcNow.AddDays(10));
        CollectionAssert.Contains(await MonitoredAsync(), later.Id, "With future on, an episode that is added later is monitored.");

        await host.SetCustomScopeAsync([], [host.EpisodeId!.Value], true);
        CollectionAssert.AreEquivalent(new[] { host.EpisodeId!.Value }, await MonitoredAsync(), "Episodes that exist and were left unchecked stay unchecked, aired or not.");
        var newest = await host.AddEpisodeAsync(1, 5, DateTime.UtcNow.AddDays(20));
        CollectionAssert.Contains(await MonitoredAsync(), newest.Id, "Only episodes that appear afterwards follow the Series.");
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
        await host.SetCustomScopeAsync([], [host.SecondEpisodeId!.Value], false);
        release.SetResult();
        await pass;

        var after = await host.GetAsync(request.Id);
        var payload = VideoRequestPayload.Parse(after.PayloadJson)!;
        Assert.AreEqual(0, host.Environment.Client.Grabs.Count, "The episode the search was for is no longer monitored.");
        Assert.AreEqual(AcquisitionRequestStatus.Approved, after.Status);
        var view = await host.Get<MonitoringResolver>().LoadAsync(host.Work.Id, CancellationToken.None);
        Assert.IsTrue(view.IsMonitored(host.SecondEpisodeId!.Value));
        Assert.IsFalse(view.IsMonitored(host.EpisodeId!.Value));
        Assert.AreEqual(1, payload.MonitoringRevision);

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
    public void AStalePayloadNeverOverwritesTheMonitoringFieldsAndAChangeKeepsItsWakeUp()
    {
        var work = Guid.NewGuid();
        var stale = new VideoRequestPayload(work, "Show", null) { Searches = 3, NextSearchUtc = DateTime.UtcNow.AddHours(6), TriedReleases = ["tried"] };
        var stored = new VideoRequestPayload(work, "Show", null) { EndedByMonitoring = true, MonitoringRevision = 1 }.Serialize();

        var merged = (VideoRequestPayload)stale.Reconcile(stored);

        Assert.IsTrue(merged.EndedByMonitoring);
        Assert.AreEqual(1, merged.MonitoringRevision);
        Assert.AreEqual(0, merged.Searches, "The change's wake-up wins over the back-off the stale search computed.");
        Assert.IsNull(merged.NextSearchUtc);
        CollectionAssert.AreEqual(new[] { "tried" }, merged.TriedReleases!.ToArray(), "What the search did stays.");

        var current = (VideoRequestPayload)(stale with { MonitoringRevision = 1 }).Reconcile(stored);
        Assert.AreEqual(3, current.Searches, "Without a change in between the search state is the search's.");
    }

    [TestMethod]
    public async Task APayloadPatchRunsAgainOnTheNewerTextWhenSomeoneElseWroteFirst()
    {
        await using var host = await MovieHostAsync();
        var request = await host.CreateApprovedAsync(new VideoRequestPayload(host.Work.Id, "Dune", 2021) { Searches = 1 });
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

                return (VideoRequestPayload.Parse(stored)! with { MonitoringRevision = VideoRequestPayload.Parse(stored)!.MonitoringRevision + 1 }).Serialize();
            },
            CancellationToken.None);

        var payload = VideoRequestPayload.Parse((await host.GetAsync(request.Id)).PayloadJson)!;
        Assert.AreEqual(2, attempts);
        Assert.AreEqual(7, payload.Searches, "The other writer's change is not lost.");
        Assert.AreEqual(1, payload.MonitoringRevision);
    }

    [TestMethod]
    public async Task SwitchingASeriesOffWhenNothingIsOpenSaysSoInsteadOfPretendingToSave()
    {
        await using var host = await SeriesHostAsync();

        Assert.AreEqual(VideoMonitoringOutcome.Unchanged, await host.Get<VideoMonitoringService>().SetSeriesAsync(host.Work.Id, VideoMonitoringService.OffScope, CancellationToken.None));
        Assert.AreEqual(0, (await host.Requests.ListAllAsync(10, CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task TurningMonitoringOffCompletesTheRequestOnlyWhenTheTitleHasMedia()
    {
        await using var host = await SeriesHostAsync();
        await host.AttachFileAsync(host.EpisodeId);
        var request = await host.CreateApprovedAsync();

        await host.Get<VideoMonitoringService>().SetSeriesAsync(host.Work.Id, VideoMonitoringService.OffScope, CancellationToken.None);

        Assert.AreEqual(AcquisitionRequestStatus.Completed, (await host.GetAsync(request.Id)).Status);
    }

    [TestMethod]
    public async Task ARequestWithoutAPayloadStartsMonitoredAndOneThatAlreadyAppliedItsChoiceFollowsTheWork()
    {
        await using var host = await MovieHostAsync();
        var plain = await host.Requests.CreateAsync(
            new AcquisitionRequestDraft(MediaAcquisitionKind.Movie, "tmdb", host.TmdbId, "Dune", null, null),
            "owner",
            AcquisitionRequestStatus.Approved,
            "owner",
            CancellationToken.None);
        await host.ProcessAsync(DateTime.UtcNow);

        Assert.AreEqual(AcquisitionRequestStatus.Downloading, (await host.GetAsync(plain.Id)).Status, "A request that carries nothing monitors the whole Movie.");
        Assert.IsTrue((await host.Get<MonitoringResolver>().LoadAsync(host.Work.Id, CancellationToken.None)).IsWorkMonitored);
        Assert.IsNull(VideoRequestPayload.Parse((await host.GetAsync(plain.Id)).PayloadJson)!.Requested, "The choice is applied once and then gone from the request.");

        await using var applied = await MovieHostAsync();
        var request = await applied.CreateApprovedAsync(new VideoRequestPayload(applied.Work.Id, "Dune", 2021));
        await applied.ProcessAsync(DateTime.UtcNow);

        Assert.AreEqual(AcquisitionRequestStatus.Rejected, (await applied.GetAsync(request.Id)).Status, "Nothing monitors the Movie and the request carries no choice, so it ends like monitoring Off.");
    }

    [TestMethod]
    public async Task AStatusAndPayloadChangeLandsTogetherOrNotAtAll()
    {
        await using var host = await MovieHostAsync();
        var request = await host.CreateApprovedAsync(new VideoRequestPayload(host.Work.Id, "Dune", 2021));
        var interleaved = false;

        // Somebody switches monitoring on between the moment the Off is decided and the moment it is written.
        var offLanded = await host.Requests.PatchPayloadAsync(
            request.Id,
            stored =>
            {
                if (!interleaved)
                {
                    interleaved = true;
                    host.Requests.UpdatePayloadAsync(request.Id, (VideoRequestPayload.Parse(stored)! with { EndedByMonitoring = false, MonitoringRevision = 5 }).Serialize(), CancellationToken.None).GetAwaiter().GetResult();
                }

                return (VideoRequestPayload.Parse(stored)! with { EndedByMonitoring = true }).Serialize();
            },
            AcquisitionRequestStatus.Approved,
            AcquisitionRequestStatus.Rejected,
            "off",
            CancellationToken.None);

        var after = await host.GetAsync(request.Id);
        var payload = VideoRequestPayload.Parse(after.PayloadJson);
        Assert.IsTrue(offLanded);
        Assert.AreEqual(AcquisitionRequestStatus.Rejected, after.Status);
        Assert.IsTrue(payload!.EndedByMonitoring, "Status and payload agree: the write ran again on the newer payload.");
        Assert.AreEqual(5, payload.MonitoringRevision);

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
    public async Task AnEngineWriteKeepsAnUncheckedEpisodeUncheckedAndMonitoringChangesKeepWhatTheSearchDid()
    {
        await using var host = await SeriesHostAsync();
        var request = await host.CreateApprovedAsync();
        await host.SetCustomScopeAsync([], [host.EpisodeId!.Value], false);
        var resolver = host.Get<MonitoringResolver>();

        await host.ProcessAsync(DateTime.UtcNow);

        var payload = VideoRequestPayload.Parse((await host.GetAsync(request.Id)).PayloadJson)!;
        Assert.AreEqual(host.EpisodeId, payload.ActiveWorkEpisodeId, "The search's own fields are written.");
        Assert.IsFalse((await resolver.LoadAsync(host.Work.Id, CancellationToken.None)).IsMonitored(host.SecondEpisodeId!.Value), "The unchecked episode stays unchecked after the engine wrote.");

        await host.SetCustomScopeAsync([], [host.EpisodeId!.Value, host.SecondEpisodeId!.Value], false);
        var afterEdit = VideoRequestPayload.Parse((await host.GetAsync(request.Id)).PayloadJson)!;
        Assert.AreEqual(host.EpisodeId, afterEdit.ActiveWorkEpisodeId, "A monitoring change does not forget the download that is in flight.");
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
        Assert.IsFalse((await host.Get<MonitoringResolver>().LoadAsync(host.Work.Id, CancellationToken.None)).IsAnyMonitored, "Monitoring stays off.");

        host.Environment.Client.BeforeGrab = null;
        host.CompleteInSabnzbd(after, "/downloads/movies/Dune.2021");
        await host.Operations.MarkSucceededAsync(after.OperationId!.Value, "Downloaded.");
        await host.ProcessAsync(DateTime.UtcNow.AddMinutes(1));

        var done = await host.GetAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, done.Status);
        Assert.IsFalse((await host.Get<MonitoringResolver>().LoadAsync(host.Work.Id, CancellationToken.None)).IsAnyMonitored);
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count);
    }

    [TestMethod]
    public async Task AnOffResultIsDroppedWhenAnAdminEditFollowedWhatTheRunRead()
    {
        await using var host = await MovieHostAsync();
        var request = await host.CreateApprovedAsync(new VideoRequestPayload(host.Work.Id, "Dune", 2021));
        var monitoring = host.Get<VideoMonitoringService>();
        var requests = host.Get<AcquisitionRequestService>();
        await monitoring.SetMovieMonitoredAsync(host.Work.Id, true, CancellationToken.None);
        await host.Requests.TryTransitionStatusAsync(request.Id, [AcquisitionRequestStatus.Approved], AcquisitionRequestStatus.Searching, null, null, CancellationToken.None);
        await monitoring.SetMovieMonitoredAsync(host.Work.Id, false, CancellationToken.None);
        var read = VideoRequestPayload.Parse((await host.GetAsync(request.Id)).PayloadJson)!;
        Assert.IsFalse((await host.Get<MonitoringResolver>().LoadAsync(host.Work.Id, CancellationToken.None)).IsAnyMonitored);
        var offResult = new AcquisitionExecution(AcquisitionRequestStatus.Rejected, VideoMonitoringService.MonitoringTurnedOff) { StillApplies = VideoRequestPayload.StillAtRevision(read.MonitoringRevision) };

        // Admin switches monitoring on after the run read Off and before its result is written.
        await monitoring.SetMovieMonitoredAsync(host.Work.Id, true, CancellationToken.None);
        await requests.ApplyManualExecutionAsync(request.Id, offResult, CancellationToken.None);

        var after = await host.GetAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, after.Status, "The stale Off does not end a request that was switched on again.");
        Assert.IsTrue((await host.Get<MonitoringResolver>().LoadAsync(host.Work.Id, CancellationToken.None)).IsAnyMonitored);

        await requests.ApplyManualExecutionAsync(request.Id, offResult with { StillApplies = VideoRequestPayload.StillAtRevision(VideoRequestPayload.Parse(after.PayloadJson)!.MonitoringRevision) }, CancellationToken.None);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, (await host.GetAsync(request.Id)).Status, "Only a request that is still being searched is ended by a result.");
    }

    [TestMethod]
    public async Task AResultThatEndsTheRequestStandsWhenNothingChangedAndIsDroppedOverAWiderScope()
    {
        await using var host = await SeriesHostAsync();
        var request = await host.CreateApprovedAsync();
        var monitoring = host.Get<VideoMonitoringService>();
        var requests = host.Get<AcquisitionRequestService>();
        await host.SetCustomScopeAsync([], [host.EpisodeId!.Value], false);
        await host.Requests.TryTransitionStatusAsync(request.Id, [AcquisitionRequestStatus.Approved], AcquisitionRequestStatus.Searching, null, null, CancellationToken.None);
        var revision = VideoRequestPayload.Parse((await host.GetAsync(request.Id)).PayloadJson)!.MonitoringRevision;
        var allAvailable = new AcquisitionExecution(AcquisitionRequestStatus.Completed, "All requested TV episodes are available.") { StillApplies = VideoRequestPayload.StillAtRevision(revision) };

        await requests.ApplyManualExecutionAsync(request.Id, allAvailable, CancellationToken.None);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, (await host.GetAsync(request.Id)).Status, "Nothing changed, so the result ends the request.");

        await using var widened = await SeriesHostAsync();
        var second = await widened.CreateApprovedAsync();
        await widened.SetCustomScopeAsync([], [widened.EpisodeId!.Value], false);
        await widened.Requests.TryTransitionStatusAsync(second.Id, [AcquisitionRequestStatus.Approved], AcquisitionRequestStatus.Searching, null, null, CancellationToken.None);
        var read = VideoRequestPayload.Parse((await widened.GetAsync(second.Id)).PayloadJson)!.MonitoringRevision;
        await widened.Get<VideoMonitoringService>().SetSeriesAsync(widened.Work.Id, "all", CancellationToken.None);

        await widened.Get<AcquisitionRequestService>().ApplyManualExecutionAsync(second.Id, allAvailable with { StillApplies = VideoRequestPayload.StillAtRevision(read) }, CancellationToken.None);

        var after = await widened.GetAsync(second.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, after.Status, "A result computed from the narrower scope does not end the widened one.");
        Assert.IsTrue((await widened.Get<MonitoringResolver>().LoadAsync(widened.Work.Id, CancellationToken.None)).IsWorkMonitored);
    }

    [TestMethod]
    public async Task ADroppedGrabNeverMarksItsReleaseAsTried()
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
        await host.SetCustomScopeAsync([], [host.SecondEpisodeId!.Value], false);
        release.SetResult();
        await pass;

        var payload = VideoRequestPayload.Parse((await host.GetAsync(request.Id)).PayloadJson)!;
        Assert.IsTrue(payload.TriedReleases is null or { Count: 0 }, "The release of the dropped grab is not tried for the new scope.");
        Assert.AreEqual(0, payload.Searches);
    }

    [TestMethod]
    public async Task ARequestWithADownloadLinkedThatWasLeftSearchingGoesBackToItsDownloadNotToANewSearch()
    {
        await using var host = await MovieHostAsync();
        var request = await host.StartAsync();
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status);
        await host.Requests.TryTransitionStatusAsync(request.Id, [AcquisitionRequestStatus.Downloading], AcquisitionRequestStatus.Searching, "Searching", null, CancellationToken.None);

        await host.ProcessAsync(DateTime.UtcNow.AddHours(1));

        var recovered = await host.GetAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, recovered.Status);
        Assert.AreEqual(request.OperationId, recovered.OperationId);
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count, "The download is followed, not started again.");
    }

    [TestMethod]
    public async Task AnEditDoesNotRestartTheStaleClockOfAClaimedRequest()
    {
        await using var host = await MovieHostAsync();
        var request = await host.CreateApprovedAsync(new VideoRequestPayload(host.Work.Id, "Dune", 2021));
        await host.Requests.TryTransitionStatusAsync(request.Id, [AcquisitionRequestStatus.Approved], AcquisitionRequestStatus.Searching, null, null, CancellationToken.None);
        var claimed = await host.GetAsync(request.Id);

        await host.Requests.PatchPayloadAsync(request.Id, stored => stored, AcquisitionRequestStatus.Searching, AcquisitionRequestStatus.Searching, "edit", CancellationToken.None);
        Assert.AreEqual(claimed.UpdatedAt, (await host.GetAsync(request.Id)).UpdatedAt, "The status did not change, so neither did the claim's age.");

        await host.Requests.PatchPayloadAsync(request.Id, stored => stored, AcquisitionRequestStatus.Searching, AcquisitionRequestStatus.Approved, "done", CancellationToken.None);
        Assert.AreNotEqual(claimed.UpdatedAt, (await host.GetAsync(request.Id)).UpdatedAt);
    }

    [TestMethod]
    public async Task ARefusedDownloadAfterAnEditKeepsItsBackOffAndProblem()
    {
        await using var host = await MovieHostAsync();
        var request = await host.CreateApprovedAsync();
        host.Environment.Client.GrabResults.Enqueue(new Jularr.Web.Features.Acquisition.Sabnzbd.SabnzbdGrabResult(false, [], "Invalid NZB"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Indexer.OnSearch = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };

        var pass = Task.Run(() => host.ProcessAsync(DateTime.UtcNow));
        await entered.Task;
        await host.Get<VideoMonitoringService>().SetMovieMonitoredAsync(host.Work.Id, true, CancellationToken.None);
        release.SetResult();
        await pass;

        var after = await host.GetAsync(request.Id);
        var payload = VideoRequestPayload.Parse(after.PayloadJson)!;
        Assert.AreEqual(AcquisitionRequestStatus.Approved, after.Status);
        Assert.IsNotNull(payload.NextSearchUtc, "The back-off of the refused download is stored although an edit was met by the run's first save.");
        StringAssert.Contains(payload.LastProblem ?? "", "Invalid NZB");
    }

    [TestMethod]
    public async Task AFailedRequestThatWasSwitchedOffOffersNoSearchAndAChangeReplacesAStaleMessage()
    {
        await using var host = await MovieHostAsync();
        var failed = await host.Requests.CreateAsync(
            new AcquisitionRequestDraft(MediaAcquisitionKind.Movie, "tmdb", host.TmdbId, "Dune", null, null, new VideoRequestPayload(host.Work.Id, "Dune", 2021).Serialize()),
            "owner",
            AcquisitionRequestStatus.Failed,
            "owner",
            CancellationToken.None);
        var wanted = host.Get<WantedListService>();

        Assert.IsFalse((await wanted.LoadAsync(CancellationToken.None)).Single().CanSearch, "A request that is off is not searched or retried.");

        await host.Get<MonitoringCommands>().SetAsync(MonitoringTargetKind.Work, host.Work.Id, true, CancellationToken.None);
        Assert.IsTrue((await wanted.LoadAsync(CancellationToken.None)).Single().CanSearch);
        Assert.IsNotNull(failed);

        await using var waiting = await MovieHostAsync();
        var request = await waiting.CreateApprovedAsync(new VideoRequestPayload(waiting.Work.Id, "Dune", 2021));
        await waiting.Requests.UpdateStatusAsync(request.Id, AcquisitionRequestStatus.Approved, "No release found. Searching again 2030-01-01 00:00 UTC.", null, null, null, CancellationToken.None);

        await waiting.Get<VideoMonitoringService>().SetMovieMonitoredAsync(waiting.Work.Id, true, CancellationToken.None);

        Assert.AreEqual(VideoMonitoringService.MonitoringChanged, (await waiting.GetAsync(request.Id)).StatusMessage);
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
            new MonitoringResolver(db),
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
