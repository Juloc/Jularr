using System.Security.Claims;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Operations;
using Jularr.Web.Infrastructure;

namespace Jularr.Web.Features.ExternalPlayback.Plex;

/// <summary>
/// Dispatches bounded Plex catalog reconciliation through Jularr's existing
/// Operations/maintenance queue. No separate worker, microservice, or
/// Plex-specific task scheduler is created.
/// </summary>
public sealed class PlexCatalogReconciliationJobs(BackgroundJobQueue jobs)
{
    public ValueTask<Guid> QueueBatchAsync(
        ClaimsPrincipal caller,
        string machineIdentifier,
        string sectionId,
        string clientIdentifier,
        int pageSize = 100,
        int maxPages = 2,
        bool restart = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var actorId = OwnerAuthService.GetAccountId(caller);
        if (!JularrPolicies.Allows(caller, JularrPolicies.AdminSystem) ||
            string.IsNullOrWhiteSpace(actorId))
        {
            throw new UnauthorizedAccessException(
                "Only an administrator can schedule Plex catalog reconciliation.");
        }

        if (maxPages is < 1 or > 4 ||
            pageSize is < 1 or > PlexLibraryClient.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPages), "The Plex batch exceeds its scan budget.");
        }

        return jobs.QueueAsync(
            new OperationDescriptor(
                "plex-catalog-reconciliation",
                "Library",
                "Reconcile Plex library",
                Subject: machineIdentifier,
                Lane: OperationLane.Maintenance,
                Retryable: true,
                ActorProfileId: actorId,
                Priority: OperationPriority.Low),
            async (_, services, token) =>
            {
                // Revalidate the actor at execution time. An account disabled
                // or demoted while the operation waited cannot retain Admin.
                var auth = services.GetRequiredService<OwnerAuthService>();
                var account = await auth.GetEnabledAccountAsync(actorId, token);
                if (account is null)
                {
                    throw new UnauthorizedAccessException(
                        "The Plex scan administrator is no longer enabled.");
                }

                var principal = OwnerAuthService.CreatePrincipal(account);
                if (!JularrPolicies.Allows(principal, JularrPolicies.AdminSystem))
                {
                    throw new UnauthorizedAccessException(
                        "Administrator privileges were revoked before the Plex scan.");
                }

                await services.GetRequiredService<PlexCatalogReconciliationService>()
                    .RunBatchAsync(
                        principal, machineIdentifier, sectionId,
                        clientIdentifier, pageSize, maxPages, restart, token);
            },
            cancellationToken);
    }
}
