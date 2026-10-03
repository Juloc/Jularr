using Jularr.Web.Data;
using Jularr.Web.Features.Playback;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Library;

/// <summary>
/// Reads what the media inventory already knows about every episode of one anime for the detail page:
/// whether an episode has a file, its runtime, and the audio and subtitle languages it carries (embedded
/// streams and imported sidecar tracks, the same two sources <c>MediaFactsService</c> unions). It only reads
/// stored rows; it never probes a file, so it stays cheap when the storage is asleep or offline.
/// </summary>
public static class AnimeEpisodeMediaQuery
{
    public static async Task<IReadOnlyDictionary<Guid, AnimeEpisodeMediaFacts>> LoadAsync(
        AppDbContext db,
        Guid animeId,
        CancellationToken cancellationToken)
    {
        var files = await (
            from media in db.MediaFiles.AsNoTracking()
            join episode in db.Episodes.AsNoTracking() on media.EpisodeId equals episode.Id
            join analysis in db.MediaAnalyses.AsNoTracking() on media.Id equals analysis.MediaFileId into analyses
            from analysis in analyses.DefaultIfEmpty()
            where episode.AnimeId == animeId
            select new { EpisodeId = episode.Id, Duration = analysis == null ? null : analysis.DurationSeconds })
            .ToListAsync(cancellationToken);

        var streams = await (
            from stream in db.MediaAnalysisStreams.AsNoTracking()
            join media in db.MediaFiles.AsNoTracking() on stream.MediaFileId equals media.Id
            join episode in db.Episodes.AsNoTracking() on media.EpisodeId equals episode.Id
            where episode.AnimeId == animeId && stream.Language != null
            select new { EpisodeId = episode.Id, stream.Kind, stream.Language })
            .ToListAsync(cancellationToken);

        var sidecar = await (
            from track in db.SubtitleTracks.AsNoTracking()
            join episode in db.Episodes.AsNoTracking() on track.EpisodeId equals episode.Id
            where episode.AnimeId == animeId
            select new { track.EpisodeId, track.Language })
            .ToListAsync(cancellationToken);

        var audioByEpisode = streams
            .Where(x => x.Kind == MediaStreamKind.Audio)
            .Select(x => (x.EpisodeId, Code: PlaybackLanguages.Normalize(x.Language)))
            .Where(x => x.Code is not null)
            .GroupBy(x => x.EpisodeId)
            .ToDictionary(group => group.Key, group => group.Select(x => x.Code!).ToHashSet(StringComparer.Ordinal));

        var subtitlesByEpisode = streams
            .Where(x => x.Kind == MediaStreamKind.Subtitle)
            .Select(x => (x.EpisodeId, Code: PlaybackLanguages.Normalize(x.Language)))
            .Concat(sidecar.Select(x => (x.EpisodeId, Code: PlaybackLanguages.Normalize(x.Language))))
            .Where(x => x.Code is not null && x.Code != PlaybackLanguages.SubtitlesOff)
            .GroupBy(x => x.EpisodeId)
            .ToDictionary(group => group.Key, group => group.Select(x => x.Code!).ToHashSet(StringComparer.Ordinal));

        return files
            .GroupBy(x => x.EpisodeId)
            .ToDictionary(
                group => group.Key,
                group => new AnimeEpisodeMediaFacts(
                    HasFile: true,
                    AnimeDetailView.RuntimeMinutes(group.Max(x => x.Duration)),
                    audioByEpisode.GetValueOrDefault(group.Key) ?? new HashSet<string>(),
                    subtitlesByEpisode.GetValueOrDefault(group.Key) ?? new HashSet<string>()));
    }
}
