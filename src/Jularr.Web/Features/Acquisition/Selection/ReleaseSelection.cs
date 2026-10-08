using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;

namespace Jularr.Web.Features.Acquisition.Selection;

/// <summary>How sure the media type is that a release is the requested target. It is decided from identity evidence alone; a profile score never changes it.</summary>
public enum IdentityConfidence
{
    /// <summary>A trustworthy id or the exact unit matched.</summary>
    Exact,

    /// <summary>Title and numbering agree without an id.</summary>
    Strong,

    /// <summary>Plausible but not provable (for example absolute versus season numbering); only a profile that allows it takes it automatically.</summary>
    Ambiguous,

    /// <summary>The release is for something else: another title, season, episode, volume or album.</summary>
    Conflict
}

/// <param name="Code">A stable machine code of the finding, such as <c>WrongEpisode</c>.</param>
/// <param name="Detail">One sentence for people.</param>
public sealed record ReleaseIdentityEvidence(IdentityConfidence Confidence, string Code, string Detail)
{
    public static ReleaseIdentityEvidence Exact(string code, string detail) => new(IdentityConfidence.Exact, code, detail);

    public static ReleaseIdentityEvidence Strong(string code, string detail) => new(IdentityConfidence.Strong, code, detail);

    public static ReleaseIdentityEvidence Ambiguous(string code, string detail) => new(IdentityConfidence.Ambiguous, code, detail);

    public static ReleaseIdentityEvidence Conflict(string code, string detail) => new(IdentityConfidence.Conflict, code, detail);
}

/// <summary>
/// What a candidate would satisfy among the wanted targets of the request. The utility favours covering more wanted units and charges
/// for units nobody asked for, so a season pack wins when most of a season is missing and a single episode wins when only that one is.
/// </summary>
public sealed record SelectionCoverage(int WantedCovered, int WantedTotal, int Unwanted)
{
    public static SelectionCoverage Single { get; } = new(1, 1, 0);

    /// <summary>The storage or duplicate-download cost of taking the candidate, in utility points (an oversized release for a small target).</summary>
    public int Cost { get; init; }

    public int Utility => WantedCovered * 100 - Unwanted * 10 - Cost;
}

/// <summary>Observed outcomes of a release group or indexer. It only ever breaks a tie late and moves by a bounded amount.</summary>
public sealed record ReleaseReliability(int Samples, int Successes)
{
    public const int MinimumSamples = 5;
    public const int MaximumPoints = 3;

    /// <summary>The bounded contribution in points; a source with too few samples contributes nothing, so a new group is never punished by chance.</summary>
    public int Points =>
        Samples < MinimumSamples
            ? 0
            : Math.Clamp((int)Math.Round(((double)Successes / Samples - 0.5) * 2 * MaximumPoints), -MaximumPoints, MaximumPoints);
}

/// <summary>One normalized release as selection sees it: parsed facts, where it came from and what the media type concluded about its identity.</summary>
public sealed record SelectionCandidate(
    string Id,
    ReleaseInfo? Parsed,
    long? SizeBytes,
    string? Indexer,
    int IndexerPriority,
    DateTimeOffset? PublishedAt,
    ReleaseIdentityEvidence Identity,
    SelectionCoverage Coverage)
{
    /// <summary>A hard safety or integrity finding (no download link, not usenet, unparseable, blocklisted); it is not a negative preference.</summary>
    public string? SafetyRejection { get; init; }

    public ReleaseReliability? Reliability { get; init; }

    /// <summary>
    /// Preference points the media type resolves from the request rather than from the profile (a preferred language, an exact volume), added to
    /// the profile's preference score. They only order eligible candidates and never repair identity or a gate.
    /// </summary>
    public int ContextScore { get; init; }
}

public enum SelectionDecision
{
    /// <summary>Acceptable now.</summary>
    Eligible,

    /// <summary>Only an owner may take it (ambiguous identity).</summary>
    ManualReview,

    Rejected
}

public enum SelectionReasonKind
{
    Safety,
    Identity,
    Profile,
    Quality
}

public sealed record SelectionReason(SelectionReasonKind Kind, string Code, string Detail);

public sealed record CandidateEvaluation(
    SelectionCandidate Candidate,
    SelectionDecision Decision,
    ReleaseScoreResult? Score,
    int ReliabilityPoints,
    IReadOnlyList<SelectionReason> Reasons)
{
    public bool IsSelectable => Decision == SelectionDecision.Eligible;

    public int QualityRank => Score?.QualityRank ?? int.MaxValue;

    public int PreferenceScore => (Score?.Score ?? 0) + Candidate.ContextScore;
}

/// <summary>What a whole search came to, which drives how Wanted retries: an indexer problem is not "nothing exists", and an identity or profile rejection is not a failure of the indexers.</summary>
public enum SelectionOutcome
{
    NoCandidates,
    IdentityInvalid,
    ProfileRejected,
    ManualReviewOnly,
    Usable
}

public sealed record SelectionResult(
    IReadOnlyList<CandidateEvaluation> Ranked,
    CandidateEvaluation? Winner,
    string? WinnerReason,
    SelectionOutcome Outcome);

/// <summary>
/// The one release-selection engine of automatic acquisition, Manual Search and profile tests. It decides in a fixed hierarchy:
/// hard safety, then identity, then the profile gates (Require, Reject, allowed quality, size), then orders what is left by quality tier,
/// preference, identity strength, coverage, bounded reliability, indexer priority and a stable final tiebreak. A preference can never repair
/// a wrong identity, a violated gate or an unsafe candidate, and the response order of the indexers is never a tiebreak, so the same
/// candidates and profile always pick the same winner.
/// </summary>
public static class ReleaseSelectionEngine
{
    public static SelectionResult Select(QualityProfile profile, IReadOnlyList<SelectionCandidate> candidates, ReleaseReliabilityLookup? reliability = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(candidates);

        var ranked = candidates
            .Select(candidate => Evaluate(profile, candidate, reliability))
            .OrderBy(evaluation => evaluation.Decision switch { SelectionDecision.Eligible => 0, SelectionDecision.ManualReview => 1, _ => 2 })
            .ThenBy(evaluation => evaluation.QualityRank)
            .ThenByDescending(evaluation => evaluation.PreferenceScore)
            .ThenBy(evaluation => evaluation.Candidate.Identity.Confidence)
            .ThenByDescending(evaluation => evaluation.Candidate.Coverage.Utility)
            .ThenByDescending(evaluation => evaluation.ReliabilityPoints)
            .ThenBy(evaluation => evaluation.Candidate.IndexerPriority)
            .ThenByDescending(evaluation => evaluation.Candidate.PublishedAt)
            .ThenBy(evaluation => evaluation.Candidate.Id, StringComparer.Ordinal)
            .ToArray();

        var winner = ranked.FirstOrDefault(evaluation => evaluation.IsSelectable);
        var runnerUp = winner is null ? null : ranked.Skip(1).FirstOrDefault(evaluation => evaluation.IsSelectable);
        return new SelectionResult(ranked, winner, winner is null ? null : WinnerReason(winner, runnerUp), OutcomeOf(ranked));
    }

    private static CandidateEvaluation Evaluate(QualityProfile profile, SelectionCandidate candidate, ReleaseReliabilityLookup? lookup)
    {
        var reasons = new List<SelectionReason>();
        var reliability = (candidate.Reliability ?? lookup?.For(candidate.Indexer, candidate.Parsed?.ReleaseGroup))?.Points ?? 0;
        if (candidate.SafetyRejection is { } safety)
        {
            reasons.Add(new SelectionReason(SelectionReasonKind.Safety, "Safety", safety));
            return new CandidateEvaluation(candidate, SelectionDecision.Rejected, null, reliability, reasons);
        }

        if (candidate.Parsed is null)
        {
            reasons.Add(new SelectionReason(SelectionReasonKind.Safety, "Unparseable", "The release name could not be parsed."));
            return new CandidateEvaluation(candidate, SelectionDecision.Rejected, null, reliability, reasons);
        }

        // The score is computed for every parsed candidate so Manual Search can show it, but it is only consulted after identity.
        var score = ReleaseScorer.Score(profile, new ReleaseCandidate(candidate.Parsed, candidate.SizeBytes, candidate.Indexer, candidate.Id));
        reasons.Add(new SelectionReason(SelectionReasonKind.Identity, candidate.Identity.Code, candidate.Identity.Detail));
        if (candidate.Identity.Confidence == IdentityConfidence.Conflict)
        {
            return new CandidateEvaluation(candidate, SelectionDecision.Rejected, score, reliability, reasons);
        }

        foreach (var rejection in score.RejectionReasons)
        {
            reasons.Add(new SelectionReason(rejection.StartsWith("Quality '", StringComparison.Ordinal) ? SelectionReasonKind.Quality : SelectionReasonKind.Profile, rejection.StartsWith("Quality '", StringComparison.Ordinal) ? "QualityNotAllowed" : "ProfileRejected", rejection));
        }

        if (!score.Accepted)
        {
            return new CandidateEvaluation(candidate, SelectionDecision.Rejected, score, reliability, reasons);
        }

        return candidate.Identity.Confidence == IdentityConfidence.Ambiguous && !profile.AllowAmbiguousIdentity
            ? new CandidateEvaluation(candidate, SelectionDecision.ManualReview, score, reliability, reasons)
            : new CandidateEvaluation(candidate, SelectionDecision.Eligible, score, reliability, reasons);
    }

    private static SelectionOutcome OutcomeOf(IReadOnlyList<CandidateEvaluation> ranked)
    {
        if (ranked.Count == 0)
        {
            return SelectionOutcome.NoCandidates;
        }

        if (ranked.Any(evaluation => evaluation.IsSelectable))
        {
            return SelectionOutcome.Usable;
        }

        if (ranked.Any(evaluation => evaluation.Decision == SelectionDecision.ManualReview))
        {
            return SelectionOutcome.ManualReviewOnly;
        }

        return ranked.Any(evaluation => evaluation.Reasons.Any(reason => reason.Kind is SelectionReasonKind.Profile or SelectionReasonKind.Quality))
            ? SelectionOutcome.ProfileRejected
            : SelectionOutcome.IdentityInvalid;
    }

    /// <summary>Why the winner beat the next selectable candidate: the first step of the hierarchy that differs, in words.</summary>
    private static string WinnerReason(CandidateEvaluation winner, CandidateEvaluation? runnerUp)
    {
        if (runnerUp is null)
        {
            return "The only candidate that passes identity and the profile.";
        }

        if (winner.QualityRank != runnerUp.QualityRank)
        {
            return $"Higher quality: {winner.Score!.QualityKey} before {runnerUp.Score!.QualityKey}.";
        }

        if (winner.PreferenceScore != runnerUp.PreferenceScore)
        {
            return $"Higher preference score: {winner.PreferenceScore} against {runnerUp.PreferenceScore}.";
        }

        if (winner.Candidate.Coverage.Utility != runnerUp.Candidate.Coverage.Utility)
        {
            return $"Covers {winner.Candidate.Coverage.WantedCovered} wanted unit(s) with {winner.Candidate.Coverage.Unwanted} unwanted; the other covers {runnerUp.Candidate.Coverage.WantedCovered} with {runnerUp.Candidate.Coverage.Unwanted}.";
        }

        if (winner.ReliabilityPoints != runnerUp.ReliabilityPoints)
        {
            return "A better track record of its release group or indexer.";
        }

        return winner.Candidate.IndexerPriority != runnerUp.Candidate.IndexerPriority
            ? "Its indexer has the higher priority."
            : winner.Candidate.PublishedAt != runnerUp.Candidate.PublishedAt ? "Posted later." : "Equal in every respect; the stable order decides.";
    }
}
