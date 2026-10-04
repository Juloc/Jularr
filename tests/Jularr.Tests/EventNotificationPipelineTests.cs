using System.Security.Claims;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Events;
using Jularr.Web.Features.Notifications;
using Jularr.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>
/// The unified event and notification system (#429): one event boundary fans out to profile channels
/// according to the canonical notification preference model, deduplicates In-App rows and isolates a
/// broken channel from the event's caller.
/// </summary>
[TestClass]
public sealed class EventNotificationPipelineTests
{
    [TestMethod]
    public async Task PublishingAProfileEventCreatesExactlyOneUnreadNotification()
    {
        await using var fixture = await Fixture.CreateAsync();

        var domainEvent = JularrEvent.Create(
            JularrEventCategory.DownloadGrabbed,
            profileId: "reader",
            messageParams: new Dictionary<string, string> { ["title"] = "Example Anime" });
        await fixture.Publisher.PublishAsync(domainEvent);

        var items = await fixture.Notifications.ListAsync("reader", unreadOnly: false);
        Assert.AreEqual(1, items.Count);
        Assert.AreEqual(JularrEventCategory.DownloadGrabbed, items[0].Category);
        Assert.IsFalse(items[0].IsRead);
        Assert.AreEqual(1, items[0].OccurrenceCount);

        var logged = await fixture.EventLog.ListRecentAsync(10);
        Assert.AreEqual(1, logged.Count);
        Assert.AreEqual(domainEvent.Id, logged[0].Id);
    }

    [TestMethod]
    public async Task DisabledPreferenceSuppressesDeliveryButNotTheAuditLog()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Subscriptions.SetEventPreferenceAsync("reader", JularrEventCategory.DownloadFailed, new NotificationEventPreferenceUpdate(false, Channels(NotificationChannel.InApp), NotificationDeliveryTiming.Immediate));

        await fixture.Publisher.PublishAsync(JularrEvent.Create(JularrEventCategory.DownloadFailed, profileId: "reader"));

        Assert.AreEqual(0, (await fixture.Notifications.ListAsync("reader", unreadOnly: false)).Count);
        Assert.AreEqual(1, (await fixture.EventLog.ListRecentAsync(10)).Count);
    }

    [TestMethod]
    public async Task ProfileInAppGateSuppressesInboxWithoutErasingEventSelection()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Subscriptions.SetProfileChannelEnabledAsync("reader", NotificationChannel.InApp, false);

        await fixture.Publisher.PublishAsync(JularrEvent.Create(JularrEventCategory.ReleaseAvailable, profileId: "reader"));

        Assert.AreEqual(0, (await fixture.Notifications.ListAsync("reader", unreadOnly: false)).Count);
        Assert.AreEqual(1, (await fixture.EventLog.ListRecentAsync(10)).Count);

        var preference = await fixture.Subscriptions.GetEventPreferenceAsync("reader", JularrEventCategory.ReleaseAvailable);
        CollectionAssert.AreEquivalent(new[] { NotificationChannel.InApp }, preference.Channels.ToArray());
    }

    [TestMethod]
    public async Task RepeatedEventWithTheSameDedupKeyBumpsOccurrenceInsteadOfSpamming()
    {
        await using var fixture = await Fixture.CreateAsync();

        for (var i = 0; i < 3; i++)
        {
            await fixture.Publisher.PublishAsync(
                JularrEvent.Create(
                    JularrEventCategory.ImportFailed,
                    profileId: "reader",
                    dedupKey: "operation:abc"));
        }

        var items = await fixture.Notifications.ListAsync("reader", unreadOnly: false);
        Assert.AreEqual(1, items.Count, "A replayed/duplicate event must not create a second row.");
        Assert.AreEqual(3, items[0].OccurrenceCount);
    }

    [TestMethod]
    public async Task MarkingReadThenRepublishingTheSameDedupKeyBringsItBackToUnread()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.Publisher.PublishAsync(
            JularrEvent.Create(JularrEventCategory.ImportFailed, profileId: "reader", dedupKey: "operation:abc"));
        var first = (await fixture.Notifications.ListAsync("reader", unreadOnly: false)).Single();
        Assert.IsTrue(await fixture.Notifications.MarkReadAsync(first.Id, "reader"));

        await fixture.Publisher.PublishAsync(
            JularrEvent.Create(JularrEventCategory.ImportFailed, profileId: "reader", dedupKey: "operation:abc"));

        var again = (await fixture.Notifications.ListAsync("reader", unreadOnly: true)).Single();
        Assert.AreEqual(first.Id, again.Id);
        Assert.AreEqual(2, again.OccurrenceCount);
    }

    [TestMethod]
    public async Task AdminAudienceEventReachesOwnersAndMediaManagersOnlyNotPlainProfiles()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.Publisher.PublishAsync(
            JularrEvent.Create(
                JularrEventCategory.StorageProblem,
                messageParams: new Dictionary<string, string> { ["message"] = "root offline" }));

        Assert.AreEqual(1, (await fixture.Notifications.ListAsync("owner", unreadOnly: false)).Count);
        Assert.AreEqual(1, (await fixture.Notifications.ListAsync("manager", unreadOnly: false)).Count);
        Assert.AreEqual(0, (await fixture.Notifications.ListAsync("reader", unreadOnly: false)).Count);
    }

    [TestMethod]
    public void ProfileEventCreationRequiresExplicitProfile()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => JularrEvent.Create(JularrEventCategory.ImportCompleted, profileId: null));
        Assert.ThrowsExactly<InvalidOperationException>(() => JularrEvent.Create(JularrEventCategory.ReleaseAvailable, profileId: "   "));
    }

    [TestMethod]
    public void AdminEventCreationRejectsProfileId()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => JularrEvent.Create(JularrEventCategory.StorageProblem, profileId: "reader"));
    }

    [TestMethod]
    public async Task PublisherRejectsManuallyConstructedAudienceMismatchBeforeAuditLog()
    {
        await using var fixture = await Fixture.CreateAsync();
        var invalid = new JularrEvent(
            Guid.NewGuid(),
            JularrEventCategory.ReleaseAvailable,
            JularrEventAudience.Admin,
            ProfileId: null,
            MediaType: null,
            SubjectId: null,
            MessageParams: null,
            JularrEventSeverity.Info,
            DeepLink: null,
            DedupKey: null,
            RelatedOperationId: null,
            DateTime.UtcNow);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Publisher.PublishAsync(invalid));

        Assert.AreEqual(0, (await fixture.EventLog.ListRecentAsync(10)).Count);
        Assert.AreEqual(0, (await fixture.Notifications.ListAsync("owner", unreadOnly: false)).Count);
    }

    [TestMethod]
    public async Task PublisherRejectsManuallyConstructedSeverityMismatchBeforeAuditLog()
    {
        await using var fixture = await Fixture.CreateAsync();
        var invalid = new JularrEvent(
            Guid.NewGuid(),
            JularrEventCategory.ReleaseAvailable,
            JularrEventAudience.Profile,
            "reader",
            MediaType: null,
            SubjectId: null,
            MessageParams: null,
            JularrEventSeverity.Critical,
            DeepLink: null,
            DedupKey: null,
            RelatedOperationId: null,
            DateTime.UtcNow);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Publisher.PublishAsync(invalid));

        Assert.AreEqual(0, (await fixture.EventLog.ListRecentAsync(10)).Count);
        Assert.AreEqual(0, (await fixture.Notifications.ListAsync("reader", unreadOnly: false)).Count);
    }

    [TestMethod]
    public async Task ImmediateExternalChannelRoutesOnlyWhenSelectedEnabledAndRegistered()
    {
        var push = new RecordingSink(NotificationChannel.Push);
        await using var fixture = await Fixture.CreateAsync(extraSinks: [push]);
        await fixture.Subscriptions.SetEventPreferenceAsync("reader", JularrEventCategory.ReleaseAvailable, new NotificationEventPreferenceUpdate(true, Channels(NotificationChannel.InApp, NotificationChannel.Push), NotificationDeliveryTiming.Immediate));
        await fixture.Subscriptions.SetProfileChannelEnabledAsync("reader", NotificationChannel.Push, true);

        var domainEvent = JularrEvent.Create(JularrEventCategory.ReleaseAvailable, profileId: "reader");
        await fixture.Publisher.PublishAsync(domainEvent);

        Assert.AreEqual(1, (await fixture.Notifications.ListAsync("reader", unreadOnly: false)).Count);
        Assert.AreEqual(1, push.Deliveries.Count);
        Assert.AreEqual(domainEvent.Id, push.Deliveries[0].EventId);
        Assert.AreEqual("reader", push.Deliveries[0].ProfileId);
    }

    [TestMethod]
    public async Task DigestTimingKeepsInAppImmediateButDoesNotInvokeExternalSink()
    {
        var push = new RecordingSink(NotificationChannel.Push);
        await using var fixture = await Fixture.CreateAsync(extraSinks: [push]);
        await fixture.Subscriptions.SetEventPreferenceAsync("reader", JularrEventCategory.ReleaseAvailable, new NotificationEventPreferenceUpdate(true, Channels(NotificationChannel.InApp, NotificationChannel.Push), NotificationDeliveryTiming.Digest));
        await fixture.Subscriptions.SetProfileChannelEnabledAsync("reader", NotificationChannel.Push, true);

        await fixture.Publisher.PublishAsync(JularrEvent.Create(JularrEventCategory.ReleaseAvailable, profileId: "reader"));

        Assert.AreEqual(1, (await fixture.Notifications.ListAsync("reader", unreadOnly: false)).Count);
        Assert.AreEqual(0, push.Deliveries.Count, "Digest external delivery belongs to the future scheduler, not the synchronous dispatcher.");
    }

    [TestMethod]
    public async Task ABrokenExternalSinkNeverFailsThePublishCallOrBlocksInApp()
    {
        await using var fixture = await Fixture.CreateAsync(extraSinks: [new ThrowingSink()]);
        await fixture.Subscriptions.SetEventPreferenceAsync("reader", JularrEventCategory.DownloadGrabbed, new NotificationEventPreferenceUpdate(true, Channels(NotificationChannel.InApp, NotificationChannel.Push), NotificationDeliveryTiming.Immediate));
        await fixture.Subscriptions.SetProfileChannelEnabledAsync("reader", NotificationChannel.Push, true);

        await fixture.Publisher.PublishAsync(JularrEvent.Create(JularrEventCategory.DownloadGrabbed, profileId: "reader"));

        Assert.AreEqual(1, (await fixture.Notifications.ListAsync("reader", unreadOnly: false)).Count, "The In-App sink must still run.");
    }

    [TestMethod]
    public async Task InboxMarkAllReadAndClearReadWork()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.Publisher.PublishAsync(JularrEvent.Create(JularrEventCategory.RequestApproved, profileId: "reader", dedupKey: "a"));
        await fixture.Publisher.PublishAsync(JularrEvent.Create(JularrEventCategory.RequestDenied, profileId: "reader", dedupKey: "b"));

        Assert.AreEqual(2, await fixture.Notifications.CountUnreadAsync("reader"));
        Assert.AreEqual(2, await fixture.Notifications.MarkAllReadAsync("reader"));
        Assert.AreEqual(0, await fixture.Notifications.CountUnreadAsync("reader"));

        Assert.AreEqual(2, await fixture.Notifications.ClearReadAsync("reader"));
        Assert.AreEqual(0, (await fixture.Notifications.ListAsync("reader", unreadOnly: false)).Count);
    }

    [TestMethod]
    public async Task UnsetPreferenceResolvesToCanonicalInAppImmediateDefault()
    {
        await using var fixture = await Fixture.CreateAsync();

        var preference = await fixture.Subscriptions.GetEventPreferenceAsync("reader", JularrEventCategory.ReleaseAvailable);
        Assert.IsTrue(preference.Enabled);
        Assert.IsFalse(preference.IsExplicit);
        Assert.AreEqual(NotificationDeliveryTiming.Immediate, preference.Timing);
        CollectionAssert.AreEquivalent(new[] { NotificationChannel.InApp }, preference.Channels.ToArray());

        var all = await fixture.Subscriptions.GetAllEventPreferencesAsync("reader");
        Assert.AreEqual(Enum.GetValues<JularrEventCategory>().Length, all.Count);
        Assert.IsTrue(all.Values.All(item => item.Enabled && !item.IsExplicit && item.Timing == NotificationDeliveryTiming.Immediate));
        Assert.IsTrue(all.Values.All(item => item.Channels.SetEquals(Channels(NotificationChannel.InApp))));
    }

    [TestMethod]
    public async Task ApprovingOrRejectingARequestPublishesAScopedRequestDecisionEvent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var accessStore = new AcquisitionAccessStore(fixture.Db);
        var recorder = new RecordingEventPublisher();
        var ownerAccount = new CurrentAccountContext(new FixedHttpContextAccessor(OwnerPrincipal()));
        var service = AcquisitionAccessFixture.DefaultsService(accessStore, ownerAccount, recorder);

        var draft = new AcquisitionRequestDraft(MediaAcquisitionKind.Manga, "test", "ext-1", "Example Manga", null, null);
        var pending = await accessStore.CreateAsync(draft, "reader", AcquisitionRequestStatus.Pending, null, CancellationToken.None);

        await service.ApproveAsync(pending.Id, CancellationToken.None);

        var approved = recorder.Published.Single();
        Assert.AreEqual(JularrEventCategory.RequestApproved, approved.Category);
        Assert.AreEqual("reader", approved.ProfileId);
        Assert.AreEqual(JularrEventAudience.Profile, approved.Audience);
        Assert.AreEqual($"acquisition-request:{pending.Id}:RequestApproved", approved.DedupKey);

        var second = await accessStore.CreateAsync(draft with { ExternalId = "ext-2" }, "reader", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        await service.RejectAsync(second.Id, "Not available", CancellationToken.None);

        var denied = recorder.Published.Single(item => item.Category == JularrEventCategory.RequestDenied);
        Assert.AreEqual("reader", denied.ProfileId);
    }

    private static IReadOnlySet<NotificationChannel> Channels(params NotificationChannel[] channels) => channels.ToHashSet();

    private static ClaimsPrincipal OwnerPrincipal() =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim(ClaimTypes.Role, AccountRoles.Owner)],
            "test"));

    private sealed class FixedHttpContextAccessor(ClaimsPrincipal user) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = new DefaultHttpContext { User = user };
    }

    private sealed class ThrowingSink : INotificationSink
    {
        public string Key => "throwing-push";

        public NotificationChannel Channel => NotificationChannel.Push;

        public Task DeliverAsync(JularrEvent domainEvent, string profileId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated channel failure.");
    }

    private sealed class RecordingSink(NotificationChannel channel) : INotificationSink
    {
        public List<(Guid EventId, string ProfileId)> Deliveries { get; } = [];

        public string Key => $"recording-{channel}";

        public NotificationChannel Channel => channel;

        public Task DeliverAsync(JularrEvent domainEvent, string profileId, CancellationToken cancellationToken)
        {
            Deliveries.Add((domainEvent.Id, profileId));
            return Task.CompletedTask;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string path;

        public required AppDbContext Db { get; init; }
        public required EventLogStore EventLog { get; init; }
        public required NotificationSubscriptionStore Subscriptions { get; init; }
        public required NotificationStore Notifications { get; init; }
        public required IJularrEventPublisher Publisher { get; init; }

        private Fixture(string path)
        {
            this.path = path;
        }

        public static async Task<Fixture> CreateAsync(IEnumerable<INotificationSink>? extraSinks = null)
        {
            var path = Path.Combine(Path.GetTempPath(), $"jularr-notifications-{Guid.NewGuid():N}.db");
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={path};Foreign Keys=True")
                .Options;
            var db = new AppDbContext(options);
            await DatabaseMigrationBridge.UpgradeAsync(db);

            var now = DateTime.UtcNow;
            db.OwnerAccounts.AddRange(
                new OwnerAccount { Id = "owner", UserName = "owner", NormalizedUserName = "OWNER", PasswordHash = "hash", Role = AccountRole.Owner, IsEnabled = true, CreatedAt = now },
                new OwnerAccount { Id = "manager", UserName = "manager", NormalizedUserName = "MANAGER", PasswordHash = "hash", Role = AccountRole.MediaManager, IsEnabled = true, CreatedAt = now },
                new OwnerAccount { Id = "reader", UserName = "reader", NormalizedUserName = "READER", PasswordHash = "hash", Role = AccountRole.User, IsEnabled = true, CreatedAt = now });
            await db.SaveChangesAsync();

            var eventLog = new EventLogStore(db);
            var subscriptions = new NotificationSubscriptionStore(db);
            var notifications = new NotificationStore(db);
            var sinks = new List<INotificationSink> { new InAppNotificationSink(notifications) };
            if (extraSinks is not null)
            {
                sinks.AddRange(extraSinks);
            }

            var dispatcher = new NotificationDispatcher(db, subscriptions, sinks, NullLogger<NotificationDispatcher>.Instance);
            var publisher = new JularrEventPublisher(eventLog, dispatcher, NullLogger<JularrEventPublisher>.Instance);

            return new Fixture(path)
            {
                Db = db,
                EventLog = eventLog,
                Subscriptions = subscriptions,
                Notifications = notifications,
                Publisher = publisher
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            File.Delete(path);
        }
    }
}
