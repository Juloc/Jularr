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

            var evaluations = candidates.Select(candidate => Evaluate(profile, candidate)).ToArray();

            // A complete book outranks a partial one. For books the requested language then wins ahead of
            // source priority; other media retain source priority. Profile rules still decide eligibility.
            var ranked = evaluations
                .OrderBy(evaluation => profile.IsBook && evaluation.Candidate.IsPart ? 1 : 0)
                .ThenBy(evaluation => profile.IsBook ? LanguageTier(profile, evaluation.Candidate.Languages) : 0)
                .ThenBy(evaluation => profile.IsBook && profile.LanguageFallback == LanguageFallbackType.Any
                    && LanguageTier(profile, evaluation.Candidate.Languages) >= 2 ? evaluation.QualityRank : 0)
                .ThenBy(evaluation => evaluation.Candidate.IndexerPriority)
                .ThenBy(evaluation => evaluation.Candidate.IsPart ? 1 : 0)
                .ThenBy(evaluation => evaluation.Decision switch { SelectionDecision.Eligible => 0, SelectionDecision.ManualReview => 1, _ => 2 })
                .ThenBy(evaluation => evaluation.QualityRank)
                .ThenByDescending(evaluation => evaluation.Score?.FormatScore ?? 0)
                .ThenBy(evaluation => UpgradePolicy.LanguageRank(profile, evaluation.Candidate.Languages))
                .ThenByDescending(evaluation => evaluation.Candidate.ContextScore)
                .ThenBy(evaluation => evaluation.Candidate.Identity.Confidence)
                .ThenByDescending(evaluation => evaluation.Candidate.Coverage.Utility)
                .ThenByDescending(evaluation => evaluation.ReliabilityPoints)
                .ThenByDescending(evaluation => evaluation.Candidate.PublishedAt)
                .ThenBy(evaluation => PreferredDistance(profile, evaluation))
                .ThenBy(evaluation => evaluation.Candidate.Id, StringComparer.Ordinal)
                .ToArray();

            var winner = ranked.FirstOrDefault(evaluation => evaluation.IsSelectable);
            var runnerUp = winner is null ? null : ranked.SkipWhile(evaluation => !ReferenceEquals(evaluation, winner)).Skip(1).FirstOrDefault(evaluation => evaluation.IsSelectable);
            return new SelectionResult(ranked, winner, winner is null ? null : WinnerReason(profile, winner, runnerUp), OutcomeOf(ranked));
        }

        // Prefer requested, original, other known languages and finally unknown; source priority breaks ties.
        private static int LanguageTier(QualityProfile profile, IReadOnlyList<string> languages)
        {
            if (UpgradePolicy.IsRequested(profile, languages)) return 0;
            if (profile.LanguageFallback == LanguageFallbackType.Any)
            {
                if (UpgradePolicy.IsOriginal(profile, languages)) return 1;
                return languages.Count > 0 ? 2 : 3;
            }

            return UpgradePolicy.IsFallback(profile, languages) ? 1 : 4;
        }

        // Closer to the preferred size wins a tie, as in Sonarr.
        private static decimal PreferredDistance(QualityProfile profile, CandidateEvaluation evaluation) =>
            profile.RuntimeMinutes is { } minutes && evaluation.Candidate.SizeBytes is { } bytes && evaluation.Score?.QualityKey is { } quality
            && profile.Sizes.TryGetValue(quality, out var size) && size.Preferred is { } preferred
                ? Math.Abs(bytes / 1_048_576m - preferred * minutes)
                : decimal.MaxValue;

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
            var score = ReleaseScorer.Score(profile, new ReleaseCandidate(candidate.Parsed, candidate.SizeBytes, candidate.Indexer, candidate.Id)
                { IndexerId = candidate.IndexerId, IndexerFlags = candidate.IndexerFlags, Languages = candidate.Languages.Count > 0 ? candidate.Languages : null });
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

            // An unrequested language needs a deliberate manual choice; fallback is not an unrestricted download.
            if (profile.IsBook && (profile.LanguageOrder.Length > 0 || profile.LanguageFallback == LanguageFallbackType.None)
                && (candidate.Identity.Confidence != IdentityConfidence.Ambiguous || profile.AllowAmbiguousIdentity)
                && !UpgradePolicy.IsRequested(profile, candidate.Languages) && !UpgradePolicy.IsFallback(profile, candidate.Languages))
            {
                reasons.Add(new SelectionReason(SelectionReasonKind.Profile, candidate.Languages.Count == 0 ? "LanguageUnknown" : "LanguageNotAllowed"));
                return new CandidateEvaluation(candidate, SelectionDecision.ManualReview, score, reliability, reasons);
            }

            // What is no upgrade of the present quality is only taken by hand, as another version.
            if (candidate.InstalledQuality is { } installed && !UpgradePolicy.IsUpgrade(profile, installed, score.QualityKey, false,
                candidate.InstalledFormatScore, score.FormatScore, candidate.InstalledLanguages, candidate.Languages))
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

            if (profile.IsBook && LanguageTier(profile, winner.Candidate.Languages) != LanguageTier(profile, runnerUp.Candidate.Languages))
            {
                return new ReleaseReason("PreferredLanguage");
            }

            if (profile.IsBook && profile.LanguageFallback == LanguageFallbackType.Any
                && LanguageTier(profile, winner.Candidate.Languages) >= 2 && LanguageTier(profile, runnerUp.Candidate.Languages) >= 2
                && winner.QualityRank != runnerUp.QualityRank)
            {
                return new ReleaseReason("HigherQuality", winner.Score!.QualityKey);
            }

            if (winner.Candidate.IndexerPriority != runnerUp.Candidate.IndexerPriority)
            {
                return new ReleaseReason("IndexerPriority");
            }

            if (winner.QualityRank != runnerUp.QualityRank)
            {
                return new ReleaseReason("HigherQuality", winner.Score!.QualityKey);
            }

            if (winner.Score?.FormatScore != runnerUp.Score?.FormatScore)
            {
                return new ReleaseReason("HigherPreference", winner.Score!.FormatScore.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (UpgradePolicy.LanguageRank(profile, winner.Candidate.Languages) != UpgradePolicy.LanguageRank(profile, runnerUp.Candidate.Languages))
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

            return winner.Candidate.PublishedAt != runnerUp.Candidate.PublishedAt ? new ReleaseReason("PostedLater") : new ReleaseReason("StableOrder");
        }
    }
}
