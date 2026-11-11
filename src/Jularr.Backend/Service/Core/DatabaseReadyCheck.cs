using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Jularr.Backend.Service.Core
{
    // /health/ready: the application serves (started, not stopping) and its database answers within two seconds, on a connection of its own,
    // with the schema of this release. The reason stays in the log; the answer carries no details.
    internal sealed class DatabaseReadyCheck(DatabaseBaseline baseline, IHostApplicationLifetime lifetime) : IHealthCheck
    {
        internal static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            if (!lifetime.ApplicationStarted.IsCancellationRequested || lifetime.ApplicationStopping.IsCancellationRequested)
            {
                return HealthCheckResult.Unhealthy("The application is not serving.");
            }

            try
            {
                return await baseline.IsCurrentAsync(Limit, cancellationToken)
                    ? HealthCheckResult.Healthy()
                    : HealthCheckResult.Unhealthy("The database schema is not this release's.");
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                return HealthCheckResult.Unhealthy("The database did not answer within " + Limit.TotalSeconds + " s (" + exception.GetType().Name + ").");
            }
        }
    }
}
