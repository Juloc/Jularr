using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Providers;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Music;

// A wanted album is requested by its MusicBrainz release group with the artist and album its executor searches for.
public sealed class MusicRequestDrafter(AppDbContext db) : IWantedRequestDrafter
{
    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Music;

    public async Task<WantedRequestDraft?> DraftAsync(Guid workId, CancellationToken cancellationToken)
    {
        var album = await (
                from a in db.MusicAlbums.AsNoTracking()
                join artist in db.MusicArtists.AsNoTracking() on a.ArtistId equals artist.Id
                join work in db.Works.AsNoTracking() on a.WorkId equals work.Id
                where a.WorkId == workId && a.MusicBrainzReleaseGroupId != null
                select new { a.WorkId, GroupId = a.MusicBrainzReleaseGroupId!, work.CanonicalTitle, work.Year, artist.Name, artist.AddedByProfileId })
            .FirstOrDefaultAsync(cancellationToken);
        if (album is null)
        {
            return null;
        }

        var payload = new MusicRequestPayload(album.WorkId, album.Name, album.CanonicalTitle, album.Year);
        var requester = album.AddedByProfileId ?? "owner";
        return new WantedRequestDraft(
            new AcquisitionRequestDraft(MediaAcquisitionKind.Music, ProviderKeys.MusicBrainz, album.GroupId, album.CanonicalTitle, album.Name, null, JsonSerializer.Serialize(payload, JsonSerializerOptions.Web)),
            requester);
    }
}
