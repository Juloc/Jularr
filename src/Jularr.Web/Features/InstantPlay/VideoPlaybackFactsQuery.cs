using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Progress;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.InstantPlay;

/// <summary>The Work a playback intent targets, its provider identity for a request, the open request of the title and the canonical playback facts.</summary>
public sealed record VideoPlaybackState(WorkMediaType MediaType, string Title, int? Year, string? ProviderId, AcquisitionRequest? OpenRequest, PlaybackFacts Facts);

/// <summary>
/// The lean read of what <see cref="PrimaryActionResolver"/> needs for one Movie or Series Work and one profile: a fixed number of
/// set-based queries, no tracking and no writes. The detail page builds the same <see cref="PlaybackFacts"/> from rows it already loads
/// for display (<see cref="VideoDetailQuery"/>); both feed the one resolver, so they cannot disagree about the action.
/// </summary>
public sealed class VideoPlaybackFactsQuery(AppDbContext db, AcquisitionAccessStore requests, VideoProgressService progress, TimeProvider clock)
{
    /// <summary>The playback state of one Work, or null when it does not exist or is neither a Movie nor a Series.</summary>
    public async Task<VideoPlaybackState?> GetAsync(string profileId, Guid workId, CancellationToken cancellationToken)
    {
        var work = await db.Works
            .AsNoTracking()
            .Where(x => x.Id == workId && (x.MediaType == WorkMediaType.Movie || x.MediaType == WorkMediaType.Series))
            .Select(x => new { x.MediaType, x.CanonicalTitle, x.Year })
            .SingleOrDefaultAsync(cancellationToken);
        if (work is null)
        {
            return null;
        }

        var tmdbId = await db.WorkExternalIdentities
            .AsNoTracking()
            .Where(x => x.WorkId == workId && x.MediaType == work.MediaType && x.Provider == TmdbDiscoveryProvider.ProviderKey)
            .OrderByDescending(x => x.IsPrimary)
            .Select(x => x.ExternalId)
            .FirstOrDefaultAsync(cancellationToken);
        var open = tmdbId is null ? null : await requests.FindOpenAsync(VideoWorkLinks.AcquisitionKind(work.MediaType), TmdbDiscoveryProvider.ProviderKey, tmdbId, cancellationToken);
        var snapshots = await progress.ListAsync(profileId, [workId], cancellationToken);

        if (work.MediaType == WorkMediaType.Movie)
        {
            var hasMedia = await db.MediaAssets.AsNoTracking().AnyAsync(
                asset => asset.WorkId == workId && asset.WorkEpisodeId == null && asset.Kind == MediaAssetKind.Video && db.StoredFiles.Any(file => file.MediaAssetId == asset.Id),
                cancellationToken);
            var movieFacts = new MoviePlaybackFacts(workId, tmdbId is not null, open is null ? null : OpenRequestFacts.ForMovie(open), hasMedia, snapshots.FirstOrDefault(x => x.WorkEpisodeId is null));
            return new VideoPlaybackState(work.MediaType, work.CanonicalTitle, work.Year, tmdbId, open, movieFacts);
        }

        var episodes = await db.WorkEpisodes
            .AsNoTracking()
            .Where(x => x.WorkId == workId)
            .Select(x => new
            {
                x.Id,
                x.SeasonId,
                x.SeasonNumber,
                x.EpisodeNumber,
                x.AiredAt,
                HasMedia = db.MediaAssets.Any(asset => asset.WorkEpisodeId == x.Id && asset.Kind == MediaAssetKind.Video && db.StoredFiles.Any(file => file.MediaAssetId == asset.Id))
            })
            .ToListAsync(cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var units = episodes.Select(x => new SeriesUnit(x.Id, x.SeasonNumber, x.EpisodeNumber, x.HasMedia, x.AiredAt is null || x.AiredAt <= now)).ToArray();
        var states = snapshots
            .Where(x => x.WorkEpisodeId is not null && x.UpdatedAt is not null)
            .ToDictionary(x => x.WorkEpisodeId!.Value, x => new EpisodeProgressState(x.WorkEpisodeId!.Value, x.PositionMs, x.IsCompleted, x.UpdatedAt!.Value));
        var openFacts = open is null ? null : OpenRequestFacts.ForSeries(open, VideoRequestSelection.For(open, workId), episodes.Select(x => (x.Id, x.SeasonId, x.AiredAt)));
        return new VideoPlaybackState(work.MediaType, work.CanonicalTitle, work.Year, tmdbId, open, new SeriesPlaybackFacts(workId, tmdbId is not null, openFacts, units, states));
    }
}
