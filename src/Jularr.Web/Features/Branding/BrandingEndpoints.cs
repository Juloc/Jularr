using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jularr.Web.Features.Branding;

/// <summary>The logo endpoint and the branded web app manifest: the two places where the browser, not a page, asks for the instance identity.</summary>
public static class BrandingEndpoints
{
    public const string LogoPath = "/branding/logo";
    public const string ManifestPath = "/manifest.webmanifest";

    private const int ShortNameLength = 12;

    /// <summary>
    /// The logo is public: the login page shows it before anyone is signed in. It is served only as an image the page embeds, sandboxed and without
    /// content sniffing, and under a versioned address so the browser may keep it for a day.
    /// </summary>
    public static IEndpointRouteBuilder MapBranding(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(LogoPath, async (InstanceBrandingStore store, HttpContext context, CancellationToken cancellationToken) =>
        {
            var logo = await store.GetLogoAsync(cancellationToken);
            if (logo is null)
            {
                return Results.NotFound();
            }

            var tag = $"\"{logo.Version}\"";
            var headers = context.Response.Headers;
            headers.ETag = tag;
            headers.CacheControl = "public, max-age=86400";
            headers.XContentTypeOptions = "nosniff";
            headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
            return context.Request.Headers.IfNoneMatch == tag ? Results.StatusCode(StatusCodes.Status304NotModified) : Results.Bytes(logo.Bytes, logo.ContentType);
        }).AllowAnonymous();
        return endpoints;
    }

    /// <summary>
    /// Answers the manifest with the instance name while custom branding is on; otherwise the static file is served as it is. It sits before the static
    /// files so the install name follows the branding without a second copy of the manifest.
    /// </summary>
    public static IApplicationBuilder UseBrandedManifest(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var store = context.RequestServices.GetService<InstanceBrandingStore>();
        if (!HttpMethods.IsGet(context.Request.Method) || !context.Request.Path.Equals(ManifestPath, StringComparison.OrdinalIgnoreCase) || store is null)
        {
            await next();
            return;
        }

        var settings = await store.GetAsync(context.RequestAborted);
        var file = context.RequestServices.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider.GetFileInfo(ManifestPath.TrimStart('/'));
        if (!settings.IsCustom || settings.Name is not { } name || !file.Exists)
        {
            await next();
            return;
        }

        await using var stream = file.CreateReadStream();
        var manifest = (await JsonNode.ParseAsync(stream, cancellationToken: context.RequestAborted))!.AsObject();
        manifest["name"] = name;
        manifest["short_name"] = name.Length <= ShortNameLength ? name : name[..ShortNameLength];
        context.Response.ContentType = "application/manifest+json";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.WriteAsync(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), context.RequestAborted);
    });
}
