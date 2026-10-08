using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Monitoring;
using Jularr.Web.Features.Providers;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Music;

/// <summary>
/// The Music upgrade scan: an installed album whose quality is below what its profile wants is wanted again through its completed request.
/// </summary>
public sealed class MusicMonitoringService(AppDbContext db, AcquisitionAccessStore requests, MonitoringResolver monitoring, TimeProvider clock, QualityProfileStore? profiles = null, CanonicalMediaStorageService? storage = null)
{
    /// <summary>How many monitored albums one page of a scan looks at.</summary>
    private const int PageSize = 500;

    /// <summary>How many albums one upgrade scan looks at; the rest follow in later scans because the monitored albums are read oldest request first.</summary>
    public const int MaxUpgradeScanAlbums = 200;

    /// <summary>How many albums one pass requests at most, so adding a long discography does not flood the lifecycle in one tick.</summary>
    public const int MaxRequestsPerPass = 25;

    /// <summary>
    /// A monitored album whose request is Completed but whose installed quality is below what its profile wants (the profile or its cutoff changed
    /// after the import) becomes Wanted again through the same request, so the tried releases are remembered and the shared lifecycle searches,
    /// grabs and imports the better release. At most once per <see cref="UpgradeScanState.Interval"/>; only albums with a recorded quality are looked at.
    /// </summary>
    public async Task<int> ReopenUpgradesAsync(UpgradeScanState scans, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (profiles is null || storage is null || !scans.TryStart(MediaAcquisitionKind.Music, nowUtc, UpgradeScanState.Interval))
        {
            return 0;
        }

        var after = scans.CursorOf(MediaAcquisitionKind.Music);
        var monitored = await monitoring.MonitoredWorkIdsAsync(WorkMediaType.Music, after, PageSize, cancellationToken);
        var albums = await (
                from album in db.MusicAlbums.AsNoTracking()
                where monitored.Contains(album.WorkId) && album.MusicBrainzReleaseGroupId != null
                      && db.MediaAssets.Any(asset => asset.WorkId == album.WorkId && asset.Kind == MediaAssetKind.Audio
                                                      && db.WorkVersions.Any(version => version.Id == asset.WorkVersionId && version.Quality != null)
                                                      && db.StoredFiles.Any(file => file.MediaAssetId == asset.Id))
                orderby album.WorkId
                select new { album.WorkId, GroupId = album.MusicBrainzReleaseGroupId! })
            .Take(MaxUpgradeScanAlbums)
            .ToListAsync(cancellationToken);
        var pageFull = monitored.Count == PageSize;
        scans.Continue(MediaAcquisitionKind.Music, pageFull ? monitored[^1] : Guid.Empty, reachedEnd: !pageFull);
        var reopened = 0;
        foreach (var album in albums)
        {
            var profile = await profiles.ResolveAsync(MediaAcquisitionKind.Music, album.WorkId, cancellationToken);
            if (!UpgradePolicy.Assess(profile, MusicInstalledQuality.OfAlbum(profile, await storage.ListAudioFilesAsync(album.WorkId, cancellationToken))).IsUpgradable
                || await requests.FindLatestAsync(MediaAcquisitionKind.Music, ProviderKeys.MusicBrainz, album.GroupId, cancellationToken) is not { Status: AcquisitionRequestStatus.Completed } latest)
            {
                continue;
            }

            await requests.PatchPayloadAsync(latest.Id, stored => JsonSerializer.Serialize(MusicRequestPayload.Of(latest with { PayloadJson = stored }) with { Searches = 0, NextSearchUtc = null, LastProblem = null }, JsonSerializerOptions.Web), cancellationToken);
            if (await requests.TryTransitionStatusAsync(latest.Id, [AcquisitionRequestStatus.Completed], AcquisitionRequestStatus.Approved, "A better release is wanted for the installed quality. Searching.", operationId: null, resultUrl: MusicLinks.AlbumPath(album.WorkId), clearOperation: true, cancellationToken) is not null)
            {
                reopened++;
            }
        }

        return reopened;
    }
}

/// <summary>
/// The Music part of the shared Wanted pass: reads the discography of artists whose last read is old (new albums appear and a monitored
/// artist's new release becomes wanted), then reopens upgrades. A provider outage is logged and retried by
/// the next pass; it never fails the pass.
/// </summary>
public sealed class MusicWantedSource(AppDbContext db, MusicLibraryService library, MusicMonitoringService monitoring, ILogger<MusicWantedSource> logger, UpgradeScanState? scans = null) : IWantedSource
{
    /// <summary>How many artists one pass refreshes at most; the provider answers one request per second.</summary>
    public const int MaxRefreshesPerPass = 3;

    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Music;

    public async Task<int> PrepareAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var due = nowUtc - MusicLibraryService.RefreshInterval;
        var monitoredArtists = (await db.Database.SqlQuery<string>($"""SELECT "SourceKey" AS "Value" FROM "WorkMonitoringSources" WHERE "Kind" = 3""").ToListAsync(cancellationToken))
            .Select(key => Guid.TryParse(key, out var id) ? id : Guid.Empty)
            .ToList();
        var artists = await db.MusicArtists.AsNoTracking()
            .Where(artist => monitoredArtists.Contains(artist.Id) && artist.MusicBrainzId != null && (artist.LastRefreshedAt == null || artist.LastRefreshedAt < due))
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

        return scans is null ? 0 : await monitoring.ReopenUpgradesAsync(scans, nowUtc, cancellationToken);
    }
}
