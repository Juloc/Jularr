using System.Threading.Channels;

namespace Jularr.Backend.Service.Core
{
    public sealed class AcquisitionSearchSignal
    {
        private readonly Channel<bool> _pending = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });

        public void RequestSearch() => _pending.Writer.TryWrite(true);

        public bool TakeRequest() => _pending.Reader.TryRead(out _);

        public ValueTask<bool> WaitForRequestAsync(CancellationToken ct) => _pending.Reader.WaitToReadAsync(ct);
    }
}
