using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Release;

namespace Jularr.Web.Features.Acquisition.Quality;

public enum ReleaseRuleField
{
    RawTitle,
    ReleaseGroup,
    Source,
    Resolution,
    VideoCodec,
    BitDepth,
    HdrFormat,
    AudioCodec,
    AudioLanguage,
    SubtitleLanguage,
    DualAudio,
    MultiAudio,
    Proper,
    Repack
}

public enum ReleaseRuleMatch
{
    Equals,
    Contains,
    Regex
}

/// <summary>
/// What a matching score rule does. Require and Reject are gates that run before any score and can never be outweighed by one;
/// Prefer and Avoid move the preference score; Info only explains.
/// </summary>
public enum ReleaseRuleEffect
{
    Prefer,
    Avoid,
    Require,
    Reject,
    Info
}

public sealed record ReleaseScoreRule(
    string Name,
    ReleaseRuleField Field,
    ReleaseRuleMatch Match,
    string Value,
    int Score)
{
    /// <summary>The effect of the rule; a rule without one prefers when its score is not negative and avoids otherwise.</summary>
    public ReleaseRuleEffect? Effect { get; init; }

    public ReleaseRuleEffect EffectiveEffect => Effect ?? (Score >= 0 ? ReleaseRuleEffect.Prefer : ReleaseRuleEffect.Avoid);
}

/// <summary>
/// One explicit step of the fallback ladder: once a target has been wanted for <see cref="AfterMinutes"/>, the qualities in
/// <see cref="AddedQualities"/> become allowed as well. Identity, safety and the permanent Require/Reject rules never relax; a
/// candidate taken from a later tier is only temporary and the target stays wanted for an upgrade.
/// </summary>
public sealed record FallbackTier(int AfterMinutes, string[] AddedQualities);

public sealed record QualityProfile(
    string Id,
    string Name,
    string[] AllowedQualities,
    string[] QualityOrder,
    bool UpgradeAllowed,
    string? UpgradeCutoffQuality,
    int MinimumScore,
    long? MinimumSizeBytes,
    long? MaximumSizeBytes,
    string[] MustContain,
    string[] MustNotContain,
    string[] RequiredRegex,
    string[] RejectedRegex,
    ReleaseScoreRule[] ScoreRules)
{
    /// <summary>The fallback ladder after tier 0 (the allowed qualities above); tiers are ordered by their wait.</summary>
    public FallbackTier[] FallbackTiers { get; init; } = [];

    /// <summary>The least preference-score gain that makes a candidate of the same quality an upgrade, so tiny differences never churn files.</summary>
    public int UpgradeMinimumScoreDelta { get; init; } = 1;

    /// <summary>The least number of quality steps a candidate must be better by to be an upgrade; 1 means any better quality.</summary>
    public int UpgradeMinimumQualitySteps { get; init; } = 1;

    /// <summary>Upgrades stop once the current file reaches this preference score; null keeps upgrading until the quality cutoff.</summary>
    public int? UpgradeUntilScore { get; init; }

    /// <summary>Whether a candidate whose identity is only ambiguous may be taken automatically; by default it waits for manual review.</summary>
    public bool AllowAmbiguousIdentity { get; init; }
}

// Media-type-agnostic quality-profile state. Profiles are shared shapes; each media type has a
// default profile (KindDefaults, keyed by AcquisitionAccessNames.Kind) and any work can override
// its profile (WorkAssignments, keyed by the work GUID). A work GUID already implies its media
// type, so assignments need no kind dimension.
public sealed record QualityProfileState(
    int Version,
    QualityProfile[] Profiles,
    Dictionary<string, string> KindDefaults,
    Dictionary<string, string> WorkAssignments)
{
    public const int CurrentVersion = 2;

    public string? DefaultProfileIdFor(MediaAcquisitionKind kind) =>
        KindDefaults.TryGetValue(AcquisitionAccessNames.Kind(kind), out var id) ? id : null;

    public string? ResolveProfileId(MediaAcquisitionKind kind, Guid? workId) =>
        workId is Guid id && WorkAssignments.TryGetValue(id.ToString("D"), out var assigned)
            ? assigned
            : DefaultProfileIdFor(kind);
}

public sealed record ReleaseCandidate(
    ReleaseInfo Release,
    long? SizeBytes = null,
    string? Indexer = null,
    string? SourceId = null);

public sealed record ReleaseScoreResult(
    ReleaseCandidate Candidate,
    bool Accepted,
    int Score,
    string QualityKey,
    int QualityRank,
    IReadOnlyList<string> RejectionReasons,
    IReadOnlyList<string> ScoreReasons)
{
    /// <summary>Rules that matched with the Info effect: shown in explanations, never part of the score or a gate.</summary>
    public IReadOnlyList<string> InfoReasons { get; init; } = [];
}

public static class ReleaseQuality
{
    public static string GetKey(ReleaseInfo release)
    {
        if (!string.IsNullOrWhiteSpace(release.DocumentFormat))
        {
            return release.DocumentFormat.Trim().ToUpperInvariant();
        }

        var source = release.Source switch
        {
            ReleaseSource.WebDl or ReleaseSource.WebRip => "WEB",
            ReleaseSource.BluRay or ReleaseSource.BluRayRip => "BLURAY",
            ReleaseSource.Hdtv => "HDTV",
            _ => "UNKNOWN"
        };

        var resolution = release.Resolution is > 0
            ? $"{release.Resolution}p"
            : "UNKNOWN";

        return $"{source}-{resolution}";
    }
}

// The anime media type's default quality profile. Anime is one registration on the shared engine
// (see AnimeAcquisitionRegistration); this factory is its seed profile.
public static class AnimeQualityProfiles
{
    public const string DefaultAnime1080pId = "anime-1080p";

    public static QualityProfile CreateDefaultAnime1080p() =>
        new(
            DefaultAnime1080pId,
            "Anime 1080p",
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
            UpgradeCutoffQuality: "BLURAY-1080p",
            MinimumScore: 0,
            MinimumSizeBytes: null,
            MaximumSizeBytes: null,
            MustContain: [],
            MustNotContain: [],
            RequiredRegex: [],
            RejectedRegex: [],
            ScoreRules:
            [
                new(
                    "Prefer Proper",
                    ReleaseRuleField.Proper,
                    ReleaseRuleMatch.Equals,
                    "true",
                    5),
                new(
                    "Prefer Repack",
                    ReleaseRuleField.Repack,
                    ReleaseRuleMatch.Equals,
                    "true",
                    5)
            ]);
}
