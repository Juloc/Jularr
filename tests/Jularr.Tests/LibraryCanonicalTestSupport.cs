using Jularr.Web.Data;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Progress;

namespace Jularr.Tests;

/// <summary>An Anime as an install holds it after the canonical backfill: the legacy record, its Work and the bridged episodes.</summary>
internal sealed record SeededAnime(Anime Anime, Work Work, IReadOnlyList<SeededEpisode> Episodes);

internal sealed record SeededEpisode(Episode Legacy, WorkEpisode Canonical);

/// <summary>
/// Seeds the canonical rows the Library read model consumes (Work, WorkEpisode, MediaAsset, StoredFile, MediaTrack,
/// MediaTechnicalAnalysis, MediaProgress) straight into the database, without files on disk.
/// </summary>
internal sealed class LibraryCanonicalSeed(AppDbContext db)
{
    private LibraryRoot? root;

    public async Task<Work> AddWorkAsync(WorkMediaType mediaType, string title, int? year = null)
    {
        var work = new Work { MediaType = mediaType, CanonicalTitle = title, Year = year };
        db.Add(work);
        await db.SaveChangesAsync();
        return work;
    }

    public async Task<WorkEpisode> AddEpisodeAsync(Work work, int seasonNumber, int number)
    {
        var episode = new WorkEpisode { WorkId = work.Id, SeasonNumber = seasonNumber, EpisodeNumber = number, IsSpecial = seasonNumber == 0 };
        db.Add(episode);
        await db.SaveChangesAsync();
        return episode;
    }

    /// <summary>One playable video file for a Movie (<paramref name="episode"/> null) or an episode, with its probed languages.</summary>
    public async Task AddVideoAsync(
        Work work,
        WorkEpisode? episode,
        string[]? audio = null,
        string[]? subtitles = null,
        double? durationSeconds = null)
    {
        if (root is null)
        {
            root = new LibraryRoot { Name = "Media", Path = $"/media/{Guid.NewGuid():N}" };
            db.Add(root);
        }

        var version = new WorkVersion
        {
            WorkId = work.Id,
            VersionKey = $"video-file:{Guid.NewGuid():N}",
            UnitKey = episode is null ? null : $"S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2}",
            Source = "local"
        };
        var asset = new MediaAsset { WorkId = work.Id, WorkEpisodeId = episode?.Id, WorkVersionId = version.Id, Kind = MediaAssetKind.Video };
        var file = new StoredFile
        {
            MediaAssetId = asset.Id,
            LibraryRootId = root.Id,
            Path = $"{root.Path}/{asset.Id:N}.mkv",
            SizeBytes = 1,
            LastWriteTimeUtc = DateTime.UtcNow
        };
        db.AddRange(version, asset, file);
        db.Add(new MediaTechnicalAnalysis
        {
            StoredFileId = file.Id,
            Status = MediaAnalysisStatus.Succeeded,
            DurationSeconds = durationSeconds,
            SourceLastWriteTimeUtc = DateTime.UtcNow
        });

        var index = 1;
        foreach (var language in audio ?? [])
        {
            db.Add(new MediaTrack { StoredFileId = file.Id, StreamIndex = index++, Kind = MediaTrackKind.Audio, Language = language });
        }

        foreach (var language in subtitles ?? [])
        {
            db.Add(new MediaTrack { StoredFileId = file.Id, StreamIndex = index++, Kind = MediaTrackKind.Subtitle, Language = language });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>A legacy Anime with its Work and episodes bridged exactly like the canonical backfill does; each episode may have a file.</summary>
    public async Task<SeededAnime> AddAnimeAsync(
        string title,
        IReadOnlyList<(int Season, int Number, bool HasFile)> episodes,
        string[]? audio = null,
        string[]? subtitles = null)
    {
        var anime = new Anime { Key = Guid.NewGuid().ToString("N"), Title = title };
        db.Add(anime);
        var work = await AddWorkAsync(WorkMediaType.Anime, title);
        db.Add(new WorkSourceLink { WorkId = work.Id, SourceKind = WorkSourceKind.Anime, SourceId = anime.Id });

        var seeded = new List<SeededEpisode>();
        foreach (var (season, number, hasFile) in episodes)
        {
            var legacy = new Episode { AnimeId = anime.Id, SeasonNumber = season, Number = number, Title = $"Episode {number}" };
            db.Add(legacy);
            db.Add(new WorkSourceLink { WorkId = work.Id, SourceKind = WorkSourceKind.Episode, SourceId = legacy.Id });
            var canonical = await AddEpisodeAsync(work, season, number);
            if (hasFile)
            {
                await AddVideoAsync(work, canonical, audio, subtitles);
            }

            seeded.Add(new SeededEpisode(legacy, canonical));
        }

        await db.SaveChangesAsync();
        return new SeededAnime(anime, work, seeded);
    }

    public Task SetProgressAsync(
        string profileId,
        Work work,
        WorkEpisode? episode,
        long positionMs,
        long? durationMs,
        bool completed,
        DateTime updatedAt) =>
        new VideoProgressService(db).ImportProgressAsync(
            profileId,
            new MediaProgressTarget(work.Id, episode?.Id),
            positionMs,
            durationMs,
            completed,
            updatedAt);
}
