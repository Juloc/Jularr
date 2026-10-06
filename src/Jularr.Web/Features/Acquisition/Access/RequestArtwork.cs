using Jularr.Web.Data;
using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Access;

/// <summary>
/// The poster every request surface (My requests, Admin Requests, Wanted, Manual Search) shows for a request. A request names its title by
/// provider identity, so the poster is resolved through the canonical artwork owner of that title and never copied into the request: the
/// locally cached Work artwork of a Movie or Series, the cached anime artwork of an anime in the library, and for titles that are not
/// resolvable to local artwork the cover the request was made from.
/// </summary>
public sealed class RequestArtworkResolver(AppDbContext db, VideoRequestWorkResolver works)
{
    /// <summary>The cached poster address of each Work, in the artwork language of the viewer; a Work without a cached poster is left out.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> ResolveWorkPostersAsync(IReadOnlyCollection<Guid> workIds, string profileId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (workIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var rows = await new WorkMetadataStore(db).LoadCardMetadataAsync([.. workIds.Distinct()], cancellationToken);
        var cards = WorkMetadataPresentation.ResolveCards(rows, await WorkMetadataLocales.ForProfileAsync(db, profileId, cancellationToken));
        return cards.Where(card => card.Value.PosterUrl is not null).ToDictionary(card => card.Key, card => card.Value.PosterUrl!);
    }

    /// <summary>The poster address per request id; a request without any poster is left out.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> ResolvePostersAsync(IReadOnlyCollection<AcquisitionRequest> requests, string profileId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        var posters = new Dictionary<Guid, string>(requests.Count);
        if (requests.Count == 0)
        {
            return posters;
        }

        var workByRequest = await works.ResolveAsync(requests, cancellationToken);
        var workPosters = await ResolveWorkPostersAsync([.. workByRequest.Values.Select(work => work.WorkId)], profileId, cancellationToken);
        foreach (var (requestId, work) in workByRequest)
        {
            if (workPosters.TryGetValue(work.WorkId, out var workPoster))
            {
                posters[requestId] = workPoster;
            }
        }

        var anime = requests.Where(request => request.Kind == MediaAcquisitionKind.Anime).ToArray();
        if (anime.Length > 0)
        {
            var externalIds = anime.Select(request => request.ExternalId).Distinct().ToArray();
            var local = await db.AnimeMetadata.AsNoTracking()
                .Where(metadata => metadata.Provider == AniListMetadataProvider.ProviderKey && externalIds.Contains(metadata.ExternalId))
                .Select(metadata => new { metadata.ExternalId, metadata.AnimeId, metadata.CoverImageUrl })
                .ToListAsync(cancellationToken);
            foreach (var request in anime)
            {
                if (local.FirstOrDefault(metadata => metadata.ExternalId == request.ExternalId) is { } match && AnimeArtworkStore.ResolvePosterUrl(match.AnimeId, match.CoverImageUrl) is { } animePoster)
                {
                    posters[request.Id] = animePoster;
                }
            }
        }

        // Media types without a local artwork owner keep the cover of the title card the request was made from.
        foreach (var request in requests)
        {
            if (!posters.ContainsKey(request.Id) && request.Kind is not (MediaAcquisitionKind.Movie or MediaAcquisitionKind.Tv) && !string.IsNullOrWhiteSpace(request.CoverImageUrl))
            {
                posters[request.Id] = request.CoverImageUrl;
            }
        }

        return posters;
    }
}
