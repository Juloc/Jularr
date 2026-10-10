using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Monitoring;
using Jularr.Web.Features.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

[TestClass]
public sealed class RequestManagementTests
{
    private static AcquisitionRequestService Manager(AcquisitionAccessFixture fixture, params IAcquisitionRequestExecutor[] executors) =>
        new(fixture.Store, executors, AcquisitionAccessFixture.Account("owner", AccountRole.Owner), new MediaCapabilityService(fixture.Capabilities), fixture.Settings, fixture.Events,
            NullLogger<AcquisitionRequestService>.Instance, intent: new RequestIntent(fixture.Db, TimeProvider.System, new WantedReconciler(fixture.Db, TimeProvider.System)));

    [TestMethod]
    public async Task DeleteRemovesOnlyExclusiveIntentAndReconcilesWantedWhilePreservingMonitoringAndMedia()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var work = new Work { CanonicalTitle = "Dune", MediaType = WorkMediaType.Movie };
        fixture.Db.Works.Add(work);
        await fixture.Db.SaveChangesAsync();
        var request = await fixture.Store.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Movie, "tmdb", "438631", "Dune", null, null, WorkId: work.Id), "alice", AcquisitionRequestStatus.Approved, "owner", CancellationToken.None);
        var wanted = new WantedReconciler(fixture.Db, TimeProvider.System);
        var intent = new RequestIntent(fixture.Db, TimeProvider.System, wanted);
        await intent.RecordAsync(request, CancellationToken.None);
        await wanted.ReconcileAsync(work.Id, CancellationToken.None);
        Assert.ContainsSingle(await fixture.Db.WantedItems.ToListAsync());

        var manager = Manager(fixture);
        Assert.AreEqual(RequestDeleteOutcome.Deleted, await manager.DeleteAsync(request.Id, CancellationToken.None));
        Assert.IsNull(await fixture.Store.GetAsync(request.Id, CancellationToken.None));
        Assert.IsEmpty(await fixture.Db.WantedItems.AsNoTracking().ToListAsync());
        Assert.ContainsSingle(await fixture.Db.Works.ToListAsync());
        Assert.AreEqual(RequestDeleteOutcome.AlreadyDeleted, await manager.DeleteAsync(request.Id, CancellationToken.None));

        await MonitoringTestSupport.Commands(fixture.Db).SetWorkAsync(work.Id, true, CancellationToken.None);
        var monitored = await fixture.Store.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Movie, "tmdb", "438631", "Dune", null, null, WorkId: work.Id), "bob", AcquisitionRequestStatus.Approved, "owner", CancellationToken.None);
        await intent.RecordAsync(monitored, CancellationToken.None);
        await wanted.ReconcileAsync(work.Id, CancellationToken.None);
        Assert.AreEqual(RequestDeleteOutcome.Deleted, await manager.DeleteAsync(monitored.Id, CancellationToken.None));
        Assert.ContainsSingle(await fixture.Db.WantedItems.AsNoTracking().ToListAsync(), "Independent monitoring still wants the Work.");
        await Assert.ThrowsExactlyAsync<AcquisitionAccessDeniedException>(() => fixture.Service("bob", false).DeleteAsync(monitored.Id, CancellationToken.None));
    }

    [TestMethod]
    public async Task ActiveOrRecoverableDownloadsBlockDeletionAndKeepIntent()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var work = new Work { CanonicalTitle = "Movie", MediaType = WorkMediaType.Movie };
        fixture.Db.Works.Add(work);
        await fixture.Db.SaveChangesAsync();
        var operation = await new OperationStore(fixture.Db).CreateAsync(new OperationDescriptor("download", "Download", "Movie", IsDownload: true), CancellationToken.None);
        var request = await fixture.Store.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Movie, "test", "1", "Movie", null, null, WorkId: work.Id), "alice", AcquisitionRequestStatus.Approved, "owner", CancellationToken.None);
        await new RequestIntent(fixture.Db, TimeProvider.System).RecordAsync(request, CancellationToken.None);
        await fixture.Store.UpdateStatusAsync(request.Id, AcquisitionRequestStatus.Failed, "Interrupted", operation, null, null, CancellationToken.None);
        Assert.AreEqual(RequestDeleteOutcome.StateChangedOrActiveDownload, await Manager(fixture).DeleteAsync(request.Id, CancellationToken.None));
        Assert.IsNotNull(await fixture.Store.GetAsync(request.Id, CancellationToken.None));
        Assert.IsTrue(await new RequestIntent(fixture.Db, TimeProvider.System).HasAsync(request.Id, CancellationToken.None));
        Assert.IsNotNull(await new OperationStore(fixture.Db).GetAsync(operation, CancellationToken.None));
    }

    [TestMethod]
    public async Task DeletingCompletedRequestPreservesInstalledVersionAssetAndPhysicalFile()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var work = new Work { CanonicalTitle = "Installed movie", MediaType = WorkMediaType.Movie };
        fixture.Db.Works.Add(work);
        await fixture.Db.SaveChangesAsync();
        var path = Path.Combine(Path.GetTempPath(), $"jularr-request-delete-{Guid.NewGuid():N}.mkv");
        await File.WriteAllTextAsync(path, "test-only installed media");
        try
        {
            await new CanonicalMediaStorageService(fixture.Db).AttachVideoAsync(work.Id, null, path, null, CancellationToken.None);
            var draft = new AcquisitionRequestDraft(MediaAcquisitionKind.Movie, "test", "installed", work.CanonicalTitle, null, null, WorkId: work.Id);
            var request = await fixture.Store.CreateAsync(draft, "alice", AcquisitionRequestStatus.Completed, "owner", CancellationToken.None);
            Assert.AreEqual(RequestDeleteOutcome.Deleted, await Manager(fixture).DeleteAsync(request.Id, CancellationToken.None));
            Assert.IsTrue(File.Exists(path));
            Assert.AreEqual("test-only installed media", await File.ReadAllTextAsync(path));
            Assert.ContainsSingle(await fixture.Db.Works.AsNoTracking().ToListAsync());
            Assert.ContainsSingle(await fixture.Db.WorkVersions.AsNoTracking().ToListAsync());
            Assert.ContainsSingle(await fixture.Db.MediaAssets.AsNoTracking().ToListAsync());
            Assert.ContainsSingle(await fixture.Db.StoredFiles.AsNoTracking().ToListAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task BulkReportsSuccessSkippedAndAcquisitionFailureWithoutLosingOtherTargets()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var first = await fixture.Store.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Book, "test", "1", "First", null, null), "alice", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        var failed = await fixture.Store.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Manga, "test", "2", "Failure", null, null), "bob", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        var done = await fixture.Store.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Book, "test", "3", "Done", null, null), "alice", AcquisitionRequestStatus.Completed, null, CancellationToken.None);
        var manager = Manager(fixture, new RecordingExecutor(MediaAcquisitionKind.Manga, fail: true));
        var results = await manager.BulkAsync([first.Id, failed.Id, done.Id, Guid.NewGuid()], RequestBulkAction.Approve, null, null, CancellationToken.None);
        Assert.AreEqual(1, results.Count(result => result.Disposition == RequestActionDisposition.Succeeded));
        Assert.AreEqual(1, results.Count(result => result.Disposition == RequestActionDisposition.Failed));
        Assert.AreEqual(2, results.Count(result => result.Disposition == RequestActionDisposition.Skipped));
        Assert.AreEqual(AcquisitionRequestStatus.Completed, (await fixture.Store.GetAsync(done.Id, CancellationToken.None))!.Status);
        await Assert.ThrowsExactlyAsync<AcquisitionAccessDeniedException>(() => fixture.Service("alice", false).BulkAsync([first.Id], RequestBulkAction.Delete, null, null, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => manager.BulkAsync(Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray(), RequestBulkAction.Delete, null, null, CancellationToken.None));
    }

    [TestMethod]
    [DataRow(RequestBulkAction.Retry, AcquisitionRequestStatus.Failed, AcquisitionRequestStatus.Pending, AcquisitionRequestStatus.Downloading)]
    [DataRow(RequestBulkAction.Cancel, AcquisitionRequestStatus.Pending, AcquisitionRequestStatus.Approved, AcquisitionRequestStatus.Rejected)]
    [DataRow(RequestBulkAction.Complete, AcquisitionRequestStatus.Pending, AcquisitionRequestStatus.Downloading, AcquisitionRequestStatus.Completed)]
    [DataRow(RequestBulkAction.Delete, AcquisitionRequestStatus.Pending, AcquisitionRequestStatus.Downloading, AcquisitionRequestStatus.Pending)]
    public async Task BulkActionsApplyOnlyToEligibleRequests(RequestBulkAction action, AcquisitionRequestStatus eligibleStatus, AcquisitionRequestStatus blockedStatus, AcquisitionRequestStatus expectedStatus)
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var eligible = await fixture.Store.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Book, "test", "eligible", "Eligible", null, null), "alice", eligibleStatus, null, CancellationToken.None);
        var blocked = await fixture.Store.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Book, "test", "blocked", "Blocked", null, null), "alice", blockedStatus, null, CancellationToken.None);
        var executor = new RecordingExecutor(MediaAcquisitionKind.Book);
        var results = await Manager(fixture, executor).BulkAsync([eligible.Id, blocked.Id, eligible.Id], action, null, null, CancellationToken.None);
        Assert.AreEqual(2, results.Count);
        Assert.AreEqual(RequestActionDisposition.Succeeded, results[0].Disposition);
        Assert.AreEqual(RequestActionDisposition.Skipped, results[1].Disposition);
        Assert.AreEqual(blockedStatus, (await fixture.Store.GetAsync(blocked.Id, CancellationToken.None))!.Status);
        var updated = await fixture.Store.GetAsync(eligible.Id, CancellationToken.None);
        if (action == RequestBulkAction.Delete)
        {
            Assert.IsNull(updated);
        }
        else
        {
            Assert.AreEqual(expectedStatus, updated!.Status);
        }

        Assert.AreEqual(action == RequestBulkAction.Retry ? 1 : 0, executor.Runs);
    }

    [TestMethod]
    public async Task BulkProfileUsesTheNumericWorkAssignmentAndSkipsActiveRequests()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var directory = Directory.CreateTempSubdirectory("jularr-bulk-profile-");
        try
        {
            var registry = new MediaAcquisitionRegistry([new BookAcquisitionRegistration()]);
            var profiles = new QualityProfileStore(directory, registry);
            await profiles.UpsertAsync(registry.DefaultProfileFor(MediaAcquisitionKind.Book) with { Id = "strict", Name = "Strict" });
            var work = new Work { CanonicalTitle = "Book", MediaType = WorkMediaType.Book };
            fixture.Db.Works.Add(work);
            await fixture.Db.SaveChangesAsync();
            var eligible = await fixture.Store.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Book, "test", "eligible", "Book", null, null, WorkId: work.Id), "alice", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
            var active = await fixture.Store.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Book, "test", "active", "Active", null, null, WorkId: work.Id), "alice", AcquisitionRequestStatus.Downloading, null, CancellationToken.None);
            var assignment = new RequestProfileAssignment(profiles, RequestWorkTestSupport.Binder(fixture.Db), new VideoRequestWorkResolver(fixture.Db), fixture.Store);
            var manager = new AcquisitionRequestService(fixture.Store, [], AcquisitionAccessFixture.Account("owner", AccountRole.Owner), new MediaCapabilityService(fixture.Capabilities), fixture.Settings, fixture.Events,
                NullLogger<AcquisitionRequestService>.Instance, profileAssignment: assignment);
            var results = await manager.BulkAsync([eligible.Id, active.Id], RequestBulkAction.Profile, null, "strict", CancellationToken.None);
            Assert.AreEqual(RequestActionDisposition.Succeeded, results[0].Disposition);
            Assert.AreEqual(RequestActionDisposition.Skipped, results[1].Disposition);
            Assert.AreEqual("strict", (await profiles.ResolveAsync(MediaAcquisitionKind.Book, work.Id)).Id);
            Assert.AreEqual(AcquisitionRequestStatus.Pending, (await fixture.Store.GetAsync(eligible.Id, CancellationToken.None))!.Status);
            Assert.AreEqual(AcquisitionRequestStatus.Downloading, (await fixture.Store.GetAsync(active.Id, CancellationToken.None))!.Status);
            Assert.AreEqual(1, await fixture.Db.Works.CountAsync());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task DeletingCompletedRequestPreservesOtherRequestIntentAndOperationHistory()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var work = new Work { CanonicalTitle = "Shared movie", MediaType = WorkMediaType.Movie };
        fixture.Db.Works.Add(work);
        await fixture.Db.SaveChangesAsync();
        var operations = new OperationStore(fixture.Db);
        var operationId = await operations.CreateAsync(new OperationDescriptor("download", "Download", "Imported movie", IsDownload: true), CancellationToken.None);
        await operations.MarkSucceededAsync(operationId, cancellationToken: CancellationToken.None);
        var removed = await fixture.Store.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Movie, "test", "completed", work.CanonicalTitle, null, null, WorkId: work.Id), "alice", AcquisitionRequestStatus.Approved, "owner", CancellationToken.None);
        var preserved = await fixture.Store.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Movie, "test", "other", work.CanonicalTitle, null, null, WorkId: work.Id), "bob", AcquisitionRequestStatus.Approved, "owner", CancellationToken.None);
        var wanted = new WantedReconciler(fixture.Db, TimeProvider.System);
        var intent = new RequestIntent(fixture.Db, TimeProvider.System, wanted);
        await intent.RecordAsync(removed, CancellationToken.None);
        await intent.RecordAsync(preserved, CancellationToken.None);
        await fixture.Store.UpdateStatusAsync(removed.Id, AcquisitionRequestStatus.Completed, "Imported", operationId, null, "owner", CancellationToken.None);
        await wanted.ReconcileAsync(work.Id, CancellationToken.None);
        Assert.AreEqual(RequestDeleteOutcome.Deleted, await Manager(fixture).DeleteAsync(removed.Id, CancellationToken.None));
        Assert.IsNotNull(await fixture.Store.GetAsync(preserved.Id, CancellationToken.None));
        Assert.IsTrue(await intent.HasAsync(preserved.Id, CancellationToken.None));
        Assert.ContainsSingle(await fixture.Db.WantedItems.AsNoTracking().ToListAsync());
        Assert.IsNotNull(await operations.GetAsync(operationId, CancellationToken.None));
        Assert.IsNotEmpty(await operations.ListLogsAsync(new OperationLogFilter(OperationId: operationId), CancellationToken.None));
        Assert.IsNotNull(await fixture.Db.Works.FindAsync(work.Id));
    }

    [TestMethod]
    public async Task StaleDeleteAndMixedBulkRejectPreserveConcurrentDecisions()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var pending = await fixture.Store.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Book, "test", "pending", "Pending", null, null), "alice", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        var complete = await fixture.Store.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Book, "test", "done", "Done", null, null), "alice", AcquisitionRequestStatus.Completed, "owner", CancellationToken.None);
        await fixture.Store.UpdateStatusAsync(pending.Id, AcquisitionRequestStatus.Approved, null, null, null, "owner", CancellationToken.None);
        Assert.IsFalse(await fixture.Store.TryDeleteAsync(pending, CancellationToken.None));
        await fixture.Store.UpdateStatusAsync(pending.Id, AcquisitionRequestStatus.Pending, null, null, null, null, CancellationToken.None);
        var results = await Manager(fixture).BulkAsync([pending.Id, complete.Id], RequestBulkAction.Reject, "Not requested by policy", null, CancellationToken.None);
        Assert.AreEqual(RequestActionDisposition.Succeeded, results[0].Disposition);
        Assert.AreEqual(RequestActionDisposition.Skipped, results[1].Disposition);
        Assert.AreEqual("Not requested by policy", (await fixture.Store.GetAsync(pending.Id, CancellationToken.None))!.StatusMessage);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, (await fixture.Store.GetAsync(complete.Id, CancellationToken.None))!.Status);
    }
}
