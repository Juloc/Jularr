using System.Security.Claims;
using Jularr.Web.Features.Auth;

namespace Jularr.Web.Features.ExternalPlayback.Plex;

/// <summary>
/// Resolves the external Plex choice only on explicit user request. A maximum
/// of two authorized servers, two libraries per server and 24 search results
/// per library are inspected; no periodic full library sync is necessary.
/// Never trust title alone: confirm IDs then revalidate the exact item.
/// </summary>
public sealed class PlexOnDemandTargetResolver(
    PlexProfileConnectionStore connections,
    PlexServerGrantStore grants,
    PlexProfileConnectionService profileAccess,
    PlexLibraryClient client,
    PlexWorkMatcher matcher,
    PlexWebDestinationService webDestination)
{
    public async Task<Uri?> ResolveAsync(
        ClaimsPrincipal caller,
        long workId,
        string title,
        string clientIdentifier,
        CancellationToken cancellationToken = default)
    {
        var profileId = OwnerAuthService.GetAccountId(caller);
        if (caller.Identity?.IsAuthenticated != true ||
            profileId is null || workId <= 0 ||
            string.IsNullOrWhiteSpace(title) || title.Length > 160)
        {
            return null;
        }

        var profile = await connections.GetStatusAsync(
            profileId, cancellationToken);
        if (profile?.IsUsable != true)
        {
            return null;
        }

        var profileToken = await connections.GetBackendTokenAsync(
            profileId, cancellationToken);
        if (string.IsNullOrWhiteSpace(profileToken))
        {
            return null;
        }

        var configured = await grants.ListAsync(cancellationToken);
        foreach (var server in configured.Take(2))
        {
            IReadOnlyList<PlexLibrarySection> sections;
            try
            {
                sections = await profileAccess.GetAccessibleLibrariesAsync(
                    caller,
                    server.MachineIdentifier,
                    clientIdentifier,
                    cancellationToken);
            }
            catch (HttpRequestException)
            {
                continue;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            var grant = await grants.GetGrantAsync(
                server.MachineIdentifier, cancellationToken);
            if (grant is null)
            {
                continue;
            }

            foreach (var section in sections.Take(2))
            {
                IReadOnlyList<PlexLibraryItem> candidates;
                try
                {
                    candidates = await client.FindCandidatesAsync(
                        grant.Server.Endpoint,
                        profileToken,
                        clientIdentifier,
                        section.Id,
                        title,
                        cancellationToken);
                }
                catch (HttpRequestException)
                {
                    continue;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    continue;
                }

                var matches = await matcher.ResolvePageAsync(
                    candidates, cancellationToken);
                foreach (var match in matches)
                {
                    if (match.WorkId != workId)
                    {
                        continue;
                    }

                    try
                    {
                        var destination = await webDestination.ResolveAsync(
                            caller,
                            server.MachineIdentifier,
                            match.Item.RatingKey,
                            workId,
                            clientIdentifier,
                            cancellationToken);
                        if (destination is not null)
                        {
                            return destination;
                        }
                    }
                    catch (HttpRequestException)
                    {
                        continue;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        continue;
                    }
                }
            }
        }

        return null;
    }
}
