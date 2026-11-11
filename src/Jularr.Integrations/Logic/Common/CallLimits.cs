using System.Globalization;
using System.Net;

namespace Jularr.Integrations.Logic.Common
{
    // The limits of one external HTTP call: connecting, the wait for the response headers, the longest pause while the body is read, and the whole call.
    internal sealed record ExternalCallLimits(TimeSpan Connect, TimeSpan FirstByte, TimeSpan Idle, TimeSpan Total)
    {
        public static readonly TimeSpan ConnectLimit = TimeSpan.FromSeconds(10);

        // An answer arrives whole within its time.
        public static ExternalCallLimits Answer(int seconds)
        {
            var limit = TimeSpan.FromSeconds(seconds);
            return new ExternalCallLimits(limit < ConnectLimit ? limit : ConnectLimit, limit, limit, limit);
        }

        // A file or long list starts like an answer and may then take long, as long as data keeps coming: two minutes without any fails it.
        public static ExternalCallLimits Download(int firstByteSeconds, TimeSpan total) => new(ConnectLimit, TimeSpan.FromSeconds(firstByteSeconds), TimeSpan.FromMinutes(2), total);
    }

    // The outermost handler of an external client. The response headers must arrive within FirstByte, the body may pause for at most Idle per read, and
    // the whole call ends after Total. A limit fails like HttpClient's own timeout (TaskCanceledException with an inner TimeoutException); a caller who
    // gives up keeps an ordinary OperationCanceledException.
    internal sealed class CallLimitHandler(ExternalCallLimits clientLimits, TimeProvider time) : DelegatingHandler
    {
        // A request that reads far more than the client's usual answers names its own limits; connecting stays the client's.
        public static readonly HttpRequestOptionsKey<ExternalCallLimits> OwnLimits = new("Jularr.ExternalCallLimits");

        public ExternalCallLimits Limits => clientLimits;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var limits = request.Options.TryGetValue(OwnLimits, out var own) ? own : clientLimits;
            var total = new CancellationTokenSource(limits.Total, time);
            HttpResponseMessage response;
            try
            {
                using var firstByte = new CancellationTokenSource(limits.FirstByte, time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, firstByte.Token, total.Token);
                try
                {
                    response = await base.SendAsync(request, linked.Token);
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && (firstByte.IsCancellationRequested || total.IsCancellationRequested))
                {
                    throw Exceeded(request, total.IsCancellationRequested ? "finish" : "answer", total.IsCancellationRequested ? limits.Total : limits.FirstByte, exception);
                }
            }
            catch
            {
                total.Dispose();
                throw;
            }

            response.Content = new LimitedContent(response.Content, new BodyLimits(request, limits, time, total));
            return response;
        }

        private static TaskCanceledException Exceeded(HttpRequestMessage request, string step, TimeSpan limit, Exception inner)
        {
            // The host only: addresses may carry keys.
            var message = string.Create(CultureInfo.InvariantCulture, $"{request.RequestUri?.Host ?? "The server"} did not {step} within {limit.TotalSeconds:0.###} s.");
            return new TaskCanceledException(message, new TimeoutException(message, inner));
        }

        // The body's timers: Idle runs only while a read waits, Total since the request started.
        private sealed class BodyLimits : IDisposable
        {
            private readonly HttpRequestMessage _request;
            private readonly ExternalCallLimits _limits;
            private readonly CancellationTokenSource _total;
            private readonly CancellationTokenSource _idle;
            private readonly CancellationTokenSource _either;

            public BodyLimits(HttpRequestMessage request, ExternalCallLimits limits, TimeProvider time, CancellationTokenSource total)
            {
                _request = request;
                _limits = limits;
                _total = total;
                _idle = new CancellationTokenSource(Timeout.InfiniteTimeSpan, time);
                _either = CancellationTokenSource.CreateLinkedTokenSource(_idle.Token, _total.Token);
            }

            public async ValueTask<T> RunAsync<T>(Func<CancellationToken, ValueTask<T>> read, CancellationToken cancellationToken)
            {
                _idle.CancelAfter(_limits.Idle);
                using var linked = cancellationToken.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _either.Token) : null;
                try
                {
                    return await read(linked?.Token ?? _either.Token);
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && _either.IsCancellationRequested)
                {
                    throw Exceeded(_request, _total.IsCancellationRequested ? "finish" : "send more data", _total.IsCancellationRequested ? _limits.Total : _limits.Idle, exception);
                }
                finally
                {
                    if (!_idle.IsCancellationRequested)
                    {
                        _idle.CancelAfter(Timeout.InfiniteTimeSpan);
                    }
                }
            }

            public void Dispose()
            {
                _either.Dispose();
                _idle.Dispose();
                _total.Dispose();
            }
        }

        private sealed class LimitedContent : HttpContent
        {
            private readonly HttpContent _inner;
            private readonly BodyLimits _limits;

            public LimitedContent(HttpContent inner, BodyLimits limits)
            {
                _inner = inner;
                _limits = limits;
                foreach (var header in inner.Headers)
                {
                    Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                Headers.ContentLength = inner.Headers.ContentLength;
            }

            protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
                new LimitedStream(await _limits.RunAsync(async token => await _inner.ReadAsStreamAsync(token), cancellationToken), _limits);

            protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            {
                await using var body = await CreateContentReadStreamAsync(cancellationToken);
                await body.CopyToAsync(stream, cancellationToken);
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

            // The copied headers carry the length when it is known.
            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                    _limits.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        private sealed class LimitedStream(Stream inner, BodyLimits limits) : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                limits.RunAsync(token => inner.ReadAsync(buffer, token), cancellationToken);

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            // A blocking reader waits under the same limits.
            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    inner.Dispose();
                }

                base.Dispose(disposing);
            }

            public override async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                await base.DisposeAsync();
            }
        }
    }
}
