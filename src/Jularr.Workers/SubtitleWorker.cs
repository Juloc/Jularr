using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Media.V1;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Workers
{
    // Missing subtitles file after file with a pause the providers' limits allow; without work it looks again later.
    internal sealed class SubtitleWorker(TimeProvider time, WorkerJobs jobs, WorkerJobRunner runner) : PeriodicWorker(time, jobs, runner, WorkerGroupType.Subtitle)
    {
        private static readonly TimeSpan Pause = TimeSpan.FromSeconds(10);

        protected override async Task RunOnceAsync(CancellationToken ct) =>
            await Runner.RunWhileBusyAsync("subtitles", async (services, token) =>
                (await services.GetRequiredService<FetchSubtitlesV1>().ExecuteAsync<UpdateResult>(new SubtitleParametersV1(), default, token)).Success, Pause, Time, ct);
    }
}
