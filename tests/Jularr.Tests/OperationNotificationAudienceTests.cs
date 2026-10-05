using Jularr.Tests.Infrastructure;
using Jularr.Web.Data;
using Jularr.Web.Features.Events;
using Jularr.Web.Features.Operations;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class OperationNotificationAudienceTests
{
    [TestMethod]
    public async Task ProfileOwnedDownloadPublishesProfileEvent()
    {
        await using var db = await CreateDbAsync();
        var events = new RecordingEventPublisher();
        var store = new OperationStore(db, events);

        var operationId = await store.CreateAsync(new OperationDescriptor("book-download", "Download", "Download book", Subject: "Example Book", ProfileId: "reader", IsDownload: true));

        await store.MarkSucceededAsync(operationId, "Downloaded.");

        var published = events.Published.Single();
        Assert.AreEqual(JularrEventCategory.DownloadGrabbed, published.Category);
        Assert.AreEqual(JularrEventAudience.Profile, published.Audience);
        Assert.AreEqual("reader", published.ProfileId);
        Assert.AreEqual(operationId, published.RelatedOperationId);
    }

    [TestMethod]
    public async Task UnownedDownloadDoesNotPublishProfileEvent()
    {
        await using var db = await CreateDbAsync();
        var events = new RecordingEventPublisher();
        var store = new OperationStore(db, events);

        var operationId = await store.CreateAsync(new OperationDescriptor("scheduled-download", "Download", "Background download", Subject: "Example", ProfileId: null, IsDownload: true));

        await store.MarkSucceededAsync(operationId, "Downloaded.");

        Assert.AreEqual(0, events.Published.Count);
        var operation = await store.GetAsync(operationId);
        Assert.IsNotNull(operation);
        Assert.AreEqual(OperationStatus.Succeeded, operation.Status);
    }

    [TestMethod]
    public async Task UnownedImportFailureDoesNotPublishProfileEvent()
    {
        await using var db = await CreateDbAsync();
        var events = new RecordingEventPublisher();
        var store = new OperationStore(db, events);

        var operationId = await store.CreateAsync(new OperationDescriptor("book-import", "Import", "Background import", Subject: "Example", ProfileId: null));

        await store.MarkFailedAsync(operationId, "Import failed.");

        Assert.AreEqual(0, events.Published.Count);
        var operation = await store.GetAsync(operationId);
        Assert.IsNotNull(operation);
        Assert.AreEqual(OperationStatus.Failed, operation.Status);
    }

    private static async Task<AppDbContext> CreateDbAsync()
    {
        var connection = TestPostgres.ResolveConnectionString($"Data Source=notification-audience-{Guid.NewGuid():N}.db");
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        return db;
    }
}
