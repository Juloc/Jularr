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
    PlaybackPreferencesSnapshot Preferences,
    CanonicalVideoNavigation Navigation,
    EpisodeSegmentDescriptor Segments,
    TrickplayDescriptor Trickplay);

/// <summary>Why a video target has no player: nothing playable is stored, or its file could not be analysed.</summary>
public enum CanonicalVideoPlayerGap
{
    None,

    /// <summary>The target is unknown, has no file, or its file is gone from disk.</summary>
    NoPlayableFile,

    /// <summary>The media tool could not run (missing, hanging), so the file is neither confirmed nor rejected yet.</summary>
    AnalysisUnavailable,

    /// <summary>The media tool ran and rejected the file as unreadable.</summary>
    AnalysisRejected
}

/// <summary>The player snapshot of a target, or the <see cref="Gap"/> that explains why there is none.</summary>
public sealed record CanonicalVideoPlayerOutcome(CanonicalVideoPlayerSnapshot? Snapshot, CanonicalVideoPlayerGap Gap)
{
    public static CanonicalVideoPlayerOutcome NoPlayableFile { get; } = new(null, CanonicalVideoPlayerGap.NoPlayableFile);
}

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
    CanonicalPlayerNavigationAssetService? navigationAssets = null)
{
    public async Task<CanonicalVideoPlayerOutcome> GetAsync(
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
            return CanonicalVideoPlayerOutcome.NoPlayableFile;
        }

        CanonicalVideoEpisode? episode = null;
        if (target.WorkEpisodeId is { } episodeId)
        {
            if (work.MediaType == WorkMediaType.Movie)
            {
                return CanonicalVideoPlayerOutcome.NoPlayableFile;
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
                return CanonicalVideoPlayerOutcome.NoPlayableFile;
            }
        }
        else if (work.MediaType != WorkMediaType.Movie)
        {
            return CanonicalVideoPlayerOutcome.NoPlayableFile;
        }

        var file = await storage.ResolveVideoAsync(
            target.WorkId,
            target.WorkEpisodeId,
            cancellationToken);
        if (file is null)
        {
            return CanonicalVideoPlayerOutcome.NoPlayableFile;
        }

        // Unreachable storage must not read as "no video": the persisted analysis still describes the file, and the storage state
        // the caller reads tells the client why it cannot play yet.
        var technical = await inventory.EnsureAnalyzedAsync(file.StoredFileId, cancellationToken)
            ?? await inventory.GetAsync(file.StoredFileId, cancellationToken);
        if (technical?.Technical is null)
        {
            var gap = technical?.Status switch
            {
                MediaAnalysisStatus.Pending => CanonicalVideoPlayerGap.AnalysisUnavailable,
                MediaAnalysisStatus.Failed => CanonicalVideoPlayerGap.AnalysisRejected,
                _ => CanonicalVideoPlayerGap.NoPlayableFile
            };
            return new CanonicalVideoPlayerOutcome(null, gap);
        }

        var progressSnapshot = await progress.GetAsync(
            profileId,
            target.ToProgressTarget(),
            cancellationToken);
        if (progressSnapshot is null)
        {
            return CanonicalVideoPlayerOutcome.NoPlayableFile;
        }

        var preferenceRow = await db.ProfilePlaybackPreferences
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProfileId == profileId, cancellationToken);
        var preferences = preferenceRow is null
            ? PlaybackPreferencesSnapshot.Default
            : new PlaybackPreferencesSnapshot(
                preferenceRow.AutoplayNext,
                preferenceRow.PreferredAudioLanguage,
                preferenceRow.PreferredSubtitleLanguage,
                preferenceRow.DefaultPlaybackSpeed);

        var navigation = episode is null
            ? CanonicalVideoNavigation.None
            : await ResolveNavigationAsync(target.WorkId, episode.Id, cancellationToken);
        var segments = navigationAssets is null
            ? new EpisodeSegmentDescriptor(new MediaSegmentOptions().SkipConfidenceThreshold, [])
            : await navigationAssets.GetSegmentsAsync(target, cancellationToken);
        var trickplay = navigationAssets is null
            ? TrickplayDescriptor.Unavailable
            : await navigationAssets.GetTrickplayAsync(
                target,
                file,
                queue: true,
                cancellationToken);

        return new CanonicalVideoPlayerOutcome(
            new CanonicalVideoPlayerSnapshot(
                target,
                work.MediaType,
                work.CanonicalTitle,
                episode,
                file,
                technical,
                progressSnapshot,
                preferences,
                navigation,
                segments,
                trickplay),
            CanonicalVideoPlayerGap.None);
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
