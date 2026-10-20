using System.Runtime.ExceptionServices;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Acquisition.V1;
using Jularr.Backend.Service.System.Provider.V1;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Workers
{
    internal sealed class AcquisitionWorker(TimeProvider time, WorkerJobs jobs, WorkerJobRunner runner, AcquisitionSearchSignal signal) : PeriodicWorker(time, jobs, runner, WorkerGroupType.Acquisition)
    {
        protected override async Task RunOnceAsync(CancellationToken ct)
        {
            // A new request or a resumed monitor makes the next search due.
            if (signal.TakeRequest())
            {
                Jobs.RequestRun("wantedSearch");
            }

            // Downloads are tracked often; metadata and due targets are refreshed in small passes.
            await Runner.RunAsync("downloads", (services, token) => RunAllAsync(token, Step<MonitorDownloadsV1>(services), Step<RefreshStoredFileFormatsV1>(services)), ct);
            await Runner.RunAsync("wantedSearch", (services, token) => RunAllAsync(token, Step<RefreshWantedMetadataV1>(services), Step<SearchDueWantedV1>(services)), ct);

            // Plex availability follows the server.
            await Runner.RunAsync("plexSync", (services, token) =>
                services.GetRequiredService<SyncPlexLibraryV1>().ExecuteAsync<UpdateResult>(new AcquisitionCycleParametersV1(), default, token), ct);
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
