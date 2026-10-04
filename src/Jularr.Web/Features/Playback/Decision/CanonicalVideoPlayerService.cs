using Jularr.Web.Data;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.MediaSegments;
using Jularr.Web.Features.Progress;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Playback.Decision;

public sealed record CanonicalVideoEpisode(
    Guid Id,
    int SeasonNumber,
    int EpisodeNumber,
    string? Title);

public sealed record CanonicalVideoNavigationItem(
    PlaybackVideoTarget Target,
    int SeasonNumber,
    int EpisodeNumber,
    string? Title);

public sealed record CanonicalVideoNavigation(
    CanonicalVideoNavigationItem? Previous,
    CanonicalVideoNavigationItem? Next)
{
    public static CanonicalVideoNavigation None { get; } = new(null, null);
}

public sealed record CanonicalVideoPlayerSnapshot(
    PlaybackVideoTarget Target,
    WorkMediaType MediaType,
    string WorkTitle,
    CanonicalVideoEpisode? Episode,
    CanonicalPlayableFile File,
    MediaInventoryEntry Inventory,
    MediaProgressSnapshot Progress,
    CanonicalVideoNavigation Navigation,
    EpisodeSegmentDescriptor Segments,
    TrickplayDescriptor Trickplay);

/// <summary>
/// Canonical Player bootstrap owner for every local video type. It resolves only
/// Work/WorkEpisode -> Asset/File/Track identities; delivery policy remains in
/// <see cref="PlaybackPlanService"/> / <see cref="PlaybackDecisionEngine"/>.
/// </summary>
public sealed class CanonicalVideoPlayerService(
    AppDbContext db,
    CanonicalMediaStorageService storage,
    MediaInventoryService inventory,
    VideoProgressService progress,
    CanonicalPlayerNavigationAssetService navigationAssets)
{
    public async Task<CanonicalVideoPlayerSnapshot?> GetAsync(
        string profileId,
        PlaybackVideoTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(target);

        var work = await db.Works
            .AsNoTracking()
            .Where(x => x.Id == target.WorkId)
            .Select(x => new { x.Id, x.MediaType, x.CanonicalTitle })
            .SingleOrDefaultAsync(cancellationToken);
        if (work is null || work.MediaType is not (WorkMediaType.Movie or WorkMediaType.Series or WorkMediaType.Anime))
        {
            return null;
        }

        CanonicalVideoEpisode? episode = null;
        if (target.WorkEpisodeId is { } episodeId)
        {
            if (work.MediaType == WorkMediaType.Movie)
            {
                return null;
            }

            episode = await db.WorkEpisodes
                .AsNoTracking()
                .Where(x => x.Id == episodeId && x.WorkId == target.WorkId)
                .Select(x => new CanonicalVideoEpisode(
                    x.Id,
                    x.SeasonNumber,
                    x.EpisodeNumber,
                    x.Title))
                .SingleOrDefaultAsync(cancellationToken);
            if (episode is null)
            {
                return null;
            }
        }
        else if (work.MediaType != WorkMediaType.Movie)
        {
            return null;
        }

        var file = await storage.ResolveVideoAsync(
            target.WorkId,
            target.WorkEpisodeId,
            cancellationToken);
        if (file is null)
        {
            return null;
        }

        var technical = await inventory.EnsureAnalyzedAsync(file.StoredFileId, cancellationToken);
        if (technical?.Technical is null)
        {
            return null;
        }

        var progressSnapshot = await progress.GetAsync(
            profileId,
            target.ToProgressTarget(),
            cancellationToken);
        if (progressSnapshot is null)
        {
            return null;
        }

        var navigation = episode is null
            ? CanonicalVideoNavigation.None
            : await ResolveNavigationAsync(target.WorkId, episode.Id, cancellationToken);
        var segments = await navigationAssets.GetSegmentsAsync(target, cancellationToken);
        var trickplay = await navigationAssets.GetTrickplayAsync(
            target,
            file,
            queue: true,
            cancellationToken);

        return new CanonicalVideoPlayerSnapshot(
            target,
            work.MediaType,
            work.CanonicalTitle,
            episode,
            file,
            technical,
            progressSnapshot,
            navigation,
            segments,
            trickplay);
    }

    private async Task<CanonicalVideoNavigation> ResolveNavigationAsync(
        Guid workId,
        Guid currentEpisodeId,
        CancellationToken cancellationToken)
    {
        var episodes = await db.WorkEpisodes
            .AsNoTracking()
            .Where(episode =>
                episode.WorkId == workId &&
                db.MediaAssets.Any(asset =>
                    asset.Kind == MediaAssetKind.Video &&
                    asset.WorkEpisodeId == episode.Id &&
                    db.StoredFiles.Any(file => file.MediaAssetId == asset.Id)))
            .OrderBy(x => x.SeasonNumber)
            .ThenBy(x => x.EpisodeNumber)
            .ThenBy(x => x.Id)
            .Select(x => new CanonicalVideoEpisode(
                x.Id,
                x.SeasonNumber,
                x.EpisodeNumber,
                x.Title))
            .ToListAsync(cancellationToken);

        var index = episodes.FindIndex(x => x.Id == currentEpisodeId);
        if (index < 0)
        {
            return CanonicalVideoNavigation.None;
        }

        CanonicalVideoNavigationItem? Map(CanonicalVideoEpisode value) =>
            new(
                PlaybackVideoTarget.Episode(workId, value.Id),
                value.SeasonNumber,
                value.EpisodeNumber,
                value.Title);

        return new CanonicalVideoNavigation(
            index > 0 ? Map(episodes[index - 1]) : null,
            index + 1 < episodes.Count ? Map(episodes[index + 1]) : null);
    }
}
