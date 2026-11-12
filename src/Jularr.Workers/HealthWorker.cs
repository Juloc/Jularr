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
    // Checks the configured services every two minutes. The log names a service once when it stops answering and once when it answers again.
    // A service Jularr needs to keep and fetch files (the database, a storage, a download client) fails the pass, so the Owner is told once and the notice
    // closes when it answers again; an optional one (a media server, a metadata provider, an indexer) is shown on the Dashboard and fails nothing.
    internal sealed class HealthWorker(TimeProvider time, WorkerJobs jobs, WorkerJobRunner runner) : PeriodicWorker(time, jobs, runner, WorkerGroupType.Health)
    {
        private readonly object _gate = new();
        private Dictionary<string, string> _down = [];

        protected override async Task RunOnceAsync(CancellationToken ct) =>
            await Runner.RunAsync("health", async (services, token) =>
            {
                var health = await services.GetRequiredService<RunHealthCheckV1>().ExecuteAsync<HealthV1>(new HealthCheckParametersV1(), default, token);
                var down = health.Services.Where(service => service.HealthStateTypeId == HealthStateType.Unavailable).ToArray();
                var (lost, back) = Change(down.ToDictionary(service => service.Key, service => service.Name ?? service.Key));
                var logger = services.GetRequiredService<ILogger<HealthWorker>>();
                if (lost.Count > 0)
                {
                    logger.LogWarning("Not answering: {Services}.", string.Join(", ", lost));
                }

                if (back.Count > 0)
                {
                    logger.LogInformation("Answering again: {Services}.", string.Join(", ", back));
                }

                if (down.Any(service => IsRequired(service.HealthServiceTypeId)))
                {
                    throw new ServiceUnavailableException("health_degraded");
                }
            }, ct);

        // Without these Jularr cannot keep or fetch files; the others only add to it.
        internal static bool IsRequired(HealthServiceType type) => type is HealthServiceType.Database or HealthServiceType.Storage or HealthServiceType.DownloadClient;

        // The services that stopped answering since the last check and those that answer again, by name.
        private (IReadOnlyList<string> Lost, IReadOnlyList<string> Back) Change(Dictionary<string, string> down)
        {
            lock (_gate)
            {
                var lost = down.Where(service => !_down.ContainsKey(service.Key)).Select(service => service.Value).ToArray();
                var back = _down.Where(service => !down.ContainsKey(service.Key)).Select(service => service.Value).ToArray();
                _down = down;
                return (lost, back);
            }
        }
    }
}
