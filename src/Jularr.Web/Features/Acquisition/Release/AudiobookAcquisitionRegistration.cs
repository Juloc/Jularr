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
                "MP3"
            ],
            [
                "M4B",
                "MP3-320",
                "MP3"
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

/// <summary>Audiobooks as one registration on the shared acquisition engine: scene parser + the audiobook profile (#440).</summary>
public sealed class AudiobookAcquisitionRegistration : IMediaAcquisitionRegistration
{
    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Audiobook;

    public IReleaseParser CreateReleaseParser() => SceneReleaseParser.Instance;

    public QualityProfile CreateDefaultQualityProfile() => AudiobookQualityProfiles.CreateDefaultAudiobook();

    // An audiobook is one whole-item unit; monitoring tracks the book, not seasons or episodes.
    public Monitoring.MonitoringGranularity MonitoringGranularity => Monitoring.MonitoringGranularity.Item;
}
