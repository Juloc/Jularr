using System.Text.Json;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Modules.Pwa
{
    // The two files that make the app installable: the web app manifest and the pass-through service worker.
    // Both are public (the browser reads them before anyone signs in) and answer at the site root; the worker caches nothing.
    // There is no path base in this host, so every address is absolute from the root.
    public static class PwaEndpoint
    {
        // The default accent seed and the surface of the shared tokens (Theme.scss, Palette.ts).
        private const string ThemeColor = "#8040f7";
        private const string BackgroundColor = "#ffffff";

        private static readonly Lazy<string> Worker = new(() =>
        {
            using var stream = typeof(PwaEndpoint).Assembly.GetManifestResourceStream("Jularr.Frontend.Modules.Pwa.ServiceWorker.js")
                ?? throw new InvalidOperationException("The service worker is not part of the build.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        });

        public static IEndpointRouteBuilder MapJularrPwa(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/manifest.webmanifest", (HttpContext http, FrontendAssets assets) =>
            {
                http.Response.Headers.CacheControl = "no-cache";
                return Results.Text(Manifest(assets), "application/manifest+json");
            }).AllowAnonymous();
            endpoints.MapGet("/service-worker.js", (HttpContext http) =>
            {
                http.Response.Headers.CacheControl = "no-cache";
                http.Response.Headers["Service-Worker-Allowed"] = "/";
                return Results.Text(Worker.Value, "text/javascript");
            }).AllowAnonymous();
            return endpoints;
        }

        // The icons are the fingerprinted brand files the layout links as well, so a new build never serves a stale picture.
        private static string Manifest(FrontendAssets assets)
        {
            var icon192 = assets.ImageUrl("Assets/Shared/icon-192.png");
            var icon512 = assets.ImageUrl("Assets/Shared/icon-512.png");
            var maskable = assets.ImageUrl("Assets/Shared/icon-maskable-512.png");
            return JsonSerializer.Serialize(new
            {
                id = "/",
                name = "Jularr",
                short_name = "Jularr",
                start_url = "/",
                scope = "/",
                display = "standalone",
                theme_color = ThemeColor,
                background_color = BackgroundColor,
                icons = new[]
                {
                    new { src = icon192, sizes = "192x192", type = "image/png", purpose = "any" },
                    new { src = icon512, sizes = "512x512", type = "image/png", purpose = "any" },
                    new { src = maskable, sizes = "512x512", type = "image/png", purpose = "maskable" }
                }
            });
        }
    }
}
