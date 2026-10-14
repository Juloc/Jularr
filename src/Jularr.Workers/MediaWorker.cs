using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Data.System.Provider.V1;
using Jularr.Backend.Service.System.Media.V1;
using Jularr.Backend.Service.System.Provider.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jularr.Workers
{
    // Secondary provider matching and progress shared with media servers run in slow passes beside the library.
    internal sealed class MediaWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<MediaWorker> logger) : PeriodicWorker(scopes, time, logger, TimeSpan.FromMinutes(10))
    {
        private static readonly TimeSpan ProgressInterval = TimeSpan.FromMinutes(30);
        private DateTimeOffset _nextProgressSync = DateTimeOffset.MinValue;

        protected override async Task RunOnceAsync(CancellationToken ct)
        {
            await RunAsync("AniList matching", (services, token) => services.GetRequiredService<MatchAniListV1>().ExecuteAsync<UpdateResult>(new MediaMatchParametersV1(), default, token), ct);

            // Each pass reads the servers' whole film and episode lists.
            if (Time.GetUtcNow() >= _nextProgressSync)
            {
                await RunAsync("Media server progress", (services, token) => services.GetRequiredService<SyncMediaServersV1>().ExecuteAsync<UpdateResult>(new MediaServerSyncParametersV1(), default, token), ct);
                _nextProgressSync = Time.GetUtcNow() + ProgressInterval;
            }
        }
    }
}
