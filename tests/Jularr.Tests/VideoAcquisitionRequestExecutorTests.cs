using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class VideoAcquisitionRequestExecutorTests
{
    [TestMethod]
    public async Task MovieUsesSharedWantedDownloadAndCompletesThroughImporter()
    {
        await using var host = await VideoAcquisitionTestHost.CreateAsync(
            MediaAcquisitionKind.Movie,
            "Dune",
            2021,
            "438631",
            "Dune.2021.1080p.WEB-DL.x264-GROUP");

        var request = await host.StartAsync();

        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status);
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count);
        Assert.IsNotNull(request.OperationId);
        Assert.AreEqual(MediaAcquisitionKind.Movie, host.OperationDetails(request).MediaKind);
        Assert.AreEqual($"/Library/Movie/{host.Work.Id:D}", request.ResultUrl, "The request points at the canonical Movie page, not at a search.");

        host.CompleteInSabnzbd(request, "/downloads/movies/Dune.2021");
        await host.Operations.MarkSucceededAsync(request.OperationId!.Value, "Downloaded.");
        await host.ProcessAsync(DateTime.UtcNow);

        var completed = await host.GetAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, completed.Status);
        Assert.AreEqual(1, host.Importer.Imports);
        Assert.IsTrue(await host.HasPlayableAsync(workEpisodeId: null));
    }

    [TestMethod]
    public async Task TvDefaultScopeAcquiresCurrentEpisodeAndKeepsFutureMonitoring()
    {
        await using var host = await VideoAcquisitionTestHost.CreateAsync(
            MediaAcquisitionKind.Tv,
            "Severance",
            2022,
            "95396",
            "Severance.S01E01.1080p.WEB-DL.x264-GROUP",
            addEpisode: true);

        var request = await host.StartAsync();

        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status);
        var payload = VideoRequestPayload.Parse(request.PayloadJson)
            ?? throw new AssertFailedException("Expected video request payload.");
        Assert.AreEqual(VideoRequestScope.AllCurrentAndFuture, payload.Scope);
        Assert.IsTrue(payload.MonitorFuture);
        Assert.AreEqual(host.EpisodeId, payload.ActiveWorkEpisodeId);

        host.CompleteInSabnzbd(request, "/downloads/tv/Severance.S01E01");
        await host.Operations.MarkSucceededAsync(request.OperationId!.Value, "Downloaded.");
        await host.ProcessAsync(DateTime.UtcNow);

        var monitoring = await host.GetAsync(request.Id);
        Assert.AreEqual(
            AcquisitionRequestStatus.Approved,
            monitoring.Status,
            "A TV request with All current + future stays as the durable future-monitor owner.");
        Assert.AreEqual(1, host.Importer.Imports);
        Assert.IsTrue(await host.HasPlayableAsync(host.EpisodeId));
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count, "Future monitoring must not duplicate the imported episode.");

        var storedPayload = VideoRequestPayload.Parse(monitoring.PayloadJson)
            ?? throw new AssertFailedException("Expected persisted TV monitoring payload.");
        Assert.IsNotNull(storedPayload.NextSearchUtc);
        Assert.IsNull(storedPayload.ActiveWorkEpisodeId);
    }

    [TestMethod]
    public async Task TvCustomScopeSearchesOnlyTheSelectedCanonicalEpisode()
    {
        await using var host = await VideoAcquisitionTestHost.CreateAsync(
            MediaAcquisitionKind.Tv,
            "Severance",
            2022,
            "95396",
            "Severance.S01E02.1080p.WEB-DL.x264-GROUP",
            addEpisode: true,
            addSecondEpisode: true);

        var custom = new VideoRequestPayload(
            host.Work.Id,
            host.Work.CanonicalTitle,
            host.Work.Year,
            VideoRequestScope.Custom,
            [host.SecondEpisodeId!.Value],
            MonitorFuture: false);

        var request = await host.StartAsync(custom);
        var payload = VideoRequestPayload.Parse(request.PayloadJson)
            ?? throw new AssertFailedException("Expected video request payload.");

        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status);
        Assert.AreEqual(host.SecondEpisodeId, payload.ActiveWorkEpisodeId);
        Assert.AreEqual(2, payload.ActiveEpisodeNumber);
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count);
    }

    [TestMethod]
    public async Task TvCustomSeasonScopeKeepsFutureEpisodesInsideSelectedCanonicalSeason()
    {
        await using var host = await VideoAcquisitionTestHost.CreateAsync(
            MediaAcquisitionKind.Tv,
            "Severance",
            2022,
            "95396",
            "Severance.S01E01.1080p.WEB-DL.x264-GROUP",
            addEpisode: true,
            addSecondEpisode: true);

        var season = new WorkSeason { WorkId = host.Work.Id, SeasonNumber = 1 };
        host.Environment.Db.WorkSeasons.Add(season);
        var episodes = await host.Environment.Db.WorkEpisodes
            .Where(x => x.WorkId == host.Work.Id)
            .OrderBy(x => x.EpisodeNumber)
            .ToListAsync();
        foreach (var episode in episodes)
        {
            episode.SeasonId = season.Id;
        }

        episodes[1].AiredAt = DateTime.UtcNow.AddDays(2);
        await host.Environment.Db.SaveChangesAsync();

        var custom = new VideoRequestPayload(
            host.Work.Id,
            host.Work.CanonicalTitle,
            host.Work.Year,
            VideoRequestScope.Custom,
            [],
            MonitorFuture: false,
            SelectedSeasonIds: [season.Id]);

        var request = await host.StartAsync(custom);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status);
        Assert.AreEqual(host.EpisodeId, VideoRequestPayload.Parse(request.PayloadJson)?.ActiveWorkEpisodeId);

        host.CompleteInSabnzbd(request, "/downloads/tv/Severance.S01E01");
        await host.Operations.MarkSucceededAsync(request.OperationId!.Value, "Downloaded.");
        await host.ProcessAsync(DateTime.UtcNow);

        var monitoring = await host.GetAsync(request.Id);
        var payload = VideoRequestPayload.Parse(monitoring.PayloadJson)
            ?? throw new AssertFailedException("Expected video request payload.");
        Assert.AreEqual(AcquisitionRequestStatus.Approved, monitoring.Status);
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count);
        Assert.IsTrue(await host.HasPlayableAsync(host.EpisodeId));
        CollectionAssert.Contains(payload.SelectedSeasonIds!, season.Id);
        Assert.IsNotNull(payload.NextSearchUtc);
        Assert.IsNull(payload.ActiveWorkEpisodeId);
    }

    [TestMethod]
    public async Task TvCustomScopeKeepsSelectedFutureEpisodeOpenWithoutFutureMonitoring()
    {
        await using var host = await VideoAcquisitionTestHost.CreateAsync(
            MediaAcquisitionKind.Tv,
            "Severance",
            2022,
            "95396",
            "Severance.S01E02.1080p.WEB-DL.x264-GROUP",
            addEpisode: true,
            addSecondEpisode: true);

        var selectedEpisodeId = host.SecondEpisodeId!.Value;
        var selectedEpisode = await host.Environment.Db.WorkEpisodes.SingleAsync(x => x.Id == selectedEpisodeId);
        selectedEpisode.AiredAt = DateTime.UtcNow.AddDays(2);
        await host.Environment.Db.SaveChangesAsync();

        var custom = new VideoRequestPayload(
            host.Work.Id,
            host.Work.CanonicalTitle,
            host.Work.Year,
            VideoRequestScope.Custom,
            [selectedEpisodeId],
            MonitorFuture: false);

        var request = await host.StartAsync(custom);
        var payload = VideoRequestPayload.Parse(request.PayloadJson)
            ?? throw new AssertFailedException("Expected video request payload.");

        Assert.AreEqual(AcquisitionRequestStatus.Approved, request.Status);
        Assert.AreEqual(0, host.Environment.Client.Grabs.Count);
        Assert.IsNotNull(payload.NextSearchUtc);
        Assert.IsNull(payload.ActiveWorkEpisodeId);
        StringAssert.Contains(request.StatusMessage ?? "", "Waiting for the next requested TV episode");
    }

    [TestMethod]
    public async Task FailedMovieDownloadRetriesTheNextUntriedReleaseOnlyOnce()
    {
        await using var host = await VideoAcquisitionTestHost.CreateAsync(
            MediaAcquisitionKind.Movie,
            "Dune",
            2021,
            "438631",
            "Dune.2021.1080p.WEB-DL.x264-GROUP",
            "Dune.2021.1080p.BluRay.x264-SECOND");

        var request = await host.StartAsync();
        var firstOperation = request.OperationId!.Value;

        await host.Operations.MarkFailedAsync(firstOperation, "Out of retention");
        await host.ProcessAsync(DateTime.UtcNow);

        var retried = await host.GetAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, retried.Status);
        Assert.AreNotEqual(firstOperation, retried.OperationId);
        Assert.AreEqual(2, host.Environment.Client.Grabs.Count);

        await host.ProcessAsync(DateTime.UtcNow.AddMinutes(1));
        Assert.AreEqual(2, host.Environment.Client.Grabs.Count, "An active retry must not submit the same request again.");
    }

    [TestMethod]
    public async Task CancelledMovieDownloadNeverGrabsAReplacement()
    {
        await using var host = await VideoAcquisitionTestHost.CreateAsync(
            MediaAcquisitionKind.Movie,
            "Dune",
            2021,
            "438631",
            "Dune.2021.1080p.WEB-DL.x264-GROUP",
            "Dune.2021.1080p.BluRay.x264-SECOND");

        var request = await host.StartAsync();
        await host.Operations.MarkCancelledAsync(request.OperationId!.Value, "Cancelled by owner.");
        await host.ProcessAsync(DateTime.UtcNow);
        await host.ProcessAsync(DateTime.UtcNow.AddDays(2));

        var failed = await host.GetAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Failed, failed.Status);
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count);
    }
}
