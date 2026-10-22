using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Data.System.Provider.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Media.V1;
using Jularr.Backend.Service.System.Provider.V1;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Workers
{
    // Secondary provider matching, the provider images of titles and their conversion to the chosen format run in slow passes beside the library.
    internal sealed class MediaWorker(TimeProvider time, WorkerJobs jobs, WorkerJobRunner runner) : PeriodicWorker(time, jobs, runner, WorkerGroupType.Media)
    {
        protected override async Task RunOnceAsync(CancellationToken ct)
        {
            await Runner.RunIfConnectedAsync("aniListSync", async (services, token) =>
                (await services.GetRequiredService<SyncAniListV1>().ExecuteAsync<UpdateResult>(new AniListSyncParametersV1(), default, token)).Success, ct);

            await Runner.RunAsync("aniListMatching", (services, token) =>
                services.GetRequiredService<MatchAniListV1>().ExecuteAsync<UpdateResult>(new MediaMatchParametersV1(), default, token), ct);

            // The relations AniList names for matched Works, while the AniList module is on.
            await Runner.RunAsync("aniListRelations", (services, token) =>
                services.GetRequiredService<SyncAniListRelationsV1>().ExecuteAsync<UpdateResult>(new MediaMatchParametersV1(), default, token), ct);

            // Covers still named by a provider, a pass of twenty at a time with a short pause.
            await Runner.RunWhileBusyAsync("covers", async (services, token) =>
                (await services.GetRequiredService<StoreCoversV1>().ExecuteAsync<UpdateResult>(new CoverParametersV1(), default, token)).Success, TimeSpan.FromSeconds(2), Time, ct);

            // Stored covers in another format than the Owner's, a few images per pass and a pause between passes.
            await Runner.RunWhileBusyAsync("coverBackfill", async (services, token) =>
                (await services.GetRequiredService<ConvertCoversV1>().ExecuteAsync<UpdateResult>(new CoverParametersV1(), default, token)).Success, TimeSpan.FromSeconds(3), Time, ct);
        }
    }
}
