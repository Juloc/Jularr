using System.Security.Claims;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Web.Features.Shell;

/// <summary>
/// Which media types exist for one profile and what it may do with each (#598). Built from the
/// canonical <see cref="MediaCapabilityView"/>; a <see cref="MediaCapability.Hidden"/> type is
/// absent everywhere the shell reaches (navigation, Library tabs, consumer routes), never greyed
/// out. Discovery (#595) and the request experience (#597) read the same object, so "which media
/// types exist for this user" has exactly one answer per request.
/// </summary>
public sealed record ShellMediaAccess(MediaCapabilityView Capabilities)
{
    public bool IsOwner => Capabilities.IsOwner;

    /// <summary>Every media type the profile may at least browse, in <see cref="WorkMediaTypes.All"/> order.</summary>
    public IReadOnlyList<WorkMediaType> VisibleMediaTypes => Capabilities.VisibleMediaTypes;

    /// <summary>The effective capability for one media type; <see cref="MediaCapability.Hidden"/> when it is not visible.</summary>
    public MediaCapability Capability(WorkMediaType mediaType) => Capabilities.Capability(mediaType);

    public bool IsVisible(WorkMediaType mediaType) => Capabilities.CanBrowse(mediaType);

    /// <summary>Whether the profile may open a Work: a title classified as Anime is gated as Anime while the Anime module runs.</summary>
    public bool IsWorkVisible(WorkMediaType technical, bool isAnime) => Capabilities.CanBrowseWork(technical, isAnime);

    /// <summary>True when at least one of <paramref name="mediaTypes"/> is visible (a hub serving several types).</summary>
    public bool IsAnyVisible(IEnumerable<WorkMediaType> mediaTypes) => mediaTypes.Any(IsVisible);

    /// <summary>
    /// Whether the profile may open a consumer route root such as <c>/Novels</c>, as declared in
    /// <see cref="UiNavigationCatalog.MediaRoutes"/>. A root the catalog does not know is closed.
    /// </summary>
    public bool CanOpen(string routeRoot) =>
        UiNavigationCatalog.MediaRoutes.Any(route =>
            string.Equals(route.Root, routeRoot, StringComparison.OrdinalIgnoreCase)
            && IsAnyVisible(route.MediaTypes));
}

/// <summary>
/// The permission-derived app shell's single source for a profile's visible media types (#598).
/// Scoped: the sidebar, the Library tabs and the consumer-route gate of one request share one
/// resolution instead of each re-reading the capability policy.
/// </summary>
public interface IAppShellService
{
    Task<ShellMediaAccess> GetMediaAccessAsync(ClaimsPrincipal? user, CancellationToken cancellationToken = default);
}

public sealed class AppShellService(IMediaCapabilityService capabilities) : IAppShellService
{
    private ClaimsPrincipal? resolvedFor;
    private Task<ShellMediaAccess>? resolved;

    public Task<ShellMediaAccess> GetMediaAccessAsync(
        ClaimsPrincipal? user,
        CancellationToken cancellationToken = default)
    {
        if (resolved is { IsFaulted: false, IsCanceled: false } && ReferenceEquals(resolvedFor, user))
        {
            return resolved;
        }

        resolvedFor = user;
        return resolved = ResolveAsync(user, cancellationToken);
    }

    private async Task<ShellMediaAccess> ResolveAsync(ClaimsPrincipal? user, CancellationToken cancellationToken) =>
        new(await capabilities.GetViewAsync(user, cancellationToken));
}
