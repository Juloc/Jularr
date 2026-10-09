namespace Jularr.Web.Features.Subtitles;

public sealed class SubtitleTrack
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EpisodeId { get; set; }
    public string Path { get; set; } = "";
    public string Language { get; set; } = "ja";
    public string Format { get; set; } = "";

    /// <summary>
    /// True for a forced (signs/foreign-dialogue-only) track. Distinguishes it from the full
    /// dialogue track for the same <see cref="Language"/> so both can be imported and tracked
    /// against a language profile's forced/SDH preference (#526) without one overwriting the other.
    /// </summary>
    public bool Forced { get; set; }

    /// <summary>True for a subtitle-for-the-deaf-or-hard-of-hearing (SDH/closed-caption) track.</summary>
    public bool Sdh { get; set; }

    public DateTime SourceUpdatedAt { get; set; }
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
}

public sealed class SubtitleCue
{
    public long Id { get; set; }
    public Guid SubtitleTrackId { get; set; }
    public int StartMs { get; set; }
    public int EndMs { get; set; }
    public string Text { get; set; } = "";
}

public sealed record SubtitleCueData(
    int StartMs,
    int EndMs,
    string Text,
    SubtitleCuePresentation? Presentation = null);

public sealed record SubtitleCuePresentation(
    int? Alignment = null,
    double? XPercent = null,
    double? YPercent = null,
    int? Layer = null,
    string? FontFamily = null,
    double? FontSize = null,
    bool? Bold = null,
    bool? Italic = null,
    string? Color = null);
