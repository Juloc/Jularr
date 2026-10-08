using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Selection;

namespace Jularr.Web.Features.Acquisition.Core;

// Judges releases with the media adapter's judge and orders them with the one shared selection engine.
public sealed class ReleaseRanker(TimeProvider clock, ReleaseReliabilityService? reliability = null)
{
    public async Task<(SelectionResult Selection, IReadOnlyList<ReleaseEvaluation<TMatch>> Evaluations)> RankAsync<TMatch>(
        IReadOnlyList<ProwlarrReleaseCandidate> releases,
        Func<ProwlarrReleaseCandidate, ReleaseJudgement<TMatch>> judge,
        QualityProfile profile,
        DateTime wantedSinceUtc,
        CancellationToken cancellationToken,
        Dictionary<string, ReleaseJudgement<TMatch>>? judgements = null)
    {
        judgements ??= new Dictionary<string, ReleaseJudgement<TMatch>>(StringComparer.Ordinal);
        var distinct = releases.GroupBy(release => release.Identity, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var wantedSince = new DateTimeOffset(DateTime.SpecifyKind(wantedSinceUtc, DateTimeKind.Utc));
        var lookup = reliability is null ? null : await reliability.LoadAsync(cancellationToken);
        var selection = ReleaseSelectionEngine.Select(
            profile,
            new SelectionContext(clock.GetUtcNow(), wantedSince),
            [.. distinct.Select(pair =>
            {
                var judgement = Judged(judgements, judge, pair.Value);
                return new SelectionCandidate(pair.Key, judgement.Parsed, pair.Value.SizeBytes, pair.Value.Indexer, pair.Value.Sources.FirstOrDefault()?.Priority ?? 0, pair.Value.PublishedAt, judgement.Evidence, judgement.Coverage)
                {
                    SafetyRejection = judgement.SafetyRejection,
                    ContextScore = judgement.ContextScore
                };
            })],
            lookup);
        var evaluations = selection.Ranked
            .Select(ranked => new ReleaseEvaluation<TMatch>(distinct[ranked.Candidate.Id], judgements[ranked.Candidate.Id].Parsed, judgements[ranked.Candidate.Id].Match, ranked))
            .ToArray();
        return (selection, evaluations);
    }

    internal static ReleaseJudgement<TMatch> Judged<TMatch>(Dictionary<string, ReleaseJudgement<TMatch>> judgements, Func<ProwlarrReleaseCandidate, ReleaseJudgement<TMatch>> judge, ProwlarrReleaseCandidate release)
    {
        if (!judgements.TryGetValue(release.Identity, out var judgement))
        {
            judgements[release.Identity] = judgement = judge(release);
        }

        return judgement;
    }
}
