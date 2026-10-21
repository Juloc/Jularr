using Jularr.Backend.Service.Core;

namespace Jularr.Frontend.Framework
{
    // Browser pages answer a failure with the styled error page (Pages/Account/Error); API calls, streams, assets and scripted requests keep their
    // plain status and Problem Details body.
    public static class ErrorPages
    {
        private static readonly string[] NotPages = ["/api", "/live", "/build", "/emulator"];

        // Whether the client navigates to a page: an address of the application's pages that accepts HTML, or an in-place (htmx) request.
        public static bool WantsPage(HttpContext context)
        {
            var path = context.Request.Path;
            if (NotPages.Any(prefix => path.StartsWithSegments(prefix))) return false;
            return context.Request.Headers.ContainsKey("HX-Request")
                || context.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
        }

        public static IApplicationBuilder UseJularrErrorPages(this IApplicationBuilder app) => app.UseWhen(WantsPage, branch =>
        {
            // Every empty failure re-executes its status as the error page ...
            branch.UseStatusCodePagesWithReExecute("/Error/{0}");

            // ... and an unhandled exception becomes the status the shared error contract gives it (500 unless it is a known failure). The exception
            // is logged here and never shown: the page names the request and the time only.
            branch.Use(async (context, next) =>
            {
                try
                {
                    await next(context);
                }
                catch (Exception exception) when (exception is not OperationCanceledException && !context.Response.HasStarted)
                {
                    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Jularr.ErrorPages");
                    var error = ServiceError.Describe(exception, context, logger);
                    context.Response.Clear();
                    context.Response.StatusCode = error.StatusCode;
                }
            });
        });
    }
}
