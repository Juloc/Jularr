namespace Jularr.Backend.Service.Core
{
    // When someone last used Jularr; background syncs that wait while it is used read it.
    public sealed class UserActivity(TimeProvider time)
    {
        private long _lastTicks;

        public void Touch() => Interlocked.Exchange(ref _lastTicks, time.GetUtcNow().UtcTicks);

        public bool IsUsedWithin(TimeSpan span) => Interlocked.Read(ref _lastTicks) is var last and > 0 && time.GetUtcNow().UtcTicks - last < span.Ticks;
    }
}
