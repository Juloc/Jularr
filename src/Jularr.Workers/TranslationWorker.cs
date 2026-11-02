using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Data.System.Reading.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Reading.V1;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Workers
{
    // Translates the library ahead of reading, a few blocks per round, in its own group: the long pass never holds up the
    // reading copies. A reader using the model comes first; then the round ends and the worker looks again on its next tick.
    internal sealed class TranslationWorker(TimeProvider time, WorkerJobs jobs, WorkerJobRunner runner) : PeriodicWorker(time, jobs, runner, WorkerGroupType.Translation)
    {
        protected override async Task RunOnceAsync(CancellationToken ct) =>
            await Runner.RunWhileBusyAsync("bookTranslations", async (services, token) =>
                (await services.GetRequiredService<TranslateReadingBooksV1>().ExecuteAsync<UpdateResult>(new BookTranslationParametersV1(), default, token)).Success, TimeSpan.Zero, Time, ct);
    }
}
