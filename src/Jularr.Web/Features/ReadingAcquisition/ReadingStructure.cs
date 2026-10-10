using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Manga;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Novels;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.ReadingAcquisition;

public sealed record ReadingStructureRefresh(ReadingUnitEnrichment? Volumes, ReadingUnitEnrichment? Chapters, string? Problem)
{
    public bool Changed => Volumes is { Created: > 0 } or { Updated: > 0 } || Chapters is { Created: > 0 } or { Updated: > 0 };
}

// Only what AniList counts becomes a unit: the volumes of a title whose volumes are counted, else its chapters. A title with neither stays one wanted unit and a
// count is never guessed. A refresh adds units and never removes or renumbers one; library files that name their volume or chapters are tied to the units.
public sealed class ReadingStructureService(AppDbContext db, ReadingUnits units, WorkService works, WantedReconciler wanted, IHttpClientFactory httpClientFactory, ILogger<ReadingStructureService> logger, MangaVersionSelector? versions = null, NovelAniListProvider? novelProvider = null)
{
    public async Task<ReadingStructureRefresh> RefreshAsync(long workId, CancellationToken cancellationToken)
    {
        var type = await db.Works.AsNoTracking().Where(work => work.Id == workId).Select(work => work.MediaType).FirstOrDefaultAsync(cancellationToken);
        var novel = type == WorkMediaType.LightNovel;
        var aniListId = await db.WorkExternalIdentities.AsNoTracking()
            .Where(identity => identity.WorkId == workId && identity.MediaType == type && identity.Provider == NovelAniListProvider.ProviderKey)
            .OrderByDescending(identity => identity.IsPrimary)
            .Select(identity => identity.ExternalId)
            .FirstOrDefaultAsync(cancellationToken);
        if (aniListId is null)
        {
            return new ReadingStructureRefresh(null, null, "The title has no AniList identity.");
        }

        (string? Title, string? NativeTitle, int? Volumes, int? Chapters)? candidate;
        try
        {
            if (novel)
            {
                var found = novelProvider is null ? null : await novelProvider.GetAsync(aniListId, cancellationToken);
                candidate = found is null ? null : (found.PreferredTitle, found.NativeTitle, found.VolumeCount, found.ChapterCount);
            }
            else
            {
                var found = await new MangaAniListService(new MangaRepository(db), httpClientFactory).GetAsync(aniListId, cancellationToken);
                candidate = found is null ? null : (found.Title, found.NativeTitle, found.VolumeCount, found.ChapterCount);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or TaskCanceledException or JsonException or NovelMetadataProviderException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "AniList could not be asked for the structure of Work {WorkId}.", workId);
            return new ReadingStructureRefresh(null, null, "AniList could not be reached.");
        }

        if (candidate is null)
        {
            return new ReadingStructureRefresh(null, null, "AniList does not know this title.");
        }

        if (!string.IsNullOrWhiteSpace(candidate.Value.NativeTitle))
        {
            await works.AddOrUpdateTitleAsync(workId, WorkTitleType.Native, "ja", candidate.Value.NativeTitle, NovelAniListProvider.ProviderKey, isPrimary: false, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(candidate.Value.Title))
        {
            await works.AddOrUpdateTitleAsync(workId, WorkTitleType.Alternative, "und", candidate.Value.Title, NovelAniListProvider.ProviderKey, isPrimary: false, cancellationToken);
        }

        ReadingStructureRefresh refresh;
        if (candidate.Value.Volumes is > 0 and var volumes)
        {
            refresh = new ReadingStructureRefresh(
                await units.EnrichVolumesAsync(
                    workId,
                    NovelAniListProvider.ProviderKey,
                    [.. Enumerable.Range(1, volumes).Select(number => new ProviderUnit($"{aniListId}:v{number}", number, null))],
                    cancellationToken),
                null,
                null);
        }
        else if (!novel && candidate.Value.Chapters is > 0 and var chapters)
        {
            refresh = new ReadingStructureRefresh(
                null,
                await units.EnrichChaptersAsync(
                    workId,
                    NovelAniListProvider.ProviderKey,
                    [.. Enumerable.Range(1, chapters).Select(number => new ProviderUnit($"{aniListId}:c{number}", number, null))],
                    cancellationToken),
                null);
        }
        else
        {
            return new ReadingStructureRefresh(null, null, null);
        }

        if (novel)
        {
            await TieNovelVolumesAsync(workId, cancellationToken);
        }
        else if (await db.WorkSourceLinks.AsNoTracking().Where(link => link.WorkId == workId && link.SourceKind == WorkSourceKind.MangaSeries).Select(link => (Guid?)link.SourceId).FirstOrDefaultAsync(cancellationToken) is { } seriesId
            && await new MangaRepository(db).FindByIdAsync(seriesId, cancellationToken) is { } series)
        {
            await new ReadingImportTies(db, units).TieAsync(workId, seriesId, series.SourcePath, cancellationToken);
            if (versions is not null)
            {
                await versions.ReselectAsync(workId, cancellationToken);
            }
        }

        return await ReconciledAsync(workId, refresh, cancellationToken);
    }

    // An EPUB volume of the library whose own title or file name states its number is that published volume; one that states nothing stays untied rather than guessed.
    private async Task TieNovelVolumesAsync(long workId, CancellationToken cancellationToken)
    {
        var canonical = await db.WorkVolumes.AsNoTracking().Where(volume => volume.WorkId == workId && volume.ExternalId != null).ToDictionaryAsync(volume => volume.Number, volume => volume.Id, cancellationToken);
        var local = await (from link in db.WorkSourceLinks.AsNoTracking()
                           join volume in db.NovelVolumes.AsNoTracking() on link.SourceId equals volume.WorkId
                           where link.WorkId == workId && link.SourceKind == WorkSourceKind.NovelWork && volume.Kind == NovelVolumeKinds.Epub
                           select new { volume.Id, volume.Number, volume.Title, volume.SourceFileName }).ToListAsync(cancellationToken);
        foreach (var volume in local)
        {
            var stated = NovelEpubImportService.ParseVolumeNumber(volume.Title) ?? NovelEpubImportService.ParseVolumeNumber(Path.GetFileNameWithoutExtension(volume.SourceFileName));
            if (stated is { } number && number == volume.Number && canonical.TryGetValue(number, out var unit))
            {
                await units.TieAsync(workId, WorkUnitLocalKind.NovelVolume, volume.Id.ToString(), unit, isOwnerMapping: false, cancellationToken);
            }
        }
    }

    public async Task<IReadOnlyList<string>> AliasesAsync(long workId, CancellationToken cancellationToken) =>
        await db.WorkTitles.AsNoTracking()
            .Where(title => title.WorkId == workId)
            .OrderBy(title => title.CreatedAt)
            .Select(title => title.Value)
            .Distinct()
            .Take(12)
            .ToListAsync(cancellationToken);

    // New units and new ties are wanted or dropped from the moment they exist, not at the next scheduled Wanted pass.
    private async Task<ReadingStructureRefresh> ReconciledAsync(long workId, ReadingStructureRefresh refresh, CancellationToken cancellationToken)
    {
        await wanted.ReconcileAsync(workId, cancellationToken);
        return refresh;
    }
}
