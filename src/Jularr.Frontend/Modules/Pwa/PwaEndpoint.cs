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
            endpoints.MapGet("/manifest.webmanifest", async (HttpContext http, FrontendAssets assets) =>
            {
                var accent = await PwaIconRenderer.AccountAccentAsync(http);
                // The manifest can differ by signed-in account. Never let a shared cache serve another account's choice.
                http.Response.Headers.CacheControl = accent is null ? "no-cache" : "private, no-store";
                http.Response.Headers.Vary = "Cookie";
                return Results.Text(Manifest(assets, accent), "application/manifest+json");
            }).AllowAnonymous();
            endpoints.MapGet("/pwa/icons/{kind}/{color}.png", (HttpContext http, string kind, string color) =>
            {
                if (kind == "favicon-svg" || !PwaIconRenderer.IsValid(kind, color)) return Results.NotFound();
                // Only the public color and asset kind affect the pixels; no IDs, secrets, cookies or profile content.
                http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                return Results.File(PwaIconRenderer.Render(kind, color), "image/png", enableRangeProcessing: true);
            }).AllowAnonymous();
            endpoints.MapGet("/pwa/icons/favicon/{color}.svg", (HttpContext http, string color) =>
            {
                if (!PwaIconRenderer.IsValid("favicon-svg", color)) return Results.NotFound();
                http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                return Results.Text(PwaIconRenderer.RenderSvg(color), "image/svg+xml");
            }).AllowAnonymous();
            // The running frontend's build identity is public, contains no account state and is never cached.
            // A standalone PWA checks it when resumed; the user decides when a new build may replace the current document.
            endpoints.MapGet("/pwa/version", (HttpContext http, FrontendAssets assets) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return Results.Json(new { build = assets.BuildId });
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
        private static string Manifest(FrontendAssets assets, string? accent)
        {
            var icon192 = accent is null ? assets.ImageUrl("Assets/Shared/icon-192.png") : PwaIconRenderer.IconUrl("192", accent);
            var icon512 = accent is null ? assets.ImageUrl("Assets/Shared/icon-512.png") : PwaIconRenderer.IconUrl("512", accent);
            var maskable = accent is null ? assets.ImageUrl("Assets/Shared/icon-maskable-512.png") : PwaIconRenderer.IconUrl("maskable-512", accent);
            return JsonSerializer.Serialize(new
            {
                id = "/",
                name = "Jularr",
                short_name = "Jularr",
                start_url = "/",
                scope = "/",
                display = "standalone",
                theme_color = accent is null ? ThemeColor : "#" + accent,
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
