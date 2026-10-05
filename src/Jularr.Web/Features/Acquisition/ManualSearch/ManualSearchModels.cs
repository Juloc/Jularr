using Jularr.Web.Features.Acquisition.Access;

namespace Jularr.Web.Features.Acquisition.ManualSearch;

/// <summary>How Manual Search presents a candidate. Rejected candidates stay visible but can never be grabbed.</summary>
public enum ManualSearchVerdict
{
    Eligible,
    Warning,
    Rejected
}

public enum ManualSearchReleaseType
{
    Movie,
    Episode,
    MultiEpisode,
    SeasonPack,
    Unknown
}

/// <summary>Why a candidate is eligible, carries a warning or is rejected. Identity reasons come before any profile reason.</summary>
public enum ManualSearchReasonCode
{
    MatchesTarget,
    ContainsTarget,
    WrongTitle,
    WrongSeason,
    WrongEpisode,
    Unparseable,
    NotUsenet,
    NoDownload,
    ProfileRejected,
    LowerQuality,
    AlreadyTried
}

/// <summary><paramref name="Detail"/> carries the explanation of the profile scorer for <see cref="ManualSearchReasonCode.ProfileRejected"/>.</summary>
public sealed record ManualSearchReason(ManualSearchReasonCode Code, string? Detail = null);

/// <summary>
/// One external release as Manual Search shows it: normalized provenance, the parsed identity, the contextual score of the active profile
/// and every reason behind the verdict. It is temporary evidence and never becomes a library identity. It carries no download URL: a grab
/// only sends <see cref="Identity"/> back and the server resolves it against a fresh search.
/// </summary>
public sealed record ManualSearchCandidate(
    string Identity,
    string Title,
    string? Indexer,
    long? SizeBytes,
    int? AgeDays,
    string? Quality,
    ManualSearchReleaseType ReleaseType,
    string? ParsedTitle,
    string? ParsedUnit,
    IReadOnlyList<string> AudioLanguages,
    IReadOnlyList<string> SubtitleLanguages,
    string? ReleaseGroup,
    ManualSearchVerdict Verdict,
    int? Score,
    IReadOnlyList<ManualSearchReason> Reasons,
    IReadOnlyList<string> ScoreBreakdown,
    bool IsTried,
    bool CanGrab);

public sealed record ManualSearchUnit(Guid Id, string Label);

/// <summary>The canonical target a Manual Search is for, plus what the Current and History tabs need.</summary>
public sealed record ManualSearchTarget(
    AcquisitionRequest Request,
    string Title,
    int? Year,
    ManualSearchUnit? Unit,
    IReadOnlyList<ManualSearchUnit> MissingUnits,
    string ProfileName,
    bool HasLocalFile,
    int Searches,
    DateTime? NextSearchUtc,
    string? LastProblem,
    IReadOnlyList<string> TriedReleases)
{
    /// <summary>Only a request that waits for a release can be searched; a running or finished one is read-only context.</summary>
    public bool CanSearch => Request.Status is AcquisitionRequestStatus.Approved or AcquisitionRequestStatus.Failed;
}

public sealed record ManualSearchIndexerWarning(string IndexerName, string Message);

/// <summary>The outcome of one search: candidates from every indexer that answered, the warnings of those that did not and any setup gap.</summary>
/// <remarks><see cref="Searched"/> is false when nothing was searched: the request is not waiting for a release, no episode is missing or the setup is incomplete.</remarks>
public sealed record ManualSearchResult(
    ManualSearchTarget Target,
    IReadOnlyList<ManualSearchCandidate> Candidates,
    IReadOnlyList<ManualSearchIndexerWarning> Warnings,
    VideoAcquisitionSetupProblem SetupProblem,
    bool Searched);

public enum ManualGrabStatus
{
    /// <summary>The release was sent to the download client.</summary>
    Submitted,

    /// <summary>The release was sent before (double submit or another selection won the race); nothing new was submitted.</summary>
    AlreadySubmitted,

    /// <summary>The request is not waiting for a release any more.</summary>
    NotSearchable,

    /// <summary>The candidate is rejected, tried or no longer returned by the indexers.</summary>
    NotAvailable,

    /// <summary>The download client refused the release; it stays tried and the request searches again later.</summary>
    ClientRejected
}

public sealed record ManualGrabOutcome(ManualGrabStatus Status, string? Message, AcquisitionRequest Request);
