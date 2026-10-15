using Jularr.Backend.Data.Enums.Acquisition;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    internal sealed class ReleaseIdentityEvidence
    {
        public ReleaseIdentityEvidence(IdentityConfidence confidence, string code, string? value = null)
        {
            Confidence = confidence;
            Code = code;
            Value = value;
        }

        public IdentityConfidence Confidence { get; }
        public string Code { get; }
        public string? Value { get; }

        public static ReleaseIdentityEvidence Exact(string code, string? value = null) => new(IdentityConfidence.Exact, code, value);

        public static ReleaseIdentityEvidence Strong(string code, string? value = null) => new(IdentityConfidence.Strong, code, value);

        public static ReleaseIdentityEvidence Ambiguous(string code, string? value = null) => new(IdentityConfidence.Ambiguous, code, value);

        public static ReleaseIdentityEvidence Conflict(string code, string? value = null) => new(IdentityConfidence.Conflict, code, value);
    }

    // What a candidate would satisfy among the wanted targets of the request.
    internal sealed class SelectionCoverage
    {
        public SelectionCoverage(int wantedCovered, int wantedTotal, int unwanted)
        {
            WantedCovered = wantedCovered;
            WantedTotal = wantedTotal;
            Unwanted = unwanted;
        }

        public int WantedCovered { get; }
        public int WantedTotal { get; }
        public int Unwanted { get; }
        public static SelectionCoverage Single { get; } = new(1, 1, 0);

        // The storage or duplicate-download cost of taking the candidate, in utility points (an oversized release for a small target).
        public int Cost { get; init; }

        public int Utility => WantedCovered * 100 - Unwanted * 10 - Cost;
    }

    // Observed outcomes of a release group or indexer.
    internal sealed class ReleaseReliability
    {
        public ReleaseReliability(int samples, int successes)
        {
            Samples = samples;
            Successes = successes;
        }

        public int Samples { get; }
        public int Successes { get; }

        public const int MinimumSamples = 5;
        public const int MaximumPoints = 3;

        // The bounded contribution in points.
        public int Points =>
            Samples < MinimumSamples
                ? 0
                : Math.Clamp((int)Math.Round(((double)Successes / Samples - 0.5) * 2 * MaximumPoints), -MaximumPoints, MaximumPoints);
    }

    // One normalized release as selection sees it.
    internal sealed class SelectionCandidate
    {
        public SelectionCandidate(string id, ReleaseInfo? parsed, long? sizeBytes, string? indexer, int indexerPriority, DateTimeOffset? publishedAt, ReleaseIdentityEvidence identity, SelectionCoverage coverage)
        {
            Id = id;
            Parsed = parsed;
            SizeBytes = sizeBytes;
            Indexer = indexer;
            IndexerPriority = indexerPriority;
            PublishedAt = publishedAt;
            Identity = identity;
            Coverage = coverage;
        }

        public string Id { get; }
        public ReleaseInfo? Parsed { get; }
        public long? SizeBytes { get; }
        public string? Indexer { get; }
        public int IndexerPriority { get; }
        public DateTimeOffset? PublishedAt { get; }
        public ReleaseIdentityEvidence Identity { get; }
        public SelectionCoverage Coverage { get; }

        // A hard safety or integrity finding (no download link, not usenet, unparseable, blocklisted).
        public string? SafetyRejection { get; init; }
        public ReleaseReliability? Reliability { get; init; }

        // Preference points the media type resolves from the request rather than from the profile (a preferred language, an exact volume), added to the profile's preference score.
        public int ContextScore { get; init; }

        // The languages the release carries (audio languages, the language of a book or chapter).
        public IReadOnlyList<string> Languages { get; init; } = [];

        // The quality already present; only an upgrade of it can be taken.
        public string? InstalledQuality { get; init; }
    }

    internal sealed class SelectionReason
    {
        public SelectionReason(SelectionReasonKind kind, string code, string? value = null)
        {
            Kind = kind;
            Code = code;
            Value = value;
        }

        public SelectionReasonKind Kind { get; }
        public string Code { get; }
        public string? Value { get; }
    }

    internal sealed class CandidateEvaluation
    {
        public CandidateEvaluation(SelectionCandidate candidate, SelectionDecision decision, ReleaseScoreResult? score, int reliabilityPoints, IReadOnlyList<SelectionReason> reasons)
        {
            Candidate = candidate;
            Decision = decision;
            Score = score;
            ReliabilityPoints = reliabilityPoints;
            Reasons = reasons;
        }

        public SelectionCandidate Candidate { get; }
        public SelectionDecision Decision { get; }
        public ReleaseScoreResult? Score { get; }
        public int ReliabilityPoints { get; }
        public IReadOnlyList<SelectionReason> Reasons { get; }

        public bool IsSelectable => Decision == SelectionDecision.Eligible;

        public int QualityRank => Score?.QualityRank ?? int.MaxValue;

        public int PreferenceScore => (Score?.Score ?? 0) + Candidate.ContextScore;
    }

    internal sealed class SelectionResult
    {
        public SelectionResult(IReadOnlyList<CandidateEvaluation> ranked, CandidateEvaluation? winner, ReleaseReason? winnerReason, SelectionOutcome outcome)
        {
            Ranked = ranked;
            Winner = winner;
            WinnerReason = winnerReason;
            Outcome = outcome;
        }

        public IReadOnlyList<CandidateEvaluation> Ranked { get; }
        public CandidateEvaluation? Winner { get; }
        public ReleaseReason? WinnerReason { get; }
        public SelectionOutcome Outcome { get; }
    }
}
