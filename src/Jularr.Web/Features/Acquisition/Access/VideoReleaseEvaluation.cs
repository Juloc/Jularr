using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Selection;

namespace Jularr.Web.Features.Acquisition.Access;

/// <summary>One canonical TV episode (or the movie itself is represented by no unit) a video search targets.</summary>
public sealed record VideoUnit(Guid Id, Guid? SeasonId, int SeasonNumber, int EpisodeNumber, DateTime? AiredAt, bool HasFile);

/// <summary>Whether a returned release is for the requested title and unit. Identity is decided before the profile score.</summary>
public enum VideoIdentityMatch
{
    Matches,
    ContainsTarget,
    WrongTitle,
    WrongYear,
    AmbiguousTitle,
    WrongSeason,
    WrongEpisode,
    Unparseable,
    NotUsenet,
    NoDownload
}

/// <summary>
/// One returned release as the shared selection engine judged it. <see cref="Score"/> is the profile score of the parsed release; it
/// exists whenever the title could be parsed, but only a selectable release (identity valid, profile accepted) is
/// <see cref="IsGrabbable"/> by automatic acquisition. A release whose identity is only ambiguous can still be taken by an owner
/// (<see cref="IsManuallyGrabbable"/>); a profile rejection never can.
/// </summary>
public sealed record VideoReleaseEvaluation(
    ProwlarrReleaseCandidate Candidate,
    ReleaseInfo? Parsed,
    VideoIdentityMatch Identity,
    CandidateEvaluation Selection)
{
    public ReleaseScoreResult? Score => Selection.Score;

    public bool IsIdentityValid => Selection.Candidate.Identity.Confidence is IdentityConfidence.Exact or IdentityConfidence.Strong;

    public bool IsGrabbable => Selection.IsSelectable && Candidate.InternalDownloadUri is not null;

    public bool IsManuallyGrabbable => (IsGrabbable || Selection.Decision == SelectionDecision.ManualReview) && Candidate.InternalDownloadUri is not null;
}

/// <summary>The result of one search: the profile it was scored with, per-indexer warnings and every evaluated release.</summary>
public sealed record VideoSearchEvaluation(
    QualityProfile Profile,
    AcquisitionSearchResult Search,
    IReadOnlyList<VideoReleaseEvaluation> Releases,
    SelectionResult Selection)
{
    /// <summary>The releases automatic acquisition may take, best first.</summary>
    public IReadOnlyList<VideoReleaseEvaluation> Grabbable => [.. Releases.Where(release => release.IsGrabbable)];
}

/// <summary>A configuration gap that stops video acquisition before any search runs.</summary>
public enum VideoAcquisitionSetupProblem
{
    None,
    NoIndexer,
    NoDownloadClient
}

/// <summary>
/// What a grab got through before it stopped, so a caller that sees an exception knows whether a download exists: the submission
/// started (its result is unknown if it was interrupted) or the download client accepted the release (a download is running).
/// </summary>
public sealed class VideoGrabProgress
{
    public bool SubmitStarted { get; set; }

    public bool Accepted { get; set; }

    public Guid? OperationId { get; set; }
}
