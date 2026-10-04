using Jularr.Web.Features.Events;

namespace Jularr.Web.Features.Notifications;

/// <summary>
/// One product notification channel. The dispatcher owns policy/routing and isolates failures; a
/// sink owns only transport-specific delivery. Multiple endpoints/devices for one product channel
/// are handled inside that channel's sink rather than by registering duplicate channel sinks.
/// </summary>
public interface INotificationSink
{
    /// <summary>Stable identifier used for diagnostics/logging.</summary>
    string Key { get; }

    /// <summary>The product channel this sink delivers.</summary>
    NotificationChannel Channel { get; }

    Task DeliverAsync(JularrEvent domainEvent, string profileId, CancellationToken cancellationToken);
}

/// <summary>The baseline In-App channel: a durable, profile-scoped inbox row.</summary>
public sealed class InAppNotificationSink(NotificationStore store) : INotificationSink
{
    public string Key => "in-app";

    public NotificationChannel Channel => NotificationChannel.InApp;

    public Task DeliverAsync(JularrEvent domainEvent, string profileId, CancellationToken cancellationToken) =>
        store.CreateAsync(NotificationDraft.From(domainEvent, profileId), cancellationToken);
}
