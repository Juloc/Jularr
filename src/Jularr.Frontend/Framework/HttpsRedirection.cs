using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.Extensions.Options;

namespace Jularr.Frontend.Framework
{
    // HTTPS is the reverse proxy's job. The application redirects to HTTPS only when an HTTPS port or endpoint is configured (HTTPS_PORT, an
    // https address of the server); without one it says so once at the start instead of warning on every request that it found no port.
    // The container's probes are never redirected.
    public static class HttpsRedirection
    {
        public static IApplicationBuilder UseJularrHttpsRedirection(this WebApplication app)
        {
            var state = new State();
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                state.IsOn = HasHttpsPort(app);
                if (!state.IsOn)
                {
                    app.Logger.LogInformation("HTTPS redirection is off: no HTTPS port or endpoint is configured.");
                }
            });
            return app.UseWhen(context => state.IsOn && !Health.IsProbe(context.Request.Path), branch => branch.UseHttpsRedirection());
        }

        // The places the redirect itself takes its port from, in its order.
        private static bool HasHttpsPort(WebApplication app) =>
            app.Services.GetRequiredService<IOptions<HttpsRedirectionOptions>>().Value.HttpsPort is not null
            || app.Configuration.GetValue<int?>("HTTPS_PORT") is not null
            || app.Configuration.GetValue<int?>("ANCM_HTTPS_PORT") is not null
            || (app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [])
                .Any(address => address.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

        private sealed class State
        {
            public volatile bool IsOn;
        }
    }
}
