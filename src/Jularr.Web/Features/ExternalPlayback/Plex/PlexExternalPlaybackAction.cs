using System.Security.Claims;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Plex;

namespace Jularr.Web.Features.ExternalPlayback.Plex;

/// <summary>
/// One shared secondary-action policy for Movie, Series and Request detail
/// surfaces. Jularr Playback and Request policies remain the primary actions.
/// </summary>
public sealed class PlexExternalPlaybackAction(
    PlexOnDemandTargetResolver resolver,
    PlexProfileConnectionStore profileConnections,
    PlexServerGrantStore adminConnections,
    PlexIdentitySettingsStore providerSettings)
{
    public async Task<bool> IsOfferVisibleAsync(
        ClaimsPrincipal caller,
        CancellationToken cancellationToken = default)
    {
        var profileId = OwnerAuthService.GetAccountId(caller);
        if (caller.Identity?.IsAuthenticated != true ||
            string.IsNullOrWhiteSpace(profileId) ||
            !(await providerSettings.GetAsync(cancellationToken)).CanConnectMedia)
        {
            return false;
        }

        var connection = await profileConnections.GetStatusAsync(
            profileId, cancellationToken);
        if (connection?.IsUsable != true)
        {
            return false;
        }

        return (await adminConnections.ListAsync(cancellationToken)).Count > 0;
    }

    /// <summary>
    /// A visible action is not a guarantee of availability. Access and
    /// matching are checked again on click, using the user's own Plex grant.
    /// </summary>
    public async Task<Uri?> OpenAsync(
        ClaimsPrincipal caller,
        long workId,
        string title,
        CancellationToken cancellationToken = default)
    {
        if (!await IsOfferVisibleAsync(caller, cancellationToken))
        {
            return null;
        }

        var settings = await providerSettings.GetAsync(cancellationToken);
        return await resolver.ResolveAsync(
            caller, workId, title, settings.ClientIdentifier,
            cancellationToken);
    }
}
