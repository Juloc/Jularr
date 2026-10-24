using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Media.V1;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Workers
{
    // Public Discover lists and, when enabled, the full local provider catalog.
    internal sealed class CatalogWorker(TimeProvider time, WorkerJobs jobs, WorkerJobRunner runner) : PeriodicWorker(time, jobs, runner, WorkerGroupType.Catalog)
    {
        protected override async Task RunOnceAsync(CancellationToken ct)
        {
            await Runner.RunAsync("discoverFeeds", (services, token) =>
                services.GetRequiredService<RefreshDiscoverFeedsV1>().ExecuteAsync<UpdateResult>(new RefreshDiscoverFeedsParametersV1(), default, token), ct);
            await Runner.RunAsync("catalog", (services, token) =>
                services.GetRequiredService<SyncExternalWorksV1>().ExecuteAsync<UpdateResult>(new SyncExternalWorksParametersV1(), default, token), ct);
        }
    }
}
