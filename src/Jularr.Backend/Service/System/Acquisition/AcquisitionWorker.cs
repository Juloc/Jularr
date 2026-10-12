using System.Security;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Acquisition.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jularr.Backend.Service.System.Acquisition
{
    internal sealed class AcquisitionWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<AcquisitionWorker> logger) : BackgroundService
    {
        private static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan SearchInterval = TimeSpan.FromMinutes(2);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var nextSearch = DateTimeOffset.MinValue;
            using var timer = new PeriodicTimer(MonitorInterval, time);
            do
            {
                // Downloads are tracked often; metadata and due targets are refreshed in small passes.
                await RunAsync<MonitorDownloadsV1>(stoppingToken);
                if (time.GetUtcNow() >= nextSearch)
                {
                    await RunAsync<RefreshWantedMetadataV1>(stoppingToken);
                    await RunAsync<SearchDueWantedV1>(stoppingToken);
                    nextSearch = time.GetUtcNow() + SearchInterval;
                }
            }
            while (await WaitAsync(timer, stoppingToken));
        }

        private async Task RunAsync<TService>(CancellationToken ct) where TService : SystemService<AcquisitionCycleParametersV1, NoData>
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<TService>().ExecuteAsync<UpdateResult>(new AcquisitionCycleParametersV1(), default, ct);
            }
            catch (SecurityException)
            {
                // Arr+ is switched off.
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Acquisition work {Service} failed.", typeof(TService).Name);
            }
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
