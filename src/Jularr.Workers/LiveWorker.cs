using Jularr.Backend.Data.System.Live.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Live.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jularr.Workers
{
    // Looks at the database for the live streams, and only while one is open: every second while a download runs, then every two or five.
    internal sealed class LiveWorker(IServiceScopeFactory scopes, LiveChanges changes, TimeProvider time, ILogger<LiveWorker> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var interval = LiveChanges.IdleInterval;
                try
                {
                    await changes.WaitForSubscribersAsync(stoppingToken);
                    await using var scope = scopes.CreateAsyncScope();
                    var version = await scope.ServiceProvider.GetRequiredService<ReadLiveVersionV1>().ExecuteAsync<LiveVersionV1>(new ReadLiveVersionParametersV1(), default, stoppingToken);
                    changes.Publish(version.Version, version.HasDownloads);
                    interval = LiveChanges.IntervalFor(version.HasDownloads, version.HasJobs);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Live changes are unavailable.");
                }

                try
                {
                    await Task.Delay(interval, time, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
