namespace Jularr.Integrations.Logic.Common
{
    // The spacing of one provider's requests, one instance for the whole process (as NewznabGate): one caller at a time, and the provider's spacing
    // between its requests, as the client counts it. A test that answers with a fake gives the client a pace without spacing.
    internal sealed class RequestPace<TClient>(TimeSpan spacing)
    {
        public TimeSpan Spacing { get; } = spacing;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        // When the last request started or ended; a pace that never sent one makes nobody wait.
        public DateTimeOffset Last { get; set; } = DateTimeOffset.MinValue;

        // The next request's turn: its wait for the spacing since the last one, which it then becomes.
        public async Task WaitForTurnAsync(CancellationToken ct)
        {
            var wait = Last + Spacing - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct);
            }

            Last = DateTimeOffset.UtcNow;
        }
    }
}
