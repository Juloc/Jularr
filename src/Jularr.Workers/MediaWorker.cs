using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Data.System.Provider.V1;
using Jularr.Backend.Service.System.Media.V1;
using Jularr.Backend.Service.System.Provider.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jularr.Workers
{
    // Secondary provider matching, progress shared with media servers and the provider images of titles run in slow passes beside the library.
    internal sealed class MediaWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<MediaWorker> logger) : PeriodicWorker(scopes, time, logger, TimeSpan.FromMinutes(10))
    {
        private static readonly TimeSpan ProgressInterval = TimeSpan.FromMinutes(30);
        private DateTimeOffset _nextProgressSync = DateTimeOffset.MinValue;

        protected override async Task RunOnceAsync(CancellationToken ct)
        {
            await RunAsync("AniList matching", (services, token) => services.GetRequiredService<MatchAniListV1>().ExecuteAsync<UpdateResult>(new MediaMatchParametersV1(), default, token), ct);

            // Covers still named by a provider, a pass of twenty at a time with a short pause.
            var busy = true;
            while (busy && !ct.IsCancellationRequested)
            {
                busy = false;
                await RunAsync("Provider covers", async (services, token) =>
                    busy = (await services.GetRequiredService<StoreCoversV1>().ExecuteAsync<UpdateResult>(new CoverParametersV1(), default, token)).Success, ct);
                if (busy)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), Time, ct);
                }
            }

            // Each pass reads the servers' whole film and episode lists.
            if (Time.GetUtcNow() >= _nextProgressSync)
            {
                await RunAsync("Media server progress", (services, token) => services.GetRequiredService<SyncMediaServersV1>().ExecuteAsync<UpdateResult>(new MediaServerSyncParametersV1(), default, token), ct);
                _nextProgressSync = Time.GetUtcNow() + ProgressInterval;
            }
        }
    }
}
