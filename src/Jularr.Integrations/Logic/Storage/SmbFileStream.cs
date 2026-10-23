using SMBLibrary;

namespace Jularr.Integrations.Logic.Storage
{
    // A file on an SMB share on its own session. Reads come in pieces of up to a megabyte, and while one is used the next is already on its way,
    // so a player or FFmpeg reading forward never waits for a round trip; seeking simply starts a new piece. Writes append.
    internal sealed class SmbFileStream : Stream
    {
        private const int PieceBytes = 1024 * 1024;

        private readonly SmbLane _lane;
        private readonly object _handle;
        private readonly bool _writable;
        private readonly int _generation;
        private long _length;
        private long _position;
        private bool _closed;
        private long _pieceStart;
        private byte[] _piece = [];
        private (long Start, Task<byte[]> Data)? _next;

        // The lane is leased for this file; closing the file returns it.
        public SmbFileStream(SmbLane lane, object handle, long length, bool writable)
        {
            _lane = lane;
            _handle = handle;
            _length = length;
            _writable = writable;
            _generation = lane.Generation;
        }

        public override bool CanRead => !_writable && !_closed;
        public override bool CanSeek => !_closed;
        public override bool CanWrite => _writable && !_closed;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_writable) throw new NotSupportedException();
            if (buffer.Length == 0 || _position >= _length) return 0;

            if (_position < _pieceStart || _position >= _pieceStart + _piece.Length)
            {
                // Only a piece that arrived replaces the current one, so a failed request never leaves old bytes at the new position.
                var next = _next;
                _next = null;
                var piece = next is { } ahead && ahead.Start == _position ? ahead.Data.GetAwaiter().GetResult() : Fetch(_position);
                (_pieceStart, _piece) = (_position, piece);
                if (_piece.Length == 0) return 0;
            }

            // The next piece follows while this one is read.
            var end = _pieceStart + _piece.Length;
            if (_next is null && end < _length)
            {
                var fetch = Task.Run(() => Fetch(end));

                // A piece passed over by a seek may fail unseen.
                _ = fetch.ContinueWith(task => task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                _next = (end, fetch);
            }

            var count = (int)Math.Min(buffer.Length, end - _position);
            _piece.AsSpan((int)(_position - _pieceStart), count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => new(Task.Run(() => Read(buffer.Span), ct));

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (!_writable) throw new NotSupportedException();
            while (buffer.Length > 0)
            {
                var position = _position;
                var chunk = buffer[..Math.Min(buffer.Length, PieceBytes)].ToArray();
                var written = _lane.Run(store =>
                {
                    var part = chunk.Length <= store.MaxWriteSize ? chunk : chunk[..(int)store.MaxWriteSize];
                    SmbShare.Check(store.WriteFile(out var count, _handle, position, part));
                    return count;
                }, _generation);
                if (written <= 0) throw new IOException("The SMB server wrote nothing.");
                buffer = buffer[written..];
                _position += written;
                _length = Math.Max(_length, _position);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => new(Task.Run(() => Write(buffer.Span), ct));

        public override long Seek(long offset, SeekOrigin origin)
        {
            var position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => _length + offset
            };
            ArgumentOutOfRangeException.ThrowIfNegative(position);
            return _position = position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_closed)
            {
                _closed = true;
                try
                {
                    // A piece still on its way finishes before the handle closes.
                    _next?.Data.Wait();
                }
                catch (AggregateException)
                {
                }

                try
                {
                    _lane.Run(store => store.CloseFile(_handle), _generation);
                }
                catch (Exception exception) when (exception is IOException or Data.Nas.NasMountException or UnauthorizedAccessException)
                {
                    // A renewed or lost session closed the handle already.
                }
                finally
                {
                    _lane.Closed();
                }
            }

            base.Dispose(disposing);
        }

        private byte[] Fetch(long position) => _lane.Run(store =>
        {
            var status = store.ReadFile(out var bytes, _handle, position, (int)Math.Min(Math.Min(PieceBytes, store.MaxReadSize), _length - position));
            if (status == NTStatus.STATUS_END_OF_FILE) return [];
            SmbShare.Check(status);
            return bytes;
        }, _generation);
    }
}
