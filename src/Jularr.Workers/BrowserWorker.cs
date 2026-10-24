using Jularr.Backend.Data.Admin.Browser.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.System.Browser.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jularr.Workers
{
    // Reconciles the optional browser after startup and module changes, without blocking the save request.
    internal sealed class BrowserWorker(IServiceScopeFactory scopes, ILogger<BrowserWorker> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            try
            {
                do
                {
                    try
                    {
                        using var scope = scopes.CreateScope();
                        await scope.ServiceProvider.GetRequiredService<ReconcileBrowserModuleV1>()
                            .ExecuteAsync<UpdateResult>(new BrowserParametersV1(), new RunBrowserActionDataV1("reconcile"), stoppingToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogWarning(exception, "Browser module reconciliation failed.");
                    }
                }
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }
    }
}
