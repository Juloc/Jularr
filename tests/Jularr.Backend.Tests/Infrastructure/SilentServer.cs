using System.Net;
using System.Text;

namespace Jularr.Backend.Tests.Infrastructure
{
    // A server that took the connection and then fell silent: it never answers, or it answers, sends a few lines and then nothing more. Either way only the
    // caller's own limit ends the request.
    internal sealed class SilentServer : HttpMessageHandler
    {
        private readonly string[]? _lines;
        private int _requests;

        private SilentServer(string[]? lines)
        {
            _lines = lines;
        }

        public int Requests => _requests;

        public static SilentServer NeverAnswers() => new(null);

        public static SilentServer StallsAfter(params string[] lines) => new(lines);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            if (_lines is null)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            var start = Encoding.UTF8.GetBytes(string.Concat(_lines!.Select(line => line + "\n")));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(start)) };
        }

        // Gives its first bytes and then waits until the reader cancels.
        private sealed class StallingStream(byte[] start) : Stream
        {
            private int _position;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_position < start.Length)
                {
                    var count = Math.Min(buffer.Length, start.Length - _position);
                    start.AsMemory(_position, count).CopyTo(buffer);
                    _position += count;
                    return count;
                }

                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
