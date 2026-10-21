using System.Runtime.ExceptionServices;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Acquisition.V1;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Workers
{
    // Looks for the wanted titles that are due on the Owner's interval, or as soon as a request makes one due.
    internal sealed class AcquisitionWorker(TimeProvider time, WorkerJobs jobs, WorkerJobRunner runner, AcquisitionSearchSignal signal) : PeriodicWorker(time, jobs, runner, WorkerGroupType.Acquisition)
    {
        protected override async Task RunOnceAsync(CancellationToken ct)
        {
            // A new request or a resumed monitor makes the next search due.
            if (signal.TakeRequest())
            {
                Jobs.RequestRun("wantedSearch");
            }

            // Custom formats of the files, metadata of wanted titles and the searches in small passes.
            await Runner.RunAsync("wantedSearch", (services, token) => RunAllAsync(token, Step<RefreshStoredFileFormatsV1>(services), Step<RefreshWantedMetadataV1>(services), Step<SearchDueWantedV1>(services)), ct);
        }

        protected override IEnumerable<Task<bool>> Wakeups(CancellationToken ct) => [signal.WaitForRequestAsync(ct).AsTask()];

        private static Func<CancellationToken, Task> Step<TService>(IServiceProvider services) where TService : SystemService<AcquisitionCycleParametersV1, NoData> =>
            token => services.GetRequiredService<TService>().ExecuteAsync<UpdateResult>(new AcquisitionCycleParametersV1(), default, token);

        // Each step runs even when an earlier one failed; the first failure is then raised.
        private static async Task RunAllAsync(CancellationToken ct, params Func<CancellationToken, Task>[] steps)
        {
            ExceptionDispatchInfo? first = null;
            foreach (var step in steps)
            {
                try
                {
                    await step(ct);
                }
                catch (Exception exception) when (!ct.IsCancellationRequested)
                {
                    first ??= ExceptionDispatchInfo.Capture(exception);
                }
            }

            first?.Throw();
        }
    }
}
