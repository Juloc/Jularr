using System.Text.RegularExpressions;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Quality;

namespace Jularr.Web.Features.Acquisition.Release;

/// <summary>
/// Music release parsing on top of the shared scene parser: the readable name of the release (separators and technical tags removed) and
/// the audio format as the quality key (<c>FLAC</c>, <c>MP3-320</c>, <c>MP3-V0</c>, <c>MP3-256</c>, <c>AAC</c>, <c>MP3</c>). A release that names no
/// format has no key and is only a last-resort candidate; the importer validates the real files.
/// </summary>
public sealed partial class MusicReleaseParser : IReleaseParser
{
    public static MusicReleaseParser Instance { get; } = new();

    public ReleaseInfo Parse(string releaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseName);

        var scene = SceneReleaseParser.Instance.Parse(releaseName);
        return scene with
        {
            SeriesTitle = ReadableName(scene.RawTitle),
            DocumentFormat = DetectQuality(releaseName) ?? "UNKNOWN-UNKNOWN"
        };
    }

    public bool TryParse(string? releaseName, out ReleaseInfo release)
    {
        release = default!;
        if (string.IsNullOrWhiteSpace(releaseName))
        {
            return false;
        }

        release = Parse(releaseName);
        return true;
    }

    /// <summary>The audio quality a release name declares, or null when it declares none.</summary>
    public static string? DetectQuality(string value)
    {
        if (FlacRegex().IsMatch(value))
        {
            return "FLAC";
        }

        if (Mp3Bitrate320Regex().IsMatch(value))
        {
            return "MP3-320";
        }

        if (VbrV0Regex().IsMatch(value))
        {
            return "MP3-V0";
        }

        if (Mp3Bitrate256Regex().IsMatch(value))
        {
            return "MP3-256";
        }

        if (AacRegex().IsMatch(value))
        {
            return "AAC";
        }

        return Mp3Regex().IsMatch(value) ? "MP3" : null;
    }

    /// <summary>The release name as words: dots, underscores and brackets become spaces, the format, bitrate and source tags are dropped.</summary>
    public static string ReadableName(string rawTitle)
    {
        var spaced = SeparatorRegex().Replace(rawTitle, " ");
        var withoutTags = TechnicalTagRegex().Replace(spaced, " ");
        return string.Join(' ', withoutTags.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:FLAC|lossless)(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FlacRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:320(?:k|kbps|kbit)?|MP3[ ._-]?320)(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Mp3Bitrate320Regex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:V0|VBR)(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VbrV0Regex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])256(?:k|kbps|kbit)?(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Mp3Bitrate256Regex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:AAC|M4A)(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AacRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])MP3(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Mp3Regex();

    [GeneratedRegex(@"[._\[\]{}]+", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:FLAC|lossless|MP3|AAC|M4A|CBR|VBR|V0|V2|WEB|CD|CDDA|CDS|WAV|320(?:k|kbps|kbit)?|256(?:k|kbps|kbit)?|24[ -]?bit|16[ -]?bit|44[.,]?1?k?hz|96k?hz|retail|proper|repack|rerip|incl|cue|log)(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalTagRegex();
}

/// <summary>
/// Seed Music profile for the shared acquisition engine. Lossless and every lossy encoding are accepted at once, ordered from lossless down, and the album stays wanted for an
/// upgrade up to lossless. A release without a declared format is last in the order because the importer validates the actual files.
/// </summary>
public static class MusicQualityProfiles
{
    public const string DefaultMusicId = "music-lossless";

    public static QualityProfile CreateDefaultMusic() =>
        new(
            DefaultMusicId,
            "Music",
            ["FLAC", "MP3-320", "MP3-V0", "MP3-256", "AAC", "MP3", "UNKNOWN-UNKNOWN"],
            ["FLAC", "MP3-320", "MP3-V0", "MP3-256", "AAC", "MP3", "UNKNOWN-UNKNOWN"],
            UpgradeAllowed: true,
            UpgradeCutoffQuality: "FLAC",
            MinimumSizeBytes: null,
            MaximumSizeBytes: null,
            MustContain: [],
            MustNotContain: [],
            RequiredRegex: [],
            RejectedRegex: [],
            ScoreRules:
            [
                new("Prefer retail", ReleaseRuleField.RawTitle, ReleaseRuleMatch.Contains, "retail", 2),
                new("Prefer CD source", ReleaseRuleField.RawTitle, ReleaseRuleMatch.Regex, @"(?<![A-Za-z0-9])CD(?:DA)?(?![A-Za-z0-9])", 2)
            ]);
}

/// <summary>Music participates in the shared acquisition registry at whole-item (album) granularity.</summary>
public sealed class MusicAcquisitionRegistration : IMediaAcquisitionRegistration
{
    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Music;

    public IReleaseParser CreateReleaseParser() => MusicReleaseParser.Instance;

    public QualityProfile CreateDefaultQualityProfile() => MusicQualityProfiles.CreateDefaultMusic();

    public MonitoringGranularity MonitoringGranularity => MonitoringGranularity.Item;
}
