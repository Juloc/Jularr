using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Acquisition.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jularr.Workers
{
    // Jularr's own Usenet downloads run back to back while there is work; otherwise the worker looks again shortly.
    internal sealed class UsenetWorker(TimeProvider time, WorkerJobRunner runner) : BackgroundService
    {
        private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(15);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(WorkerJobs.Definition("usenet").StartDelay, time, stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    await runner.RunWhileBusyAsync("usenet", async (services, token) =>
                        (await services.GetRequiredService<DownloadUsenetV1>().ExecuteAsync<UpdateResult>(new AcquisitionCycleParametersV1(), default, token)).Success, TimeSpan.Zero, time, stoppingToken);
                    await Task.Delay(IdleDelay, time, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }
    }
}
