using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Acquisition.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jularr.Workers
{
    // Jularr's own torrent downloads run back to back while there is work. Without a torrent job, or with the module off, the worker only waits for a grab:
    // no engine, no timer, no database polling until one is announced.
    internal sealed class TorrentWorker(IServiceScopeFactory scopes, WorkSignals signals, TimeProvider time, ILogger<TorrentWorker> logger) : BackgroundService
    {
        private static readonly TimeSpan JobTick = TimeSpan.FromSeconds(30);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8), time, stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    var hasJobs = false;
                    try
                    {
                        TorrentCycleResultV1 cycle;
                        do
                        {
                            await using var scope = scopes.CreateAsyncScope();
                            cycle = await scope.ServiceProvider.GetRequiredService<DownloadTorrentsV1>().ExecuteAsync<TorrentCycleResultV1>(new AcquisitionCycleParametersV1(), default, stoppingToken);
                            hasJobs = cycle.HasJobs;
                        }
                        while (cycle.Progressed && !stoppingToken.IsCancellationRequested);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogWarning(exception, "A pass of the torrent downloads failed.");
                        hasJobs = true;
                    }

                    // Paused, waiting or resting jobs are looked at again shortly; with none the worker sleeps until a grab wakes it.
                    if (hasJobs) await signals.Torrents.WaitAsync(JobTick, time, stoppingToken);
                    else await signals.Torrents.WaitToReadAsync(stoppingToken);
                    signals.Torrents.TryTake();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }
    }
}
