using Jularr.Tests.Infrastructure;
using Jularr.Web.Data;
using Jularr.Web.Features.Events;
using Jularr.Web.Features.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class NotificationPreferenceStoreTests
{
    [TestMethod]
    public async Task MissingRowsResolveFromCanonicalDefaultsWithoutBecomingExplicit()
    {
        await using var db = await CreateDbAsync();
        var store = new NotificationSubscriptionStore(db);

        var preference = await store.GetEventPreferenceAsync("reader", JularrEventCategory.ReleaseAvailable);
        var channels = await store.GetProfileChannelPreferencesAsync("reader");

        Assert.IsTrue(preference.Enabled);
        Assert.IsFalse(preference.IsExplicit);
        Assert.AreEqual(NotificationDeliveryTiming.Immediate, preference.Timing);
        CollectionAssert.AreEquivalent(new[] { NotificationChannel.InApp }, preference.Channels.ToArray());

        Assert.IsTrue(channels[NotificationChannel.InApp].Enabled);
        Assert.IsFalse(channels[NotificationChannel.InApp].IsExplicit);
        Assert.IsFalse(channels[NotificationChannel.Push].Enabled);
        Assert.IsFalse(channels[NotificationChannel.Email].Enabled);
    }

    [TestMethod]
    public async Task EventPreferencePersistsMultipleSelectedChannelsAndResetRestoresDefault()
    {
        await using var db = await CreateDbAsync();
        var store = new NotificationSubscriptionStore(db);
        IReadOnlySet<NotificationChannel> selected = new HashSet<NotificationChannel>
        {
            NotificationChannel.InApp,
            NotificationChannel.Push,
            NotificationChannel.Email
        };

        await store.SetEventPreferenceAsync("reader", JularrEventCategory.ReleaseAvailable, new NotificationEventPreferenceUpdate(true, selected, NotificationDeliveryTiming.Digest));

        var saved = await store.GetEventPreferenceAsync("reader", JularrEventCategory.ReleaseAvailable);
        Assert.IsTrue(saved.IsExplicit);
        Assert.IsTrue(saved.Enabled);
        Assert.AreEqual(NotificationDeliveryTiming.Digest, saved.Timing);
        CollectionAssert.AreEquivalent(selected.ToArray(), saved.Channels.ToArray());

        await store.ResetEventPreferenceAsync("reader", JularrEventCategory.ReleaseAvailable);

        var reset = await store.GetEventPreferenceAsync("reader", JularrEventCategory.ReleaseAvailable);
        Assert.IsFalse(reset.IsExplicit);
        Assert.AreEqual(NotificationDeliveryTiming.Immediate, reset.Timing);
        CollectionAssert.AreEquivalent(new[] { NotificationChannel.InApp }, reset.Channels.ToArray());
    }

    [TestMethod]
    public async Task DisablingAnEventCanPreserveItsSelectedChannels()
    {
        await using var db = await CreateDbAsync();
        var store = new NotificationSubscriptionStore(db);
        IReadOnlySet<NotificationChannel> selected = new HashSet<NotificationChannel>
        {
            NotificationChannel.InApp,
            NotificationChannel.Push
        };

        await store.SetEventPreferenceAsync("reader", JularrEventCategory.RequestApproved, new NotificationEventPreferenceUpdate(false, selected, NotificationDeliveryTiming.Immediate));

        var saved = await store.GetEventPreferenceAsync("reader", JularrEventCategory.RequestApproved);

        Assert.IsFalse(saved.Enabled);
        CollectionAssert.AreEquivalent(selected.ToArray(), saved.Channels.ToArray());
    }

    [TestMethod]
    public async Task ProfileChannelGateDoesNotRewriteEventSelection()
    {
        await using var db = await CreateDbAsync();
        var store = new NotificationSubscriptionStore(db);
        IReadOnlySet<NotificationChannel> selected = new HashSet<NotificationChannel>
        {
            NotificationChannel.InApp,
            NotificationChannel.Push
        };

        await store.SetEventPreferenceAsync("reader", JularrEventCategory.ReleaseAvailable, new NotificationEventPreferenceUpdate(true, selected, NotificationDeliveryTiming.Immediate));
        await store.SetProfileChannelEnabledAsync("reader", NotificationChannel.Push, true);
        await store.SetProfileChannelEnabledAsync("reader", NotificationChannel.Push, false);

        var eventPreference = await store.GetEventPreferenceAsync("reader", JularrEventCategory.ReleaseAvailable);
        var channels = await store.GetProfileChannelPreferencesAsync("reader");

        CollectionAssert.AreEquivalent(selected.ToArray(), eventPreference.Channels.ToArray());
        Assert.IsFalse(channels[NotificationChannel.Push].Enabled);
        Assert.IsTrue(channels[NotificationChannel.Push].IsExplicit);
    }

    [TestMethod]
    public async Task InvalidDigestWriteFailsBeforeReplacingExistingPreference()
    {
        await using var db = await CreateDbAsync();
        var store = new NotificationSubscriptionStore(db);
        IReadOnlySet<NotificationChannel> selected = new HashSet<NotificationChannel> { NotificationChannel.InApp };

        await store.SetEventPreferenceAsync("reader", JularrEventCategory.ImportFailed, new NotificationEventPreferenceUpdate(true, selected, NotificationDeliveryTiming.Immediate));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            store.SetEventPreferenceAsync("reader", JularrEventCategory.ImportFailed, new NotificationEventPreferenceUpdate(true, selected, NotificationDeliveryTiming.Digest)));

        var saved = await store.GetEventPreferenceAsync("reader", JularrEventCategory.ImportFailed);
        Assert.IsTrue(saved.Enabled);
        Assert.AreEqual(NotificationDeliveryTiming.Immediate, saved.Timing);
        CollectionAssert.AreEquivalent(new[] { NotificationChannel.InApp }, saved.Channels.ToArray());
    }

    [TestMethod]
    public async Task GetAllReturnsDefaultsAndExplicitPreferencesInOneCanonicalShape()
    {
        await using var db = await CreateDbAsync();
        var store = new NotificationSubscriptionStore(db);
        IReadOnlySet<NotificationChannel> selected = new HashSet<NotificationChannel> { NotificationChannel.Email };

        await store.SetEventPreferenceAsync("reader", JularrEventCategory.RequestDenied, new NotificationEventPreferenceUpdate(true, selected, NotificationDeliveryTiming.Digest));

        var preferences = await store.GetAllEventPreferencesAsync("reader");

        Assert.AreEqual(Enum.GetValues<JularrEventCategory>().Length, preferences.Count);
        Assert.IsTrue(preferences[JularrEventCategory.RequestDenied].IsExplicit);
        CollectionAssert.AreEquivalent(new[] { NotificationChannel.Email }, preferences[JularrEventCategory.RequestDenied].Channels.ToArray());
        Assert.IsFalse(preferences[JularrEventCategory.ReleaseAvailable].IsExplicit);
    }

    private static async Task<AppDbContext> CreateDbAsync()
    {
        var connection = TestPostgres.ResolveConnectionString($"Data Source=notification-preferences-{Guid.NewGuid():N}.db");
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        return db;
    }
}
