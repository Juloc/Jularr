using Jularr.Integrations.Data.Browser;

namespace Jularr.Backend.Service.Core
{
    // Downloads that wait for the source's check continue by themselves once the source browser is through it:
    // it runs and has read the source last. A source that stays closed is asked again only once per interval,
    // so a check that needs a person never makes the downloads ask the source in a loop.
    internal sealed class SourceGateRelease(ISourceBrowser browser, TimeProvider time)
    {
        internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
        private readonly object _lock = new();
        private DateTimeOffset? _releasedAt;

        public bool IsDue()
        {
            if (!browser.IsAvailable || browser.Status is not { IsRunning: true, LastOutcome: "read" }) return false;
            lock (_lock)
            {
                return _releasedAt is null || time.GetUtcNow() - _releasedAt >= Interval;
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
