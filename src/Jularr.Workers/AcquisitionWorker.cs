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
    internal sealed class AcquisitionWorker(TimeProvider time, WorkerJobs jobs, WorkerJobRunner runner, AcquisitionSearchSignal signal, LiveChanges live) : PeriodicWorker(time, jobs, runner, WorkerGroupType.Acquisition)
    {
        // Set by the wake-up below: this pass only reads what the download clients report.
        private bool _isWatchPass;

        protected override async Task RunOnceAsync(CancellationToken ct)
        {
            var isWatchPass = _isWatchPass;
            _isWatchPass = false;

            // A new request or a resumed monitor makes the next search due.
            if (signal.TakeRequest())
            {
                Jobs.RequestRun("wantedSearch");
            }

            // Downloads are tracked often; metadata and due targets are refreshed in small passes.
            // While someone watches a running download its progress is read every second, without the rest of the pass.
            await Runner.RunAsync("downloads", (services, token) => isWatchPass
                ? RunAllAsync(token, Step<MonitorDownloadsV1>(services))
                : RunAllAsync(token, Step<MonitorDownloadsV1>(services), Step<RefreshStoredFileFormatsV1>(services)), ct);
            await Runner.RunAsync("wantedSearch", (services, token) => RunAllAsync(token, Step<RefreshWantedMetadataV1>(services), Step<SearchDueWantedV1>(services)), ct);

            // Plex availability follows the server.
            await Runner.RunAsync("plexSync", (services, token) =>
                services.GetRequiredService<SyncPlexLibraryV1>().ExecuteAsync<UpdateResult>(new AcquisitionCycleParametersV1(), default, token), ct);
        }

        protected override IEnumerable<Task<bool>> Wakeups(CancellationToken ct) => [signal.WaitForRequestAsync(ct).AsTask(), WaitForWatchedDownloadAsync(ct)];

        // Wakes the worker once a second for as long as someone watches a download (the paused job answers no run now and simply waits for its tick).
        private async Task<bool> WaitForWatchedDownloadAsync(CancellationToken ct)
        {
            do
            {
                await Task.Delay(LiveChanges.DownloadInterval, Time, ct);
            }
            while (!live.WantsDownloadProgress);

            _isWatchPass = true;
            Jobs.RequestRun("downloads");
            return true;
        }

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
