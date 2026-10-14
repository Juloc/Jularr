using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Operation.V1;
using Jularr.Backend.Data.System.Storage.V1;
using Jularr.Backend.Service.System.Operation.V1;
using Jularr.Backend.Service.System.Storage.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jularr.Workers
{
    // Lost NAS connections reconnect and libraries rescan in the background.
    internal sealed class StorageWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<StorageWorker> logger) : PeriodicWorker(scopes, time, logger, TimeSpan.FromMinutes(1))
    {
        protected override async Task RunOnceAsync(CancellationToken ct)
        {
            await RunAsync("Storage reconnection", (services, token) =>
                services.GetRequiredService<ReconnectStorageMountsV1>().ExecuteAsync<UpdateResult>(new StorageConnectionsParametersV1(), default, token), ct);
            await RunAsync("Library scan scheduling", (services, token) =>
                services.GetRequiredService<ScheduleLibraryScansV1>().ExecuteAsync<UpdateResult>(new LibraryScanScheduleParametersV1(), default, token), ct);
        }
    }
}
