using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Data.System.Library.V1;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Data.System.Operation.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Library.V1;
using Jularr.Backend.Service.System.Media.V1;
using Jularr.Backend.Service.System.Operation.V1;
using Jularr.Backend.Service.System.Acquisition.V1;
using Jularr.Backend.Data.System.Acquisition.V1;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Workers
{
    // Hourly housekeeping of stored history, recycled files and cover files.
    internal sealed class MaintenanceWorker(TimeProvider time, WorkerJobs jobs, WorkerJobRunner runner) : PeriodicWorker(time, jobs, runner, WorkerGroupType.Maintenance)
    {
        protected override async Task RunOnceAsync(CancellationToken ct) =>
            await Runner.RunAsync("maintenance", async (services, token) =>
            {
                await services.GetRequiredService<PruneOperationsV1>().ExecuteAsync<UpdateResult>(new PruneOperationsParametersV1(), default, token);
                await services.GetRequiredService<CleanRecycleBinsV1>().ExecuteAsync<UpdateResult>(new CleanRecycleBinsParametersV1(), default, token);
                await services.GetRequiredService<CleanCoversV1>().ExecuteAsync<UpdateResult>(new CoverParametersV1(), default, token);
                await services.GetRequiredService<ReconcileDirectDownloadFoldersV1>().ExecuteAsync<UpdateResult>(new AcquisitionCycleParametersV1(), default, token);
            }, ct);
    }
}
