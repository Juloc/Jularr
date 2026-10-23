using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Data.System.Backup.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Backup.V1;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Workers
{
    // Automatic backups join the operation queue; run now queues one whatever the interval says.
    internal sealed class BackupWorker(TimeProvider time, WorkerJobs jobs, WorkerJobRunner runner) : PeriodicWorker(time, jobs, runner, WorkerGroupType.Backup)
    {
        protected override async Task RunOnceAsync(CancellationToken ct) =>
            await Runner.RunAsync("backup", (services, token) =>
                services.GetRequiredService<ScheduleBackupV1>().ExecuteAsync<UpdateResult>(new BackupScheduleParametersV1(Jobs.IsRequestedRun("backup")), default, token), ct);
    }
}
