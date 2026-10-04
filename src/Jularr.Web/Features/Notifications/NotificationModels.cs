using Jularr.Web.Features.Events;

namespace Jularr.Web.Features.Notifications;

/// <summary>
/// Transitional compatibility shape used by the current dispatcher/settings page until #835 Phase 3/5
/// convert their call sites to the canonical event preference model below. It is no longer persisted.
/// </summary>
public enum NotificationMode
{
    Off = 0,
    InApp = 1,
    Push = 2,
    Digest = 3
}

/// <summary>
/// Transitional compatibility record for the current UI. Canonical persistence is
/// <see cref="NotificationEventPreference"/>.
/// </summary>
public sealed record NotificationSubscription(string ProfileId, JularrEventCategory Category, NotificationMode Mode, DateTime UpdatedAtUtc)
{
    public const NotificationMode DefaultMode = NotificationMode.InApp;
}

/// <summary>One profile's resolved preference for one event category.</summary>
public sealed record NotificationEventPreference(
    string ProfileId,
    JularrEventCategory Category,
    bool Enabled,
    IReadOnlySet<NotificationChannel> Channels,
    NotificationDeliveryTiming Timing,
    DateTime? UpdatedAtUtc,
    bool IsExplicit)
{
    public static NotificationEventPreference FromDefault(string profileId, JularrEventCategory category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        var policy = JularrEventCategories.Of(category);
        return new NotificationEventPreference(profileId.Trim(), category, policy.DefaultEnabled, policy.DefaultChannels, policy.DefaultTiming, null, false);
    }
}

/// <summary>Canonical mutation contract for one profile/event notification preference.</summary>
public sealed record NotificationEventPreferenceUpdate(bool Enabled, IReadOnlySet<NotificationChannel> Channels, NotificationDeliveryTiming Timing);

/// <summary>One profile-wide optional-delivery gate for a notification channel.</summary>
public sealed record NotificationProfileChannelPreference(string ProfileId, NotificationChannel Channel, bool Enabled, DateTime? UpdatedAtUtc, bool IsExplicit)
{
    public static NotificationProfileChannelPreference FromDefault(string profileId, NotificationChannel channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        return new NotificationProfileChannelPreference(profileId.Trim(), channel, NotificationProfileChannelDefaults.IsEnabledByDefault(channel), null, false);
    }
}

public static class NotificationProfileChannelDefaults
{
    public static bool IsEnabledByDefault(NotificationChannel channel) =>
        channel switch
        {
            NotificationChannel.InApp => true,
            NotificationChannel.Push or NotificationChannel.Email => false,
            _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown notification channel.")
        };
}

/// <summary>Single validation boundary for profile event-preference writes.</summary>
public static class NotificationPreferencePolicy
{
    public static void Validate(JularrEventCategory category, NotificationEventPreferenceUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(update.Channels);

        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown notification event category.");
        }

        if (!Enum.IsDefined(update.Timing))
        {
            throw new ArgumentOutOfRangeException(nameof(update), update.Timing, "Unknown notification delivery timing.");
        }

        var policy = JularrEventCategories.Of(category);
        if (!update.Enabled && !policy.CanDisable)
        {
            throw new InvalidOperationException($"{category} cannot be disabled.");
        }

        if (update.Timing == NotificationDeliveryTiming.Digest && !policy.AllowsDigest)
        {
            throw new InvalidOperationException($"{category} does not allow Digest delivery.");
        }

        foreach (var channel in update.Channels)
        {
            if (!Enum.IsDefined(channel))
            {
                throw new ArgumentOutOfRangeException(nameof(update), channel, "Unknown notification channel.");
            }

            if (!policy.Supports(channel))
            {
                throw new InvalidOperationException($"{category} does not support the {channel} channel.");
            }
        }

        if (update.Enabled && policy.RequiresAtLeastOneRoute && update.Channels.Count == 0)
        {
            throw new InvalidOperationException($"{category} requires at least one notification channel.");
        }
    }
}

/// <summary>One inbox row: an event delivered to one profile through the in-app sink.</summary>
public sealed record NotificationItem(
    Guid Id,
    string ProfileId,
    Guid EventId,
    JularrEventCategory Category,
    JularrEventSeverity Severity,
    string? MediaType,
    string? SubjectId,
    IReadOnlyDictionary<string, string>? MessageParams,
    string? DeepLink,
    string? DedupKey,
    int OccurrenceCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? ReadAtUtc)
{
    public bool IsRead => ReadAtUtc is not null;

    public string MessageKey => JularrEventCategories.Of(Category).MessageKey;
}

/// <summary>What the in-app sink (or a future sink) needs to record one delivery.</summary>
public sealed record NotificationDraft(
    string ProfileId,
    Guid EventId,
    JularrEventCategory Category,
    JularrEventSeverity Severity,
    string? MediaType,
    string? SubjectId,
    IReadOnlyDictionary<string, string>? MessageParams,
    string? DeepLink,
    string? DedupKey)
{
    public static NotificationDraft From(JularrEvent domainEvent, string profileId) =>
        new(
            profileId,
            domainEvent.Id,
            domainEvent.Category,
            domainEvent.Severity,
            domainEvent.MediaType,
            domainEvent.SubjectId,
            domainEvent.MessageParams,
            domainEvent.DeepLink,
            domainEvent.DedupKey);
}
