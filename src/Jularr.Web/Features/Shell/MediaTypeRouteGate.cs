using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace Jularr.Web.Features.Shell;

/// <summary>
/// Page-level authorization for one media type's consumer routes (#598): a profile that cannot at
/// least browse any of <see cref="MediaTypes"/> gets 404 (the media type does not exist for that
/// profile) before the page is even constructed. Owners are unrestricted through the capability
/// policy itself. It runs as an authorization filter, so the page model and its services are
/// never resolved for a hidden type.
/// </summary>
public sealed class MediaTypeGateFilter(IReadOnlyList<WorkMediaType> mediaTypes) : IAsyncAuthorizationFilter
{
    public IReadOnlyList<WorkMediaType> MediaTypes { get; } = mediaTypes;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var shell = context.HttpContext.RequestServices.GetRequiredService<IAppShellService>();
        var access = await shell.GetMediaAccessAsync(context.HttpContext.User, context.HttpContext.RequestAborted);
        if (!access.IsAnyVisible(MediaTypes))
        {
            context.Result = new NotFoundResult();
        }
    }
}

public static class MediaTypeRouteGateExtensions
{
    private const string RouteProperty = "Jularr.MediaRoute";

    /// <summary>
    /// Gates every page folder named in <see cref="UiNavigationCatalog.MediaRoutes"/> with a
    /// <see cref="MediaTypeGateFilter"/>. The navigation catalog is the one table that says which
    /// route belongs to which media type, so a route can never be hidden from the sidebar yet
    /// stay reachable, or the other way round.
    /// </summary>
    public static PageConventionCollection AddMediaTypeGates(this PageConventionCollection conventions)
    {
        // A root gates every page whose route lies under it, so a narrow root (the Anime pages) can sit inside
        // a wider hub root (/Library); such a page carries both gates and needs both to pass. The route is only
        // known on the route model, so it is carried to the application model as a property.
        conventions.AddFolderRouteModelConvention(
            "/",
            model =>
            {
                if (model.Selectors.Select(selector => selector.AttributeRouteModel?.Template).FirstOrDefault(value => value is not null) is { } template)
                {
                    model.Properties[RouteProperty] = "/" + template.TrimStart('/');
                }
            });
        conventions.AddFolderApplicationModelConvention(
            "/",
            model =>
            {
                if (model.Properties.TryGetValue(RouteProperty, out var value) && value is string path)
                {
                    foreach (var route in UiNavigationCatalog.MediaRoutes.Where(candidate => new PathString(path).StartsWithSegments(candidate.Root, StringComparison.OrdinalIgnoreCase)))
                    {
                        model.Filters.Add(new MediaTypeGateFilter(route.MediaTypes));
                    }
                }
            });
        return conventions;
    }
}
