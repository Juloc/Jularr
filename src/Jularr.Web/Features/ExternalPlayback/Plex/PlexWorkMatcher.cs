using Jularr.Web.Data;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.ExternalPlayback.Plex;

/// <summary>
/// Bounded, unambiguous mapping from a Plex item to the canonical Jularr Work.
/// No title/year guessing, no implicit episode-to-series fallback, and no
/// side effects on requests, libraries or playback progress.
/// </summary>
public sealed class PlexWorkMatcher(AppDbContext db)
{
    public async Task<long?> ResolveWorkIdAsync(
        PlexLibraryItem item,
        CancellationToken cancellationToken)
    {
        var mediaType = item.Type switch
        {
            "movie" => WorkMediaType.Movie,
            "show" => WorkMediaType.Series,
            _ => (WorkMediaType?)null
        };

        if (mediaType is null || item.ExternalIds.Count == 0 ||
            item.ExternalIds.Count > 64)
        {
            return null;
        }

        var pairs = item.ExternalIds
            .Where(x => x.Provider is "tmdb" or "imdb" or "tvdb" &&
                !string.IsNullOrWhiteSpace(x.Id) && x.Id.Length <= 160)
            .Select(x => (
                Provider: MediaCoreNormalization.NormalizeProvider(x.Provider),
                ExternalId: MediaCoreNormalization.NormalizeExternalId(x.Id)))
            .Distinct()
            .ToArray();

        if (pairs.Length == 0)
        {
            return null;
        }

        var providers = pairs.Select(x => x.Provider).Distinct().ToArray();
        var externalIds = pairs.Select(x => x.ExternalId).Distinct().ToArray();

        var possibilities = await db.Set<WorkExternalIdentity>()
            .AsNoTracking()
            .Where(identity => identity.MediaType == mediaType &&
                identity.ReviewState == MappingReviewState.Confirmed &&
                providers.Contains(identity.Provider) &&
                externalIds.Contains(identity.ExternalId))
            .Select(identity => new
            {
                identity.Provider,
                identity.ExternalId,
                identity.WorkId
            })
            .ToListAsync(cancellationToken);

        var workIds = possibilities
            .Where(identity => pairs.Contains((
                identity.Provider,
                identity.ExternalId)))
            .Select(identity => identity.WorkId)
            .Distinct()
            .Take(2)
            .ToArray();

        return workIds.Length == 1 ? workIds[0] : null;
    }
}
