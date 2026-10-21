using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Acquisition.V1;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Workers
{
    // Tracks the downloads of external clients: every 15 seconds while one runs, otherwise on a slow safety tick or as soon as a new one is grabbed.
    internal sealed class DownloadWorker(TimeProvider time, WorkerJobs jobs, WorkerJobRunner runner, WorkSignals signals) : PeriodicWorker(time, jobs, runner, WorkerGroupType.Download, ActiveInterval)
    {
        private static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(15);

        protected override async Task RunOnceAsync(CancellationToken ct)
        {
            // A new download makes the next look due at once.
            if (signals.Downloads.TryTake())
            {
                Jobs.RequestRun("downloads");
            }

            var isActive = false;
            if (await Runner.RunAsync("downloads", async (services, token) =>
                isActive = (await services.GetRequiredService<MonitorDownloadsV1>().ExecuteAsync<UpdateResult>(new AcquisitionCycleParametersV1(), default, token)).Success, ct) is not null && isActive)
            {
                Jobs.RunAgainWithin("downloads", ActiveInterval);
            }
        }

        protected override IEnumerable<Task<bool>> Wakeups(CancellationToken ct) => [signals.Downloads.WaitToReadAsync(ct).AsTask()];
    }
}
