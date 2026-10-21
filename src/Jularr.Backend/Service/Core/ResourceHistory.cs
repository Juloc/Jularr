using System.Diagnostics;
using Jularr.Backend.Data.System.Instance.V1;
using Jularr.Backend.Service.System.Instance.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jularr.Backend.Service.Core
{
    // Processor and memory use of this process once a minute for the last six hours; a restart begins empty.
    // Each minute it also follows the Diagnostics module for all in-memory diagnostics.
    internal sealed class ResourceHistory(TimeProvider time, WorkerJobRunner runner, RecentLogs logs, ServiceTimings timings) : BackgroundService
    {
        private const int Capacity = 360;
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
        private readonly Queue<ResourceSample> _samples = new();
        private TimeSpan _processorTime;
        private long _sampledAt;

        // Oldest first.
        public IReadOnlyList<ResourceSample> Read()
        {
            lock (_samples)
            {
                return [.. _samples];
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var process = Process.GetCurrentProcess();
            _processorTime = process.TotalProcessorTime;
            _sampledAt = Stopwatch.GetTimestamp();
            using var timer = new PeriodicTimer(Interval, time);
            try
            {
                do
                {
                    await runner.RunAsync("resources", async (services, token) =>
                    {
                        await FollowModuleAsync(services, token);
                        process.Refresh();
                        Sample(process);
                    }, stoppingToken);
                }
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task FollowModuleAsync(IServiceProvider services, CancellationToken ct)
        {
            var enabled = (await services.GetRequiredService<ReadDiagnosticsStateV1>().ExecuteAsync<DiagnosticsStateV1>(new DiagnosticsStateParametersV1(), default, ct)).IsEnabled;
            logs.Enable(enabled);
            timings.Enable(enabled);
            if (!enabled)
            {
                lock (_samples)
                {
                    _samples.Clear();
                }
            }
        }

        // The share of all processors used since the last sample.
        private void Sample(Process process)
        {
            if (!timings.IsEnabled)
            {
                return;
            }

            var processorTime = process.TotalProcessorTime;
            var elapsed = Stopwatch.GetElapsedTime(_sampledAt);
            var share = elapsed > TimeSpan.Zero ? (processorTime - _processorTime) / (elapsed * Environment.ProcessorCount) : 0;
            _processorTime = processorTime;
            _sampledAt = Stopwatch.GetTimestamp();
            var sample = new ResourceSample(time.GetUtcNow().UtcDateTime, Math.Clamp(Math.Round((decimal)share * 100, 1), 0, 100), process.WorkingSet64);
            lock (_samples)
            {
                _samples.Enqueue(sample);
                while (_samples.Count > Capacity) _samples.Dequeue();
            }
        }
    }

    internal sealed record ResourceSample(DateTime At, decimal ProcessorPercent, long MemoryBytes);
}
