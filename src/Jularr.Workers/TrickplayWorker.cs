using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Service.System.Media.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jularr.Workers
{
    // Seek thumbnails file after file with a short pause; without work it looks again later.
    internal sealed class TrickplayWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<TrickplayWorker> logger) : PeriodicWorker(scopes, time, logger, TimeSpan.FromMinutes(5))
    {
        private static readonly TimeSpan Pause = TimeSpan.FromSeconds(5);

        protected override async Task RunOnceAsync(CancellationToken ct)
        {
            var busy = true;
            while (busy && !ct.IsCancellationRequested)
            {
                busy = false;
                await RunAsync("Seek thumbnails", async (services, token) =>
                    busy = (await services.GetRequiredService<GenerateTrickplayV1>().ExecuteAsync<UpdateResult>(new TrickplayParametersV1(), default, token)).Success, ct);
                if (busy)
                {
                    await Task.Delay(Pause, Time, ct);
                }
            }
        }
    }
}
