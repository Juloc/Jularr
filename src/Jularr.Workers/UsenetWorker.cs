using System.Security;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Service.System.Acquisition.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jularr.Workers
{
    // Jularr's own Usenet downloads run back to back while there is work; otherwise the worker looks again shortly.
    internal sealed class UsenetWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<UsenetWorker> logger) : BackgroundService
    {
        private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(15);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var busy = false;
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    busy = (await scope.ServiceProvider.GetRequiredService<DownloadUsenetV1>().ExecuteAsync<UpdateResult>(new AcquisitionCycleParametersV1(), default, stoppingToken)).Success;
                }
                catch (SecurityException)
                {
                    logger.LogDebug("Usenet downloads skipped: Arr+ is switched off.");
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "The Usenet download pass failed.");
                }

                if (!busy)
                {
                    try
                    {
                        await Task.Delay(IdleDelay, time, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }
    }
}
