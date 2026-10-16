using System.Collections.Concurrent;

namespace Jularr.Integrations.Logic.Storage
{
    // The open SMB sessions, one per connection and saved configuration; a session closes after 15 idle minutes so the NAS may sleep.
    internal sealed class SmbShares : IDisposable
    {
        public static readonly TimeSpan IdleTime = TimeSpan.FromMinutes(15);

        private readonly ConcurrentDictionary<(long StorageMountId, long Version), SmbShare> _shares = new();
        private readonly TimeProvider _time;
        private readonly ITimer _timer;

        public SmbShares(TimeProvider time)
        {
            _time = time;
            _timer = time.CreateTimer(_ => CloseIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        // A changed connection gets a new session; the old one closes once idle.
        public SmbShare Get(long storageMountId, long version, Func<SmbSession> connect) =>
            _shares.GetOrAdd((storageMountId, version), _ => new SmbShare(connect, _time));

        public void CloseIdle()
        {
            // The share objects stay; only their sessions close, so a share in use never runs beside a second one.
            foreach (var share in _shares.Values)
            {
                share.CloseIfIdle(IdleTime);
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
            foreach (var share in _shares.Values) share.Dispose();
            _shares.Clear();
        }
    }
}
