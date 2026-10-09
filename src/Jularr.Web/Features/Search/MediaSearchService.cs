using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Search;

/// <summary>
/// The one global search over what is on this server (#434): anime, light novels, manga, books,
/// movies, series, audiobooks and franchises in one ranked, bounded, paged result list.
/// <list type="bullet">
/// <item>Matching and ranking are PostgreSQL full-text + <c>pg_trgm</c> (#570, see
/// <see cref="MediaSearchQuery"/>): exact title, then prefix, then full-text, then fuzzy. They never
/// depend on an external provider.</item>
/// <item><b>Canonical grouping.</b> Source records that bridge to the same media-core <c>Work</c>
/// (<c>WorkSourceLink</c>) are one result row, not one per source table; the row lists every type it
/// covers. A record with no bridge yet stands alone.</item>
/// <item><b>Franchises</b> are their own rows: a franchise matches by its title or by the title of one
/// of its works (and lists how many works the profile may see).</item>
/// <item><b>Media Facts filters</b> (type, local, monitored, wanted, language, genre, year) apply to
/// the ranked candidates. Type and visibility narrow the query itself; the rest need per-title facts
/// and so filter the best <see cref="CandidateCap"/> matches, whose facts are loaded with a fixed
/// number of set-based queries.</item>
/// <item><b>Visibility.</b> <see cref="MediaSearchRequest.VisibleMediaTypes"/> comes from the profile's
/// media capabilities; a hidden type is never queried, so it cannot appear as a row, inside a
/// canonical row or through a franchise.</item>
/// </list>
/// <para>
/// Seam for the remote half (discovery providers): a provider result is merged into this list by
/// resolving its provider identity to a canonical work (<c>WorkQueryService.FindWorkIdByExternalIdentityAsync</c>)
/// and comparing it with <see cref="MediaSearchResult.WorkId"/>. This service stays local so search
/// keeps working when a provider is down.
/// </para>
/// </summary>
public sealed class MediaSearchService(
    AppDbContext db,
    AnimeMonitoring animeMonitoring,
    AcquisitionAccessStore requests,
    IInstanceModuleService? instanceModules = null)
{
    public const int DefaultLimit = 40;
    public const int MaxLimit = 100;

    /// <summary>Longer queries are cut here: no title is longer, and it bounds the trigram work of one request.</summary>
    public const int MaxQueryLength = 200;

    /// <summary>The best matches whose facts are loaded and filtered; also the most a query can ever page through.</summary>
    public const int CandidateCap = 500;

    public async Task<MediaSearchPage> SearchAsync(
        MediaSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var limit = Math.Clamp(request.Limit, 1, MaxLimit);
        var offset = Math.Clamp(request.Offset, 0, CandidateCap);
        var query = (request.Query ?? string.Empty).Trim();
        if (query.Length > MaxQueryLength)
        {
            query = query[..MaxQueryLength].TrimEnd();
        }

        if (query.Length == 0)
        {
            return MediaSearchPage.Empty(limit, offset);
        }

        var filters = request.Filters ?? MediaSearchFilters.None;
        var types = SearchableTypes(filters, request.VisibleMediaTypes);
        if (instanceModules is not null)
        {
            var instance = await instanceModules.GetAsync(cancellationToken);
            types = types
                .Where(type => instance.IsEnabled(MediaSearchTypes.ToInstanceModule(type)))
                .ToArray();
        }

        if (types.Count == 0)
        {
            return MediaSearchPage.Empty(limit, offset);
        }

        var source = new MediaSearchQuery(db);
        var variants = await source.FindWorksAsync(query, types, CandidateCap, cancellationToken);

        // A franchise has no facts of its own, so it never passes a fact filter.
        IReadOnlyList<MediaSearchFranchiseHit> franchiseHits = filters.HasFactFilters
            ? []
            : await source.FindFranchisesAsync(
                query,
                [.. types.Select(MediaSearchTypes.ToWorkMediaType).Distinct()],
                CandidateCap,
                cancellationToken);

        var (workOf, workYear) = await ResolveWorksAsync(variants, cancellationToken);
        var groups = MediaSearchGrouping.Group(variants, workOf);
        var variantFacts = await new MediaSearchFactsLoader(db, animeMonitoring, requests)
            .LoadAsync(variants, cancellationToken);

        var works = groups
            .Select(group => new MediaSearchResult(
                MediaSearchResultKind.Work,
                group.Best.Id,
                group.WorkId,
                group.Best.Type,
                group.Best.Title,
                group.Best.Score,
                0,
                group.Variants,
                MediaSearchGrouping.Aggregate(
                    group.Variants.Select(variant =>
                        variantFacts.GetValueOrDefault((variant.Type, variant.Id), MediaSearchFacts.Unknown)),
                    group.WorkId is { } workId ? workYear.GetValueOrDefault(workId) : null)))
            .ToArray();

        // Filter values are those of the matches before the fact filters narrowed them, so the
        // controls keep offering what the query can reach.
        var facets = MediaSearchGrouping.BuildFacets([.. works.Select(work => work.Facts)]);

        var kept = works
            .Where(work => MediaSearchGrouping.Matches(work.Facts, filters))
            .Concat(franchiseHits.Select(hit => new MediaSearchResult(
                MediaSearchResultKind.Franchise,
                hit.Id,
                null,
                null,
                hit.Title,
                hit.Score,
                hit.MemberCount,
                [],
                MediaSearchFacts.Unknown)));

        var ordered = MediaSearchGrouping.Order(kept).ToArray();
        return new MediaSearchPage(
            [.. ordered.Skip(offset).Take(limit)],
            ordered.Length,
            offset,
            limit,
            facets);
    }

    /// <summary>The source types to query: the filter's types (all when none) that the profile may browse.</summary>
    private static IReadOnlyCollection<MediaSearchType> SearchableTypes(
        MediaSearchFilters filters,
        IReadOnlyCollection<WorkMediaType>? visible) =>
    [
        .. MediaSearchTypes.All.Where(type =>
            (filters.Types is not { Count: > 0 } || filters.Types.Contains(type))
            && (visible is null || visible.Contains(MediaSearchTypes.ToWorkMediaType(type))))
    ];

    // The canonical work each variant is bridged to (media core WorkSourceLink) and the year the work
    // itself carries, in one query for every candidate.
    private async Task<(Dictionary<(MediaSearchType Type, Guid Id), Guid> WorkOf, Dictionary<Guid, int?> Years)>
        ResolveWorksAsync(IReadOnlyCollection<MediaSearchVariant> variants, CancellationToken cancellationToken)
    {
        var workOf = new Dictionary<(MediaSearchType, Guid), Guid>();
        var years = new Dictionary<Guid, int?>();
        if (variants.Count == 0)
        {
            return (workOf, years);
        }

        var ids = variants.Select(variant => variant.Id).Distinct().ToArray();
        var links = await (
            from link in db.Set<WorkSourceLink>().AsNoTracking()
            where ids.Contains(link.SourceId)
            join work in db.Set<Work>().AsNoTracking() on link.WorkId equals work.Id
            select new { link.SourceKind, link.SourceId, link.WorkId, work.Year })
            .ToListAsync(cancellationToken);

        var bySource = links.ToDictionary(link => (link.SourceKind, link.SourceId));
        foreach (var variant in variants)
        {
            if (bySource.TryGetValue((MediaSearchTypes.ToSourceKind(variant.Type), variant.Id), out var link))
            {
                workOf[(variant.Type, variant.Id)] = link.WorkId;
                years[link.WorkId] = link.Year;
            }
        }

        return (workOf, years);
    }
}
