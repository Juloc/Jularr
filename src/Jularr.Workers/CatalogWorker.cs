using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Service.System.Media.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jularr.Workers
{
    // Full local discovery: a pass of the provider catalog every minute while the LocalDiscovery module is on.
    internal sealed class CatalogWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<CatalogWorker> logger) : PeriodicWorker(scopes, time, logger, TimeSpan.FromMinutes(1))
    {
        protected override async Task RunOnceAsync(CancellationToken ct)
        {
            await RunAsync("Provider catalog sync", (services, token) => services.GetRequiredService<SyncExternalWorksV1>().ExecuteAsync<UpdateResult>(new SyncExternalWorksParametersV1(), default, token), ct);
        }
    }
}
