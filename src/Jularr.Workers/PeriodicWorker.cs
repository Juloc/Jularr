using System.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jularr.Workers
{
    // Runs one pass at start and then on every tick; a failed step is logged and the next tick tries again.
    internal abstract class PeriodicWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger logger, TimeSpan interval) : BackgroundService
    {
        protected TimeProvider Time { get; } = time;

        protected abstract Task RunOnceAsync(CancellationToken ct);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(interval, Time);
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await WaitForNextRunAsync(timer, stoppingToken));
        }

        // One System service call in its own scope.
        protected async Task RunAsync(string name, Func<IServiceProvider, CancellationToken, Task> step, CancellationToken ct)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await step(scope.ServiceProvider, ct);
            }
            catch (SecurityException)
            {
                logger.LogDebug("{Step} skipped: its module is switched off.", name);
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(exception, "{Step} failed.", name);
            }
        }

        protected virtual async Task<bool> WaitForNextRunAsync(PeriodicTimer timer, CancellationToken ct)
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
