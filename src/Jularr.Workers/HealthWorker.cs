using Jularr.Backend.Data.Admin.System.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Data.System.Instance.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Instance.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jularr.Workers
{
    // Checks the configured services every two minutes. A service that does not answer fails the pass, so the Owner is told once and the notice closes when everything answers again.
    internal sealed class HealthWorker(TimeProvider time, WorkerJobs jobs, WorkerJobRunner runner) : PeriodicWorker(time, jobs, runner, WorkerGroupType.Health)
    {
        protected override async Task RunOnceAsync(CancellationToken ct) =>
            await Runner.RunAsync("health", async (services, token) =>
            {
                var health = await services.GetRequiredService<RunHealthCheckV1>().ExecuteAsync<HealthV1>(new HealthCheckParametersV1(), default, token);
                if (!health.IsStable)
                {
                    // The log names what does not answer; the failure itself is told to the Owner as a notice.
                    var down = health.Services.Where(service => service.HealthStateTypeId == HealthStateType.Unavailable).Select(service => service.Name ?? service.Key);
                    services.GetRequiredService<ILogger<HealthWorker>>().LogWarning("Not answering: {Services}.", string.Join(", ", down));
                    throw new ServiceUnavailableException("health_degraded");
                }
            }, ct);
    }
}
