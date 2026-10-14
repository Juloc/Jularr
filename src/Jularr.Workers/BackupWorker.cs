using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Backup.V1;
using Jularr.Backend.Service.System.Backup.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jularr.Workers
{
    // Automatic backups join the operation queue.
    internal sealed class BackupWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<BackupWorker> logger) : PeriodicWorker(scopes, time, logger, TimeSpan.FromMinutes(30))
    {
        protected override Task RunOnceAsync(CancellationToken ct) =>
            RunAsync("Backup scheduling", (services, token) => services.GetRequiredService<ScheduleBackupV1>().ExecuteAsync<UpdateResult>(new BackupScheduleParametersV1(), default, token), ct);
    }
}
