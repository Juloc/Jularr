namespace Jularr.Backend.Logic.Acquisition
{
    /// <summary>How sure the media type is that a release is the requested target. It is decided from identity evidence alone; a profile score never changes it.</summary>
    internal enum IdentityConfidence
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
    /// <param name="Value">The data the code refers to, such as the title a release carries.</param>
    internal sealed record ReleaseIdentityEvidence(IdentityConfidence Confidence, string Code, string? Value = null)
    {
        public static ReleaseIdentityEvidence Exact(string code, string? value = null) => new(IdentityConfidence.Exact, code, value);

        public static ReleaseIdentityEvidence Strong(string code, string? value = null) => new(IdentityConfidence.Strong, code, value);

        public static ReleaseIdentityEvidence Ambiguous(string code, string? value = null) => new(IdentityConfidence.Ambiguous, code, value);

        public static ReleaseIdentityEvidence Conflict(string code, string? value = null) => new(IdentityConfidence.Conflict, code, value);
    }

    /// <summary>
    /// What a candidate would satisfy among the wanted targets of the request. The utility favours covering more wanted units and charges
    /// for units nobody asked for, so a season pack wins when most of a season is missing and a single episode wins when only that one is.
    /// </summary>
    internal sealed record SelectionCoverage(int WantedCovered, int WantedTotal, int Unwanted)
    {
        public static SelectionCoverage Single { get; } = new(1, 1, 0);

        /// <summary>The storage or duplicate-download cost of taking the candidate, in utility points (an oversized release for a small target).</summary>
        public int Cost { get; init; }

        public int Utility => WantedCovered * 100 - Unwanted * 10 - Cost;
    }

    /// <summary>Observed outcomes of a release group or indexer. It only ever breaks a tie late and moves by a bounded amount.</summary>
    internal sealed record ReleaseReliability(int Samples, int Successes)
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
    internal sealed record SelectionCandidate(
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

        /// <summary>The languages the release carries (audio languages, the language of a book or chapter); empty when it states none.</summary>
        public IReadOnlyList<string> Languages { get; init; } = [];
    }

    internal enum SelectionDecision
    {
        /// <summary>Acceptable now.</summary>
        Eligible,

        /// <summary>Only an owner may take it (ambiguous identity).</summary>
        ManualReview,

        Rejected
    }

    internal enum SelectionReasonKind
    {
        Safety,
        Identity,
        Profile,
        Quality
    }

    internal sealed record SelectionReason(SelectionReasonKind Kind, string Code, string? Value = null);

    internal sealed record CandidateEvaluation(
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
    internal enum SelectionOutcome
    {
        NoCandidates,
        IdentityInvalid,
        ProfileRejected,
        ManualReviewOnly,
        Usable
    }

    internal sealed record SelectionResult(
        IReadOnlyList<CandidateEvaluation> Ranked,
        CandidateEvaluation? Winner,
        ReleaseReason? WinnerReason,
        SelectionOutcome Outcome);

    /// <summary>
    /// The one release-selection engine of automatic acquisition, Manual Search and profile tests. It decides in a fixed hierarchy:
    /// hard safety, then identity, then the profile gates (Require, Reject, allowed quality, size), then orders what is left by quality tier,
    /// preference, identity strength, coverage, bounded reliability, indexer priority and a stable final tiebreak. A preference can never repair
    /// a wrong identity, a violated gate or an unsafe candidate, and the response order of the indexers is never a tiebreak, so the same
    /// candidates and profile always pick the same winner.
    /// </summary>
    internal static class ReleaseSelectionEngine
    {
        public static SelectionResult Select(QualityProfile profile, IReadOnlyList<SelectionCandidate> candidates)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(candidates);

            var ranked = candidates
                .Select(candidate => Evaluate(profile, candidate))
                .OrderBy(evaluation => evaluation.Decision switch { SelectionDecision.Eligible => 0, SelectionDecision.ManualReview => 1, _ => 2 })
                .ThenBy(evaluation => evaluation.QualityRank)
                .ThenBy(evaluation => LanguageRank(profile, evaluation.Candidate))
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
            return new SelectionResult(ranked, winner, winner is null ? null : WinnerReason(profile, winner, runnerUp), OutcomeOf(ranked));
        }

        // The best position any language of the candidate has in the profile's language order; a candidate that states no language sits after the listed ones
        // and one that only has other languages after that. Without a language order the layer decides nothing.
        private static int LanguageRank(QualityProfile profile, SelectionCandidate candidate)
        {
            if (profile.LanguageOrder.Length == 0)
            {
                return 0;
            }

            var best = candidate.Languages
                .Select(language => Array.FindIndex(profile.LanguageOrder, wanted => language.StartsWith(wanted, StringComparison.OrdinalIgnoreCase) || wanted.StartsWith(language, StringComparison.OrdinalIgnoreCase)))
                .Where(index => index >= 0)
                .DefaultIfEmpty(-1)
                .Min();
            return best >= 0 ? best : candidate.Languages.Count == 0 ? profile.LanguageOrder.Length : profile.LanguageOrder.Length + 1;
        }

        private static CandidateEvaluation Evaluate(QualityProfile profile, SelectionCandidate candidate)
        {
            var reasons = new List<SelectionReason>();
            var reliability = candidate.Reliability?.Points ?? 0;
            if (candidate.SafetyRejection is { } safety)
            {
                reasons.Add(new SelectionReason(SelectionReasonKind.Safety, safety));
                return new CandidateEvaluation(candidate, SelectionDecision.Rejected, null, reliability, reasons);
            }

            if (candidate.Parsed is null)
            {
                reasons.Add(new SelectionReason(SelectionReasonKind.Safety, "Unparseable"));
                return new CandidateEvaluation(candidate, SelectionDecision.Rejected, null, reliability, reasons);
            }

            // The score is computed for every parsed candidate so Manual Search can show it, but it is only consulted after identity.
            var score = ReleaseScorer.Score(profile, new ReleaseCandidate(candidate.Parsed, candidate.SizeBytes, candidate.Indexer, candidate.Id));
            reasons.Add(new SelectionReason(SelectionReasonKind.Identity, candidate.Identity.Code, candidate.Identity.Value));
            if (candidate.Identity.Confidence == IdentityConfidence.Conflict)
            {
                return new CandidateEvaluation(candidate, SelectionDecision.Rejected, score, reliability, reasons);
            }

            foreach (var rejection in score.RejectionReasons)
            {
                reasons.Add(new SelectionReason(rejection.Code == "QualityNotAllowed" ? SelectionReasonKind.Quality : SelectionReasonKind.Profile, rejection.Code, rejection.Value));
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
        private static ReleaseReason WinnerReason(QualityProfile profile, CandidateEvaluation winner, CandidateEvaluation? runnerUp)
        {
            if (runnerUp is null)
            {
                return new ReleaseReason("OnlyCandidate");
            }

            if (winner.QualityRank != runnerUp.QualityRank)
            {
                return new ReleaseReason("HigherQuality", winner.Score!.QualityKey);
            }

            if (LanguageRank(profile, winner.Candidate) != LanguageRank(profile, runnerUp.Candidate))
            {
                return new ReleaseReason("PreferredLanguage");
            }

            if (winner.PreferenceScore != runnerUp.PreferenceScore)
            {
                return new ReleaseReason("HigherPreference", winner.PreferenceScore.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (winner.Candidate.Coverage.Utility != runnerUp.Candidate.Coverage.Utility)
            {
                return new ReleaseReason("BetterCoverage", winner.Candidate.Coverage.WantedCovered.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (winner.ReliabilityPoints != runnerUp.ReliabilityPoints)
            {
                return new ReleaseReason("BetterReliability");
            }

            return winner.Candidate.IndexerPriority != runnerUp.Candidate.IndexerPriority
                ? new ReleaseReason("IndexerPriority")
                : winner.Candidate.PublishedAt != runnerUp.Candidate.PublishedAt ? new ReleaseReason("PostedLater") : new ReleaseReason("StableOrder");
        }
    }
}
