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
public sealed class ReadingStructureService(AppDbContext db, ReadingUnits units, WorkService works, WantedReconciler wanted, IHttpClientFactory httpClientFactory, ILogger<ReadingStructureService> logger)
{
    public async Task<ReadingStructureRefresh> RefreshAsync(long workId, CancellationToken cancellationToken)
    {
        var aniListId = await db.WorkExternalIdentities.AsNoTracking()
            .Where(identity => identity.WorkId == workId && identity.MediaType == WorkMediaType.Manga && identity.Provider == NovelAniListProvider.ProviderKey)
            .OrderByDescending(identity => identity.IsPrimary)
            .Select(identity => identity.ExternalId)
            .FirstOrDefaultAsync(cancellationToken);
        if (aniListId is null)
        {
            return new ReadingStructureRefresh(null, null, "The title has no AniList identity.");
        }

        MangaAniListCandidate? candidate;
        try
        {
            candidate = await new MangaAniListService(new MangaRepository(db), httpClientFactory).GetAsync(aniListId, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "AniList could not be asked for the structure of Work {WorkId}.", workId);
            return new ReadingStructureRefresh(null, null, "AniList could not be reached.");
        }

        if (candidate is null)
        {
            return new ReadingStructureRefresh(null, null, "AniList does not know this title.");
        }

        if (!string.IsNullOrWhiteSpace(candidate.NativeTitle))
        {
            await works.AddOrUpdateTitleAsync(workId, WorkTitleType.Native, "ja", candidate.NativeTitle, NovelAniListProvider.ProviderKey, isPrimary: false, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(candidate.Title))
        {
            await works.AddOrUpdateTitleAsync(workId, WorkTitleType.Alternative, "und", candidate.Title, NovelAniListProvider.ProviderKey, isPrimary: false, cancellationToken);
        }

        ReadingStructureRefresh refresh;
        if (candidate.VolumeCount is > 0 and var volumes)
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
        else if (candidate.ChapterCount is > 0 and var chapters)
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

        if (await db.WorkSourceLinks.AsNoTracking().Where(link => link.WorkId == workId && link.SourceKind == WorkSourceKind.MangaSeries).Select(link => (Guid?)link.SourceId).FirstOrDefaultAsync(cancellationToken) is { } seriesId
            && await new MangaRepository(db).FindByIdAsync(seriesId, cancellationToken) is { } series)
        {
            await new ReadingImportTies(db, units).TieAsync(workId, seriesId, series.SourcePath, cancellationToken);
        }

        return await ReconciledAsync(workId, refresh, cancellationToken);
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
