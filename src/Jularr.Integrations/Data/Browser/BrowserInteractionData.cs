namespace Jularr.Integrations.Data.Browser
{
    internal interface IBrowserInteraction
    {
        bool HasManualControl { get; }
        Task<BrowserViewport> BeginManualAsync(Uri address, DateTimeOffset expiresAt, CancellationToken ct, bool opensPage = false);
        Task<byte[]> CaptureManualAsync(DateTimeOffset expiresAt, CancellationToken ct);
        Task InputManualAsync(string action, int x, int y, int delta, string? key, string? text, DateTimeOffset expiresAt, CancellationToken ct);
        Task EndManualAsync(CancellationToken ct);
    }

    internal sealed record BrowserViewport(int Width, int Height);
}
