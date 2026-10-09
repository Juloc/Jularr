using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.MediaCore;

/// <summary>One unit a provider vouches for. The provider's id is its identity; the number only places it.</summary>
public sealed record ProviderUnit(string ExternalId, double Number, string? Title, bool IsSpecial = false);

/// <param name="Created">Units that were new.</param>
/// <param name="Updated">Units of the same provider identity whose number or title changed.</param>
/// <param name="Conflicts">Units not stored because another unit of the Work already has their number: the two are never merged.</param>
public sealed record ReadingUnitEnrichment(int Created, int Updated, int Conflicts);

/// <summary>
/// The canonical volumes and chapters of a Manga or Light Novel Work. A unit with a provider identity is created and refreshed only from that identity
/// (idempotent enrichment); a local volume or chapter is tied to one only by an owner mapping or by the import of an acquisition made for that unit.
/// Local numbering and titles never identify a unit, so a Work whose units are unknown keeps being wanted as a whole.
/// </summary>
public sealed class ReadingUnits(AppDbContext db)
{
    public async Task<ReadingUnitEnrichment> EnrichVolumesAsync(long workId, string provider, IReadOnlyList<ProviderUnit> units, CancellationToken cancellationToken)
    {
        var identified = Identified(units, volumes: true);
        var existing = await db.WorkVolumes.Where(volume => volume.WorkId == workId).ToListAsync(cancellationToken);
        var byIdentity = existing.Where(volume => volume.Provider == provider).ToDictionary(volume => volume.ExternalId!);
        var taken = existing.ToDictionary(volume => volume.Number);
        int created = 0, updated = 0, conflicts = 0;
        foreach (var unit in identified)
        {
            var number = (int)unit.Number;
            if (byIdentity.TryGetValue(unit.ExternalId, out var volume))
            {
                if (volume.Number == number && volume.Title == unit.Title)
                {
                    continue;
                }

                if (volume.Number != number && taken.ContainsKey(number))
                {
                    conflicts++;
                    continue;
                }

                taken.Remove(volume.Number);
                (volume.Number, volume.Title) = (number, unit.Title);
                taken[number] = volume;
                updated++;
            }
            else if (taken.ContainsKey(number))
            {
                conflicts++;
            }
            else
            {
                var added = new WorkVolume { WorkId = workId, Number = number, Title = unit.Title, Provider = provider, ExternalId = unit.ExternalId };
                db.WorkVolumes.Add(added);
                byIdentity[unit.ExternalId] = added;
                taken[number] = added;
                created++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return new ReadingUnitEnrichment(created, updated, conflicts);
    }

    public async Task<ReadingUnitEnrichment> EnrichChaptersAsync(long workId, string provider, IReadOnlyList<ProviderUnit> units, CancellationToken cancellationToken)
    {
        var identified = Identified(units, volumes: false);
        var existing = await db.WorkChapters.Where(chapter => chapter.WorkId == workId).ToListAsync(cancellationToken);
        var byIdentity = existing.Where(chapter => chapter.Provider == provider).ToDictionary(chapter => chapter.ExternalId!);
        var taken = existing.ToDictionary(chapter => chapter.Number);
        int created = 0, updated = 0, conflicts = 0;
        foreach (var unit in identified)
        {
            if (byIdentity.TryGetValue(unit.ExternalId, out var chapter))
            {
                if (chapter.Number == unit.Number && chapter.Title == unit.Title && chapter.IsSpecial == unit.IsSpecial)
                {
                    continue;
                }

                if (chapter.Number != unit.Number && taken.ContainsKey(unit.Number))
                {
                    conflicts++;
                    continue;
                }

                taken.Remove(chapter.Number);
                (chapter.Number, chapter.Title, chapter.IsSpecial) = (unit.Number, unit.Title, unit.IsSpecial);
                taken[unit.Number] = chapter;
                updated++;
            }
            else if (taken.ContainsKey(unit.Number))
            {
                conflicts++;
            }
            else
            {
                var added = new WorkChapter { WorkId = workId, Number = unit.Number, Title = unit.Title, IsSpecial = unit.IsSpecial, Provider = provider, ExternalId = unit.ExternalId };
                db.WorkChapters.Add(added);
                byIdentity[unit.ExternalId] = added;
                taken[unit.Number] = added;
                created++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return new ReadingUnitEnrichment(created, updated, conflicts);
    }

    /// <summary>
    /// Ties a local unit to a canonical volume (a NovelVolume) or chapter (a MangaChapter) of the same Work. An owner mapping replaces any earlier binding of the
    /// local unit; an import-made one never replaces an owner's. Returns false when the units do not belong to the Work or the owner's mapping stands.
    /// </summary>
    public async Task<bool> TieAsync(long workId, WorkUnitLocalKind localKind, string localId, Guid unitId, bool isOwnerMapping, CancellationToken cancellationToken)
    {
        var volume = localKind == WorkUnitLocalKind.NovelVolume;
        var unitOfWork = volume
            ? await db.WorkVolumes.AnyAsync(unit => unit.Id == unitId && unit.WorkId == workId, cancellationToken)
            : await db.WorkChapters.AnyAsync(unit => unit.Id == unitId && unit.WorkId == workId, cancellationToken);
        if (!unitOfWork || !await LocalBelongsToWorkAsync(workId, localKind, localId, cancellationToken))
        {
            return false;
        }

        var bindings = await db.WorkUnitBindings.Where(item => item.LocalKind == localKind && item.LocalId == localId).ToListAsync(cancellationToken);
        if (bindings.Any(binding => binding.IsOwnerMapping) && !isOwnerMapping)
        {
            return false;
        }

        // The local unit is exactly this canonical unit afterwards; an earlier tie (also to several units) is replaced.
        db.WorkUnitBindings.RemoveRange(bindings);
        db.WorkUnitBindings.Add(new WorkUnitBinding
        {
            WorkId = workId,
            LocalKind = localKind,
            LocalId = localId,
            WorkVolumeId = volume ? unitId : null,
            WorkChapterId = volume ? null : unitId,
            IsOwnerMapping = isOwnerMapping
        });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // An import-made tie never replaces an owner's mapping.
    public async Task<bool> TieChaptersAsync(long workId, string localId, IReadOnlyCollection<Guid> chapterIds, CancellationToken cancellationToken)
    {
        if (chapterIds.Count == 0
            || await db.WorkChapters.CountAsync(unit => unit.WorkId == workId && chapterIds.Contains(unit.Id), cancellationToken) != chapterIds.Count
            || !await LocalBelongsToWorkAsync(workId, WorkUnitLocalKind.MangaChapter, localId, cancellationToken))
        {
            return false;
        }

        return await ReplaceMangaBindingsAsync(workId, localId, chapterIds, volumes: false, cancellationToken);
    }

    // An import-made tie never replaces an owner's mapping.
    public async Task<bool> TieVolumesAsync(long workId, string localId, IReadOnlyCollection<Guid> volumeIds, CancellationToken cancellationToken)
    {
        if (volumeIds.Count == 0
            || await db.WorkVolumes.CountAsync(unit => unit.WorkId == workId && volumeIds.Contains(unit.Id), cancellationToken) != volumeIds.Count
            || !await LocalBelongsToWorkAsync(workId, WorkUnitLocalKind.MangaChapter, localId, cancellationToken))
        {
            return false;
        }

        return await ReplaceMangaBindingsAsync(workId, localId, volumeIds, volumes: true, cancellationToken);
    }

    // The local manga unit covers exactly these canonical units afterwards. Repeating the import of the same release changes nothing, and an owner's mapping is never replaced.
    private async Task<bool> ReplaceMangaBindingsAsync(long workId, string localId, IReadOnlyCollection<Guid> unitIds, bool volumes, CancellationToken cancellationToken)
    {
        var bindings = await db.WorkUnitBindings.Where(item => item.LocalKind == WorkUnitLocalKind.MangaChapter && item.LocalId == localId).ToListAsync(cancellationToken);
        if (bindings.Any(binding => binding.IsOwnerMapping))
        {
            return false;
        }

        var current = bindings.Select(binding => volumes ? binding.WorkVolumeId : binding.WorkChapterId).Where(id => id is not null).Select(id => id!.Value).ToHashSet();
        if (bindings.Count == current.Count && current.SetEquals(unitIds))
        {
            return true;
        }

        db.WorkUnitBindings.RemoveRange(bindings);
        await db.SaveChangesAsync(cancellationToken);
        db.WorkUnitBindings.AddRange(unitIds.Select(unitId => new WorkUnitBinding
        {
            WorkId = workId,
            LocalKind = WorkUnitLocalKind.MangaChapter,
            LocalId = localId,
            WorkVolumeId = volumes ? unitId : null,
            WorkChapterId = volumes ? null : unitId
        }));
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task UnbindAsync(long workId, WorkUnitLocalKind localKind, string localId, CancellationToken cancellationToken) =>
        await db.WorkUnitBindings.Where(binding => binding.WorkId == workId && binding.LocalKind == localKind && binding.LocalId == localId).ExecuteDeleteAsync(cancellationToken);

    private async Task<bool> LocalBelongsToWorkAsync(long workId, WorkUnitLocalKind localKind, string localId, CancellationToken cancellationToken)
    {
        if (localKind == WorkUnitLocalKind.NovelVolume)
        {
            return Guid.TryParse(localId, out var id)
                && await (from volume in db.NovelVolumes.AsNoTracking()
                          join link in db.WorkSourceLinks.AsNoTracking() on volume.WorkId equals link.SourceId
                          where volume.Id == id && link.WorkId == workId && link.SourceKind == WorkSourceKind.NovelWork
                          select 1).AnyAsync(cancellationToken);
        }

        return await db.Database.SqlQuery<bool>(
            $"""
            SELECT EXISTS (
                SELECT 1 FROM "MangaChapters" chapter
                JOIN "WorkSourceLinks" link ON link."SourceId"::text = chapter."SeriesId" AND link."SourceKind" = {(int)WorkSourceKind.MangaSeries}
                WHERE chapter."Id" = {localId} AND link."WorkId" = {workId}) AS "Value"
            """).SingleAsync(cancellationToken);
    }

    // A unit without a provider id is not stored (nothing would identify it), a volume needs a whole number, and one id counts once.
    private static IEnumerable<ProviderUnit> Identified(IReadOnlyList<ProviderUnit> units, bool volumes) =>
        units.Where(unit => !string.IsNullOrWhiteSpace(unit.ExternalId) && (!volumes || (unit.Number >= 0 && unit.Number == Math.Floor(unit.Number))))
            .DistinctBy(unit => unit.ExternalId);
}
