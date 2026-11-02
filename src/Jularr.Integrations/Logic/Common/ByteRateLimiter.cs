namespace Jularr.Integrations.Logic.Common
{
    // One rate shared by every session of a downloader: each block waits until its bytes fit the rate after the blocks before it; zero lets everything through.
    internal sealed class ByteRateLimiter
    {
        private readonly object _gate = new();
        private long _bytesPerSecond;
        private DateTimeOffset _free = DateTimeOffset.MinValue;

        public long BytesPerSecond
        {
            get => Interlocked.Read(ref _bytesPerSecond);
            set => Interlocked.Exchange(ref _bytesPerSecond, Math.Max(0, value));
        }

        public async ValueTask WaitAsync(long bytes, CancellationToken ct)
        {
            var delay = Reserve(bytes, DateTimeOffset.UtcNow);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }
        }

        // The time this block waits: the rate's clock moves on by the block, starting no earlier than now (an idle downloader saves nothing up).
        internal TimeSpan Reserve(long bytes, DateTimeOffset now)
        {
            var rate = BytesPerSecond;
            if (rate <= 0 || bytes <= 0)
            {
                return TimeSpan.Zero;
            }

            lock (_gate)
            {
                var start = _free > now ? _free : now;
                _free = start + TimeSpan.FromSeconds((double)bytes / rate);
                return start - now;
            }
        }
    }
}
