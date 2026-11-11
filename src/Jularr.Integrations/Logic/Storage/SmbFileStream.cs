using SMBLibrary;

namespace Jularr.Integrations.Logic.Storage
{
    // A file on an SMB share on its own session. Reads come in pieces of up to a megabyte, and while one is used the next is already on its way,
    // so a player or FFmpeg reading forward never waits for a round trip; seeking simply starts a new piece. Writes append. Every request runs on
    // the session's own thread with its deadline: an asynchronous read holds no thread while it waits, and closing never waits for a NAS that
    // does not answer.
    internal sealed class SmbFileStream : Stream
    {
        private const int PieceBytes = 1024 * 1024;

        // How long closing waits for the NAS to confirm; one that does not answer gets the close later, or loses the handle with its session.
        private static readonly TimeSpan CloseWait = TimeSpan.FromMilliseconds(50);

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

        // A synchronous reader waits for the piece on its own thread.
        public override int Read(Span<byte> buffer)
        {
            if (!Readable(buffer.Length)) return 0;
            if (!Holds) Use(Next().GetAwaiter().GetResult());
            return Copy(buffer);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!Readable(buffer.Length)) return 0;
            if (!Holds) Use(await Next().WaitAsync(ct));
            return Copy(buffer.Span);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Writable();
            while (buffer.Length > 0)
            {
                var chunk = buffer[..Math.Min(buffer.Length, PieceBytes)].ToArray();
                buffer = buffer[Advance(Send(chunk, CancellationToken.None).GetAwaiter().GetResult())..];
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Writable();
            while (buffer.Length > 0)
            {
                var chunk = buffer[..Math.Min(buffer.Length, PieceBytes)].ToArray();
                buffer = buffer[Advance(await Send(chunk, ct))..];
            }
        }

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
            // On the lane's own thread (a file its caller no longer wanted) nothing waits for the lane.
            if (Release() is { } closing && !_lane.IsCurrent)
            {
                try
                {
                    closing.Wait(CloseWait);
                }
                catch (AggregateException)
                {
                }
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Release() is { } closing)
            {
                try
                {
                    await closing.WaitAsync(CloseWait);
                }
                catch (Exception)
                {
                    // Not confirmed in time, or the session that held the handle is gone.
                }
            }

            await base.DisposeAsync();
        }

        private bool Readable(int count)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_writable) throw new NotSupportedException();
            return count > 0 && _position < _length;
        }

        private void Writable()
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (!_writable) throw new NotSupportedException();
        }

        private bool Holds => _position >= _pieceStart && _position < _pieceStart + _piece.Length;

        // The piece at the position: the one fetched ahead when it starts there, else a new request.
        private Task<byte[]> Next()
        {
            var next = _next;
            _next = null;
            return next is { } ahead && ahead.Start == _position ? ahead.Data : Fetch(_position);
        }

        // Only a piece that arrived replaces the current one, so a failed request never leaves old bytes at the new position.
        private void Use(byte[] piece) => (_pieceStart, _piece) = (_position, piece);

        private int Copy(Span<byte> buffer)
        {
            if (!Holds) return 0;

            // The next piece follows while this one is read; one passed over by a seek may fail unseen.
            var end = _pieceStart + _piece.Length;
            if (_next is null && end < _length)
            {
                var fetch = Fetch(end);
                _ = fetch.ContinueWith(static task => task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                _next = (end, fetch);
            }

            var count = (int)Math.Min(buffer.Length, end - _position);
            _piece.AsSpan((int)(_position - _pieceStart), count).CopyTo(buffer);
            _position += count;
            return count;
        }

        private Task<byte[]> Fetch(long position) => _lane.RunAsync(store =>
        {
            var status = store.ReadFile(out var bytes, _handle, position, (int)Math.Min(Math.Min(PieceBytes, store.MaxReadSize), _length - position));
            if (status == NTStatus.STATUS_END_OF_FILE) return [];
            SmbShare.Check(status);
            return bytes;
        }, SmbLane.PieceTime, CancellationToken.None, _generation);

        // One chunk at the position, as much of it as the server takes at once.
        private Task<int> Send(byte[] chunk, CancellationToken ct)
        {
            var position = _position;
            return _lane.RunAsync(store =>
            {
                var part = chunk.Length <= store.MaxWriteSize ? chunk : chunk[..(int)store.MaxWriteSize];
                SmbShare.Check(store.WriteFile(out var count, _handle, position, part));
                return count;
            }, SmbLane.PieceTime, ct, _generation);
        }

        private int Advance(int written)
        {
            if (written <= 0) throw new IOException("The SMB server wrote nothing.");
            _position += written;
            _length = Math.Max(_length, _position);
            return written;
        }

        // Hands the handle back on the file's session after any piece still on its way, and returns the lane; null once closed.
        private Task? Release()
        {
            if (_closed)
            {
                return null;
            }

            _closed = true;
            var closing = _lane.RunAsync(store => store.CloseFile(_handle), SmbLane.RequestTime, CancellationToken.None, _generation);

            // A renewed or lost session closed the handle already.
            _ = closing.ContinueWith(static task => task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _lane.Closed();
            return closing;
        }
    }
}
