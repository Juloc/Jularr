using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Data.System.Operation.V1;
using Jularr.Backend.Data.System.Storage.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Operation.V1;
using Jularr.Backend.Service.System.Storage.V1;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Workers
{
    // Failed storage is checked again and libraries rescan in the background.
    internal sealed class StorageWorker(TimeProvider time, WorkerJobs jobs, WorkerJobRunner runner) : PeriodicWorker(time, jobs, runner, WorkerGroupType.Storage)
    {
        protected override async Task RunOnceAsync(CancellationToken ct)
        {
            await Runner.RunAsync("storage", (services, token) =>
                services.GetRequiredService<CheckStorageMountsV1>().ExecuteAsync<UpdateResult>(new StorageChecksParametersV1(), default, token), ct);
            await Runner.RunAsync("scanScheduling", (services, token) =>
                services.GetRequiredService<ScheduleLibraryScansV1>().ExecuteAsync<UpdateResult>(new LibraryScanScheduleParametersV1(), default, token), ct);
        }
    }
}
