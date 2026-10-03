using System.ComponentModel.DataAnnotations.Schema;

namespace Jularr.Web.Features.Library;

public enum MediaAnalysisStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2
}

/// <summary>
/// Canonical embedded stream kind. Audio/Subtitle retain their historical numeric values so the
/// in-place MediaAnalysisStream -> MediaTrack migration preserves existing rows exactly.
/// </summary>
public enum MediaTrackKind
{
    Audio = 0,
    Subtitle = 1,
    Video = 2
}

/// <summary>Canonical technical analysis of one physical StoredFile.</summary>
public sealed class MediaTechnicalAnalysis
{
    // The compatibility property name stays mapped while the database column is renamed to StoredFileId.
    // Existing callers therefore keep compiling while the persisted relation is canonical.
    public Guid MediaFileId { get; set; }

    [NotMapped]
    public Guid StoredFileId
    {
        get => MediaFileId;
        set => MediaFileId = value;
    }

    public MediaAnalysisStatus Status { get; set; }
    public int ProbeVersion { get; set; }
    public long SourceSizeBytes { get; set; }
    public DateTime SourceLastWriteTimeUtc { get; set; }
    public string? SourceFingerprint { get; set; }
    public string? Diagnostic { get; set; }
    public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;
    public string? Container { get; set; }
    public double? DurationSeconds { get; set; }
    public string? VideoCodec { get; set; }
    public string? VideoProfile { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? PixelFormat { get; set; }
    public int? BitDepth { get; set; }
    public string? DynamicRange { get; set; }
}

/// <summary>Canonical embedded video/audio/subtitle stream of a StoredFile.</summary>
public sealed class MediaTrack
{
    public Guid MediaFileId { get; set; }

    [NotMapped]
    public Guid StoredFileId
    {
        get => MediaFileId;
        set => MediaFileId = value;
    }

    public int StreamIndex { get; set; }
    public MediaTrackKind Kind { get; set; }
    public string? Codec { get; set; }
    public string? Language { get; set; }
    public string? Title { get; set; }
    public int? Channels { get; set; }
    public string? ChannelLayout { get; set; }
    public bool IsDefault { get; set; }
    public bool IsForced { get; set; }
}

public sealed record MediaVideoInfo(
    string? Codec,
    string? Profile,
    int? Width,
    int? Height,
    string? PixelFormat,
    int? BitDepth,
    string? DynamicRange,
    int? StreamIndex = null);

public sealed record MediaStreamInfo(
    int Index,
    MediaTrackKind Kind,
    string? Codec,
    string? Language,
    string? Title,
    int? Channels,
    string? ChannelLayout,
    bool IsDefault,
    bool IsForced)
{
    public bool IsText =>
        Kind == MediaTrackKind.Subtitle &&
        Jularr.Web.Features.Subtitles.SubtitleFormats.IsText(Codec);
}

public sealed record MediaTechnicalInfo(
    string? Container,
    double? DurationSeconds,
    MediaVideoInfo? Video,
    IReadOnlyList<MediaStreamInfo> Streams)
{
    public IReadOnlyList<MediaStreamInfo> AudioStreams =>
        [.. Streams.Where(x => x.Kind == MediaTrackKind.Audio)];

    public IReadOnlyList<MediaStreamInfo> SubtitleStreams =>
        [.. Streams.Where(x => x.Kind == MediaTrackKind.Subtitle)];
}

public sealed record MediaInventoryEntry(
    Guid MediaFileId,
    MediaAnalysisStatus Status,
    int ProbeVersion,
    DateTime AnalyzedAt,
    string? Diagnostic,
    MediaTechnicalInfo? Technical)
{
    public Guid StoredFileId => MediaFileId;
}

public sealed record MediaInventoryReconciliation(
    int Unchanged,
    int Analyzed,
    int Failed,
    int Deferred);
