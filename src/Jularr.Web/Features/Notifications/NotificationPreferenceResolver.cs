using System.Collections.Frozen;
using Jularr.Web.Features.Events;

namespace Jularr.Web.Features.Notifications;

/// <summary>
/// Result of resolving one profile/event preference against profile channel gates, event policy and
/// currently registered channel sinks. Effective channels preserve timing; ImmediateChannels are the
/// subset the current dispatcher may deliver synchronously.
/// </summary>
public sealed record NotificationRouteResolution(NotificationDeliveryTiming Timing, FrozenSet<NotificationChannel> EffectiveChannels, FrozenSet<NotificationChannel> ImmediateChannels);

/// <summary>
/// Canonical notification-route policy. Persistence remains owned by
/// <see cref="NotificationSubscriptionStore"/>; this type owns only the effective-route calculation.
/// </summary>
public static class NotificationPreferenceResolver
{
    public static NotificationRouteResolution Resolve(
        NotificationEventPreference preference,
        IReadOnlyDictionary<NotificationChannel, NotificationProfileChannelPreference> profileChannels,
        IEnumerable<NotificationChannel> availableChannels)
    {
        ArgumentNullException.ThrowIfNull(preference);
        ArgumentNullException.ThrowIfNull(profileChannels);
        ArgumentNullException.ThrowIfNull(availableChannels);

        var available = availableChannels.ToFrozenSet();
        if (!preference.Enabled)
        {
            return Empty(preference.Timing);
        }

        var policy = JularrEventCategories.Of(preference.Category);
        var effective = preference.Channels
            .Where(channel => policy.Supports(channel))
            .Where(channel => IsProfileChannelEnabled(preference.ProfileId, channel, profileChannels))
            .Where(available.Contains)
            .ToFrozenSet();

        var immediate = preference.Timing == NotificationDeliveryTiming.Immediate
            ? effective
            : effective.Where(channel => channel == NotificationChannel.InApp).ToFrozenSet();

        return new NotificationRouteResolution(preference.Timing, effective, immediate);
    }

    private static bool IsProfileChannelEnabled(string profileId, NotificationChannel channel, IReadOnlyDictionary<NotificationChannel, NotificationProfileChannelPreference> preferences)
    {
        if (!preferences.TryGetValue(channel, out var preference))
        {
            return NotificationProfileChannelDefaults.IsEnabledByDefault(channel);
        }

        if (!string.Equals(profileId, preference.ProfileId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Profile channel preference for {channel} belongs to a different profile.");
        }

        return preference.Enabled;
    }

    private static NotificationRouteResolution Empty(NotificationDeliveryTiming timing)
    {
        var empty = Array.Empty<NotificationChannel>().ToFrozenSet();
        return new NotificationRouteResolution(timing, empty, empty);
    }
}
