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

    /// <summary>The client could not write or read its own storage (a full disk, permissions, a missing folder): a problem of this server, never of the release.</summary>
    Storage,
    Unknown
}

public static class SabnzbdFailureKinds
{
    /// <summary>
    /// Whether a failure says something about the release itself (missing articles, a corrupt or protected archive). Anything else (the client's own
    /// storage, its scripts, a job that vanished, a removed client, an unclassified message) is a local or unknown problem and never counts against
    /// an indexer or a release group.
    /// </summary>
    public static bool IsReleaseFault(SabnzbdFailureKind kind) => kind is SabnzbdFailureKind.Download or SabnzbdFailureKind.Unpack or SabnzbdFailureKind.Verification or SabnzbdFailureKind.Password;

    /// <summary>The release-fault test for a kind stored by name in the operation's details; an unrecorded kind is not evidence.</summary>
    public static bool IsReleaseFault(string? kind) => Enum.TryParse<SabnzbdFailureKind>(kind, ignoreCase: true, out var parsed) && IsReleaseFault(parsed);
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
            SabnzbdFailureKind.Storage => "the download client could not use its storage (disk space or permissions)",
            _ => "download failed"
        };

        return string.IsNullOrWhiteSpace(message)
            ? $"SABnzbd: {reason}."
            : $"SABnzbd: {reason} — {message.Trim()}";
    }
}
