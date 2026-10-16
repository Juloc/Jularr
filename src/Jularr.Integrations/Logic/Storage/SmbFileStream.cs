using SMBLibrary;

namespace Jularr.Integrations.Logic.Storage
{
    // A file on an SMB share: reads seek freely, writes append; every request takes its turn on the share's session.
    internal sealed class SmbFileStream : Stream
    {
        private readonly SmbShare _share;
        private readonly object _handle;
        private readonly bool _writable;
        private readonly int _generation;
        private long _length;
        private long _position;
        private bool _closed;

        public SmbFileStream(SmbShare share, object handle, long length, bool writable)
        {
            _share = share;
            _handle = handle;
            _length = length;
            _writable = writable;
            _generation = share.Generation;
            share.Opened();
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

            var position = _position;
            var wanted = buffer.Length;
            var data = _share.Run(store =>
            {
                var status = store.ReadFile(out var bytes, _handle, position, (int)Math.Min(wanted, Math.Min(store.MaxReadSize, _length - position)));
                if (status == NTStatus.STATUS_END_OF_FILE) return [];
                SmbShare.Check(status);
                return bytes;
            }, _generation);
            data.CopyTo(buffer);
            _position += data.Length;
            return data.Length;
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
                var chunk = buffer[..Math.Min(buffer.Length, 1024 * 1024)].ToArray();
                var written = _share.Run(store =>
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
                    _share.Run(store => store.CloseFile(_handle), _generation);
                }
                catch (Exception exception) when (exception is IOException or Data.Nas.NasMountException or UnauthorizedAccessException)
                {
                    // A renewed or lost session closed the handle already.
                }
                finally
                {
                    _share.Closed();
                }
            }

            base.Dispose(disposing);
        }
    }
}
