using System.Security.Claims;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Web.Features.Auth;

/// <summary>
/// Resolves the effective per-media-type capability for a signed-in principal from the canonical
/// <see cref="MediaCapabilityStore"/> policy, and gates server-side actions by it (#436).
/// </summary>
public sealed class MediaCapabilityService(
    MediaCapabilityStore store,
    IInstanceModuleService? instanceModules = null) : IMediaCapabilityService
{
    public async Task<MediaCapabilityView> GetViewAsync(
        ClaimsPrincipal? user,
        CancellationToken cancellationToken = default)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            // An unauthenticated principal has no media at all; every type is hidden.
            return new MediaCapabilityView(
                IsOwner: false,
                WorkMediaTypes.All.ToDictionary(type => type, _ => MediaCapability.Hidden));
        }

        var role = ResolveRole(user);
        var profileId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        var policy = await store.LoadAsync(cancellationToken);

        var capabilities = WorkMediaTypes.All.ToDictionary(
            type => type,
            type => policy.Resolve(role, profileId, type));

        var animeModuleEnabled = true;
        if (instanceModules is not null)
        {
            var instance = await instanceModules.GetAsync(cancellationToken);
            animeModuleEnabled = InstanceModuleMedia.IsCapabilityFamilyEnabled(instance, WorkMediaType.Anime);
            foreach (var mediaType in WorkMediaTypes.All)
            {
                if (!InstanceModuleMedia.IsCapabilityFamilyEnabled(instance, mediaType))
                {
                    capabilities[mediaType] = MediaCapability.Hidden;
                }
            }
        }

        return new MediaCapabilityView(role == AccountRole.Owner, capabilities) { AnimeModuleEnabled = animeModuleEnabled };
    }

    public async Task<MediaCapability> GetEffectiveCapabilityAsync(
        ClaimsPrincipal? user,
        WorkMediaType mediaType,
        CancellationToken cancellationToken = default) =>
        (await GetViewAsync(user, cancellationToken)).Capability(mediaType);

    public async Task<IReadOnlyList<WorkMediaType>> GetVisibleMediaTypesAsync(
        ClaimsPrincipal? user,
        CancellationToken cancellationToken = default) =>
        (await GetViewAsync(user, cancellationToken)).VisibleMediaTypes;

    public async Task EnsureCapabilityAsync(
        ClaimsPrincipal? user,
        WorkMediaType mediaType,
        MediaCapability required,
        CancellationToken cancellationToken = default)
    {
        var actual = await GetEffectiveCapabilityAsync(user, mediaType, cancellationToken);
        if (actual < required)
        {
            throw new MediaCapabilityDeniedException(mediaType, required, actual);
        }
    }

    /// <summary>Maps a principal to the highest role it carries (Owner &gt; MediaManager &gt; User).</summary>
    public static AccountRole ResolveRole(ClaimsPrincipal user)
    {
        if (user.IsInRole(AccountRoles.Owner))
        {
            return AccountRole.Owner;
        }

        return user.IsInRole(AccountRoles.MediaManager)
            ? AccountRole.MediaManager
            : AccountRole.User;
    }
}
