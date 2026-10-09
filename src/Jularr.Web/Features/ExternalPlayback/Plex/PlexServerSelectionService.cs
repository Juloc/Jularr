using Microsoft.AspNetCore.DataProtection;

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
        PlexServerCandidate discovered,
        Uri serverEndpoint,
        string clientIdentifier,
        CancellationToken cancellationToken)
    {
        ValidateDiscoveredEndpoint(discovered, serverEndpoint);
        return libraries.GetSectionsAsync(
            serverEndpoint, discovered.AccessToken, clientIdentifier,
            cancellationToken);
    }

    public async Task<PlexSelectedServer> ApproveAsync(
        PlexServerCandidate discovered,
        Uri serverEndpoint,
        IReadOnlyCollection<string> selectedLibraryIds,
        string clientIdentifier,
        CancellationToken cancellationToken)
    {
        ValidateDiscoveredEndpoint(discovered, serverEndpoint);
        if (selectedLibraryIds.Count == 0 ||
            selectedLibraryIds.Count > 100 ||
            selectedLibraryIds.Distinct(StringComparer.Ordinal).Count() != selectedLibraryIds.Count)
        {
            throw new ArgumentException(
                "Choose unique non-empty Plex libraries.",
                nameof(selectedLibraryIds));
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

    private static void ValidateDiscoveredEndpoint(
        PlexServerCandidate discovered,
        Uri serverEndpoint)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentNullException.ThrowIfNull(serverEndpoint);
        if (!serverEndpoint.IsAbsoluteUri ||
            serverEndpoint.Scheme != Uri.UriSchemeHttps ||
            !discovered.Connections.Any(x => x.Url == serverEndpoint))
        {
            throw new ArgumentException(
                "Use only a HTTPS endpoint reported by Plex for this server.",
                nameof(serverEndpoint));
        }
    }
}
