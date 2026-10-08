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
    Repack,

    /// <summary>The name of the indexer entry the release came from: a source penalty or preference is an ordinary Avoid / Prefer rule on it, and Reject or Require work the same way.</summary>
    Indexer
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
/// Which indexers an Acquisition Profile may search and which it prefers, by the canonical entry id of the Indexer settings (the profile never copies
/// an indexer's configuration). An empty allow list is "every indexer that takes part in this kind of search". A restricted profile is never silently
/// widened: when none of its indexers can be searched the search says so and asks nobody else. Preferred entries only win ties between candidates that are
/// otherwise equal, so a preferred source never makes an unacceptable release acceptable. Fallback-only entries are asked only when every other source of the
/// search returned nothing at all, so a backup indexer never competes with the primary ones and costs no query while they answer.
/// </summary>
public sealed record AcquisitionSourcePolicy(Guid[] AllowedEntryIds, Guid[] PreferredEntryIds)
{
    public static AcquisitionSourcePolicy Unrestricted { get; } = new([], []);

    /// <summary>Indexer entries that are searched only when the other sources of the search returned no release.</summary>
    public Guid[] FallbackOnlyEntryIds { get; init; } = [];

    public bool IsRestricted => AllowedEntryIds.Length > 0;

    public bool IsDefault => AllowedEntryIds.Length == 0 && PreferredEntryIds.Length == 0 && FallbackOnlyEntryIds.Length == 0;
}

public sealed record QualityProfile(
    string Id,
    string Name,
    string[] AllowedQualities,
    string[] QualityOrder,
    bool UpgradeAllowed,
    string? UpgradeCutoffQuality,
    long? MinimumSizeBytes,
    long? MaximumSizeBytes,
    string[] MustContain,
    string[] MustNotContain,
    string[] RequiredRegex,
    string[] RejectedRegex,
    ReleaseScoreRule[] ScoreRules)
{
    /// <summary>The least number of quality steps a candidate must be better by to be an upgrade; 1 means any better quality.</summary>
    public int UpgradeMinimumQualitySteps { get; init; } = 1;

    /// <summary>Whether a candidate whose identity is only ambiguous may be taken automatically; by default it waits for manual review.</summary>
    public bool AllowAmbiguousIdentity { get; init; }

    /// <summary>The indexers this profile may search and prefers; the same policy applies to Automatic and Manual Search of every media type the profile serves.</summary>
    public AcquisitionSourcePolicy SourcePolicy { get; init; } = AcquisitionSourcePolicy.Unrestricted;
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
    public const int CurrentVersion = 3;

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
