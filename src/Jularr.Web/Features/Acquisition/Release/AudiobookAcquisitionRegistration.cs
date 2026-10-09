using System.Text.RegularExpressions;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;

namespace Jularr.Web.Features.Acquisition.Release;

/// <summary>
/// The seed quality profile for the audiobook media type (#440). Audiobook quality is about the audio
/// container/bitrate rather than a video resolution ladder: the canonical chaptered M4B is preferred, a
/// high-bitrate MP3 next, then any MP3. A new install gets this default; the owner can tune it from the
/// quality-profile settings like every other media type.
/// </summary>
public static class AudiobookQualityProfiles
{
    public const string DefaultAudiobookId = "audiobook-standard";

    public static QualityProfile CreateDefaultAudiobook() =>
        new(
            DefaultAudiobookId,
            "Audiobooks",
            [
                "M4B",
                "MP3-320",
                "MP3",
                "UNKNOWN-UNKNOWN"
            ],
            [
                "M4B",
                "MP3-320",
                "MP3",
                "UNKNOWN-UNKNOWN"
            ],
            UpgradeAllowed: true,
            UpgradeCutoffQuality: "M4B",
            MinimumSizeBytes: null,
            MaximumSizeBytes: null,
            MustContain: [],
            MustNotContain: [],
            RequiredRegex: [],
            RejectedRegex: [],
            ScoreRules:
            [
                new("Prefer Proper", ReleaseRuleField.Proper, ReleaseRuleMatch.Equals, "true", 5),
                new("Prefer Repack", ReleaseRuleField.Repack, ReleaseRuleMatch.Equals, "true", 5)
            ]);
}

/// <summary>
/// Audiobook release parsing on top of the shared scene parser: the audio container as the quality key (<c>M4B</c>, <c>MP3-320</c>, <c>MP3</c>). A release that names
/// no container has no key and is only a last-resort candidate; the importer validates the real files.
/// </summary>
public sealed partial class AudiobookReleaseParser : IReleaseParser
{
    public static AudiobookReleaseParser Instance { get; } = new();

    public ReleaseInfo Parse(string releaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseName);
        return SceneReleaseParser.Instance.Parse(releaseName) with { DocumentFormat = DetectQuality(releaseName) ?? "UNKNOWN-UNKNOWN" };
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

    public static string? DetectQuality(string value) =>
        M4bRegex().IsMatch(value) ? "M4B" : Mp3Bitrate320Regex().IsMatch(value) ? "MP3-320" : Mp3Regex().IsMatch(value) ? "MP3" : null;

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:M4B|M4A|AAC)(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex M4bRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:320(?:k|kbps|kbit)?|MP3[ ._-]?320)(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Mp3Bitrate320Regex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])MP3(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Mp3Regex();
}

/// <summary>Audiobooks as one registration on the shared acquisition engine: the audiobook parser and profile (#440).</summary>
public sealed class AudiobookAcquisitionRegistration : IMediaAcquisitionRegistration
{
    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Audiobook;

    public IReleaseParser CreateReleaseParser() => AudiobookReleaseParser.Instance;

    public QualityProfile CreateDefaultQualityProfile() => AudiobookQualityProfiles.CreateDefaultAudiobook();

    // An audiobook is one whole-item unit; monitoring tracks the book, not seasons or episodes.
    public Monitoring.MonitoringGranularity MonitoringGranularity => Monitoring.MonitoringGranularity.Item;
}
