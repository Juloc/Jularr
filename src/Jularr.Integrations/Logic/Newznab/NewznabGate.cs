using System.Net.Http.Headers;
using Jularr.Integrations.Data.Newznab;

namespace Jularr.Integrations.Logic.Newznab
{
    // One pace per indexer host for every caps read, search and NZB fetch: calls keep their spacing, and a 429 stops the host until its Retry-After has passed.
    internal sealed class NewznabGate(TimeSpan spacing)
    {
        public static readonly TimeSpan DefaultSpacing = TimeSpan.FromSeconds(1);

        private static readonly TimeSpan DefaultPause = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan MinimumPause = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan MaximumPause = TimeSpan.FromDays(1);

        private readonly object _lock = new();
        private readonly Dictionary<string, HostPace> _hosts = new(StringComparer.Ordinal);

        public static string HostKey(Uri address) => address.IdnHost.ToLowerInvariant() + ":" + address.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Seconds, or a date, after which the indexer wants to be asked again.
        public static TimeSpan? ReadRetryAfter(HttpResponseHeaders headers) =>
            headers.RetryAfter is { } header ? header.Delta ?? header.Date - DateTimeOffset.UtcNow : null;

        // Waits for the host's next turn; a host that asked for a pause refuses at once instead of holding the caller.
        public async Task WaitAsync(string host, CancellationToken ct)
        {
            var wait = Reserve(host);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct);
            }

            // A 429 that came in while this call waited stops it too.
            lock (_lock)
            {
                ThrowIfPaused(host, Environment.TickCount64);
            }
        }

        // Stops every call to the host; the answer's own Retry-After counts, bounded.
        public TimeSpan Pause(string host, TimeSpan? retryAfter)
        {
            var pause = retryAfter ?? DefaultPause;
            pause = pause < MinimumPause ? MinimumPause : pause > MaximumPause ? MaximumPause : pause;
            lock (_lock)
            {
                var pace = Pace(host);
                pace.PausedUntil = Math.Max(pace.PausedUntil, Environment.TickCount64 + (long)pause.TotalMilliseconds);
                pace.NextTurn = Math.Max(pace.NextTurn, pace.PausedUntil);
            }

            return pause;
        }

        private TimeSpan Reserve(string host)
        {
            lock (_lock)
            {
                var now = Environment.TickCount64;
                ThrowIfPaused(host, now);
                var pace = Pace(host);
                var turn = Math.Max(now, pace.NextTurn);
                pace.NextTurn = turn + (long)spacing.TotalMilliseconds;
                return TimeSpan.FromMilliseconds(turn - now);
            }
        }

        private void ThrowIfPaused(string host, long now)
        {
            if (Pace(host).PausedUntil - now is > 0 and var remaining)
            {
                throw new NewznabException(NewznabFailure.RateLimited, "The indexer asked for a pause.", TimeSpan.FromMilliseconds(remaining));
            }
        }

        private HostPace Pace(string host) => _hosts.TryGetValue(host, out var pace) ? pace : _hosts[host] = new HostPace();

        private sealed class HostPace
        {
            public long NextTurn;
            public long PausedUntil;
        }
    }
}
