using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Manga;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Novels;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.ReadingAcquisition;

/// <param name="Problem">Why AniList could not be asked or does not know the title; the Work is left as it was.</param>
public sealed record ReadingStructureRefresh(ReadingUnitEnrichment? Volumes, ReadingUnitEnrichment? Chapters, string? Problem)
{
    public bool Changed => Volumes is { Created: > 0 } or { Updated: > 0 } || Chapters is { Created: > 0 } or { Updated: > 0 };
}

/// <summary>
/// The volumes and chapters AniList states for a Manga Work. Only what AniList counts becomes a unit, each with a stable provider identity: the volumes of a
/// title whose volumes are counted, else the chapters of a title whose chapters are counted. An ongoing title with neither stays a single wanted unit, and a
/// count is never guessed; a refresh adds units that became known and never removes or renumbers a unit that has files tied to it.
/// </summary>
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

        if (candidate.VolumeCount is > 0 and var volumes)
        {
            var created = await units.EnrichVolumesAsync(
                workId,
                NovelAniListProvider.ProviderKey,
                [.. Enumerable.Range(1, volumes).Select(number => new ProviderUnit($"{aniListId}:v{number}", number, null))],
                cancellationToken);
            return await ReconciledAsync(workId, new ReadingStructureRefresh(created, null, null), cancellationToken);
        }

        if (candidate.ChapterCount is > 0 and var chapters)
        {
            var created = await units.EnrichChaptersAsync(
                workId,
                NovelAniListProvider.ProviderKey,
                [.. Enumerable.Range(1, chapters).Select(number => new ProviderUnit($"{aniListId}:c{number}", number, null))],
                cancellationToken);
            return await ReconciledAsync(workId, new ReadingStructureRefresh(null, created, null), cancellationToken);
        }

        return new ReadingStructureRefresh(null, null, null);
    }

    /// <summary>The other names the Work is known by (native, English, romaji, synonyms): search aliases next to the title the request carries.</summary>
    public async Task<IReadOnlyList<string>> AliasesAsync(long workId, CancellationToken cancellationToken) =>
        await db.WorkTitles.AsNoTracking()
            .Where(title => title.WorkId == workId)
            .OrderBy(title => title.CreatedAt)
            .Select(title => title.Value)
            .Distinct()
            .Take(12)
            .ToListAsync(cancellationToken);

    // New units are wanted from the moment they exist, not at the next scheduled Wanted pass.
    private async Task<ReadingStructureRefresh> ReconciledAsync(long workId, ReadingStructureRefresh refresh, CancellationToken cancellationToken)
    {
        if (refresh.Changed)
        {
            await wanted.ReconcileAsync(workId, cancellationToken);
        }

        return refresh;
    }
}
