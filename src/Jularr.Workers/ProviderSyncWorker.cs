using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Provider;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Data.System.Provider.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Provider.V1;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Workers
{
    // Plex, Jellyfin and Emby are read in one pass on the Owner's interval. Each provider has its own switch and result, and one that fails does not hold up the next.
    internal sealed class ProviderSyncWorker(TimeProvider time, WorkerJobs jobs, WorkerJobRunner runner) : PeriodicWorker(time, jobs, runner, WorkerGroupType.Provider)
    {
        private const string Job = "providerSync";

        protected override async Task RunOnceAsync(CancellationToken ct) =>
            await Runner.RunAsync(Job, async (_, token) =>
            {
                foreach (var provider in Enum.GetValues<ProviderSyncType>())
                {
                    // Between two providers the job may have been paused or have to wait for use.
                    if (Jobs.IsHalted(Job))
                    {
                        break;
                    }

                    await Runner.RunIfConnectedAsync(Job + provider, async (services, step) =>
                        (await services.GetRequiredService<SyncProviderV1>().ExecuteAsync<UpdateResult>(new ProviderSyncParametersV1(provider), default, step)).Success, token);
                }
            }, ct);
    }
}
