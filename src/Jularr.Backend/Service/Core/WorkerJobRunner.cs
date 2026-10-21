using System.Security;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Data.System.Worker.V1;
using Jularr.Backend.Service.System.Worker.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jularr.Backend.Service.Core
{
    // Runs one pass of a job for its worker: asks the registry whether the job may run, runs it in its own scope and reports how it went.
    // A switched-off module is no failure; anything else is told to the registry and, as a notice, to the Owner.
    public sealed class WorkerJobRunner(IServiceScopeFactory scopes, WorkerJobs jobs, ILogger<WorkerJobRunner> logger)
    {
        // Null when the job did not run: it is paused, waits while Jularr is used, is not due or its choices could not be read yet.
        public Task<WorkerJobResultType?> RunAsync(string key, Func<IServiceProvider, CancellationToken, Task> step, CancellationToken ct) =>
            RunPassAsync(key, async token =>
            {
                await using var scope = scopes.CreateAsyncScope();
                await step(scope.ServiceProvider, token);
                return true;
            }, ct);

        // A step that finds nothing to do (a server that is not connected) leaves the job off instead of succeeded.
        public Task<WorkerJobResultType?> RunIfConnectedAsync(string key, Func<IServiceProvider, CancellationToken, Task<bool>> step, CancellationToken ct) =>
            RunPassAsync(key, async token =>
            {
                await using var scope = scopes.CreateAsyncScope();
                return await step(scope.ServiceProvider, token);
            }, ct);

        // The pass repeats while its step reports more work, each round in a new scope, and stops between rounds when the job is paused or has to wait.
        public Task<WorkerJobResultType?> RunWhileBusyAsync(string key, Func<IServiceProvider, CancellationToken, Task<bool>> step, TimeSpan pause, TimeProvider time, CancellationToken ct) =>
            RunPassAsync(key, async token =>
            {
                var busy = true;
                while (busy && !token.IsCancellationRequested)
                {
                    await using (var scope = scopes.CreateAsyncScope())
                    {
                        busy = await step(scope.ServiceProvider, token);
                    }

                    if (!busy || jobs.IsHalted(key))
                    {
                        break;
                    }

                    if (pause > TimeSpan.Zero)
                    {
                        await Task.Delay(pause, time, token);
                    }
                }

                return true;
            }, ct);

        private async Task<WorkerJobResultType?> RunPassAsync(string key, Func<CancellationToken, Task<bool>> pass, CancellationToken ct)
        {
            if (!await EnsureLoadedAsync(ct) || jobs.Begin(key) != WorkerJobGate.Run)
            {
                return null;
            }

            var result = WorkerJobResultType.Succeeded;
            string? errorKey = null;
            try
            {
                if (!await pass(ct))
                {
                    result = WorkerJobResultType.Skipped;
                }
            }
            catch (SecurityException)
            {
                logger.LogDebug("{Job} skipped: its module is switched off.", key);
                result = WorkerJobResultType.Skipped;
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(exception, "{Job} failed.", key);
                result = WorkerJobResultType.Failed;
                errorKey = ServiceError.FromException(exception).Code;
            }

            await ReportAsync(key, jobs.Finish(key, result, errorKey), ct);
            return result;
        }

        // The Owner's choices are read before the first pass, so a paused job does not run once after a restart.
        private async Task<bool> EnsureLoadedAsync(CancellationToken ct)
        {
            if (jobs.IsLoaded)
            {
                return true;
            }

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<LoadWorkerJobsV1>().ExecuteAsync<UpdateResult>(new LoadWorkerJobsParametersV1(), default, ct);
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(exception, "The background jobs' settings could not be read.");
            }

            return jobs.IsLoaded;
        }

        private async Task ReportAsync(string key, RecordWorkerJobDataV1? record, CancellationToken ct)
        {
            if (record is null)
            {
                return;
            }

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<RecordWorkerJobV1>().ExecuteAsync<UpdateResult>(new WorkerJobParametersV1(key), record, ct);
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(exception, "The state of {Job} could not be kept.", key);
            }
        }
    }
}
