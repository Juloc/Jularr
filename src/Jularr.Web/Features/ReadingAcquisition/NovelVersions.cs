using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Novels;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.ReadingAcquisition;

// The EPUB volumes an import brought are the published volumes of the same number when the Work has them identified; a number nothing identifies stays untied.
public sealed class NovelImportTies(AppDbContext db, ReadingUnits units)
{
    public async Task<int> TieAsync(long workId, Guid novelWorkId, IReadOnlyCollection<int> volumeNumbers, CancellationToken cancellationToken)
    {
        var canonical = await db.WorkVolumes.AsNoTracking().Where(volume => volume.WorkId == workId && volume.ExternalId != null).ToDictionaryAsync(volume => volume.Number, volume => volume.Id, cancellationToken);
        var tied = 0;
        foreach (var volume in await db.NovelVolumes.AsNoTracking().Where(item => item.WorkId == novelWorkId && item.Kind == NovelVolumeKinds.Epub && volumeNumbers.Contains(item.Number)).ToListAsync(cancellationToken))
        {
            if (canonical.TryGetValue(volume.Number, out var unit) && await units.TieAsync(workId, WorkUnitLocalKind.NovelVolume, volume.Id.ToString(), unit, isOwnerMapping: false, cancellationToken))
            {
                tied++;
            }
        }

        return tied;
    }
}

// A volume keeps every EPUB edition it was imported from. The reader shows the best one by the Work's profile (a profile that allows none of them still shows one); a
// better edition replaces the shown content only through the same chapter sync, so reading progress, bookmarks and notes stay, and no file is ever deleted.
public sealed class LightNovelVersionSelector(AppDbContext db, QualityProfileStore profiles, NovelEpubImportService importer)
{
    public async Task ReselectAsync(long workId, CancellationToken cancellationToken)
    {
        var profile = await profiles.ResolveAsync(MediaAcquisitionKind.LightNovel, workId, cancellationToken);
        var volumes = await (from link in db.WorkSourceLinks.AsNoTracking()
                             join volume in db.NovelVolumes.AsNoTracking() on link.SourceId equals volume.WorkId
                             where link.WorkId == workId && link.SourceKind == WorkSourceKind.NovelWork && volume.Kind == NovelVolumeKinds.Epub
                             select volume).ToListAsync(cancellationToken);
        foreach (var volume in volumes)
        {
            var editions = await db.NovelVolumeEditions.AsNoTracking().Where(edition => edition.VolumeId == volume.Id).ToListAsync(cancellationToken);
            if (editions.Count < 2)
            {
                continue;
            }

            var best = editions
                .OrderBy(edition => profile.AllowedQualities.Contains(edition.Quality, StringComparer.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(edition => UpgradePolicy.RankOf(profile, edition.Quality))
                .ThenByDescending(edition => edition.ImportedAt)
                .ThenBy(edition => edition.Id)
                .First();
            if (best.ContentHash != volume.SourceContentHash)
            {
                await importer.ShowEditionAsync(volume.WorkId, volume.Id, best, cancellationToken);
            }
        }
    }
}
