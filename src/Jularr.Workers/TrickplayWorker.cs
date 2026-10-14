using System.Security;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Service.System.Media.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jularr.Workers
{
    // Seek thumbnails are rendered file after file with a short breather; without work the worker looks again later.
    internal sealed class TrickplayWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<TrickplayWorker> logger) : BackgroundService
    {
        private static readonly TimeSpan IdleDelay = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan BusyDelay = TimeSpan.FromSeconds(5);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var busy = false;
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    busy = (await scope.ServiceProvider.GetRequiredService<GenerateTrickplayV1>().ExecuteAsync<UpdateResult>(new TrickplayParametersV1(), default, stoppingToken)).Success;
                }
                catch (SecurityException)
                {
                    logger.LogDebug("Seek thumbnails skipped: their module is switched off.");
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "Seek thumbnail rendering failed.");
                }

                try
                {
                    await Task.Delay(busy ? BusyDelay : IdleDelay, time, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
