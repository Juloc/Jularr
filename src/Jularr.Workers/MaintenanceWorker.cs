using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Operation.V1;
using Jularr.Backend.Service.System.Operation.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jularr.Workers
{
    // Hourly housekeeping of stored history.
    internal sealed class MaintenanceWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<MaintenanceWorker> logger) : PeriodicWorker(scopes, time, logger, TimeSpan.FromHours(1))
    {
        protected override Task RunOnceAsync(CancellationToken ct) =>
            RunAsync("Operation history pruning", (services, token) => services.GetRequiredService<PruneOperationsV1>().ExecuteAsync<UpdateResult>(new PruneOperationsParametersV1(), default, token), ct);
    }
}
