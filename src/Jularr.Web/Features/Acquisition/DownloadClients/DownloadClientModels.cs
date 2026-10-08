using System.Text.Json;
using System.Text.Json.Serialization;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Operations;

namespace Jularr.Web.Features.Acquisition.DownloadClients;

/// <summary>
/// Kind of download client connection. Jularr is usenet-only: torrent
/// clients (qBittorrent) are intentionally unsupported.
/// </summary>
public enum DownloadClientType
{
    Sabnzbd
}

/// <summary>
/// Type-specific settings. SABnzbd keeps one category mapping for every
/// media acquisition kind it can receive.
/// </summary>
public sealed record DownloadClientSettings
{
    public DownloadClientSettings(
        string baseUrl,
        IReadOnlyDictionary<MediaAcquisitionKind, string?>? categories = null)
    {
        BaseUrl = baseUrl;
        Categories = Enum.GetValues<MediaAcquisitionKind>()
            .ToDictionary(
                kind => kind,
                kind => categories is not null && categories.TryGetValue(kind, out var category)
                    ? CleanCategory(category)
                    : null);
    }

    public string BaseUrl { get; init; }

    public IReadOnlyDictionary<MediaAcquisitionKind, string?> Categories { get; init; }

    public string? CategoryFor(MediaAcquisitionKind kind) =>
        Categories.TryGetValue(kind, out var category) ? CleanCategory(category) : null;

    /// <summary>
    /// The media type a job in this SABnzbd category belongs to, or null for a category no media
    /// type is mapped to (a job Jularr did not submit).
    /// </summary>
    public MediaAcquisitionKind? KindForCategory(string? category)
    {
        var cleaned = CleanCategory(category);
        if (cleaned is null)
        {
            return null;
        }

        foreach (var (kind, configured) in Categories)
        {
            if (CleanCategory(configured) is { } mapped &&
                mapped.Equals(cleaned, StringComparison.OrdinalIgnoreCase))
            {
                return kind;
            }
        }

        return null;
    }

    public static DownloadClientSettings CreateDefault(string baseUrl) =>
        new(
            baseUrl,
            new Dictionary<MediaAcquisitionKind, string?>
            {
                [MediaAcquisitionKind.Anime] = "anime",
                [MediaAcquisitionKind.Tv] = "tv",
                [MediaAcquisitionKind.Movie] = "movies",
                [MediaAcquisitionKind.Manga] = "manga",
                [MediaAcquisitionKind.LightNovel] = "lightnovels",
                [MediaAcquisitionKind.Book] = "books",
                [MediaAcquisitionKind.Music] = "music",
                [MediaAcquisitionKind.Audiobook] = "audiobooks"
            });

    private static string? CleanCategory(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// One entry of the canonical download client list. <see cref="Secret"/> is
/// the SABnzbd API key, protected at rest. <see cref="Priority"/> is
/// lower-is-first, and lets several SABnzbd connections fail over to each
/// other on submission failure.
/// </summary>
public sealed record DownloadClientEntry(
    Guid Id,
    string Name,
    DownloadClientType Type,
    bool Enabled,
    int Priority,
    DownloadClientSettings Settings,
    [property: JsonIgnore] string? Secret)
{
    public string? CategoryFor(MediaAcquisitionKind mediaKind) =>
        Settings.CategoryFor(mediaKind);
}

public sealed record DownloadClientTestResult(
    bool Success,
    string? Version = null,
    string? Error = null);

/// <summary>
/// One release submission. Exactly one of <see cref="Url"/>/<see cref="File"/> is set. <see cref="Priority"/> is how soon the client
/// should take it before its other queued downloads: <see cref="OperationPriority.High"/> for a release a profile is waiting to watch.
/// </summary>
public sealed record DownloadClientSubmitRequest(
    Uri? Url,
    string? Name,
    MediaAcquisitionKind MediaKind,
    Stream? File = null,
    string? FileName = null,
    OperationPriority Priority = OperationPriority.Normal);

/// <summary>Where the completed-download import stands for one external download.</summary>
public enum DownloadImportState
{
    /// <summary>The files cannot be imported yet (path not reported, share offline, storage busy).</summary>
    Waiting,
    /// <summary>The completed files are being checked before they enter the media library.</summary>
    Verifying,
    /// <summary>The media importer is reading or placing the completed files.</summary>
    Importing,
    /// <summary>The imported media is being reconciled with its requested provider metadata.</summary>
    MatchingMetadata,
    Completed,
    /// <summary>The downloaded package was unsuitable for the media type.</summary>
    Rejected,
    /// <summary>Waiting timed out; the owner has to fix the path or import by hand.</summary>
    GaveUp,
    /// <summary>Some files need an owner decision before they are imported (Anime manual review).</summary>
    ManualReview,
    /// <summary>The import failed; the result says why.</summary>
    Failed
}

/// <summary>
/// The import side of an external download as the shared completed-download import recorded
/// it: the path the download client reported, the path Jularr read after the remote-path
/// mapping, where the media went, with which import mode (null: read in place) and the result.
/// </summary>
public sealed record DownloadImportDetails(
    DownloadImportState State,
    string Result,
    DateTime UpdatedAtUtc,
    string? ReportedPath = null,
    string? LocalPath = null,
    string? Destination = null,
    ImportMode? Mode = null);

/// <summary>
/// Durable routing metadata for a submitted external download. The monitor
/// uses the exact selected connection instead of whichever client currently
/// has the highest priority. <see cref="Import"/> is filled once the
/// completed download was handed to its media importer. <see cref="ReleaseSource"/> and <see cref="ReleaseGroup"/> say where the release came
/// from, so the outcome of the download can later count for or against that indexer and release group (<c>ReleaseReliabilityService</c>).
/// <see cref="FailureKind"/> is why a failed download failed (a <c>SabnzbdFailureKind</c> name), so only failures that are the release's fault count as evidence.
/// </summary>
public sealed record DownloadOperationDetails(
    Guid ClientEntryId,
    MediaAcquisitionKind MediaKind,
    string? Category,
    DownloadImportDetails? Import = null,
    string? TargetKey = null,
    string? ReleaseSource = null,
    string? ReleaseGroup = null,
    string? FailureKind = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public string Serialize() => JsonSerializer.Serialize(
        new PersistedDetails(
            ClientEntryId,
            AcquisitionAccessNames.Kind(MediaKind),
            string.IsNullOrWhiteSpace(Category) ? null : Category.Trim(),
            Import,
            string.IsNullOrWhiteSpace(TargetKey) ? null : TargetKey.Trim(),
            string.IsNullOrWhiteSpace(ReleaseSource) ? null : ReleaseSource.Trim(),
            string.IsNullOrWhiteSpace(ReleaseGroup) ? null : ReleaseGroup.Trim(),
            string.IsNullOrWhiteSpace(FailureKind) ? null : FailureKind.Trim()),
        JsonOptions);

    public static bool TryParse(string? json, out DownloadOperationDetails? details)
    {
        details = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            var persisted = JsonSerializer.Deserialize<PersistedDetails>(json, JsonOptions);
            if (persisted is null || persisted.ClientEntryId == Guid.Empty || string.IsNullOrWhiteSpace(persisted.MediaKind))
            {
                return false;
            }

            details = new DownloadOperationDetails(
                persisted.ClientEntryId,
                AcquisitionAccessNames.ParseKind(persisted.MediaKind),
                string.IsNullOrWhiteSpace(persisted.Category) ? null : persisted.Category.Trim(),
                persisted.Import,
                string.IsNullOrWhiteSpace(persisted.TargetKey) ? null : persisted.TargetKey.Trim(),
                persisted.ReleaseSource,
                persisted.ReleaseGroup,
                persisted.FailureKind);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed record PersistedDetails(
        Guid ClientEntryId,
        string MediaKind,
        string? Category,
        DownloadImportDetails? Import = null,
        string? TargetKey = null,
        string? ReleaseSource = null,
        string? ReleaseGroup = null,
        string? FailureKind = null);
}

public sealed record DownloadClientSubmitResult(
    bool Success,
    string? ExternalId,
    string? Error = null);

public enum DownloadClientJobState
{
    Queued,
    Downloading,
    PostProcessing,
    Completed,
    Failed,
    Unknown
}

public sealed record DownloadClientJobStatus(
    string ExternalId,
    string Name,
    DownloadClientJobState State,
    double? Percentage,
    TimeSpan? TimeLeft,
    long? SizeBytes,
    long? SizeLeftBytes,
    double? BytesPerSecond,
    string? StoragePath,
    string? FailureMessage)
{
    public bool IsCompleted => State == DownloadClientJobState.Completed;
    public bool IsFailed => State == DownloadClientJobState.Failed;
}

/// <summary>
/// One download client implementation. SABnzbd is the only implementation;
/// the pipeline and Books submission still depend only on this interface,
/// never on <c>SabnzbdDownloadClient</c> directly.
/// </summary>
public interface IDownloadClient
{
    /// <summary>Stable id stored as an Operation's ExternalProvider for jobs sent to this client.</summary>
    string ProviderId { get; }

    Task<DownloadClientTestResult> TestAsync(
        DownloadClientEntry entry,
        CancellationToken cancellationToken);

    Task<DownloadClientSubmitResult> SubmitAsync(
        DownloadClientEntry entry,
        DownloadClientSubmitRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DownloadClientJobStatus>> GetStatusAsync(
        DownloadClientEntry entry,
        IReadOnlyCollection<string> externalIds,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(
        DownloadClientEntry entry,
        string externalId,
        bool deleteFiles,
        CancellationToken cancellationToken);
}

public sealed class DownloadClientException(string message, Exception? innerException = null)
    : Exception(message, innerException);
