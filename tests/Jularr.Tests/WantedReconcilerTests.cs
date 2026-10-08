using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class WantedReconcilerTests
{
    private static Task<VideoAcquisitionTestHost> MovieAsync() =>
        VideoAcquisitionTestHost.CreateAsync(MediaAcquisitionKind.Movie, "Dune", 2021, "438631", "Dune.2021.1080p.WEB-DL.x264-GROUP");

    private static Task<VideoAcquisitionTestHost> SeriesAsync() =>
        VideoAcquisitionTestHost.CreateAsync(MediaAcquisitionKind.Tv, "Severance", 2022, "95396", "Severance.S01E01.1080p.WEB-DL.x264-GROUP", addEpisode: true, addSecondEpisode: true);

    private static async Task<IReadOnlyList<WantedItem>> ListAsync(VideoAcquisitionTestHost host)
    {
        await host.Get<WantedReconciler>().ReconcileAsync(host.Work.Id, CancellationToken.None);
        return await host.Environment.Db.WantedItems.AsNoTracking().Where(x => x.WorkId == host.Work.Id).ToListAsync();
    }

    [TestMethod]
    public async Task Movie_WantedOnlyWhileMonitoredAndMissing()
    {
        await using var host = await MovieAsync();
        var commands = MonitoringTestSupport.Commands(host.Environment.Db);

        Assert.IsEmpty(await ListAsync(host));

        await commands.SetAsync(MonitoringTargetKind.Work, host.Work.Id, true, CancellationToken.None);
        var target = Assert.ContainsSingle(await ListAsync(host));
        Assert.AreEqual((WantedTargetKind.Work, host.Work.Id), (target.TargetKind, target.TargetId));

        var again = await ListAsync(host);
        Assert.AreEqual(target.Id, Assert.ContainsSingle(again).Id, "A second reconcile keeps the same item.");

        await host.AttachFileAsync(null);
        Assert.IsEmpty(await ListAsync(host));

        await commands.SetAsync(MonitoringTargetKind.Work, host.Work.Id, false, CancellationToken.None);
        Assert.IsEmpty(await ListAsync(host));
    }

    [TestMethod]
    public async Task Series_EpisodesFollowDecisionsAndOnlyAiredMissingOnesAreWanted()
    {
        await using var host = await SeriesAsync();
        var commands = MonitoringTestSupport.Commands(host.Environment.Db);
        var upcoming = await host.AddEpisodeAsync(1, 3, DateTime.UtcNow.AddDays(5));
        await commands.SetAsync(MonitoringTargetKind.Work, host.Work.Id, true, CancellationToken.None);

        var ids = (await ListAsync(host)).Select(x => x.TargetId).ToHashSet();
        CollectionAssert.AreEquivalent(new[] { host.EpisodeId!.Value, host.SecondEpisodeId!.Value }, ids.ToArray());
        Assert.DoesNotContain(upcoming.Id, ids);

        await commands.SetAsync(MonitoringTargetKind.Episode, host.SecondEpisodeId!.Value, false, CancellationToken.None);
        await host.AttachFileAsync(host.EpisodeId);
        Assert.IsEmpty(await ListAsync(host));

        await commands.SetAsync(MonitoringTargetKind.Episode, host.SecondEpisodeId!.Value, null, CancellationToken.None);
        Assert.AreEqual(host.SecondEpisodeId, Assert.ContainsSingle(await ListAsync(host)).TargetId);
    }

    [TestMethod]
    public async Task Source_RelationsReachingOneMovieOpenOneRequestAndALostFileReopensIt()
    {
        await using var host = await MovieAsync();
        var db = host.Environment.Db;
        db.Set<WorkCredit>().Add(new WorkCredit { WorkId = host.Work.Id, Kind = WorkCreditKind.Crew, Position = 0, Name = "p1", Role = "Director", Source = "tmdb", ProviderPersonId = "p1", FetchedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "WorkMetadataFacts" ("WorkId", "OriginalLanguage", "Studios", "ProductionCountries", "UpdatedAt") VALUES ({host.Work.Id}, 'en', ARRAY['Studio X'], ARRAY[]::text[], now())""");
        var commands = MonitoringTestSupport.Commands(db);
        await commands.SetRelationAsync(new MonitoringRelationSource(MonitoringRelationKind.Person, "p1", "Pat", null, "owner"), true, false, CancellationToken.None);
        await commands.SetRelationAsync(new MonitoringRelationSource(MonitoringRelationKind.Studio, "studio x", "Studio X", null, "owner"), true, false, CancellationToken.None);
        var source = new VideoWantedSource(MediaAcquisitionKind.Movie, host.Get<WantedReconciler>(), host.Get<Jularr.Web.Features.Acquisition.Monitoring.VideoMonitoringService>());

        Assert.AreEqual((1, 0), (await source.PrepareAsync(DateTime.UtcNow, CancellationToken.None), await source.PrepareAsync(DateTime.UtcNow, CancellationToken.None)));
        var first = Assert.ContainsSingle(await host.Requests.ListAllAsync(10, CancellationToken.None));

        await host.AttachFileAsync(null);
        await host.Requests.UpdateStatusAsync(first.Id, AcquisitionRequestStatus.Completed, "Imported.", null, null, null, CancellationToken.None);
        Assert.AreEqual(0, await source.PrepareAsync(DateTime.UtcNow, CancellationToken.None));

        db.StoredFiles.RemoveRange(db.StoredFiles);
        await db.SaveChangesAsync();
        Assert.AreEqual(1, await source.PrepareAsync(DateTime.UtcNow, CancellationToken.None));
        Assert.AreEqual(1, (await host.Requests.ListAllAsync(10, CancellationToken.None)).Count(request => request.IsOpen));
    }
}
