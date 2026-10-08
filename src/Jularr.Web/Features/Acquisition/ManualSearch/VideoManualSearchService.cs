using Jularr.Web.Features.Acquisition.Core;
using System.Text.RegularExpressions;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Instance;

namespace Jularr.Web.Features.Acquisition.ManualSearch;

/// <summary>
/// Admin Manual Search for one Movie or TV request. Candidates come from the same indexer coordinator, parser and scorer as
/// automatic acquisition (<see cref="VideoAcquisitionEngine"/>); a selected candidate is submitted through the same grab path and
/// recorded on the request like an automatic grab. The browser only sends an opaque release identity: every grab runs a fresh
/// search and re-validates the identity, so Manual Search is never an arbitrary download-URL endpoint. Filtering and sorting read the
/// short-lived candidate evidence of the search executor, so they do not hit the indexers again; the evidence holds no profile score.
/// </summary>
public sealed partial class VideoManualSearchService(
    VideoAcquisitionEngine engine,
    AcquisitionAccessStore requests,
    AcquisitionRequestService requestService,
    TimeProvider clock,
    ILogger<VideoManualSearchService> logger,
    IInstanceModuleService? instanceModules = null,
    ManualGrabCoordinator? grabCoordinator = null)
{
    private readonly ManualGrabCoordinator coordinator = grabCoordinator ?? new ManualGrabCoordinator(requests, requestService, Microsoft.Extensions.Logging.Abstractions.NullLogger<ManualGrabCoordinator>.Instance);

    // A striped lock is enough: it serializes grabs of the same request in this process without one lock object per request id.
    private static readonly SemaphoreSlim[] GrabLocks = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    [GeneratedRegex(@"(?i)(api[_-]?key|apikey|key|token)=[^&\s""']+")]
    private static partial Regex SecretQueryValue();

    /// <summary>The target of one request, or null when the request does not exist, is not Movie/TV, its module is disabled or its Work is gone.</summary>
    public async Task<ManualSearchTarget?> LoadAsync(Guid requestId, Guid? unitId, CancellationToken cancellationToken)
    {
        var request = await FindSupportedRequestAsync(requestId, cancellationToken);
        if (request is null)
        {
            return null;
        }

        var target = await engine.ResolveManualTargetAsync(request, unitId, cancellationToken);
        return target is null ? null : ToTarget(request, target);
    }

    public async Task<ManualSearchResult?> SearchAsync(Guid requestId, Guid? unitId, bool refresh, CancellationToken cancellationToken, SearchDepth depth = SearchDepth.Normal)
    {
        var request = await FindSupportedRequestAsync(requestId, cancellationToken);
        if (request is null || await engine.ResolveManualTargetAsync(request, unitId, cancellationToken) is not { } target)
        {
            return null;
        }

        var shown = ToTarget(request, target);
        if (!shown.CanSearch || request.Kind == MediaAcquisitionKind.Tv && target.Unit is null)
        {
            return new ManualSearchResult(shown, [], [], VideoAcquisitionSetupProblem.None, Searched: false, TargetChanged: target.UnitChanged);
        }

        var setupProblem = await engine.FindSetupProblemAsync(cancellationToken);
        if (setupProblem != VideoAcquisitionSetupProblem.None)
        {
            return new ManualSearchResult(shown, [], [], setupProblem, Searched: false);
        }

        var evaluation = await engine.SearchManualAsync(request, target, cancellationToken, depth, refresh);
        var tried = new HashSet<string>(shown.TriedReleases, StringComparer.OrdinalIgnoreCase);
        var bestResolution = HighestAllowedResolution(evaluation.Profile);
        var candidates = evaluation.Releases
            .Select(release => ToCandidate(request.Kind, release, tried, bestResolution))
            .OrderBy(candidate => candidate.Verdict == ManualSearchVerdict.Rejected)
            .ThenByDescending(candidate => candidate.Score ?? int.MinValue)
            .ThenBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var warnings = evaluation.Search.Warnings.Select(warning => new ManualSearchIndexerWarning(warning.IndexerName, Redact(warning.Message))).ToArray();
        return new ManualSearchResult(shown, candidates, warnings, VideoAcquisitionSetupProblem.None, Searched: true)
        {
            Summary = new ManualSearchSummary(depth, evaluation.Search.RawResultCount, evaluation.Search.Releases.Count, evaluation.Search.Outcomes, evaluation.Search.Trace)
        };
    }

    /// <summary>
    /// Sends the selected release to the download client through the shared grab path. Idempotent and race-safe: after the (slow) search
    /// the request is claimed with one conditional status write that only succeeds while it still waits for a release, so a request
    /// that was rejected, cancelled or grabbed by the scheduler meanwhile is never grabbed; a double submit, a second tab or a concurrent
    /// request never creates a second download. The claim is released to the status it was taken from only when nothing was submitted;
    /// once the download client may have the release the request moves on to Downloading or Failed, never back to a state the scheduler
    /// would grab from again. Cancelling the resulting download keeps the usual meaning: the request fails and nothing replaces the
    /// release automatically.
    /// </summary>
    public async Task<ManualGrabOutcome> GrabAsync(Guid requestId, Guid? unitId, string releaseIdentity, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseIdentity);

        var gate = GrabLocks[(requestId.GetHashCode() & int.MaxValue) % GrabLocks.Length];
        await gate.WaitAsync(cancellationToken);
        try
        {
            var request = await FindSupportedRequestAsync(requestId, cancellationToken);
            if (request is null || await engine.ResolveManualTargetAsync(request, unitId, cancellationToken) is not { } target)
            {
                return new ManualGrabOutcome(ManualGrabStatus.NotFound, null, request);
            }

            var shown = ToTarget(request, target);
            if (target.UnitChanged)
            {
                return new ManualGrabOutcome(ManualGrabStatus.TargetChanged, null, request);
            }

            // The first submit already moved the request to Downloading, so a repeated selection of the same release lands here.
            if (shown.TriedReleases.Contains(releaseIdentity, StringComparer.OrdinalIgnoreCase))
            {
                return new ManualGrabOutcome(ManualGrabStatus.AlreadySubmitted, null, request);
            }

            if (!shown.CanSearch || request.Kind == MediaAcquisitionKind.Tv && target.Unit is null)
            {
                return new ManualGrabOutcome(ManualGrabStatus.NotSearchable, null, request);
            }

            // The grab never trusts what the list showed: it searches again past the evidence cache and validates the identity.
            var evaluation = await engine.SearchManualAsync(request, target, cancellationToken, SearchDepth.Normal, refresh: true);
            var selected = evaluation.Releases.FirstOrDefault(release => release.Candidate.Identity.Equals(releaseIdentity, StringComparison.Ordinal));
            if (selected is null || !selected.IsManuallyGrabbable)
            {
                return new ManualGrabOutcome(ManualGrabStatus.NotAvailable, null, request);
            }

            // The search took seconds: the request is claimed only if it still waits for a release and read again, so the grab works on
            // what the claim froze, not on what was read before the search.
            var outcome = await coordinator.GrabAsync(
                request,
                [AcquisitionRequestStatus.Approved, AcquisitionRequestStatus.Failed],
                async (claimed, progress) =>
                {
                    var fresh = await engine.ResolveManualTargetAsync(claimed, unitId, cancellationToken) ?? target;
                    return (fresh.Payload.TriedReleases ?? []).Contains(releaseIdentity, StringComparer.OrdinalIgnoreCase)
                        ? null
                        : await engine.GrabManualAsync(claimed, fresh, selected, progress, cancellationToken);
                },
                cancellationToken);
            return outcome with { Request = outcome.Request ?? await requests.GetAsync(requestId, cancellationToken) ?? request };
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<AcquisitionRequest?> FindSupportedRequestAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var request = await requests.GetAsync(requestId, cancellationToken);
        if (request is null || request.Kind is not (MediaAcquisitionKind.Movie or MediaAcquisitionKind.Tv))
        {
            return null;
        }

        if (instanceModules is not null)
        {
            var instance = await instanceModules.GetAsync(cancellationToken);
            if (!instance.IsEnabled(InstanceModule.Acquisition) || !instance.IsEnabled(AcquisitionInstanceModules.For(request.Kind)))
            {
                return null;
            }
        }

        return request;
    }

    private static ManualSearchTarget ToTarget(AcquisitionRequest request, VideoManualTarget target) =>
        new(
            request,
            target.Title,
            target.Year,
            target.Unit is { } unit ? ToUnit(unit) : null,
            target.MissingUnits.Select(ToUnit).ToArray(),
            target.Profile.Name,
            target.HasLocalFile,
            target.Payload.Searches,
            target.Payload.NextSearchUtc,
            target.Payload.LastProblem,
            target.Payload.TriedReleases ?? []);

    private static ManualSearchUnit ToUnit(VideoUnit unit) => new(unit.Id, $"S{unit.SeasonNumber:00}E{unit.EpisodeNumber:00}");

    /// <summary>The highest resolution among the qualities the profile allows; an accepted release below it is shown with a lower-quality warning. Null for qualities without a resolution, such as documents.</summary>
    private static int? HighestAllowedResolution(QualityProfile profile) =>
        (profile.AllowedQualities.Length == 0 ? profile.QualityOrder : profile.AllowedQualities).Select(ResolutionOf).Max();

    /// <summary>The resolution a quality key ends with, for example 1080 for <c>WEB-1080p</c>.</summary>
    private static int? ResolutionOf(string qualityKey) =>
        int.TryParse(qualityKey[(qualityKey.LastIndexOf('-') + 1)..].TrimEnd('p'), out var resolution) ? resolution : null;

    private static ManualSearchCandidate ToCandidate(MediaAcquisitionKind kind, ReleaseEvaluation<VideoIdentityMatch> evaluation, HashSet<string> tried, int? bestResolution)
    {
        var candidate = evaluation.Candidate;
        var parsed = evaluation.Parsed;
        var isTried = tried.Contains(candidate.Identity);
        var reasons = new List<ManualSearchReason> { new(IdentityReason(evaluation.Match)) };
        if (evaluation.Score is { } score)
        {
            reasons.AddRange(score.RejectionReasons.Select(reason => new ManualSearchReason(ManualSearchReasonCode.ProfileRejected, reason)));
        }

        var lowerQuality = evaluation.IsGrabbable && ResolutionOf(evaluation.Score!.QualityKey) is { } resolution && resolution < bestResolution;
        if (lowerQuality)
        {
            reasons.Add(new ManualSearchReason(ManualSearchReasonCode.LowerQuality, evaluation.Score!.QualityKey));
        }

        if (isTried)
        {
            reasons.Add(new ManualSearchReason(ManualSearchReasonCode.AlreadyTried));
        }

        var verdict = !evaluation.IsManuallyGrabbable
            ? ManualSearchVerdict.Rejected
            : lowerQuality || evaluation.Selection.Decision == SelectionDecision.ManualReview ? ManualSearchVerdict.Warning : ManualSearchVerdict.Eligible;
        return new ManualSearchCandidate(
            candidate.Identity,
            candidate.Title,
            candidate.Indexer,
            candidate.SizeBytes,
            candidate.AgeDays,
            evaluation.Score?.QualityKey,
            ReleaseTypeOf(parsed, kind),
            parsed?.SeriesTitle,
            ParsedUnit(parsed),
            parsed?.AudioLanguages ?? [],
            parsed?.SubtitleLanguages ?? [],
            parsed?.ReleaseGroup,
            verdict,
            evaluation.Score?.Score,
            reasons,
            evaluation.Score?.ScoreReasons ?? [],
            isTried,
            CanGrab: evaluation.IsManuallyGrabbable && !isTried)
        {
            Provenance = candidate.Provenance,
            Sources = [.. candidate.Sources.Select(source => source.Indexer)]
        };
    }

    private static ManualSearchReasonCode IdentityReason(VideoIdentityMatch identity) =>
        identity switch
        {
            VideoIdentityMatch.Matches => ManualSearchReasonCode.MatchesTarget,
            VideoIdentityMatch.ContainsTarget => ManualSearchReasonCode.ContainsTarget,
            VideoIdentityMatch.WrongTitle => ManualSearchReasonCode.WrongTitle,
            VideoIdentityMatch.WrongYear => ManualSearchReasonCode.WrongYear,
            VideoIdentityMatch.AmbiguousTitle => ManualSearchReasonCode.AmbiguousIdentity,
            VideoIdentityMatch.WrongSeason => ManualSearchReasonCode.WrongSeason,
            VideoIdentityMatch.WrongEpisode => ManualSearchReasonCode.WrongEpisode,
            VideoIdentityMatch.Unparseable => ManualSearchReasonCode.Unparseable,
            VideoIdentityMatch.NotUsenet => ManualSearchReasonCode.NotUsenet,
            VideoIdentityMatch.NoDownload => ManualSearchReasonCode.NoDownload,
            _ => throw new ArgumentOutOfRangeException(nameof(identity))
        };

    private static ManualSearchReleaseType ReleaseTypeOf(ReleaseInfo? parsed, MediaAcquisitionKind kind)
    {
        if (parsed is null)
        {
            return ManualSearchReleaseType.Unknown;
        }

        if (parsed.IsSeasonPack)
        {
            return ManualSearchReleaseType.SeasonPack;
        }

        if (kind == MediaAcquisitionKind.Movie)
        {
            return ManualSearchReleaseType.Movie;
        }

        if (parsed.EpisodeStart is null)
        {
            return ManualSearchReleaseType.Unknown;
        }

        return parsed.IsMultiEpisode ? ManualSearchReleaseType.MultiEpisode : ManualSearchReleaseType.Episode;
    }

    /// <summary>The season/episode identity the release was parsed as, for example <c>S01E03</c>, <c>S01E01-E03</c> or <c>S01</c> for a pack.</summary>
    private static string? ParsedUnit(ReleaseInfo? parsed)
    {
        if (parsed?.SeasonNumber is not { } season)
        {
            return null;
        }

        if (parsed.IsSeasonPack || parsed.EpisodeStart is null)
        {
            return $"S{season:00}";
        }

        return parsed.EpisodeEnd is { } end && end != parsed.EpisodeStart
            ? $"S{season:00}E{parsed.EpisodeStart:00}-E{end:00}"
            : $"S{season:00}E{parsed.EpisodeStart:00}";
    }

    private static string Redact(string message) => SecretQueryValue().Replace(message, "$1=***");

}
