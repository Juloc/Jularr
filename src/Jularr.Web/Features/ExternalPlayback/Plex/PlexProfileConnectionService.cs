using System.Net;
using System.Security.Claims;
using Jularr.Web.Features.Auth;

namespace Jularr.Web.Features.ExternalPlayback.Plex;

/// <summary>
/// Verifies a signed-in profile's own Plex grant against an Admin-approved
/// server and library scope. This service never substitutes the Admin's Plex
/// token for a user token. A valid server grant is not proof of user access.
/// </summary>
public sealed class PlexProfileConnectionService(
    PlexProfileConnectionStore profiles,
    PlexServerGrantStore servers,
    PlexLibraryClient libraries)
{
    public Task<PlexProfileConnectionStatus?> GetCurrentStatusAsync(
        ClaimsPrincipal caller,
        CancellationToken cancellationToken = default) =>
        profiles.GetStatusAsync(
            RequireCurrentProfileId(caller), cancellationToken);

    public Task<bool> DisconnectCurrentAsync(
        ClaimsPrincipal caller,
        CancellationToken cancellationToken = default) =>
        profiles.DisconnectAsync(
            RequireCurrentProfileId(caller), cancellationToken);

    public async Task<IReadOnlyList<PlexLibrarySection>> GetAccessibleLibrariesAsync(
        ClaimsPrincipal caller,
        string machineIdentifier,
        string clientIdentifier,
        CancellationToken cancellationToken = default)
    {
        var profileId = RequireCurrentProfileId(caller);
        var status = await profiles.GetStatusAsync(
            profileId, cancellationToken);
        if (status?.IsUsable != true)
        {
            return [];
        }

        var approved = await servers.GetGrantAsync(
            machineIdentifier, cancellationToken);
        if (approved is null)
        {
            return [];
        }

        // Profile-specific secret only: an Admin token could bypass the
        // requesting user's Plex shared-library restrictions.
        var profileToken = await profiles.GetBackendTokenAsync(
            profileId, cancellationToken);
        if (profileToken is null)
        {
            return [];
        }

        IReadOnlyList<PlexLibrarySection> available;
        try
        {
            available = await libraries.GetSectionsAsync(
                approved.Server.Endpoint,
                profileToken,
                clientIdentifier,
                cancellationToken);
        }
        catch (HttpRequestException e)
            when (e.StatusCode is HttpStatusCode.Unauthorized or
                HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            // An expired or restricted Plex profile must not inherit an
            // administrator's library access.
            return [];
        }

        var approvedIds = approved.Server.LibrarySectionIds.ToHashSet(
            StringComparer.Ordinal);
        return available
            .Where(section => approvedIds.Contains(section.Id))
            .ToArray();
    }

    private static string RequireCurrentProfileId(ClaimsPrincipal caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var accountId = OwnerAuthService.GetAccountId(caller);
        if (caller.Identity?.IsAuthenticated != true ||
            string.IsNullOrWhiteSpace(accountId))
        {
            throw new UnauthorizedAccessException(
                "A signed-in Jularr profile is required.");
        }

        // V1 Jularr profiles are addressed by their canonical Account ID;
        // do not read an arbitrary profile ID from an HTTP query or body.
        return accountId;
    }
}
