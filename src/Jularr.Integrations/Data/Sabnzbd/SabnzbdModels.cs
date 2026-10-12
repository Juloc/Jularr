using System.Text.Json.Serialization;

namespace Jularr.Integrations.Data.Sabnzbd
{
    internal sealed record SabnzbdConnection(string BaseUrl, [property: JsonIgnore] string ApiKey);

    // Version alone proves reachability; the queue read proves the full API key.
    internal sealed record SabnzbdConnectionTestResult(bool IsAvailable, string? Version);

    internal sealed record SabnzbdGrabRequest(Uri NzbUrl, string? NzbName, string? Category);

    internal sealed record SabnzbdGrabResult(bool Success, IReadOnlyList<string> NzoIds);

    internal enum SabnzbdFailureKind : byte
    {
        None,
        Download,
        Unpack,
        Verification,
        Password,
        Script,
        Storage,
        Unknown
    }

    internal static class SabnzbdFailureKinds
    {
        // Only these say something about the release; client problems never exclude it.
        public static bool IsReleaseFault(SabnzbdFailureKind kind) =>
            kind is SabnzbdFailureKind.Download or SabnzbdFailureKind.Unpack or SabnzbdFailureKind.Verification or SabnzbdFailureKind.Password;
    }

    internal sealed record SabnzbdQueueJob(string NzoId, string Name, double? Percentage);

    internal sealed record SabnzbdHistoryJob(string NzoId, string Name, string? Status, string? StoragePath, SabnzbdFailureKind FailureKind)
    {
        public bool IsCompleted => FailureKind == SabnzbdFailureKind.None && string.Equals(Status, "Completed", StringComparison.OrdinalIgnoreCase);

        public bool IsFailed => FailureKind != SabnzbdFailureKind.None || string.Equals(Status, "Failed", StringComparison.OrdinalIgnoreCase);
    }

    internal sealed record SabnzbdQueueSnapshot(IReadOnlyList<SabnzbdQueueJob> Jobs);

    internal sealed record SabnzbdHistorySnapshot(IReadOnlyList<SabnzbdHistoryJob> Jobs);

    // Messages never contain the API key.
    internal sealed class SabnzbdException(string message, Exception? innerException = null) : Exception(message, innerException);
}
