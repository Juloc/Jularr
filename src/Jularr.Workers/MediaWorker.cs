using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Service.System.Media.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jularr.Workers
{
    // Secondary provider matching runs in slow passes beside the library.
    internal sealed class MediaWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<MediaWorker> logger) : PeriodicWorker(scopes, time, logger, TimeSpan.FromMinutes(10))
    {
        protected override Task RunOnceAsync(CancellationToken ct) =>
            RunAsync("AniList matching", (services, token) => services.GetRequiredService<MatchAniListV1>().ExecuteAsync<UpdateResult>(new MediaMatchParametersV1(), default, token), ct);
    }
}
