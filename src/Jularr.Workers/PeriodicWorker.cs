using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Service.Core;
using Microsoft.Extensions.Hosting;

namespace Jularr.Workers
{
    // Looks at its jobs on every tick; the registry decides which are due, paused or waiting.
    // The first look comes after the group's start delay, and a run-now request wakes the worker early.
    internal abstract class PeriodicWorker : BackgroundService
    {
        private readonly TimeSpan _interval;
        private readonly TimeSpan _startDelay;

        protected PeriodicWorker(TimeProvider time, WorkerJobs jobs, WorkerJobRunner runner, WorkerGroupType group)
        {
            Time = time;
            Jobs = jobs;
            Runner = runner;
            Group = group;
            var definitions = WorkerJobs.Of(group);
            _interval = definitions.Min(definition => definition.Interval) ?? throw new InvalidOperationException("A periodic worker needs a job with an interval.");
            _startDelay = definitions.Min(definition => definition.StartDelay);
        }

        protected TimeProvider Time { get; }
        protected WorkerJobs Jobs { get; }
        protected WorkerJobRunner Runner { get; }
        protected WorkerGroupType Group { get; }

        protected abstract Task RunOnceAsync(CancellationToken ct);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(_interval, Time);
            var isRunning = _startDelay <= TimeSpan.Zero || await WaitAsync(async token =>
            {
                await Task.Delay(_startDelay, Time, token);
                return true;
            }, stoppingToken);
            while (isRunning)
            {
                await RunOnceAsync(stoppingToken);
                isRunning = await WaitAsync(token => timer.WaitForNextTickAsync(token).AsTask(), stoppingToken);
            }
        }

        // More reasons to look again before the next tick, such as a signal other services raise.
        protected virtual IEnumerable<Task<bool>> Wakeups(CancellationToken ct) => [];

        private async Task<bool> WaitAsync(Func<CancellationToken, Task<bool>> tick, CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var waits = new List<Task<bool>> { tick(linked.Token), Jobs.WaitForRunRequestAsync(Group, linked.Token).AsTask() };
            waits.AddRange(Wakeups(linked.Token));
            var completed = await Task.WhenAny(waits);
            await linked.CancelAsync();
            try
            {
                return await completed;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            finally
            {
                try
                {
                    await Task.WhenAll(waits);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }
}
