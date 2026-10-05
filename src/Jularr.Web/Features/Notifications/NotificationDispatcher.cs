using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Events;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Notifications;

/// <summary>
/// Turns one published <see cref="JularrEvent"/> into per-profile channel deliveries. Recipient
/// resolution remains here; event/profile/channel policy is resolved by
/// <see cref="NotificationPreferenceResolver"/>. Sink failures are isolated so notification delivery
/// can never fail the domain operation that raised the event.
/// </summary>
public sealed class NotificationDispatcher(AppDbContext db, NotificationSubscriptionStore subscriptions, IEnumerable<INotificationSink> sinks, ILogger<NotificationDispatcher> logger)
{
    private readonly IReadOnlyDictionary<NotificationChannel, INotificationSink> _sinks = sinks.ToDictionary(sink => sink.Channel);

    public async Task DispatchAsync(JularrEvent domainEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        foreach (var profileId in await ResolveRecipientsAsync(domainEvent, cancellationToken))
        {
            var preference = await subscriptions.GetEventPreferenceAsync(profileId, domainEvent.Category, cancellationToken);
            var profileChannels = await subscriptions.GetProfileChannelPreferencesAsync(profileId, cancellationToken);
            var route = NotificationPreferenceResolver.Resolve(preference, profileChannels, _sinks.Keys);

            foreach (var channel in route.ImmediateChannels)
            {
                var sink = _sinks[channel];
                try
                {
                    await sink.DeliverAsync(domainEvent, profileId, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning(
                        exception,
                        "Notification sink {Sink} failed to deliver event {EventId} ({Category}) to profile {ProfileId}.",
                        sink.Key,
                        domainEvent.Id,
                        domainEvent.Category,
                        profileId);
                }
            }
        }
    }

    private async Task<IReadOnlyList<string>> ResolveRecipientsAsync(JularrEvent domainEvent, CancellationToken cancellationToken) =>
        domainEvent.Audience switch
        {
            JularrEventAudience.Profile when !string.IsNullOrWhiteSpace(domainEvent.ProfileId) => [domainEvent.ProfileId],
            JularrEventAudience.Profile => [],
            JularrEventAudience.Admin => await AdminProfileIdsAsync(cancellationToken),
            _ => []
        };

    private async Task<IReadOnlyList<string>> AdminProfileIdsAsync(CancellationToken cancellationToken) =>
        await db.OwnerAccounts
            .AsNoTracking()
            .Where(account => account.IsEnabled && (account.Role == AccountRole.Owner || account.Role == AccountRole.MediaManager))
            .Select(account => account.Id)
            .ToListAsync(cancellationToken);
}
