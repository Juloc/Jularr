using System.Diagnostics;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Instance.V1;
using Jularr.Backend.Service.System.Instance.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jularr.Backend.Service.Core
{
    // Processor and memory use of this process once a minute for the last 24 hours. Every few minutes the new samples and the service timings go to the database in one batch,
    // and a restart reads them back. Each minute it also follows the Diagnostics module for all in-memory diagnostics.
    internal sealed class ResourceHistory(TimeProvider time, WorkerJobRunner runner, RecentLogs logs, ServiceTimings timings) : BackgroundService
    {
        private const int Capacity = 1440;
        private const int PassesPerSave = 5;
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
        private readonly Queue<ResourceSample> _samples = new();
        private TimeSpan _processorTime;
        private long _sampledAt;
        private DateTime _savedUntil = DateTime.MinValue;
        private bool _isLoaded;
        private int _passes;

        // Oldest first.
        public IReadOnlyList<ResourceSample> Read()
        {
            lock (_samples)
            {
                return [.. _samples];
            }
        }

        // The samples the database does not have yet, oldest first.
        public IReadOnlyList<ResourceSample> ReadUnsaved()
        {
            lock (_samples)
            {
                return [.. _samples.Where(sample => sample.At > _savedUntil)];
            }
        }

        public void MarkSaved(DateTime until)
        {
            lock (_samples)
            {
                if (until > _savedUntil) _savedUntil = until;
            }
        }

        // What an earlier run kept; samples taken since the start stay.
        public void Load(IEnumerable<ResourceSample> kept)
        {
            lock (_samples)
            {
                var since = _samples.Count > 0 ? _samples.Peek().At : DateTime.MaxValue;
                var merged = kept.Where(sample => sample.At < since).Concat(_samples).OrderBy(sample => sample.At).TakeLast(Capacity).ToList();
                _samples.Clear();
                foreach (var sample in merged) _samples.Enqueue(sample);
                if (kept.Any()) _savedUntil = kept.Max(sample => sample.At);
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
                        await KeepAsync(services, token);
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
                _isLoaded = false;
                lock (_samples)
                {
                    _samples.Clear();
                    _savedUntil = DateTime.MinValue;
                }
            }
        }

        // The first pass reads the last 24 hours back; then the new samples and changed slices are written every few passes.
        private async Task KeepAsync(IServiceProvider services, CancellationToken ct)
        {
            if (!timings.IsEnabled)
            {
                return;
            }

            if (!_isLoaded)
            {
                await services.GetRequiredService<LoadPerformanceHistoryV1>().ExecuteAsync<UpdateResult>(new PerformanceHistoryParametersV1(), default, ct);
                _isLoaded = true;
                return;
            }

            if (++_passes % PassesPerSave == 0)
            {
                await services.GetRequiredService<SavePerformanceHistoryV1>().ExecuteAsync<UpdateResult>(new PerformanceHistoryParametersV1(), default, ct);
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
            Add(sample);
        }

        internal void Add(ResourceSample sample)
        {
            lock (_samples)
            {
                _samples.Enqueue(sample);
                while (_samples.Count > Capacity) _samples.Dequeue();
            }
        }
    }

    internal sealed record ResourceSample(DateTime At, decimal ProcessorPercent, long MemoryBytes);
}
