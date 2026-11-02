using System.Threading.Channels;

namespace Jularr.Backend.Service.Core
{
    // Wakes a worker that sleeps between two looks when work has been put down for it. A missed wake costs at most the worker's slow safety tick.
    public sealed class WorkSignal
    {
        private readonly Channel<bool> _pending = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });

        public void Notify() => _pending.Writer.TryWrite(true);

        public bool TryTake() => _pending.Reader.TryRead(out _);

        // Completes when work has been announced, without taking the announcement.
        public ValueTask<bool> WaitToReadAsync(CancellationToken ct) => _pending.Reader.WaitToReadAsync(ct);

        // Sleeps until work is announced or the safety tick has passed, and takes the announcement.
        public async Task WaitAsync(TimeSpan safetyTick, TimeProvider time, CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var announced = _pending.Reader.WaitToReadAsync(linked.Token).AsTask();
            var ticked = Task.Delay(safetyTick, time, linked.Token);
            await Task.WhenAny(announced, ticked);
            await linked.CancelAsync();
            try
            {
                await Task.WhenAll(announced, ticked);
            }
            catch (OperationCanceledException)
            {
            }

            ct.ThrowIfCancellationRequested();
            _pending.Reader.TryRead(out _);
        }
    }

    // The queues the workers wait on: scans, backups, benchmarks, library searches, newly grabbed downloads and Jularr's own torrents.
    public sealed class WorkSignals
    {
        public WorkSignal Imports { get; } = new();
        public WorkSignal Scans { get; } = new();
        public WorkSignal Backups { get; } = new();
        public WorkSignal Benchmarks { get; } = new();
        public WorkSignal Searches { get; } = new();
        public WorkSignal Downloads { get; } = new();
        public WorkSignal Torrents { get; } = new();
    }
}
