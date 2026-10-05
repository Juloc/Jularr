using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;

namespace Jularr.Web.Features.Acquisition.Access;

/// <summary>One canonical TV episode (or the movie itself is represented by no unit) a video search targets.</summary>
public sealed record VideoUnit(Guid Id, Guid? SeasonId, int SeasonNumber, int EpisodeNumber, DateTime? AiredAt, bool HasFile);

/// <summary>Whether a returned release is for the requested title and unit. Identity is decided before the profile score.</summary>
public enum VideoIdentityMatch
{
    Matches,
    ContainsTarget,
    WrongTitle,
    WrongSeason,
    WrongEpisode,
    Unparseable,
    NotUsenet,
    NoDownload
}

/// <summary>
/// One returned release evaluated by the shared video pipeline. <see cref="Score"/> is the profile score of the parsed release;
/// it exists whenever the title could be parsed, but only an identity-valid, accepted release is <see cref="IsGrabbable"/>.
/// </summary>
public sealed record VideoReleaseEvaluation(
    ProwlarrReleaseCandidate Candidate,
    ReleaseInfo? Parsed,
    ReleaseScoreResult? Score,
    VideoIdentityMatch Identity)
{
    public bool IsIdentityValid => Identity is VideoIdentityMatch.Matches or VideoIdentityMatch.ContainsTarget;

    public bool IsGrabbable => IsIdentityValid && Score is { Accepted: true } && Candidate.InternalDownloadUri is not null;
}

/// <summary>The result of one search: the profile it was scored with, per-indexer warnings and every evaluated release.</summary>
public sealed record VideoSearchEvaluation(
    QualityProfile Profile,
    IndexerAnimeSearchResult Search,
    IReadOnlyList<VideoReleaseEvaluation> Releases);

/// <summary>A configuration gap that stops video acquisition before any search runs.</summary>
public enum VideoAcquisitionSetupProblem
{
    None,
    NoIndexer,
    NoDownloadClient
}
