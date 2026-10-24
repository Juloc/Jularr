using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Browser.V1;
using Jularr.Backend.Service.System.Browser.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jularr.Workers
{
    // One module reconciliation at a time; the first pass runs after database initialization.
    internal sealed class BrowserModuleWorker(IServiceScopeFactory scopes, ILogger<BrowserModuleWorker> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            do
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<ReconcileBrowserModuleV1>()
                        .ExecuteAsync<UpdateResult>(new BrowserCycleParametersV1(), default, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Optional browser module reconciliation failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
    }
}
