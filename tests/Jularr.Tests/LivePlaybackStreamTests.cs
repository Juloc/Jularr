using Jularr.Web.Features.Playback;

namespace Jularr.Tests;

/// <summary>
/// The first-output wait of a progressive stream, tested against fake pipes: a silent encoder must time out and be ended
/// (a child-process pipe read ignores cancellation), a cancelled request must end it too, and the bytes read ahead must
/// reach the client in order.
/// </summary>
[TestClass]
public sealed class LivePlaybackStreamTests
{
    [TestMethod]
    public async Task ASilentEncoderTimesOutIsKilledAndReleasesItsLease()
    {
        var pipe = new ScriptedPipe();
        var lease = new CountingLease();
        var killed = 0;
        var stream = LivePlaybackStream.Wrap(pipe, Task.FromResult(""), () => killed++, lease);

        await Assert.ThrowsAsync<TimeoutException>(() => stream.WaitForFirstBytesAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));

        Assert.AreEqual(1, killed, "The silent ffmpeg is killed.");
        Assert.AreEqual(1, lease.Disposed, "The slot comes back.");
        Assert.IsTrue(pipe.Disposed, "The pipe is closed.");
    }

    [TestMethod]
    public async Task ACancelledRequestEndsTheEncoderInsteadOfHangingOnThePipe()
    {
        var pipe = new ScriptedPipe();
        var lease = new CountingLease();
        var killed = 0;
        var stream = LivePlaybackStream.Wrap(pipe, Task.FromResult(""), () => killed++, lease);
        using var cancellation = new CancellationTokenSource();

        var waiting = stream.WaitForFirstBytesAsync(TimeSpan.FromMinutes(5), cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
        Assert.AreEqual(1, killed);
        Assert.AreEqual(1, lease.Disposed);
    }

    [TestMethod]
    public async Task AnEncoderThatEndsWithoutOutputFailsWithItsOwnError()
    {
        var pipe = new ScriptedPipe(endOfStream: true);
        var stream = LivePlaybackStream.Wrap(pipe, Task.FromResult("Stream map matches no streams.\nInvalid data found when processing input\n"), () => { }, null);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => stream.WaitForFirstBytesAsync(TimeSpan.FromSeconds(5), CancellationToken.None));

        StringAssert.Contains(failure.Message, "Invalid data found when processing input");
    }

    [TestMethod]
    public async Task TheBytesReadAheadAreServedFirstAndInOrder()
    {
        var pipe = new ScriptedPipe([1, 2, 3], [4, 5]);
        var lease = new CountingLease();
        var stream = LivePlaybackStream.Wrap(pipe, Task.FromResult(""), () => { }, lease);
        await stream.WaitForFirstBytesAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        var small = new byte[2];
        Assert.AreEqual(2, await stream.ReadAsync(small, 0, 2, CancellationToken.None));
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, small);
        using var rest = new MemoryStream();
        await stream.CopyToAsync(rest);

        CollectionAssert.AreEqual(new byte[] { 3, 4, 5 }, rest.ToArray(), "The remaining prefix comes before the live bytes.");
        stream.Dispose();
        stream.Dispose();
        Assert.AreEqual(1, lease.Disposed, "Disposing twice releases the slot once.");
    }

    private sealed class CountingLease : IDisposable
    {
        public int Disposed { get; private set; }

        public void Dispose() => Disposed++;
    }

    /// <summary>A pipe that returns its chunks and then ends; with no chunks it blocks forever, ignoring cancellation like a child-process pipe does.</summary>
    private sealed class ScriptedPipe : Stream
    {
        private readonly Queue<byte[]> _chunks;
        private readonly bool _endOfStream;
        private readonly TaskCompletionSource _never = new();

        public ScriptedPipe(params byte[][] chunks)
        {
            _chunks = new Queue<byte[]>(chunks);
            _endOfStream = chunks.Length > 0;
        }

        public ScriptedPipe(bool endOfStream)
        {
            _chunks = [];
            _endOfStream = endOfStream;
        }

        public bool Disposed { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_chunks.TryDequeue(out var chunk))
            {
                chunk.CopyTo(buffer);
                return chunk.Length;
            }

            if (_endOfStream || Disposed)
            {
                return 0;
            }

            await _never.Task;
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            _never.TrySetResult();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
