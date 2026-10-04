using Jularr.Web.Features.Notifications;

namespace Jularr.Web.Features.Events;

/// <summary>
/// The one boundary features publish domain events through (#429), instead of calling a
/// notification channel directly. Registered as a scoped service so acquisition/operations/request
/// code can take a dependency on the interface without knowing anything about delivery.
/// </summary>
public interface IJularrEventPublisher
{
    Task PublishAsync(JularrEvent domainEvent, CancellationToken cancellationToken = default);
}

public sealed class JularrEventPublisher(
    EventLogStore log,
    NotificationDispatcher dispatcher,
    ILogger<JularrEventPublisher> logger) : IJularrEventPublisher
{
    public async Task PublishAsync(JularrEvent domainEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        JularrEventCategories.ValidateOccurrence(domainEvent);

        // The durable audit log write can throw (for example a full disk); that is surfaced to the
        // caller like any other persistence failure. Delivery to profiles, once the event itself is
        // safely recorded, must never fail the originating operation (#429 "a channel failure must
        // not fail the originating media operation").
        await log.AppendAsync(domainEvent, cancellationToken);

        try
        {
            await dispatcher.DispatchAsync(domainEvent, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                exception,
                "Failed to dispatch notifications for event {EventId} ({Category}).",
                domainEvent.Id,
                domainEvent.Category);
        }
    }
}
