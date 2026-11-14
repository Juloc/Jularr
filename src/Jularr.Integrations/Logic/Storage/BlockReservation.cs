namespace Jularr.Integrations.Logic.Storage
{
    // Reserves storage for a stream of writes in large blocks, so the slow storage check runs rarely; a block's bytes are flushed before its reservation ends.
    internal sealed class BlockReservation(Func<long, IDisposable>? reserve, long? expectedBytes) : IDisposable
    {
        private const long KnownBlock = 256L * 1024 * 1024;
        private const long UnknownBlock = 64L * 1024 * 1024;
        private IDisposable? _current;
        private long _left;
        private long _written;

        public async ValueTask BeforeWriteAsync(int bytes, Func<CancellationToken, Task> flush, CancellationToken ct)
        {
            if (reserve is null || bytes <= _left)
            {
                _left -= bytes;
                _written += bytes;
                return;
            }

            await ReleaseAsync(flush, ct);
            var block = Math.Max(bytes, expectedBytes is { } expected ? Math.Min(KnownBlock, expected - _written) : UnknownBlock);
            (_current, var reserved) = Reserve(reserve, block, bytes);
            _left = reserved - bytes;
            _written += bytes;
        }

        // The written bytes reach the disk before the last block is given back.
        public async ValueTask ReleaseAsync(Func<CancellationToken, Task> flush, CancellationToken ct)
        {
            if (_current is null) return;
            await flush(ct);
            _current.Dispose();
            _current = null;
            _left = 0;
        }

        public void Dispose()
        {
            _current?.Dispose();
            _current = null;
        }

        // A block that does not fit any more falls back to just the write at hand; the check itself decides whether that still fits.
        public static (IDisposable Reservation, long Bytes) Reserve(Func<long, IDisposable> reserve, long block, long needed)
        {
            if (block <= needed) return (reserve(block), block);
            try
            {
                return (reserve(block), block);
            }
            catch
            {
                return (reserve(needed), needed);
            }
        }
    }
}
