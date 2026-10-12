using Jularr.Backend.Data.Enums.Acquisition;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    internal sealed class ReleaseScoreRule
    {
        public ReleaseScoreRule(string name, ReleaseRuleField field, ReleaseRuleMatch match, string value, int score)
        {
            Name = name;
            Field = field;
            Match = match;
            Value = value;
            Score = score;
        }

        public string Name { get; }
        public ReleaseRuleField Field { get; }
        public ReleaseRuleMatch Match { get; }
        public string Value { get; }
        public int Score { get; }

        // The effect of the rule.
        public ReleaseRuleEffect? Effect { get; init; }

        public ReleaseRuleEffect EffectiveEffect => Effect ?? (Score >= 0 ? ReleaseRuleEffect.Prefer : ReleaseRuleEffect.Avoid);
    }

    // Which indexers an Acquisition Profile may search and which it prefers, by the canonical entry id of the Indexer settings (the profile never copies an indexer's configuration).
    internal sealed class AcquisitionSourcePolicy
    {
        public AcquisitionSourcePolicy(long[] allowedEntryIds, long[] preferredEntryIds)
        {
            AllowedEntryIds = allowedEntryIds;
            PreferredEntryIds = preferredEntryIds;
        }

        public long[] AllowedEntryIds { get; }
        public long[] PreferredEntryIds { get; }
        public static AcquisitionSourcePolicy Unrestricted { get; } = new([], []);

        // Indexer entries that are searched only when the other sources of the search returned no release.
        public long[] FallbackOnlyEntryIds { get; init; } = [];

        public bool IsRestricted => AllowedEntryIds.Length > 0;

        public bool IsDefault => AllowedEntryIds.Length == 0 && PreferredEntryIds.Length == 0 && FallbackOnlyEntryIds.Length == 0;
    }

    internal sealed class QualityProfile
    {
        public QualityProfile(string id, string name, string[] allowedQualities, string[] qualityOrder, bool upgradeAllowed, string? upgradeCutoffQuality, long? minimumSizeBytes, long? maximumSizeBytes, string[] mustContain, string[] mustNotContain, string[] requiredRegex, string[] rejectedRegex, ReleaseScoreRule[] scoreRules)
        {
            Id = id;
            Name = name;
            AllowedQualities = allowedQualities;
            QualityOrder = qualityOrder;
            UpgradeAllowed = upgradeAllowed;
            UpgradeCutoffQuality = upgradeCutoffQuality;
            MinimumSizeBytes = minimumSizeBytes;
            MaximumSizeBytes = maximumSizeBytes;
            MustContain = mustContain;
            MustNotContain = mustNotContain;
            RequiredRegex = requiredRegex;
            RejectedRegex = rejectedRegex;
            ScoreRules = scoreRules;
        }

        public string Id { get; }
        public string Name { get; }
        public string[] AllowedQualities { get; }
        public string[] QualityOrder { get; }
        public bool UpgradeAllowed { get; }
        public string? UpgradeCutoffQuality { get; }
        public long? MinimumSizeBytes { get; }
        public long? MaximumSizeBytes { get; }
        public string[] MustContain { get; }
        public string[] MustNotContain { get; }
        public string[] RequiredRegex { get; }
        public string[] RejectedRegex { get; }
        public ReleaseScoreRule[] ScoreRules { get; }

        // The least number of quality steps a candidate must be better by to be an upgrade.
        public int UpgradeMinimumQualitySteps { get; init; } = 1;

        // Whether a candidate whose identity is only ambiguous may be taken automatically.
        public bool AllowAmbiguousIdentity { get; init; }

        // The languages the profile wants, best first.
        public string[] LanguageOrder { get; init; } = [];

        // The indexers this profile may search and prefers.
        public AcquisitionSourcePolicy SourcePolicy { get; init; } = AcquisitionSourcePolicy.Unrestricted;
    }

    internal sealed class ReleaseCandidate
    {
        public ReleaseCandidate(ReleaseInfo release, long? sizeBytes = null, string? indexer = null, string? sourceId = null)
        {
            Release = release;
            SizeBytes = sizeBytes;
            Indexer = indexer;
            SourceId = sourceId;
        }

        public ReleaseInfo Release { get; }
        public long? SizeBytes { get; }
        public string? Indexer { get; }
        public string? SourceId { get; }
    }

    internal sealed class ReleaseScoreResult
    {
        public ReleaseScoreResult(ReleaseCandidate candidate, bool accepted, int score, string qualityKey, int qualityRank, IReadOnlyList<ReleaseReason> rejectionReasons, IReadOnlyList<ReleaseReason> scoreReasons)
        {
            Candidate = candidate;
            Accepted = accepted;
            Score = score;
            QualityKey = qualityKey;
            QualityRank = qualityRank;
            RejectionReasons = rejectionReasons;
            ScoreReasons = scoreReasons;
        }

        public ReleaseCandidate Candidate { get; }
        public bool Accepted { get; }
        public int Score { get; }
        public string QualityKey { get; }
        public int QualityRank { get; }
        public IReadOnlyList<ReleaseReason> RejectionReasons { get; }
        public IReadOnlyList<ReleaseReason> ScoreReasons { get; }

        // Rules that matched with the Info effect.
        public IReadOnlyList<string> InfoReasons { get; init; } = [];
    }

    internal sealed class UpgradeAssessment
    {
        public UpgradeAssessment(UpgradeState state, ReleaseReason reason)
        {
            State = state;
            Reason = reason;
        }

        public UpgradeState State { get; }
        public ReleaseReason Reason { get; }

        public bool IsUpgradable => State == UpgradeState.Upgradable;
    }

    // The stored JSON definition of an acquisition profile.
    internal sealed class AcquisitionProfileDefinition
    {
        public AcquisitionProfileDefinition(string[] allowedQualities, string[] qualityOrder, bool upgradeAllowed, string? upgradeCutoffQuality, long? minimumSizeBytes, long? maximumSizeBytes,
            string[] mustContain, string[] mustNotContain, string[] requiredRegex, string[] rejectedRegex, ReleaseScoreRule[] scoreRules)
        {
            AllowedQualities = allowedQualities;
            QualityOrder = qualityOrder;
            UpgradeAllowed = upgradeAllowed;
            UpgradeCutoffQuality = upgradeCutoffQuality;
            MinimumSizeBytes = minimumSizeBytes;
            MaximumSizeBytes = maximumSizeBytes;
            MustContain = mustContain;
            MustNotContain = mustNotContain;
            RequiredRegex = requiredRegex;
            RejectedRegex = rejectedRegex;
            ScoreRules = scoreRules;
        }

        public string[] AllowedQualities { get; }
        public string[] QualityOrder { get; }
        public bool UpgradeAllowed { get; }
        public string? UpgradeCutoffQuality { get; }
        public long? MinimumSizeBytes { get; }
        public long? MaximumSizeBytes { get; }
        public string[] MustContain { get; }
        public string[] MustNotContain { get; }
        public string[] RequiredRegex { get; }
        public string[] RejectedRegex { get; }
        public ReleaseScoreRule[] ScoreRules { get; }
    }
}
