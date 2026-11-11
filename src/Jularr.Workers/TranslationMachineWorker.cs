using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Data.System.Translation.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Translation.V1;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Workers
{
    // Checks the translation machines every thirty seconds in a worker of its own, so a long translation round never delays it; a new order of machines asks at once.
    internal sealed class TranslationMachineWorker(TimeProvider time, WorkerJobs jobs, WorkerJobRunner runner) : PeriodicWorker(time, jobs, runner, WorkerGroupType.TranslationMachines, Tick)
    {
        // A machine that is back translates again after thirty seconds at most.
        internal static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

        protected override async Task RunOnceAsync(CancellationToken ct) =>
            await Runner.RunAsync("translationMachines", (services, token) =>
                services.GetRequiredService<RefreshTranslationMachinesV1>().ExecuteAsync<UpdateResult>(new TranslationMachinesParametersV1(), default, token), ct);
    }
}
