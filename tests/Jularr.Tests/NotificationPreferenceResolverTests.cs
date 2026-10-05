using Jularr.Web.Features.Events;
using Jularr.Web.Features.Notifications;

namespace Jularr.Tests;

[TestClass]
public sealed class NotificationPreferenceResolverTests
{
    [TestMethod]
    public void EffectiveChannelsRequireSelectionProfileGateEventSupportAndRegisteredSink()
    {
        var preference = Preference(enabled: true, Channels(NotificationChannel.InApp, NotificationChannel.Push, NotificationChannel.Email), NotificationDeliveryTiming.Immediate);
        var profileChannels = ProfileChannels((NotificationChannel.InApp, true), (NotificationChannel.Push, true), (NotificationChannel.Email, false));
        var route = NotificationPreferenceResolver.Resolve(preference, profileChannels, Channels(NotificationChannel.InApp, NotificationChannel.Push));

        CollectionAssert.AreEquivalent(new[] { NotificationChannel.InApp, NotificationChannel.Push }, route.EffectiveChannels.ToArray());
        CollectionAssert.AreEquivalent(new[] { NotificationChannel.InApp, NotificationChannel.Push }, route.ImmediateChannels.ToArray());
    }

    [TestMethod]
    public void DisabledEventHasNoEffectiveRoutes()
    {
        var preference = Preference(enabled: false, Channels(NotificationChannel.InApp, NotificationChannel.Push), NotificationDeliveryTiming.Immediate);
        var route = NotificationPreferenceResolver.Resolve(preference, ProfileChannels((NotificationChannel.InApp, true), (NotificationChannel.Push, true)), Channels(NotificationChannel.InApp, NotificationChannel.Push));

        Assert.AreEqual(0, route.EffectiveChannels.Count);
        Assert.AreEqual(0, route.ImmediateChannels.Count);
    }

    [TestMethod]
    public void DigestKeepsInAppImmediateButDefersExternalChannels()
    {
        var preference = Preference(enabled: true, Channels(NotificationChannel.InApp, NotificationChannel.Push, NotificationChannel.Email), NotificationDeliveryTiming.Digest);
        var profileChannels = ProfileChannels((NotificationChannel.InApp, true), (NotificationChannel.Push, true), (NotificationChannel.Email, true));
        var route = NotificationPreferenceResolver.Resolve(preference, profileChannels, Channels(NotificationChannel.InApp, NotificationChannel.Push, NotificationChannel.Email));

        CollectionAssert.AreEquivalent(new[] { NotificationChannel.InApp, NotificationChannel.Push, NotificationChannel.Email }, route.EffectiveChannels.ToArray());
        CollectionAssert.AreEquivalent(new[] { NotificationChannel.InApp }, route.ImmediateChannels.ToArray());
    }

    [TestMethod]
    public void MissingProfileGateUsesCanonicalChannelDefault()
    {
        var preference = Preference(enabled: true, Channels(NotificationChannel.InApp, NotificationChannel.Push), NotificationDeliveryTiming.Immediate);
        var route = NotificationPreferenceResolver.Resolve(preference, new Dictionary<NotificationChannel, NotificationProfileChannelPreference>(), Channels(NotificationChannel.InApp, NotificationChannel.Push));

        CollectionAssert.AreEquivalent(new[] { NotificationChannel.InApp }, route.EffectiveChannels.ToArray());
    }

    [TestMethod]
    public void ChannelPreferenceFromAnotherProfileIsRejected()
    {
        var preference = Preference(enabled: true, Channels(NotificationChannel.Push), NotificationDeliveryTiming.Immediate);
        var profileChannels = new Dictionary<NotificationChannel, NotificationProfileChannelPreference>
        {
            [NotificationChannel.Push] = new("someone-else", NotificationChannel.Push, true, DateTime.UtcNow, true)
        };

        Assert.ThrowsExactly<InvalidOperationException>(() => NotificationPreferenceResolver.Resolve(preference, profileChannels, Channels(NotificationChannel.Push)));
    }

    private static IReadOnlySet<NotificationChannel> Channels(params NotificationChannel[] channels) => channels.ToHashSet();

    private static NotificationEventPreference Preference(bool enabled, IReadOnlySet<NotificationChannel> channels, NotificationDeliveryTiming timing) =>
        new("reader", JularrEventCategory.ReleaseAvailable, enabled, channels, timing, DateTime.UtcNow, true);

    private static IReadOnlyDictionary<NotificationChannel, NotificationProfileChannelPreference> ProfileChannels(params (NotificationChannel Channel, bool Enabled)[] values) =>
        values.ToDictionary(value => value.Channel, value => new NotificationProfileChannelPreference("reader", value.Channel, value.Enabled, DateTime.UtcNow, true));
}
