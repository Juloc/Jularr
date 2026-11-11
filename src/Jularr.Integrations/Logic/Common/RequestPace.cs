using System.Diagnostics;

namespace Jularr.Integrations.Logic.Common
{
    // The spacing of one provider's requests, one instance for the whole process (as NewznabGate): one caller at a time, and the provider's spacing
    // between its requests, as the client counts it. A test that answers with a fake gives the client a pace without spacing.
    // Counted on the monotonic clock, so a system clock that jumps back never holds requests back.
    internal sealed class RequestPace<TClient>(TimeSpan spacing)
    {
        private long _last;

        public TimeSpan Spacing { get; } = spacing;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        // The time since the last request started or ended; a pace that never sent one makes nobody wait.
        public TimeSpan SinceLast => _last == 0 ? TimeSpan.MaxValue : Stopwatch.GetElapsedTime(_last);

        // A request started or ended now.
        public void MarkNow() => _last = Stopwatch.GetTimestamp();

        // The next request's turn: its wait for the spacing since the last one, which it then becomes.
        public async Task WaitForTurnAsync(CancellationToken ct)
        {
            var since = SinceLast;
            if (since < Spacing)
            {
                await Task.Delay(Spacing - since, ct);
            }

            MarkNow();
        }
    }
}
