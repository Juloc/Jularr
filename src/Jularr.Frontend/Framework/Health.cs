using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace Jularr.Frontend.Framework
{
    // The container's probes, anonymous, past the setup gate, the rate limiter and the HTTPS redirect, and without details: /health/live
    // answers 200 while the process runs; /health/ready answers 200 once the application serves with a current database, else 503.
    public static class Health
    {
        public const string LivePath = "/health/live";
        public const string ReadyPath = "/health/ready";

        public static bool IsProbe(PathString path) => path.StartsWithSegments("/health");

        public static void MapJularrHealth(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapHealthChecks(LivePath, new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous().DisableRateLimiting();
            endpoints.MapHealthChecks(ReadyPath).AllowAnonymous().DisableRateLimiting();
        }

        // The status listener's answer while the backup and the migrations run (or after a failed update): alive, not ready.
        internal static async Task<bool> AnswerStartingAsync(HttpContext context)
        {
            var path = context.Request.Path;
            var isLive = path.Equals(LivePath, StringComparison.OrdinalIgnoreCase);
            if (!isLive && !path.Equals(ReadyPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            context.Response.StatusCode = isLive ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.CacheControl = "no-store, no-cache";
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync(isLive ? "Healthy" : "Unhealthy");
            return true;
        }
    }
}
