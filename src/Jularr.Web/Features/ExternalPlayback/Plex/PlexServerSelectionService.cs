using System.Security.Claims;
using Jularr.Web.Features.Auth;

namespace Jularr.Web.Features.ExternalPlayback.Plex;

/// <summary>
/// Verifies a prospective server and selected library IDs before persisting
/// a grant. An actual Admin handler must authorize the caller separately.
/// </summary>
public sealed class PlexServerSelectionService(
    PlexLibraryClient libraries,
    PlexServerGrantStore grants)
{
    public Task<IReadOnlyList<PlexLibrarySection>> GetAvailableLibrariesAsync(
        ClaimsPrincipal admin,
        PlexServerCandidate discovered,
        Uri serverEndpoint,
        string clientIdentifier,
        CancellationToken cancellationToken)
    {
        RequireAdmin(admin);
        ValidateDiscoveredEndpoint(discovered, serverEndpoint);
        return libraries.GetSectionsAsync(
            serverEndpoint, discovered.AccessToken, clientIdentifier,
            cancellationToken);
    }

    public async Task<PlexSelectedServer> ApproveAsync(
        ClaimsPrincipal admin,
        PlexServerCandidate discovered,
        Uri serverEndpoint,
        IReadOnlyCollection<string> selectedLibraryIds,
        string clientIdentifier,
        CancellationToken cancellationToken)
    {
        RequireAdmin(admin);
        ValidateDiscoveredEndpoint(discovered, serverEndpoint);
        if (selectedLibraryIds.Count == 0 ||
            selectedLibraryIds.Count > 100 ||
            selectedLibraryIds.Distinct(StringComparer.Ordinal).Count() != selectedLibraryIds.Count)
        {
            throw new ArgumentException(
                "Choose unique non-empty Plex libraries.",
                nameof(selectedLibraryIds));
        }

        var actualMachineId = await libraries.GetServerIdentityAsync(
            serverEndpoint, discovered.AccessToken, clientIdentifier,
            cancellationToken);
        if (!string.Equals(
                actualMachineId, discovered.MachineIdentifier,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The connected Plex endpoint is not the discovered server.");
        }

        var available = await libraries.GetSectionsAsync(
            serverEndpoint, discovered.AccessToken, clientIdentifier,
            cancellationToken);
        var availableIds = available.Select(x => x.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (!selectedLibraryIds.All(availableIds.Contains))
        {
            throw new InvalidOperationException(
                "Selected Plex libraries are not accessible with this grant.");
        }

        await grants.SaveSelectedAsync(
            discovered, serverEndpoint, selectedLibraryIds,
            cancellationToken);

        return (await grants.ListAsync(cancellationToken))
            .Single(x => x.MachineIdentifier == discovered.MachineIdentifier);
    }

    /// <summary>
    /// Revoking a server grant requires the same AdminSystem permission as
    /// approving it; user sessions never receive the saved PMS credential.
    /// </summary>
    public async Task RevokeAsync(
        ClaimsPrincipal admin,
        string machineIdentifier,
        CancellationToken cancellationToken)
    {
        RequireAdmin(admin);
        await grants.RemoveAsync(machineIdentifier, cancellationToken);
    }

    private static void RequireAdmin(ClaimsPrincipal caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!JularrPolicies.Allows(caller, JularrPolicies.AdminSystem))
        {
            throw new UnauthorizedAccessException(
                "Only an authorized administrator may approve Plex server grants.");
        }
    }

    private static void ValidateDiscoveredEndpoint(
        PlexServerCandidate discovered,
        Uri serverEndpoint)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentNullException.ThrowIfNull(serverEndpoint);
        if (!discovered.Owned ||
            !serverEndpoint.IsAbsoluteUri ||
            serverEndpoint.Scheme != Uri.UriSchemeHttps ||
            !discovered.Connections.Any(x => x.Url == serverEndpoint))
        {
            throw new ArgumentException(
                "Use only a HTTPS endpoint reported by Plex for this server.",
                nameof(serverEndpoint));
        }
    }
}
