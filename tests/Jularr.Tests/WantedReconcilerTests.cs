using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Monitoring;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

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

    [TestMethod]
    public async Task Source_AFailedRequestOfAMonitoredMovieIsNotOpenedAgainByThePass()
    {
        await using var host = await MovieAsync();
        await MonitoringTestSupport.Commands(host.Environment.Db).SetAsync(MonitoringTargetKind.Work, host.Work.Id, true, CancellationToken.None);
        var failed = await host.CreateApprovedAsync(new VideoRequestPayload(host.Work.Id, host.Work.CanonicalTitle, host.Work.Year));
        await host.Requests.UpdateStatusAsync(failed.Id, AcquisitionRequestStatus.Failed, "Gave up.", null, null, null, CancellationToken.None);
        var source = new VideoWantedSource(MediaAcquisitionKind.Movie, host.Get<WantedReconciler>(), host.Get<Jularr.Web.Features.Acquisition.Monitoring.VideoMonitoringService>());

        Assert.AreEqual(0, await source.PrepareAsync(DateTime.UtcNow, CancellationToken.None), "A request that gave up stays with its owner until they retry it.");
        Assert.ContainsSingle(await host.Requests.ListAllAsync(10, CancellationToken.None));
    }

    private static async Task<AcquisitionRequest> ApprovedWithChoiceAsync(VideoAcquisitionTestHost host, VideoRequestPayload payload, AcquisitionRequestStatus status = AcquisitionRequestStatus.Approved)
    {
        var request = await host.Requests.CreateAsync(
            new AcquisitionRequestDraft(host.Kind, "tmdb", host.TmdbId, host.Work.CanonicalTitle, null, null, payload.Serialize()),
            "owner",
            status,
            status == AcquisitionRequestStatus.Pending ? null : "owner",
            CancellationToken.None);
        await host.Get<RequestIntent>().RecordAsync(request, CancellationToken.None);
        return request;
    }

    [TestMethod]
    public async Task Request_NamedEpisodesAreWantedWithMonitoringOffAndStopWhenTheRequestEnds()
    {
        await using var host = await SeriesAsync();
        var payload = MonitoringTestSupport.Choosing(host.Work.Id, host.Work.CanonicalTitle, host.Work.Year, VideoRequestScope.Custom, [host.SecondEpisodeId!.Value]);

        var request = await ApprovedWithChoiceAsync(host, payload);
        var items = await ListAsync(host);

        Assert.AreEqual(host.SecondEpisodeId, Assert.ContainsSingle(items).TargetId, "Monitoring was never switched on; the request alone wants its episode.");

        await host.Requests.UpdateStatusAsync(request.Id, AcquisitionRequestStatus.Completed, "Imported.", null, null, null, CancellationToken.None);
        Assert.IsEmpty(await ListAsync(host));
    }

    [TestMethod]
    public async Task Request_SwitchingMonitoringOffNeverCancelsAnApprovedRequestButAPendingOneAsksForNothing()
    {
        await using var movie = await MovieAsync();
        var commands = MonitoringTestSupport.Commands(movie.Environment.Db);
        await commands.SetAsync(MonitoringTargetKind.Work, movie.Work.Id, true, CancellationToken.None);
        await ApprovedWithChoiceAsync(movie, MonitoringTestSupport.Choosing(movie.Work.Id, movie.Work.CanonicalTitle, movie.Work.Year, VideoRequestScope.WholeWork));

        await commands.SetAsync(MonitoringTargetKind.Work, movie.Work.Id, false, CancellationToken.None);
        Assert.AreEqual(movie.Work.Id, Assert.ContainsSingle(await ListAsync(movie)).TargetId, "The request still wants the movie.");

        await using var pending = await MovieAsync();
        await ApprovedWithChoiceAsync(pending, MonitoringTestSupport.Choosing(pending.Work.Id, pending.Work.CanonicalTitle, pending.Work.Year, VideoRequestScope.WholeWork), AcquisitionRequestStatus.Pending);
        Assert.IsEmpty(await ListAsync(pending), "A request that waits for approval changes nothing.");
    }

    [TestMethod]
    public async Task Request_TheMonitoringPathKeepsAnApprovedRequestThatNamesEpisodesWhileMonitoringIsOff()
    {
        await using var host = await SeriesAsync();
        var request = await ApprovedWithChoiceAsync(host, MonitoringTestSupport.Choosing(host.Work.Id, host.Work.CanonicalTitle, host.Work.Year, VideoRequestScope.Custom, [host.SecondEpisodeId!.Value]));

        var outcome = await host.Get<VideoMonitoringService>().ReconcileAsync(host.Work.Id, MediaAcquisitionKind.Tv, wake: true, CancellationToken.None);
        var after = await host.GetAsync(request.Id);

        Assert.AreEqual(VideoMonitoringOutcome.Saved, outcome);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, after.Status);
        Assert.IsFalse(VideoRequestPayload.Parse(after.PayloadJson)!.EndedByMonitoring);
        Assert.AreEqual(host.SecondEpisodeId, Assert.ContainsSingle(await ListAsync(host)).TargetId);
    }

    [TestMethod]
    public async Task Request_AnOpenRequestWithoutIntentEndsWhenMonitoringIsOff()
    {
        await using var host = await SeriesAsync();
        var request = await host.CreateApprovedAsync(new VideoRequestPayload(host.Work.Id, host.Work.CanonicalTitle, host.Work.Year));

        await host.Get<VideoMonitoringService>().ReconcileAsync(host.Work.Id, MediaAcquisitionKind.Tv, wake: true, CancellationToken.None);

        Assert.AreNotEqual(AcquisitionRequestStatus.Approved, (await host.GetAsync(request.Id)).Status);
    }

    [TestMethod]
    public async Task Intent_APlainRequestIsTheWholeTitleAndOneThatMonitoringOpenedNamesNothing()
    {
        await using var plain = await MovieAsync();
        var plainRequest = await plain.Requests.CreateAsync(new AcquisitionRequestDraft(plain.Kind, "tmdb", plain.TmdbId, plain.Work.CanonicalTitle, null, null), "owner", AcquisitionRequestStatus.Approved, "owner", CancellationToken.None);
        await plain.Get<RequestIntent>().RecordAsync(plainRequest, CancellationToken.None);
        Assert.AreEqual(plain.Work.Id, Assert.ContainsSingle(await ListAsync(plain)).TargetId, "Without a payload the executor reads the request as the whole title.");

        await using var opened = await MovieAsync();
        var seed = new VideoRequestPayload(opened.Work.Id, opened.Work.CanonicalTitle, opened.Work.Year);
        var monitoringRequest = await opened.CreateApprovedAsync(seed);
        await opened.Get<RequestIntent>().RecordAsync(monitoringRequest, CancellationToken.None);
        Assert.IsFalse(await opened.Get<RequestIntent>().HasAsync(monitoringRequest.Id, CancellationToken.None), "A request Monitoring opened asks for nothing of its own.");
    }

    [TestMethod]
    public void WantedStatementsLiveInWantedSqlAndTakeNamedParametersOnly()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
        {
            directory = directory.Parent;
        }

        var wanted = Path.Combine(directory!.FullName, "src", "Jularr.Web", "Features", "Acquisition", "Wanted");
        var positional = new Regex(@"\{\d+\}");
        var rawSqlOutsideWantedSql = new Regex(@"ExecuteSqlInterpolated|\bSqlQuery<|(ExecuteSqlRawAsync|SqlQueryRaw<[^>]+>)\((?>\s*)(?!WantedSql\.)");
        Assert.IsTrue(positional.IsMatch("WHERE x = {0}") && rawSqlOutsideWantedSql.IsMatch("db.Database.ExecuteSqlRawAsync(\"DELETE\", ") && !rawSqlOutsideWantedSql.IsMatch("db.Database.ExecuteSqlRawAsync(WantedSql.Reconcile, "), "The guard patterns must recognise what they forbid.");

        var files = Directory.EnumerateFiles(wanted, "*.cs").ToArray();
        var withPositional = files.Where(path => positional.IsMatch(File.ReadAllText(path))).Select(Path.GetFileName);
        var withOtherSql = files.Where(path => rawSqlOutsideWantedSql.IsMatch(File.ReadAllText(path))).Select(Path.GetFileName);

        Assert.IsEmpty(withPositional, "A positional placeholder hides what each parameter is; use a named parameter.");
        Assert.IsEmpty(withOtherSql, "Wanted statements belong in WantedSql.");
    }
}
