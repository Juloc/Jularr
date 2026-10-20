using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Library.V1;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Data.System.Operation.V1;
using Jularr.Backend.Service.System.Library.V1;
using Jularr.Backend.Service.System.Media.V1;
using Jularr.Backend.Service.System.Operation.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jularr.Workers
{
    // Hourly housekeeping of stored history, recycled files and cover files.
    internal sealed class MaintenanceWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<MaintenanceWorker> logger) : PeriodicWorker(scopes, time, logger, TimeSpan.FromHours(1))
    {
        protected override async Task RunOnceAsync(CancellationToken ct)
        {
            await RunAsync("Operation history pruning", (services, token) => services.GetRequiredService<PruneOperationsV1>().ExecuteAsync<UpdateResult>(new PruneOperationsParametersV1(), default, token), ct);
            await RunAsync("Recycle folder cleanup", (services, token) => services.GetRequiredService<CleanRecycleBinsV1>().ExecuteAsync<UpdateResult>(new CleanRecycleBinsParametersV1(), default, token), ct);
            await RunAsync("Cover cleanup", (services, token) => services.GetRequiredService<CleanCoversV1>().ExecuteAsync<UpdateResult>(new CoverParametersV1(), default, token), ct);
        }
    }
}
