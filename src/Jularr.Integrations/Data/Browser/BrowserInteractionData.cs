namespace Jularr.Integrations.Data.Browser
{
    internal interface IBrowserInteraction
    {
        bool HasManualControl { get; }
        // The progress callback is told each step as it begins: the address check, the wait for automatic work to let go, the browser start.
        Task<BrowserViewport> BeginManualAsync(Uri address, DateTimeOffset expiresAt, CancellationToken ct, bool opensPage = false, Action<BrowserOpenPhase>? progress = null);
        Task<byte[]> CaptureManualAsync(DateTimeOffset expiresAt, CancellationToken ct);
        Task InputManualAsync(string action, int x, int y, int delta, string? key, string? text, DateTimeOffset expiresAt, CancellationToken ct);
        Task EndManualAsync(CancellationToken ct);
    }

    internal enum BrowserOpenPhase { Checking, Waiting, Starting }

    internal sealed record BrowserViewport(int Width, int Height);
}
