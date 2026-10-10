using Jularr.Web.Data;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.ExternalPlayback.Plex;

public sealed record PlexWorkMatch(PlexLibraryItem Item, long? WorkId);

/// <summary>
/// Read-only matching from Plex GUIDs to confirmed Jularr Work identities.
/// Match a whole library page with one database query, not one query per item.
/// No title guessing, conflicting matches or episode-as-series fallback.
/// </summary>
public sealed class PlexWorkMatcher(AppDbContext db)
{
    public async Task<long?> ResolveWorkIdAsync(
        PlexLibraryItem item,
        CancellationToken cancellationToken)
    {
        var result = await ResolvePageAsync([item], cancellationToken);
        return result[0].WorkId;
    }

    public async Task<IReadOnlyList<PlexWorkMatch>> ResolvePageAsync(
        IReadOnlyList<PlexLibraryItem> page,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Count > PlexLibraryClient.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(page), "Plex identity mapping is limited to one page.");
        }

        if (page.Count == 0)
        {
            return [];
        }

        var candidates = page.Select(item =>
        {
            WorkMediaType[] mediaTypes = item.Type switch
            {
                "movie" => [WorkMediaType.Movie],
                // Anime is a classification of Series, never a separate Work media type.
                "show" => [WorkMediaType.Series],
                _ => []
            };

            var pairs = item.ExternalIds.Count > 64
                ? Array.Empty<(string Provider, string ExternalId)>()
                : item.ExternalIds
                    .Where(x => x.Provider is "tmdb" or "imdb" or "tvdb" &&
                        !string.IsNullOrWhiteSpace(x.Id) && x.Id.Length <= 160)
                    .Select(x => (
                        Provider: MediaCoreNormalization.NormalizeProvider(x.Provider),
                        ExternalId: MediaCoreNormalization.NormalizeExternalId(x.Id)))
                    .Distinct()
                    .ToArray();

            return (Item: item, Types: mediaTypes, Pairs: pairs);
        }).ToArray();

        var allTypes = candidates.SelectMany(x => x.Types)
            .Distinct()
            .ToArray();
        var allPairs = candidates.SelectMany(x => x.Pairs).Distinct().ToArray();
        if (allTypes.Length == 0 || allPairs.Length == 0)
        {
            return page.Select(item => new PlexWorkMatch(item, null)).ToArray();
        }

        var providers = allPairs.Select(x => x.Provider).Distinct().ToArray();
        var externalIds = allPairs.Select(x => x.ExternalId).Distinct().ToArray();
        var fetched = await db.Set<WorkExternalIdentity>()
            .AsNoTracking()
            .Where(identity => allTypes.Contains(identity.MediaType) &&
                identity.ReviewState == MappingReviewState.Confirmed &&
                providers.Contains(identity.Provider) &&
                externalIds.Contains(identity.ExternalId))
            .Select(identity => new
            {
                identity.MediaType,
                identity.Provider,
                identity.ExternalId,
                identity.WorkId
            })
            .ToListAsync(cancellationToken);

        var index = fetched
            .GroupBy(x => (x.MediaType, x.Provider, x.ExternalId))
            .ToDictionary(
                group => group.Key,
                group => group.Select(x => x.WorkId).Distinct().ToArray());

        return candidates.Select(candidate =>
        {
            if (candidate.Types.Length == 0 || candidate.Pairs.Length == 0)
            {
                return new PlexWorkMatch(candidate.Item, null);
            }

            var ids = candidate.Types
                .SelectMany(type => candidate.Pairs.SelectMany(pair =>
                    index.TryGetValue(
                        (type, pair.Provider, pair.ExternalId), out var workIds)
                        ? workIds
                        : []))
                .Distinct()
                .Take(2)
                .ToArray();

            return new PlexWorkMatch(
                candidate.Item, ids.Length == 1 ? ids[0] : null);
        }).ToArray();
    }
}
