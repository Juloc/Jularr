using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Tests;

[TestClass]
public sealed class BookWorkAdminTests
{
    [TestMethod]
    public async Task TheAudiobookIsMonitoredWantedAndRequestedApartFromTheBook()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var work = await works.CreateWorkAsync(WorkMediaType.Book, "Frieren", 2020, CancellationToken.None);
        await works.LinkExternalIdentityAsync(work.Id, WorkMediaType.Book, BookCatalogService.CatalogRequestProvider, "c1", 1.0, "test", isPrimary: true, isManualOverride: false, MappingReviewState.Confirmed, CancellationToken.None);
        var requests = new AcquisitionAccessStore(db);
        var query = new BookWorkAdminQuery(db, MonitoringTestSupport.Resolver(db), requests);

        await MonitoringTestSupport.Commands(db).SetAudiobookAsync(work.Id, true, CancellationToken.None);
        await new WantedReconciler(db, TimeProvider.System).ReconcileAsync(work.Id, CancellationToken.None);
        await requests.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Audiobook, BookCatalogService.CatalogRequestProvider, "c1", "Frieren", null, null) { WorkId = work.Id }, "owner", AcquisitionRequestStatus.Approved, "owner", CancellationToken.None);

        var view = (await query.GetAsync(work.Id, CancellationToken.None))!;

        Assert.IsTrue(view.Audiobook.Monitored && view.Audiobook.Wanted);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, view.Audiobook.RequestStatus);
        Assert.IsFalse(view.Book.Monitored || view.Book.Wanted);
        Assert.IsNull(view.Book.RequestStatus, "Requesting the audiobook says nothing about the Book.");
    }
}
