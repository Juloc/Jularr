using System.Security;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Operation.V1;
using Jularr.Backend.Data.System.Storage.V1;
using Jularr.Backend.Service.System.Operation.V1;
using Jularr.Backend.Service.System.Storage.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jularr.Workers
{
    // Lost NAS connections reconnect and libraries rescan in the background.
    internal sealed class StorageWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<StorageWorker> logger) : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(Interval, time);
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<ReconnectStorageMountsV1>().ExecuteAsync<UpdateResult>(new StorageConnectionsParametersV1(), default, stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "Storage reconnection failed.");
                }

                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<ScheduleLibraryScansV1>().ExecuteAsync<UpdateResult>(new LibraryScanScheduleParametersV1(), default, stoppingToken);
                }
                catch (SecurityException)
                {
                    // The Library is switched off.
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "Library scan scheduling failed.");
                }
            }
            while (await WaitAsync(timer, stoppingToken));
        }

        private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
        {
            try
            {
                return await timer.WaitForNextTickAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }
}
