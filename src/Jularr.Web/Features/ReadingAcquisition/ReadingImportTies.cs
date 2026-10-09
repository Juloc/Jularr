using Jularr.Web.Data;
using Jularr.Web.Features.Manga;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Storage;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.ReadingAcquisition;

/// <summary>
/// Ties the library chapters an import brought to the canonical volumes and chapters of the Work, from what the files and the release say they hold: a
/// file that names chapters is those chapters, one that names a volume (or several) is that whole volume, and a file that names neither takes what its
/// release names. A single chapter never completes a volume, and a file that says nothing is left untied rather than guessed. Repeating the import
/// replaces the same ties with the same result.
/// </summary>
public sealed class ReadingImportTies(AppDbContext db, ReadingUnits units)
{
    public async Task<int> TieAsync(long workId, Guid seriesId, string releasePath, CancellationToken cancellationToken)
    {
        var volumes = await db.WorkVolumes.AsNoTracking().Where(volume => volume.WorkId == workId && volume.ExternalId != null).Select(volume => new { volume.Number, volume.Id }).ToListAsync(cancellationToken);
        var chapters = await db.WorkChapters.AsNoTracking().Where(chapter => chapter.WorkId == workId && chapter.ExternalId != null).Select(chapter => new { chapter.Number, chapter.Id }).ToListAsync(cancellationToken);
        if (volumes.Count == 0 && chapters.Count == 0)
        {
            return 0;
        }

        var release = ReadingReleaseParser.Parse(Path.GetFileName(releasePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        var tied = 0;
        foreach (var local in await new MangaRepository(db).GetChapterSourcesAsync(seriesId, cancellationToken))
        {
            if (!StoragePaths.AreSame(local.SourcePath, releasePath) && !StoragePaths.IsBelow(local.SourcePath, releasePath))
            {
                continue;
            }

            var named = ReadingReleaseParser.Parse(Directory.Exists(local.SourcePath) ? new DirectoryInfo(local.SourcePath).Name : Path.GetFileNameWithoutExtension(local.SourcePath));
            var holds = named.VolumeNumber is null && named.ChapterStart is null ? release : named;
            var localId = local.Id.ToString();
            var chapterIds = holds.ChapterStart is { } first
                ? chapters.Where(chapter => chapter.Number >= first && chapter.Number <= (holds.ChapterEnd ?? first)).Select(chapter => chapter.Id).ToArray()
                : [];
            var volumeIds = holds.VolumeNumber is { } firstVolume && (holds.ChapterStart is null || holds.ChapterEnd > holds.ChapterStart)
                ? volumes.Where(volume => volume.Number >= firstVolume && volume.Number <= (holds.VolumeEnd ?? firstVolume)).Select(volume => volume.Id).ToArray()
                : [];

            if (chapterIds.Length > 0 && await units.TieChaptersAsync(workId, localId, chapterIds, cancellationToken)
                || volumeIds.Length > 0 && await units.TieVolumesAsync(workId, localId, volumeIds, cancellationToken))
            {
                tied++;
            }
        }

        return tied;
    }
}
