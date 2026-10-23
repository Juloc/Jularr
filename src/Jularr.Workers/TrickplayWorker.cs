using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Media.V1;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Workers
{
    // Seek thumbnails file after file with a short pause; without work it looks again later.
    internal sealed class TrickplayWorker(TimeProvider time, WorkerJobs jobs, WorkerJobRunner runner) : PeriodicWorker(time, jobs, runner, WorkerGroupType.Trickplay)
    {
        private static readonly TimeSpan Pause = TimeSpan.FromSeconds(5);

        protected override async Task RunOnceAsync(CancellationToken ct) =>
            await Runner.RunWhileBusyAsync("trickplay", async (services, token) =>
                (await services.GetRequiredService<GenerateTrickplayV1>().ExecuteAsync<UpdateResult>(new TrickplayParametersV1(), default, token)).Success, Pause, Time, ct);
    }
}
