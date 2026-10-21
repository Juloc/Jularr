using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Media.V1;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Workers
{
    // Secondary provider matching and the provider images of titles run in slow passes beside the library.
    internal sealed class MediaWorker(TimeProvider time, WorkerJobs jobs, WorkerJobRunner runner) : PeriodicWorker(time, jobs, runner, WorkerGroupType.Media)
    {
        protected override async Task RunOnceAsync(CancellationToken ct)
        {
            await Runner.RunAsync("aniListMatching", (services, token) =>
                services.GetRequiredService<MatchAniListV1>().ExecuteAsync<UpdateResult>(new MediaMatchParametersV1(), default, token), ct);

            // Covers still named by a provider, a pass of twenty at a time with a short pause.
            await Runner.RunWhileBusyAsync("covers", async (services, token) =>
                (await services.GetRequiredService<StoreCoversV1>().ExecuteAsync<UpdateResult>(new CoverParametersV1(), default, token)).Success, TimeSpan.FromSeconds(2), Time, ct);
        }
    }
}
