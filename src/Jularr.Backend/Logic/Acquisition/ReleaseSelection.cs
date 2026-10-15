using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;

namespace Jularr.Backend.Logic.Acquisition
{
    // The one release-selection engine of automatic and manual acquisition.
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

            // What is no upgrade of the present quality is only taken by hand, as another version.
            if (candidate.InstalledQuality is { } installed && !UpgradePolicy.IsUpgrade(profile, installed, score.QualityKey))
            {
                reasons.Add(new SelectionReason(SelectionReasonKind.Quality, "NotAnUpgrade", installed));
                return new CandidateEvaluation(candidate, SelectionDecision.ManualReview, score, reliability, reasons);
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

        // Why the winner beat the next selectable candidate.
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
