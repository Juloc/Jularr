using System.Security;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Public.Setup.V1;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Backend.Service.Public.Setup.V1;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Frontend.Framework
{
    // While the setup is not finished every page leads back to it: the first Owner is created, then only that Owner continues at the step last reached,
    // everyone else is told the instance is being set up. Once it is finished the setup addresses are closed.
    public sealed class SetupGate(RequestDelegate next)
    {
        private volatile bool _completed;

        public async Task InvokeAsync(HttpContext context)
        {
            // Only pages are gated; assets, the live stream, the installable app and the service API answer as they do.
            if (context.GetEndpoint()?.Metadata.GetMetadata<PageActionDescriptor>() is null)
            {
                await next(context);
                return;
            }

            // Finished setup never reopens, so it is remembered; an open one is read again for every page.
            var path = context.Request.Path;
            ReadSetupResultV1? setup = null;
            if (!_completed)
            {
                setup = await context.RequestServices.GetRequiredService<ReadSetupV1>().ExecuteAsync<ReadSetupResultV1>(cancellationToken: context.RequestAborted);
                _completed = setup.IsCompleted;
            }

            if (_completed)
            {
                if (path.StartsWithSegments("/Setup")) context.Response.Redirect("/");
                else await next(context);
                return;
            }

            // The Error pages and the Owner's own sign-in are all that is open to everybody.
            var open = path.StartsWithSegments("/Error") || path.StartsWithSegments("/Account/Login") || path.StartsWithSegments("/Account/Logout");
            if (setup!.RequiresOwner)
            {
                if (path.Equals("/Setup", StringComparison.OrdinalIgnoreCase) || path.StartsWithSegments("/Error")) await next(context);
                else context.Response.Redirect("/Setup");
                return;
            }

            if (context.User.Identity?.IsAuthenticated is not true)
            {
                if (open) await next(context);
                else context.Response.Redirect("/Account/Login");
                return;
            }

            // Only the Owner can read the progress; any other account waits.
            SetupProgressV1 progress;
            try
            {
                progress = await context.RequestServices.GetRequiredService<ReadSetupProgressV1>().ExecuteAsync<SetupProgressV1>(new SetupProgressParametersV1(), context.RequestAborted);
            }
            catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException)
            {
                if (path.StartsWithSegments("/Setup/Waiting") || path.StartsWithSegments("/Account/Logout") || path.StartsWithSegments("/Error")) await next(context);
                else context.Response.Redirect("/Setup/Waiting");
                return;
            }

            context.Items[SetupWizard.ProgressKey] = progress;
            if (path.StartsWithSegments("/Setup/Waiting")) context.Response.Redirect("/Setup/Progress");
            else if (path.StartsWithSegments("/Setup") || open) await next(context);
            else context.Response.Redirect("/Setup/Progress");
        }
    }

    public static class SetupGateExtensions
    {
        public static IApplicationBuilder UseJularrSetupGate(this IApplicationBuilder app) => app.UseMiddleware<SetupGate>();
    }
}
