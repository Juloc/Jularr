using System.Text.Json.Serialization;

namespace Jularr.Web.Features.Acquisition.Sabnzbd;

/// <summary>
/// What a SABnzbd job is for. Each purpose has its own configurable
/// SABnzbd category so completed files land where the owner expects.
/// </summary>
public enum SabnzbdPurpose
{
    Books,
    Anime
}

/// <summary>Effective, validated connection settings.</summary>
public sealed record SabnzbdSettings(
    string BaseUrl,
    string? BooksCategory = null,
    string? AnimeCategory = null)
{
    public string? CategoryFor(SabnzbdPurpose purpose) =>
        purpose switch
        {
            SabnzbdPurpose.Books => BooksCategory,
            SabnzbdPurpose.Anime => AnimeCategory,
            _ => null
        };
}

public sealed record SabnzbdConnection(
    SabnzbdSettings Settings,
    [property: JsonIgnore] string ApiKey);

public sealed record SabnzbdConnectionTestResult(
    bool Success,
    string? Version = null,
    string? Error = null,
    bool CanMonitor = false);

public sealed record SabnzbdGrabRequest(
    Uri NzbUrl,
    string? NzbName = null,
    string? Category = null,
    int? Priority = null);

public sealed record SabnzbdGrabResult(
    bool Success,
    IReadOnlyList<string> NzoIds,
    string? Error = null);

public enum SabnzbdFailureKind
{
    None,
    Download,
    Unpack,
    Verification,
    Password,
    Script,
    Unknown
}

public sealed record SabnzbdQueueJob(
    string NzoId,
    string Name,
    string? Status,
    string? Category,
    double? Percentage,
    TimeSpan? TimeLeft,
    long? SizeBytes,
    long? SizeLeftBytes);

public sealed record SabnzbdHistoryJob(
    string NzoId,
    string Name,
    string? Status,
    string? Category,
    string? StoragePath,
    string? FailureMessage,
    SabnzbdFailureKind FailureKind,
    DateTimeOffset? CompletedAt,
    long? SizeBytes = null)
{
    public bool IsCompleted =>
        FailureKind == SabnzbdFailureKind.None
        && string.Equals(Status, "Completed", StringComparison.OrdinalIgnoreCase);

    public bool IsFailed =>
        FailureKind != SabnzbdFailureKind.None
        || string.Equals(Status, "Failed", StringComparison.OrdinalIgnoreCase);
}

public sealed record SabnzbdQueueSnapshot(
    bool Paused,
    double? BytesPerSecond,
    TimeSpan? TimeLeft,
    IReadOnlyList<SabnzbdQueueJob> Jobs);

public sealed record SabnzbdHistorySnapshot(
    IReadOnlyList<SabnzbdHistoryJob> Jobs);

public sealed record SabnzbdActionResult(
    bool Success,
    string? NewNzoId = null,
    string? Error = null);

public sealed class SabnzbdException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public static class SabnzbdFailureDescriptions
{
    public static string Describe(
        SabnzbdFailureKind kind,
        string? message)
    {
        var reason = kind switch
        {
            SabnzbdFailureKind.Password => "release is password-protected",
            SabnzbdFailureKind.Unpack => "extraction failed",
            SabnzbdFailureKind.Verification => "release is corrupt and could not be repaired",
            SabnzbdFailureKind.Download => "download is incomplete (missing articles)",
            SabnzbdFailureKind.Script => "post-processing script failed",
            _ => "download failed"
        };

        return string.IsNullOrWhiteSpace(message)
            ? $"SABnzbd: {reason}."
            : $"SABnzbd: {reason} — {message.Trim()}";
    }
}
