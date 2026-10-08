using Jularr.Web.Features.Acquisition.Core;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Selection;

namespace Jularr.Web.Features.Acquisition.Access;

/// <summary>One canonical TV episode (or the movie itself is represented by no unit) a video search targets.</summary>
public sealed record VideoUnit(Guid Id, Guid? SeasonId, int SeasonNumber, int EpisodeNumber, DateTime? AiredAt, bool HasFile, string? InstalledQuality = null);

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

/// <summary>A configuration gap that stops video acquisition before any search runs.</summary>
public enum VideoAcquisitionSetupProblem
{
    None,
    NoIndexer,
    NoDownloadClient
}


