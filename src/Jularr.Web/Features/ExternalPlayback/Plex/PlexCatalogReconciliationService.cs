using System.Security.Claims;
using Jularr.Web.Features.Auth;

namespace Jularr.Web.Features.ExternalPlayback.Plex;

public sealed record PlexCatalogReconciliationResult(
    PlexCatalogCheckpoint Checkpoint,
    int PagesProcessed);

/// <summary>
/// One bounded invocation of the existing Admin-approved Plex scanner.
/// A future admin-scheduled worker can call the same pipeline; no second
/// matching or progress tracker is introduced.
/// </summary>
public sealed class PlexCatalogReconciliationService(
    PlexServerGrantStore grants,
    PlexServerCatalogScanService scanner,
    PlexCatalogCheckpointStore checkpoints)
{
    public async Task<PlexCatalogReconciliationResult> RunBatchAsync(
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
        if (!JularrPolicies.Allows(caller, JularrPolicies.AdminSystem))
        {
            throw new UnauthorizedAccessException(
                "Only an administrator may reconcile Plex catalogs.");
        }

        if (maxPages is < 1 or > 4 ||
            pageSize is < 1 or > PlexLibraryClient.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPages), "A Plex scan must be bounded to four pages.");
        }

        var selected = (await grants.ListAsync(cancellationToken))
            .SingleOrDefault(x => x.MachineIdentifier == machineIdentifier);
        if (selected is null ||
            !selected.LibrarySectionIds.Contains(sectionId, StringComparer.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "The Plex library is not currently approved.");
        }

        var current = await checkpoints.BeginAsync(
            machineIdentifier, sectionId,
            selected.UpdatedAtUtc, pageSize, restart, cancellationToken);
        var processed = 0;

        while (!current.Complete && processed < maxPages)
        {
            var start = current.NextStart!.Value;
            var page = await scanner.ScanApprovedPageAsync(
                caller, machineIdentifier, sectionId, start, pageSize,
                clientIdentifier, cancellationToken);

            // The current page must be persisted before advancing the cursor.
            // An upstream timeout or an interrupted disk write can be retried.
            current = await checkpoints.CommitAsync(
                current, page, cancellationToken);
            processed++;
        }

        return new PlexCatalogReconciliationResult(current, processed);
    }
}
