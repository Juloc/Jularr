using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;

namespace Jularr.Web.Features.Acquisition.Release;

/// <summary>
/// The seed quality profiles for the video media types that share the scene/usenet grammar (Movie #593,
/// TV #594). Both start from the same 1080p ladder as anime; a media type gets its own profile id/name so
/// the owner can tune them independently from the quality-profile settings.
/// </summary>
public static class VideoQualityProfiles
{
    public const string DefaultMovie1080pId = "movie-1080p";
    public const string DefaultTv1080pId = "tv-1080p";

    public static QualityProfile CreateDefaultMovie1080p() => Create1080p(DefaultMovie1080pId, "Movies 1080p");

    public static QualityProfile CreateDefaultTv1080p() => Create1080p(DefaultTv1080pId, "TV 1080p");

    private static QualityProfile Create1080p(string id, string name) =>
        new(
            id,
            name,
            [
                "BLURAY-1080p",
                "WEB-1080p",
                "HDTV-1080p",
                "BLURAY-720p",
                "WEB-720p",
                "HDTV-720p"
            ],
            [
                "BLURAY-1080p",
                "WEB-1080p",
                "HDTV-1080p",
                "BLURAY-720p",
                "WEB-720p",
                "HDTV-720p"
            ],
            UpgradeAllowed: true,
            UpgradeCutoffQuality: "WEB-1080p",
            MinimumScore: 0,
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

/// <summary>Movies as one registration on the shared acquisition engine: scene parser + the 1080p movie profile (#593).</summary>
public sealed class MovieAcquisitionRegistration : IMediaAcquisitionRegistration
{
    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Movie;

    public IReleaseParser CreateReleaseParser() => SceneReleaseParser.Instance;

    public QualityProfile CreateDefaultQualityProfile() => VideoQualityProfiles.CreateDefaultMovie1080p();

    // A movie is one whole-item unit; monitoring tracks the film, not seasons or episodes.
    public Monitoring.MonitoringGranularity MonitoringGranularity => Monitoring.MonitoringGranularity.Item;
}

/// <summary>TV/Series as one registration on the shared acquisition engine: scene parser + the 1080p TV profile (#594).</summary>
public sealed class TvAcquisitionRegistration : IMediaAcquisitionRegistration
{
    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Tv;

    public IReleaseParser CreateReleaseParser() => SceneReleaseParser.Instance;

    public QualityProfile CreateDefaultQualityProfile() => VideoQualityProfiles.CreateDefaultTv1080p();
}
