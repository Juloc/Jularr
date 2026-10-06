using Jularr.Web.Data;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Tests;

/// <summary>Gives a Work a cached poster in the canonical Work artwork, as the metadata refresh leaves it.</summary>
internal static class RequestArtworkSeed
{
    public static Task AddPosterAsync(AppDbContext db, Guid workId)
    {
        var key = Jularr.Web.Features.Artwork.WorkArtworkCache.CacheKey(workId, WorkArtworkSlot.Poster, "", "tmdb", "/poster.jpg");
        var poster = new WorkArtworkCandidate(WorkArtworkSlot.Poster, "", "/poster.jpg", new Uri("https://image.tmdb.org/t/p/w780/poster.jpg"), 500, 750, 5, 1);
        return new WorkMetadataStore(db).UpsertArtworkAsync(workId, poster, "tmdb", key, DateTime.UtcNow, CancellationToken.None);
    }
}
