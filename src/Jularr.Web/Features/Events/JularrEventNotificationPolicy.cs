namespace Jularr.Web.Features.Events;

/// <summary>
/// Profile-facing notification transports. Digest is deliberately not a channel; it is a delivery
/// timing choice applied to channels that support scheduled summaries.
/// </summary>
public enum NotificationChannel
{
    InApp = 1,
    Push = 2,
    Email = 3
}

/// <summary>When an enabled event should be delivered through its selected channels.</summary>
public enum NotificationDeliveryTiming
{
    Immediate = 1,
    Digest = 2
}

/// <summary>
/// Stable product grouping used by Notification Settings. Groups are catalog metadata, never
/// inferred independently by Razor pages.
/// </summary>
public enum NotificationTopicGroup
{
    Media = 1,
    Requests = 2,
    Learning = 3,
    Community = 4,
    Account = 5,
    SystemAdmin = 6
}

/// <summary>
/// Whether profile Quiet Hours may defer interruptive/external delivery for an event.
/// Scheduling itself is implemented by the notification delivery scheduler, not the event catalog.
/// </summary>
public enum NotificationQuietHoursPolicy
{
    Delayable = 1,
    Bypass = 2
}
