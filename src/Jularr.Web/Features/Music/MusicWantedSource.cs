using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Library;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Music;

/// <summary>
/// The Music part of the shared Wanted pass: reads the discography of artists whose last read is old, so a monitored artist's new release becomes
/// wanted. A provider outage is logged and retried by the next pass; it never fails the pass.
/// </summary>
public sealed class MusicWantedSource(AppDbContext db, MusicLibraryService library, ILogger<MusicWantedSource> logger) : IWantedSource
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

        return 0;
    }
}

// An installed album is only as good as its weakest track; it is upgradable while that quality is below what its profile wants.
public sealed class MusicUpgradeAssessor(QualityProfileStore profiles, CanonicalMediaStorageService storage) : IUpgradeAssessor
{
    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Music;

    public WantedTargetKind TargetKind => WantedTargetKind.Work;

    public async Task<IReadOnlyList<HeldTarget>> UpgradableAsync(Guid workId, IReadOnlyList<HeldTarget> held, CancellationToken cancellationToken)
    {
        var profile = await profiles.ResolveAsync(MediaAcquisitionKind.Music, workId, cancellationToken);
        return UpgradePolicy.Assess(profile, MusicInstalledQuality.OfAlbum(profile, await storage.ListAudioFilesAsync(workId, cancellationToken))).IsUpgradable ? held : [];
    }
}
