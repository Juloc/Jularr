using System.Security;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Data.System.Worker.V1;
using Jularr.Backend.Service.System.Worker.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jularr.Backend.Service.Core
{
    // Runs one pass of a job for its worker: asks the registry whether the job may run, runs it in its own scope and reports how it went.
    // A switched-off module is no failure; anything else is told to the registry and, as a notice, to the Owner.
    // Every round runs under its job's time limit, so a round that hangs ends as a failure and frees its group.
    // From the host's stop no pass, round or claim begins; a round still running gets a short grace and is then cancelled, and the log names it.
    public sealed class WorkerJobRunner(IServiceScopeFactory scopes, WorkerJobs jobs, TimeProvider time, ILogger<WorkerJobRunner> logger, IHostApplicationLifetime? lifetime = null)
    {
        // A pass that keeps finding work is kept after this long and goes on at the next tick.
        internal static readonly TimeSpan PassLimit = TimeSpan.FromMinutes(10);

        // How long a stopped round may take to notice it before the log names it.
        internal static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(30);
        internal static readonly TimeSpan ReportLimit = TimeSpan.FromSeconds(10);
        internal static readonly TimeSpan ReportRetry = TimeSpan.FromSeconds(2);

        // How long a round may go on after the host began to stop (or the caller's token was cancelled) before it is cancelled.
        internal static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);

        // The host's stop as the workers see it: their waits end and they begin nothing new.
        public CancellationToken Stopping => lifetime?.ApplicationStopping ?? CancellationToken.None;

        // Null when the job did not run: it is paused, waits while Jularr is used, is not due or its choices could not be read yet.
        public Task<WorkerJobResultType?> RunAsync(string key, Func<IServiceProvider, CancellationToken, Task> step, CancellationToken ct) =>
            RunPassAsync(key, token => RunRoundAsync(key, async round =>
            {
                await using var scope = scopes.CreateAsyncScope();
                await step(scope.ServiceProvider, round);
                return true;
            }, token), ct);

        // A step that finds nothing to do (a server that is not connected) leaves the job off instead of succeeded.
        public Task<WorkerJobResultType?> RunIfConnectedAsync(string key, Func<IServiceProvider, CancellationToken, Task<bool>> step, CancellationToken ct) =>
            RunPassAsync(key, token => RunRoundAsync(key, async round =>
            {
                await using var scope = scopes.CreateAsyncScope();
                return await step(scope.ServiceProvider, round);
            }, token), ct);

        // The pass repeats while its step reports more work, each round in a new scope, and stops between rounds when the job is paused or has to wait.
        // After the pass limit it is kept and the job is due again at once, so its state stays current and the group's other jobs get their turn.
        public async Task<WorkerJobResultType?> RunWhileBusyAsync(string key, Func<IServiceProvider, CancellationToken, Task<bool>> step, TimeSpan pause, CancellationToken ct)
        {
            var isCut = false;
            var result = await RunPassAsync(key, async token =>
            {
                var startedAt = time.GetTimestamp();
                while (!IsStopping(ct))
                {
                    var busy = await RunRoundAsync(key, async round =>
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        return await step(scope.ServiceProvider, round);
                    }, token);
                    if (!busy || jobs.IsHalted(key))
                    {
                        break;
                    }

                    if (time.GetElapsedTime(startedAt) >= PassLimit)
                    {
                        isCut = true;
                        break;
                    }

                    if (pause > TimeSpan.Zero && !await PauseAsync(pause, ct))
                    {
                        break;
                    }
                }

                return true;
            }, ct);

            if (isCut && result == WorkerJobResultType.Succeeded)
            {
                jobs.RunAgainWithin(key, TimeSpan.Zero);
            }

            return result;
        }

        // One round under its job's limit. Past the limit the round is asked to stop and fails with job_round_timeout; one that ignores it
        // is named in the log after the grace and still awaited, never abandoned. The pass's shutdown token cancels it after the shutdown grace.
        private async Task<T> RunRoundAsync<T>(string key, Func<CancellationToken, Task<T>> round, CancellationToken ct)
        {
            var roundLimit = WorkerJobs.Definition(key).RoundLimit;
            using var limit = new CancellationTokenSource(roundLimit, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
            using var watch = new CancellationTokenSource();
            using var interrupted = ct.Register(() => logger.LogWarning("{Job} was cancelled: the stop's grace of {Grace} ran out while it was running.", key, ShutdownGrace));
            var running = round(linked.Token);
            var overdue = Task.Delay(roundLimit + StopGrace, time, watch.Token);
            if (await Task.WhenAny(running, overdue) == overdue)
            {
                logger.LogError("{Job} ignores its time limit of {Limit} and is still running.", key, roundLimit);
            }

            await watch.CancelAsync();
            try
            {
                var value = await running;
                if (!limit.IsCancellationRequested || ct.IsCancellationRequested)
                {
                    return value;
                }
            }
            catch (Exception exception) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new ServiceUnavailableException("job_round_timeout", exception);
            }

            // The round came back only after its limit: it overran even though it swallowed the stop.
            throw new ServiceUnavailableException("job_round_timeout");
        }

        private async Task<WorkerJobResultType?> RunPassAsync(string key, Func<CancellationToken, Task<bool>> pass, CancellationToken ct)
        {
            if (IsStopping(ct) || !await EnsureLoadedAsync(ct) || IsStopping(ct) || jobs.Begin(key) != WorkerJobGate.Run)
            {
                return null;
            }

            // The rounds and the record of the pass see the stop only after the grace.
            using var shutdown = new ShutdownGraceSource(Stopping, ct, time);
            var result = WorkerJobResultType.Succeeded;
            string? errorKey = null;
            try
            {
                if (!await pass(shutdown.Token))
                {
                    result = WorkerJobResultType.Skipped;
                }
            }
            catch (SecurityException)
            {
                logger.LogDebug("{Job} skipped: its module is switched off.", key);
                result = WorkerJobResultType.Skipped;
            }
            catch (Exception exception) when (IsStopping(ct))
            {
                // Cut by the stop: no failure and nothing kept; the job runs again after the start, and an operation it held is queued again.
                logger.LogDebug(exception, "{Job} ended with the stop.", key);
                return null;
            }
            catch (Exception exception)
            {
                errorKey = ServiceError.FromException(exception).Code;

                // A service that is not available is a known state, not a fault worth a stack trace.
                if (exception is ServiceUnavailableException)
                {
                    logger.LogWarning("{Job} failed: {Error}.", key, errorKey);
                }
                else
                {
                    logger.LogWarning(exception, "{Job} failed.", key);
                }

                result = WorkerJobResultType.Failed;
            }

            await ReportAsync(key, jobs.Finish(key, result, errorKey), shutdown.Token);
            return result;
        }

        private bool IsStopping(CancellationToken ct) => ct.IsCancellationRequested || Stopping.IsCancellationRequested;

        // The pause between rounds; false when the stop came first.
        private async Task<bool> PauseAsync(TimeSpan pause, CancellationToken ct)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, Stopping);
            try
            {
                await Task.Delay(pause, time, stop.Token);
                return true;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return false;
            }
        }

        // Cancelled the shutdown grace after the host began to stop or the caller's token was cancelled, whichever came first. All passes hear the
        // host's stop at the same moment, so the workers, stopped one after the other, still end within one grace.
        private sealed class ShutdownGraceSource : IDisposable
        {
            private readonly CancellationTokenSource _source;
            private readonly CancellationTokenRegistration _stopping;
            private readonly CancellationTokenRegistration _caller;
            private int _isStarted;

            public ShutdownGraceSource(CancellationToken stopping, CancellationToken caller, TimeProvider time)
            {
                _source = new CancellationTokenSource(Timeout.InfiniteTimeSpan, time);
                _stopping = stopping.Register(Start);
                _caller = caller.Register(Start);
            }

            public CancellationToken Token => _source.Token;

            private void Start()
            {
                if (Interlocked.Exchange(ref _isStarted, 1) == 0)
                {
                    _source.CancelAfter(ShutdownGrace);
                }
            }

            public void Dispose()
            {
                _stopping.Dispose();
                _caller.Dispose();
                _source.Dispose();
            }
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

        // The record has a limit of its own and a second try, so a busy database neither holds the worker nor loses the state;
        // a record that still fails is written again by the next pass.
        private async Task ReportAsync(string key, RecordWorkerJobDataV1? record, CancellationToken ct)
        {
            if (record is null)
            {
                return;
            }

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var limit = new CancellationTokenSource(ReportLimit, time);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<RecordWorkerJobV1>().ExecuteAsync<UpdateResult>(new WorkerJobParametersV1(key), record, linked.Token);
                    return;
                }
                catch (Exception exception) when (!ct.IsCancellationRequested && attempt == 1)
                {
                    logger.LogDebug(exception, "The state of {Job} could not be kept; trying again.", key);
                }
                catch (Exception exception) when (!ct.IsCancellationRequested)
                {
                    jobs.ForgetKept(key);
                    logger.LogWarning(exception, "The state of {Job} could not be kept.", key);
                    return;
                }

                await Task.Delay(ReportRetry, time, ct);
            }
        }
    }
}
