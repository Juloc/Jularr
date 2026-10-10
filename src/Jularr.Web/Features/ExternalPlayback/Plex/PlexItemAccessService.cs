using System.Net;
using System.Security.Claims;
using Jularr.Web.Features.Auth;

namespace Jularr.Web.Features.ExternalPlayback.Plex;

/// <summary>
/// Final backend access check for a Plex movie/show candidate. A scan result,
/// title match or Admin library grant alone must never make a Plex target
/// available to another Jularr profile.
/// </summary>
public sealed class PlexItemAccessService(
    PlexProfileConnectionService profiles,
    PlexProfileConnectionStore connections,
    PlexServerGrantStore servers,
    PlexLibraryClient plex,
    PlexWorkMatcher matcher)
{
    public async Task<bool> IsAccessibleMatchAsync(
        ClaimsPrincipal caller,
        string machineIdentifier,
        string ratingKey,
        long requestedWorkId,
        string clientIdentifier,
        CancellationToken cancellationToken = default)
    {
        if (requestedWorkId <= 0)
        {
            return false;
        }

        // This method derives the profile from the signed-in principal, never
        // from the browser's chosen profile or Plex account ID.
        var visible = await profiles.GetAccessibleLibrariesAsync(
            caller, machineIdentifier, clientIdentifier, cancellationToken);
        if (visible.Count == 0)
        {
            return false;
        }

        var profileId = OwnerAuthService.GetAccountId(caller);
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return false;
        }

        var grant = await servers.GetGrantAsync(
            machineIdentifier, cancellationToken);
        var userToken = await connections.GetBackendTokenAsync(
            profileId, cancellationToken);
        if (grant is null || userToken is null)
        {
            return false;
        }

        PlexLibraryItem? item;
        try
        {
            item = await plex.GetItemAsync(
                grant.Server.Endpoint,
                userToken,
                clientIdentifier,
                ratingKey,
                cancellationToken);
        }
        catch (HttpRequestException e)
            when (e.StatusCode is HttpStatusCode.Unauthorized or
                HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return false;
        }

        // Plex must explicitly name the library the item currently belongs to.
        // Missing/unknown section or a conflicting identity is not sufficient.
        if (item?.LibrarySectionId is not { } sectionId ||
            !grant.Server.LibrarySectionIds.Contains(sectionId, StringComparer.Ordinal) ||
            !visible.Any(section => section.Id == sectionId))
        {
            return false;
        }

        return await matcher.ResolveWorkIdAsync(
            item, cancellationToken) == requestedWorkId;
    }
}
