using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Service.System.Media.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jularr.Workers
{
    // Missing subtitles file after file with a pause the providers' limits allow; without work it looks again later.
    internal sealed class SubtitleWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<SubtitleWorker> logger) : PeriodicWorker(scopes, time, logger, TimeSpan.FromMinutes(30))
    {
        private static readonly TimeSpan Pause = TimeSpan.FromSeconds(10);

        protected override async Task RunOnceAsync(CancellationToken ct)
        {
            var busy = true;
            while (busy && !ct.IsCancellationRequested)
            {
                busy = false;
                await RunAsync("Subtitles", async (services, token) =>
                    busy = (await services.GetRequiredService<FetchSubtitlesV1>().ExecuteAsync<UpdateResult>(new SubtitleParametersV1(), default, token)).Success, ct);
                if (busy)
                {
                    await Task.Delay(Pause, Time, ct);
                }
            }
        }
    }
}
