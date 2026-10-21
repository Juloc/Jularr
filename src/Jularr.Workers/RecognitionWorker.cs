using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Data.System.Reading.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Reading.V1;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Workers
{
    // Scanned books one after another; without work it looks again shortly, since a reader is waiting.
    internal sealed class RecognitionWorker(TimeProvider time, WorkerJobs jobs, WorkerJobRunner runner) : PeriodicWorker(time, jobs, runner, WorkerGroupType.Recognition)
    {
        protected override async Task RunOnceAsync(CancellationToken ct) =>
            await Runner.RunWhileBusyAsync("recognition", async (services, token) =>
                (await services.GetRequiredService<RecognizeReadingV1>().ExecuteAsync<UpdateResult>(new ReadingRecognitionParametersV1(), default, token)).Success, TimeSpan.Zero, Time, ct);
    }
}
