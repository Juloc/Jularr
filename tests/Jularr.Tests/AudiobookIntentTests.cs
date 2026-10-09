using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Audiobooks;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class AudiobookIntentTests
{
    private static async Task<Work> BookAsync(AppDbContext db) =>
        await new WorkService(db).CreateWorkAsync(WorkMediaType.Book, "Frieren", 2020, CancellationToken.None);

    private static async Task<Guid> EditionOfAsync(AppDbContext db, Work work) =>
        (await db.WorkEditions.AsNoTracking().SingleAsync(edition => edition.WorkId == work.Id && edition.Format == "audiobook")).Id;

    private static async Task<List<WantedItem>> WantedAsync(AppDbContext db, Work work, WantedReconciler? reconciler = null)
    {
        await (reconciler ?? new WantedReconciler(db, TimeProvider.System)).ReconcileAsync(work.Id, CancellationToken.None);
        return await db.WantedItems.AsNoTracking().Where(item => item.WorkId == work.Id).ToListAsync();
    }

    private static async Task InstallAsync(AppDbContext db, Work work, string format)
    {
        var audiobook = new Audiobook { Key = $"frieren-{format}", Title = "Frieren" };
        db.Add(audiobook);
        db.Add(new AudiobookFile { AudiobookId = audiobook.Id, FileKey = "a", FileName = $"a.{format}", Format = format });
        db.WorkSourceLinks.Add(new WorkSourceLink { WorkId = work.Id, SourceKind = WorkSourceKind.Audiobook, SourceId = audiobook.Id });
        await db.SaveChangesAsync();
    }

    [TestMethod]
    public async Task MonitoringTheBookNeverWantsItsAudiobook()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var work = await BookAsync(db);
        await MonitoringTestSupport.Commands(db).SetAsync(MonitoringTargetKind.Work, work.Id, true, CancellationToken.None);

        var wanted = await WantedAsync(db, work);

        Assert.AreEqual(WantedTargetKind.Work, wanted.Single().TargetKind, "Only the book itself is wanted.");
        Assert.IsFalse(await db.WorkEditions.AnyAsync(edition => edition.WorkId == work.Id), "No audio edition is made for a monitored book.");
    }

    [TestMethod]
    public async Task MonitoringTheAudiobookWantsItsTypedEditionBeforeAnyFileAndTheBooksDecisionNeverTouchesIt()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var work = await BookAsync(db);
        var commands = MonitoringTestSupport.Commands(db);

        Assert.AreEqual(work.Id, await commands.SetAudiobookAsync(work.Id, true, CancellationToken.None));
        var edition = await db.WorkEditions.AsNoTracking().SingleAsync(row => row.WorkId == work.Id);
        Assert.AreEqual("audiobook", edition.Format);
        Assert.AreEqual("audiobook", edition.EditionKey);
        var wanted = await WantedAsync(db, work);
        Assert.AreEqual((WantedTargetKind.Edition, edition.Id), (wanted.Single().TargetKind, wanted.Single().TargetId), "The audio edition is wanted although the book is not monitored and no file exists.");

        foreach (var bookDecision in new bool?[] { true, false, null })
        {
            await commands.SetAsync(MonitoringTargetKind.Work, work.Id, bookDecision, CancellationToken.None);
        }

        await commands.FutureAsync(work.Id, CancellationToken.None);
        Assert.IsTrue((await MonitoringTestSupport.Resolver(db).LoadAsync(work.Id, CancellationToken.None)).IsEditionMonitored(edition.Id), "A decision on the book never replaces the audiobook's.");

        await commands.SetAudiobookAsync(work.Id, false, CancellationToken.None);
        Assert.IsFalse((await WantedAsync(db, work)).Any(item => item.TargetKind == WantedTargetKind.Edition));
        Assert.IsNull(await commands.SetAudiobookAsync(Guid.NewGuid(), true, CancellationToken.None), "Only an existing Book has an audiobook.");
    }

    [TestMethod]
    public async Task AnAudiobookRequestNamesTheTypedEditionNotTheWork()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var work = await BookAsync(db);
        var request = await new AcquisitionAccessStore(db).CreateAsync(
            new AcquisitionRequestDraft(MediaAcquisitionKind.Audiobook, "catalog", "c1", "Frieren", null, null) { WorkId = work.Id },
            "owner",
            AcquisitionRequestStatus.Approved,
            "owner",
            CancellationToken.None);

        await new RequestIntent(db, TimeProvider.System).RecordAsync(request, CancellationToken.None);

        var edition = await EditionOfAsync(db, work);
        var target = await db.Database.SqlQuery<string>($"""SELECT "TargetKind"::text || ':' || "TargetId"::text AS "Value" FROM "RequestTargets" WHERE "RequestId" = {request.Id.ToString()}""").SingleAsync();
        Assert.AreEqual($"4:{edition}", target);
        Assert.AreEqual(edition, (await WantedAsync(db, work)).Single().TargetId);
    }

    [TestMethod]
    public async Task AnInstalledMp3IsAnUpgradeWhereTheProfileWantsAnM4bAndAnM4bIsFinal()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var work = await BookAsync(db);
        await MonitoringTestSupport.Commands(db).SetAudiobookAsync(work.Id, true, CancellationToken.None);
        var profiles = new QualityProfileStore(new DirectoryInfo(Path.Combine(Path.GetTempPath(), "jularr-audiobook-upgrade-" + Guid.NewGuid().ToString("N"))), new MediaAcquisitionRegistry([new AudiobookAcquisitionRegistration()]));
        var reconciler = new WantedReconciler(db, TimeProvider.System, null, new UpgradeAssessors([new AudiobookUpgradeAssessor(db, profiles)]));

        await InstallAsync(db, work, "MP3");
        Assert.AreEqual(WantedTargetKind.Edition, (await WantedAsync(db, work, reconciler)).Single().TargetKind, "An MP3 is below the M4B cutoff of the audiobook profile.");

        await InstallAsync(db, work, "M4B");
        Assert.IsEmpty(await WantedAsync(db, work, reconciler), "With an M4B the audiobook is final.");
    }

    [TestMethod]
    public async Task AMonitoredAudiobookOpensAnAudiobookRequestAndMonitoringTheBookOpensOnlyABookRequest()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var work = await BookAsync(db);
        await works.LinkExternalIdentityAsync(work.Id, WorkMediaType.Book, Jularr.Web.Features.Books.BookCatalogService.CatalogRequestProvider, "c1", 1.0, "test", isPrimary: true, isManualOverride: false, MappingReviewState.Confirmed, CancellationToken.None);
        var commands = MonitoringTestSupport.Commands(db);
        var requests = new AcquisitionAccessStore(db);
        async Task<int> PassAsync(MediaAcquisitionKind kind) =>
            await new WantedRequestSource(kind, new WantedReconciler(db, TimeProvider.System), requests, new IdentityRequestDrafter(kind, db)).PrepareAsync(DateTime.UtcNow, CancellationToken.None);

        await commands.SetAsync(MonitoringTargetKind.Work, work.Id, true, CancellationToken.None);
        Assert.AreEqual(0, await PassAsync(MediaAcquisitionKind.Audiobook), "Monitoring the book asks for no audiobook.");
        Assert.AreEqual(1, await PassAsync(MediaAcquisitionKind.Book));

        await commands.SetAudiobookAsync(work.Id, true, CancellationToken.None);
        Assert.AreEqual(1, await PassAsync(MediaAcquisitionKind.Audiobook));
        var request = (await requests.ListAllAsync(10, CancellationToken.None)).Single(item => item.Kind == MediaAcquisitionKind.Audiobook);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, request.Status);
        Assert.AreEqual(0, await PassAsync(MediaAcquisitionKind.Audiobook), "The open request carries the edition.");
    }
}
