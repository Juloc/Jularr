using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.ManualSearch;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Operations;

namespace Jularr.Web.Features.Acquisition.Core;

// What a media type concluded about one release before any profile rule is looked at; Match is its own finding (video identity, music evidence).
public sealed record ReleaseJudgement<TMatch>(TMatch Match, ReleaseInfo? Parsed, ReleaseIdentityEvidence Evidence, SelectionCoverage Coverage, string? SafetyRejection)
{
    public int ContextScore { get; init; }
}

// One returned release as the shared selection engine ranked it.
public sealed record ReleaseEvaluation<TMatch>(ProwlarrReleaseCandidate Candidate, ReleaseInfo? Parsed, TMatch Match, CandidateEvaluation Selection)
{
    public ReleaseScoreResult? Score => Selection.Score;

    public bool IsIdentityValid => Selection.Candidate.Identity.Confidence is IdentityConfidence.Exact or IdentityConfidence.Strong;

    public bool IsGrabbable => Selection.IsSelectable && Candidate.InternalDownloadUri is not null;

    public bool IsManuallyGrabbable => (IsGrabbable || Selection.Decision == SelectionDecision.ManualReview) && Candidate.InternalDownloadUri is not null;
}

public sealed record SearchEvaluation<TMatch>(QualityProfile Profile, AcquisitionSearchResult Search, IReadOnlyList<ReleaseEvaluation<TMatch>> Releases, SelectionResult Selection)
{
    public IReadOnlyList<ReleaseEvaluation<TMatch>> Grabbable => [.. Releases.Where(release => release.IsGrabbable)];
}

// What a media adapter hands the core to search one target: the query facts and its identity judge.
public sealed record MediaSearchPlan<TMatch>(SearchIntent Intent, Func<ProwlarrReleaseCandidate, ReleaseJudgement<TMatch>> Judge);

// How the winning release is queued in the download client and linked to its library target.
public sealed record GrabTarget(string OperationKind, string OperationTitle, string DisplayTitle, MediaAcquisitionKind Kind, string MediaTargetKey, OperationPriority Priority = OperationPriority.Normal);

// The one search, rank and grab path every media type runs; adapters only supply the plan and the grab target.
public sealed class AcquisitionCore(IndexerSearchCoordinator indexers, ReleaseRequestTracker tracker, DownloadClientSubmissionService downloads, ReleaseReliabilityService? reliability = null)
{
    private readonly ReleaseRanker ranker = new(reliability);

    public async Task<SearchEvaluation<TMatch>> SearchAsync<TMatch>(MediaSearchPlan<TMatch> plan, QualityProfile profile, SearchOptions options, CancellationToken cancellationToken)
    {
        var judgements = new Dictionary<string, ReleaseJudgement<TMatch>>(StringComparer.Ordinal);
        var search = await indexers.SearchAsync(
            plan.Intent,
            options.WithSourcePolicy(profile.SourcePolicy) with { UsableCount = releases => releases.Count(release => ReleaseRanker.Judged(judgements, plan.Judge, release) is { SafetyRejection: null, Evidence.Confidence: IdentityConfidence.Exact or IdentityConfidence.Strong }) },
            cancellationToken);
        var (selection, evaluations) = await ranker.RankAsync(search.Releases, plan.Judge, profile, cancellationToken, judgements);
        return new SearchEvaluation<TMatch>(profile, search, evaluations, selection);
    }

    // Runs the tracker lifecycle over the releases (best first) and submits the first untried one; Manual Search passes the one the owner chose.
    public async Task<AcquisitionExecution> GrabAsync<TMatch, TPayload>(
        AcquisitionRequest request,
        TPayload payload,
        IReadOnlyList<ReleaseEvaluation<TMatch>> releases,
        string noReleaseReason,
        GrabTarget target,
        CancellationToken cancellationToken,
        ManualGrabProgress? progress = null,
        bool searchUnavailable = false)
        where TPayload : ReleaseRequestPayload
    {
        var candidates = releases.Select(release => new ReleaseRequestCandidate(release.Candidate.Identity, release.Candidate.Title, release.Candidate.InternalDownloadUri!, release.Candidate.Indexer, release.Candidate.ParsedRelease.ReleaseGroup)).ToArray();
        var byIdentity = releases.ToDictionary(release => release.Candidate.Identity, release => release.Candidate, StringComparer.Ordinal);
        return await tracker.ContinueAsync(
            request,
            payload,
            candidates,
            noReleaseReason,
            async release =>
            {
                progress?.SubmitStarted = true;

                // A release several indexers returned is one candidate with several sources: the next source is offered before the release counts as failed.
                var sources = byIdentity[release.Identity].Sources.Select(source => source.DownloadUri).OfType<Uri>().Distinct().ToArray();
                var outcome = await downloads.SubmitFirstAcceptedAsync(
                    sources.Length == 0 ? [release.DownloadUri] : sources,
                    uri => new DownloadSubmissionSpec(target.OperationKind, target.OperationTitle, target.DisplayTitle, request.RequestedByProfileId, uri, release.Title, target.Kind, MediaTargetKey: target.MediaTargetKey, Priority: target.Priority, ReleaseSource: release.Source, ReleaseGroup: release.ReleaseGroup),
                    cancellationToken);
                if (outcome.Accepted && progress is not null)
                {
                    progress.Accepted = true;
                    progress.OperationId = outcome.OperationId;
                }

                return new ReleaseRequestSubmission(outcome.Accepted, outcome.OperationId, outcome.Message);
            },
            cancellationToken,
            searchUnavailable);
    }
}
