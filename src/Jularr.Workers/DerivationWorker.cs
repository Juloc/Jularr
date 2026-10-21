using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Data.System.Reading.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Reading.V1;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Workers
{
    // Reading copies of PDFs one after another in the background; a person opening a book comes first, so without work or with one waiting it looks again shortly.
    internal sealed class DerivationWorker(TimeProvider time, WorkerJobs jobs, WorkerJobRunner runner) : PeriodicWorker(time, jobs, runner, WorkerGroupType.Derivation)
    {
        private static readonly TimeSpan Pause = TimeSpan.FromSeconds(2);

        protected override async Task RunOnceAsync(CancellationToken ct) =>
            await Runner.RunWhileBusyAsync("readingCopies", async (services, token) =>
                (await services.GetRequiredService<DeriveReadingEbooksV1>().ExecuteAsync<UpdateResult>(new ReadingDerivationParametersV1(), default, token)).Success, Pause, Time, ct);
    }
}
