using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Data.System.Operation.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Backup.V1;
using Jularr.Backend.Service.System.Operation.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jularr.Workers
{
    // Queued scans and backups, each in its own lane: scans one after another, one backup at a time, a backup also while a long scan runs.
    // A lane sleeps until work is queued for it and looks again on a slow safety tick; a restart resumes what it interrupted.
    internal sealed class OperationWorker(TimeProvider time, IServiceScopeFactory scopes, WorkerJobRunner runner, WorkSignals signals, ILogger<OperationWorker> logger) : BackgroundService
    {
        private static readonly TimeSpan SafetyTick = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await RecoverAsync(stoppingToken);
                await Task.WhenAll(RunLaneAsync("operations", OperationType.LibraryScan, signals.Scans, stoppingToken),
                    RunLaneAsync("backupQueue", OperationType.Backup, signals.Backups, stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }

        // Work a restart interrupted is queued again before either lane starts.
        private async Task RecoverAsync(CancellationToken ct)
        {
            while (true)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<RequeueOperationsV1>().ExecuteAsync<UpdateResult>(new OperationQueueParametersV1(), default, ct);
                    return;
                }
                catch (Exception exception) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "Interrupted operations could not be queued again.");
                    await Task.Delay(RetryInterval, time, ct);
                }
            }
        }

        private async Task RunLaneAsync(string job, OperationType operationType, WorkSignal signal, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await runner.RunWhileBusyAsync(job, (services, token) => RunNextAsync(services, logger, operationType, token), TimeSpan.Zero, time, ct);
                await signal.WaitAsync(result == WorkerJobResultType.Failed ? RetryInterval : SafetyTick, time, ct);
            }
        }

        // Claims the oldest queued operation of the lane's kind and runs it; false when none waits.
        internal static async Task<bool> RunNextAsync(IServiceProvider services, ILogger logger, OperationType operationType, CancellationToken ct)
        {
            var claimed = await services.GetRequiredService<ClaimOperationV1>().ExecuteAsync<ClaimOperationResultV1>(new ClaimOperationParametersV1(operationType), default, ct);
            if (claimed.OperationId is not { } operationId)
            {
                return false;
            }

            try
            {
                if (claimed.OperationTypeId == OperationType.Backup)
                {
                    await services.GetRequiredService<ExecuteBackupV1>().ExecuteAsync<UpdateResult>(new OperationParametersV1(operationId), default, ct);
                }
                else
                {
                    await services.GetRequiredService<ExecuteLibraryScanV1>().ExecuteAsync<UpdateResult>(new OperationParametersV1(operationId), default, ct);
                }
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                // Record a sanitized failure.
                logger.LogWarning(exception, "Operation {OperationId} failed.", operationId);
                await services.GetRequiredService<FailOperationV1>().ExecuteAsync<UpdateResult>(new OperationParametersV1(operationId),
                    new FailOperationDataV1(ServiceError.FromException(exception).Code), ct);
            }

            return true;
        }
    }
}
