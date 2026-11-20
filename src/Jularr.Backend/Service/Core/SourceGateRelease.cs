using Jularr.Integrations.Data.Browser;

namespace Jularr.Backend.Service.Core
{
    // Downloads that wait for the source's check continue by themselves: at once when the source browser is through it
    // (it runs and has read the source last), otherwise once per interval, because a source may drop its check without anybody
    // opening the browser. A source that stays closed is asked again only once per interval, so a check that needs a person
    // never makes the downloads ask the source in a loop.
    internal sealed class SourceGateRelease(ISourceBrowser browser, TimeProvider time)
    {
        internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
        private readonly object _lock = new();
        private readonly DateTimeOffset _started = time.GetUtcNow();
        private DateTimeOffset? _releasedAt;

        public bool IsDue()
        {
            var through = browser.IsAvailable && browser.Status is { IsRunning: true, LastOutcome: "read" };
            lock (_lock)
            {
                // Without the browser the first retry also waits an interval, so a fresh check is not asked again at once.
                var since = _releasedAt ?? (through ? null : _started);
                return since is null || time.GetUtcNow() - since >= Interval;
            }
        }

        // Only a release that moved downloads starts the interval.
        public void Released()
        {
            lock (_lock)
            {
                _releasedAt = time.GetUtcNow();
            }
        }
    }
}
