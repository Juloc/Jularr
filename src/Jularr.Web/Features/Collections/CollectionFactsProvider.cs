using Jularr.Web.Data;
using Jularr.Web.Features.Franchises;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.MediaFacts;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Watchlist;
using Jularr.Web.Ui;
using Microsoft.EntityFrameworkCore;
using MediaFactsProjection = Jularr.Web.Features.MediaFacts.MediaFacts;

namespace Jularr.Web.Features.Collections;

/// <summary>
/// Builds the canonical <see cref="WorkFactSnapshot"/> for works from the data Jularr already has (#427):
/// the media core <see cref="Work"/> row, its bridged per-type record's <see cref="MediaFacts"/> (status,
/// counts, per-language coverage) via <see cref="MediaFactsService"/>, and its franchise membership from
/// <see cref="FranchiseStore"/>. This is the single integration seam between the pure rule evaluator and the
/// rest of the app; nothing here evaluates a rule, so the evaluator stays testable without a database.
/// </summary>
public sealed class CollectionFactsProvider(
    AppDbContext db,
    MediaFactsService mediaFacts,
    FranchiseStore franchises)
{
    /// <summary>A fact snapshot for every media-core work, ready for smart-collection evaluation.</summary>
    public Task<IReadOnlyList<WorkFactSnapshot>> BuildAllAsync(CancellationToken cancellationToken) =>
        BuildAsync(null, cancellationToken);

    /// <summary>Fact snapshots for a specific set of works, used to render a collection's stored membership.</summary>
    public Task<IReadOnlyList<WorkFactSnapshot>> BuildForWorksAsync(
        IReadOnlyCollection<long> workIds,
        CancellationToken cancellationToken) =>
        workIds.Count == 0
            ? Task.FromResult<IReadOnlyList<WorkFactSnapshot>>([])
            : BuildAsync(workIds, cancellationToken);

    private async Task<IReadOnlyList<WorkFactSnapshot>> BuildAsync(
        IReadOnlyCollection<long>? workIds,
        CancellationToken cancellationToken)
    {
        var query = db.Works.AsNoTracking().AsQueryable();
        if (workIds is not null)
        {
            query = query.Where(x => workIds.Contains(x.Id));
        }

        var works = await query
            .OrderBy(x => x.CanonicalTitle)
            .ToListAsync(cancellationToken);

        var sourceLinks = (await db.WorkSourceLinks.AsNoTracking().ToListAsync(cancellationToken))
            .GroupBy(x => x.WorkId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var aniListIdentities = (await db.WorkExternalIdentities.AsNoTracking()
                .Where(x => x.Provider == AniListMetadataProvider.ProviderKey)
                .Select(x => new { x.WorkId, x.MediaType, x.ExternalId })
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.WorkId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var snapshots = new List<WorkFactSnapshot>(works.Count);
        foreach (var work in works)
        {
            var links = sourceLinks.GetValueOrDefault(work.Id) ?? [];
            var facts = await ResolveFactsAsync(work, links, cancellationToken);
            var franchiseIds = await ResolveFranchiseIdsAsync(
                aniListIdentities.GetValueOrDefault(work.Id) is { } ids
                    ? ids.Select(x => new WatchlistIdentity(
                        WorkMediaTypes.ToWatchlist(x.MediaType),
                        AniListMetadataProvider.ProviderKey,
                        x.ExternalId))
                    : [],
                cancellationToken);

            snapshots.Add(new WorkFactSnapshot(
                work.Id,
                work.MediaType,
                work.CanonicalTitle,
                work.Year ?? facts.ReleaseYear,
                facts.Status,
                facts.PrimaryUnitCount,
                facts.SecondaryUnitCount,
                facts.RuntimeMinutes,
                WorkFactSnapshot.LanguagesFrom(facts),
                franchiseIds));
        }

        return snapshots;
    }

    /// <summary>
    /// The detailed <see cref="MediaFacts"/> of a work through its legacy bridge. Anime, manga and light
    /// novels have a per-type facts projection; other types (books/movies/series) carry no extra facts here
    /// yet, so their snapshot is built from the <see cref="Work"/> row alone rather than a fabricated one.
    /// </summary>
    private async Task<MediaFactsProjection> ResolveFactsAsync(
        Work work,
        IReadOnlyList<WorkSourceLink> links,
        CancellationToken cancellationToken)
    {
        foreach (var link in links)
        {
            switch (link.SourceKind)
            {
                case WorkSourceKind.Anime:
                    return await mediaFacts.GetAnimeFactsAsync(link.SourceId, cancellationToken);
                case WorkSourceKind.MangaSeries:
                    return await mediaFacts.GetMangaFactsAsync(link.SourceId, cancellationToken);
                case WorkSourceKind.NovelWork:
                    return await mediaFacts.GetNovelFactsAsync(link.SourceId, cancellationToken);
            }
        }

        var kind = work.MediaType switch
        {
            WorkMediaType.Anime => MediaBannerKind.Anime,
            WorkMediaType.Manga => MediaBannerKind.Manga,
            WorkMediaType.LightNovel => MediaBannerKind.LightNovel,
            _ => MediaBannerKind.Book
        };
        return MediaFactsProjection.Empty(kind);
    }

    private async Task<IReadOnlyList<Guid>> ResolveFranchiseIdsAsync(
        IEnumerable<WatchlistIdentity> identities,
        CancellationToken cancellationToken)
    {
        var ids = new HashSet<Guid>();
        foreach (var identity in identities)
        {
            if (!FranchiseService.CanSeed(identity))
            {
                continue;
            }

            foreach (var franchise in await franchises.FindForMemberAsync(identity, cancellationToken))
            {
                ids.Add(franchise.Id);
            }
        }

        return [.. ids];
    }
}
