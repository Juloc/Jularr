using System.Security.Claims;
using Jularr.Web.Features.Auth;

namespace Jularr.Web.Features.ExternalPlayback.Plex;

public sealed record PlexCatalogScanPage(
    string MachineIdentifier,
    string SectionId,
    int Start,
    int TotalSize,
    int? NextStart,
    IReadOnlyList<PlexWorkMatch> Matches);

/// <summary>
/// Admin-only, read-only scan of one approved PMS library page.
/// No automatic background jobs, no credential-bearing links or progress writes.
/// The next-page cursor lets a later worker implement bounded resumable scans.
/// </summary>
public sealed class PlexServerCatalogScanService(
    PlexServerGrantStore grants,
    PlexLibraryClient client,
    PlexWorkMatcher matcher)
{
    public async Task<PlexCatalogScanPage> ScanApprovedPageAsync(
        ClaimsPrincipal caller,
        string machineIdentifier,
        string sectionId,
        int start,
        int pageSize,
        string clientIdentifier,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!JularrPolicies.Allows(caller, JularrPolicies.AdminSystem))
        {
            throw new UnauthorizedAccessException(
                "Only an authorized administrator may scan Plex server grants.");
        }

        if (start < 0 || pageSize is < 1 or > PlexLibraryClient.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize), "The Plex scan page is out of range.");
        }

        var grant = await grants.GetGrantAsync(
            machineIdentifier, cancellationToken);
        if (grant is null ||
            !grant.Server.LibrarySectionIds.Contains(sectionId,
                StringComparer.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "This Plex library has not been explicitly approved.");
        }

        var result = await client.GetItemsAsync(
            grant.Server.Endpoint, grant.AccessToken, clientIdentifier,
            sectionId, start, pageSize, cancellationToken);
        var mapped = await matcher.ResolvePageAsync(
            result.Items, cancellationToken);

        // The source cursor advances by returned Plex entries, including items
        // rejected by our mapper; otherwise malformed entries cause repeated scans.
        var next = result.ReturnedSize > 0 &&
            result.TotalSize > start + result.ReturnedSize
                ? checked(start + result.ReturnedSize)
                : (int?)null;

        return new PlexCatalogScanPage(
            machineIdentifier, sectionId, start, result.TotalSize,
            next, mapped);
    }
}
