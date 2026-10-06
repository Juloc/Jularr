using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Providers;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Music;

/// <summary>
/// Turns monitoring into Wanted: a monitored album without files, whose artist is monitored and which has been released, gets one approved
/// request on the shared lifecycle. The request is the only Wanted state; this service creates it and never searches. A title is requested at
/// most once at a time (the store's unique open-request index), an owner's rejection or a request that gave up is respected, and a library
/// album that lost its files is wanted again.
/// </summary>
public sealed class MusicMonitoringService(AppDbContext db, AcquisitionAccessStore requests, TimeProvider clock)
{
    /// <summary>How many albums one pass requests at most, so adding a long discography does not flood the lifecycle in one tick.</summary>
    public const int MaxRequestsPerPass = 25;

    public async Task<int> EnsureRequestsAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var candidates = await (
                from album in db.MusicAlbums.AsNoTracking()
                join artist in db.MusicArtists.AsNoTracking() on album.ArtistId equals artist.Id
                join work in db.Works.AsNoTracking() on album.WorkId equals work.Id
                where album.Monitored
                      && artist.Monitor != MusicMonitorMode.None
                      && album.MusicBrainzReleaseGroupId != null
                      && (album.ReleaseDate == null || album.ReleaseDate <= now)
                      && !db.MediaAssets.Any(asset => asset.WorkId == album.WorkId && asset.Kind == MediaAssetKind.Audio && db.StoredFiles.Any(file => file.MediaAssetId == asset.Id))
                orderby album.ReleaseDate descending
                select new { album.WorkId, GroupId = album.MusicBrainzReleaseGroupId!, work.CanonicalTitle, work.Year, artist.Name, artist.AddedByProfileId })
            .Take(MaxRequestsPerPass * 4)
            .ToListAsync(cancellationToken);

        var created = 0;
        foreach (var candidate in candidates)
        {
            if (created >= MaxRequestsPerPass)
            {
                break;
            }

            if (await requests.FindLatestAsync(MediaAcquisitionKind.Music, ProviderKeys.MusicBrainz, candidate.GroupId, cancellationToken) is { } latest
                && latest.Status != AcquisitionRequestStatus.Completed)
            {
                // Open, failed (waits for the owner) or rejected: monitoring never overrides what the lifecycle or the owner decided.
                continue;
            }

            var payload = new MusicRequestPayload(candidate.WorkId, candidate.Name, candidate.CanonicalTitle, candidate.Year);
            var draft = new AcquisitionRequestDraft(MediaAcquisitionKind.Music, ProviderKeys.MusicBrainz, candidate.GroupId, candidate.CanonicalTitle, candidate.Name, null, JsonSerializer.Serialize(payload, JsonSerializerOptions.Web));
            var requester = candidate.AddedByProfileId ?? "owner";
            try
            {
                await requests.CreateAsync(draft, requester, AcquisitionRequestStatus.Approved, requester, cancellationToken);
                created++;
            }
            catch (OpenRequestExistsException)
            {
                // Another pass or an owner requested the album in the same moment; the open request is the Wanted state.
            }
        }

        return created;
    }
}

/// <summary>
/// The Music part of the shared Wanted pass: reads the discography of artists whose last read is old (new albums appear and a monitored
/// artist's new release becomes wanted), then requests the monitored albums that are missing. A provider outage is logged and retried by
/// the next pass; it never fails the pass.
/// </summary>
public sealed class MusicWantedSource(AppDbContext db, MusicLibraryService library, MusicMonitoringService monitoring, ILogger<MusicWantedSource> logger) : IWantedSource
{
    /// <summary>How many artists one pass refreshes at most; the provider answers one request per second.</summary>
    public const int MaxRefreshesPerPass = 3;

    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Music;

    public async Task<int> PrepareAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var due = nowUtc - MusicLibraryService.RefreshInterval;
        var artists = await db.MusicArtists.AsNoTracking()
            .Where(artist => artist.Monitor != MusicMonitorMode.None && artist.MusicBrainzId != null && (artist.LastRefreshedAt == null || artist.LastRefreshedAt < due))
            .OrderBy(artist => artist.LastRefreshedAt)
            .Select(artist => artist.Id)
            .Take(MaxRefreshesPerPass)
            .ToListAsync(cancellationToken);
        foreach (var artistId in artists)
        {
            try
            {
                await library.RefreshArtistAsync(artistId, cancellationToken);
            }
            catch (MusicMetadataException exception)
            {
                logger.LogWarning(exception, "The discography of artist {ArtistId} could not be read; the next pass tries again.", artistId);
                break;
            }
        }

        return await monitoring.EnsureRequestsAsync(cancellationToken);
    }
}
