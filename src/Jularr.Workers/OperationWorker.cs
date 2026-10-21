using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Data.System.Operation.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Backup.V1;
using Jularr.Backend.Service.System.Operation.V1;
using Jularr.Backend.Service.System.Transcoding.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jularr.Workers
{
    // Queued scans and backups one after another; a restart resumes what it interrupted.
    internal sealed class OperationWorker(TimeProvider time, WorkerJobRunner runner, ILogger<OperationWorker> logger) : BackgroundService
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var recovered = false;
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var result = await runner.RunWhileBusyAsync("operations", async (services, token) =>
                    {
                        // Resume work interrupted by a restart.
                        if (!recovered)
                        {
                            await services.GetRequiredService<RequeueOperationsV1>().ExecuteAsync<UpdateResult>(new OperationQueueParametersV1(), default, token);
                            recovered = true;
                        }

                        return await RunNextAsync(services, logger, token);
                    }, TimeSpan.Zero, time, stoppingToken);
                    await Task.Delay(result == WorkerJobResultType.Failed ? RetryInterval : PollInterval, time, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }

        internal static async Task<bool> RunNextAsync(IServiceProvider services, ILogger logger, CancellationToken ct)
        {
            var claimed = await services.GetRequiredService<ClaimOperationV1>().ExecuteAsync<ClaimOperationResultV1>(new OperationQueueParametersV1(), default, ct);
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
                else if (claimed.OperationTypeId == OperationType.TranscodeBenchmark)
                {
                    await services.GetRequiredService<ExecuteTranscodeBenchmarkV1>().ExecuteAsync<UpdateResult>(new OperationParametersV1(operationId), default, ct);
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
