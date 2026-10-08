using Jularr.Web.Data;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Monitoring;
using Jularr.Web.Features.Providers;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Music;

/// <summary>
/// The owner of artists and their albums: adds an artist from the metadata provider, reads its discography into album Works and decides
/// which albums are monitored. Monitoring only decides what is wanted; the shared acquisition lifecycle (<c>MusicMonitoringService</c>
/// creates the requests, the Wanted pass searches and imports) does the rest. Reads and refreshes are idempotent: the same release group
/// always resolves to the same Work, so a refresh never duplicates an album.
/// </summary>
public sealed class MusicLibraryService(AppDbContext db, IMusicMetadataProvider provider, WorkService works, MonitoringCommands commands, TimeProvider clock)
{
    /// <summary>The albums of an artist are read again when the last read is older than this.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);

    /// <summary>Adds the artist (or returns the one already added), applies the monitor command and reads its discography.</summary>
    public async Task<MusicArtist> AddArtistAsync(string musicBrainzId, MusicMonitorMode mode, string? addedByProfileId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(musicBrainzId);
        var id = musicBrainzId.Trim().ToLowerInvariant();
        var existing = await db.MusicArtists.FirstOrDefaultAsync(artist => artist.MusicBrainzId == id, cancellationToken);
        if (existing is null)
        {
            var summary = await provider.GetArtistAsync(id, cancellationToken)
                ?? throw new MusicMetadataException("The provider does not know this artist.");
            existing = new MusicArtist
            {
                Name = summary.Name,
                SortName = summary.SortName,
                MusicBrainzId = id,
                AddedAt = clock.GetUtcNow().UtcDateTime,
                AddedByProfileId = addedByProfileId
            };
            db.MusicArtists.Add(existing);
            await db.SaveChangesAsync(cancellationToken);
        }

        // The discography is read first, so "future" can switch off the albums that exist today.
        var refreshed = await RefreshArtistAsync(existing.Id, cancellationToken);
        await SetArtistMonitorAsync(existing.Id, mode, cancellationToken);
        return refreshed;
    }

    /// <summary>Reads the discography and upserts one album Work per release group. An album without a decision of its own inherits the artist's monitoring.</summary>
    public async Task<MusicArtist> RefreshArtistAsync(Guid artistId, CancellationToken cancellationToken)
    {
        var artist = await db.MusicArtists.FirstOrDefaultAsync(candidate => candidate.Id == artistId, cancellationToken)
            ?? throw new InvalidOperationException("The artist no longer exists.");
        if (artist.MusicBrainzId is null)
        {
            return artist;
        }

        var groups = await provider.ListReleaseGroupsAsync(artist.MusicBrainzId, cancellationToken);
        var known = await db.MusicAlbums.Where(album => album.ArtistId == artistId).ToDictionaryAsync(album => album.MusicBrainzReleaseGroupId ?? string.Empty, cancellationToken);
        foreach (var group in groups)
        {
            var work = await works.EnsureWorkByExternalIdentityAsync(WorkMediaType.Music, ProviderKeys.MusicBrainz, group.MusicBrainzId, group.Title, group.Year, cancellationToken);
            if (work.CanonicalTitle != group.Title || work.Year != group.Year)
            {
                work.CanonicalTitle = group.Title;
                work.Year = group.Year;
                work.UpdatedAt = clock.GetUtcNow().UtcDateTime;
            }

            await works.AddOrUpdateTitleAsync(work.Id, WorkTitleType.Primary, "und", group.Title, ProviderKeys.MusicBrainz, isPrimary: true, cancellationToken);
            if (known.TryGetValue(group.MusicBrainzId, out var album))
            {
                album.Type = group.Type;
                album.ReleaseDate = group.ReleaseDate;
                continue;
            }

            db.MusicAlbums.Add(new MusicAlbum
            {
                WorkId = work.Id,
                ArtistId = artistId,
                Type = group.Type,
                ReleaseDate = group.ReleaseDate,
                MusicBrainzReleaseGroupId = group.MusicBrainzId,
                CreatedAt = clock.GetUtcNow().UtcDateTime
            });
        }

        artist.LastRefreshedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return artist;
    }

    /// <summary>
    /// Applies a monitor command to the artist: All monitors the artist (every release of it, today's and later ones), Future monitors only what appears
    /// from now on, None stops monitoring. A command replaces the earlier choices on the artist's albums.
    /// </summary>
    public async Task SetArtistMonitorAsync(Guid artistId, MusicMonitorMode mode, CancellationToken cancellationToken)
    {
        var artist = await db.MusicArtists.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == artistId, cancellationToken)
            ?? throw new InvalidOperationException("The artist no longer exists.");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""DELETE FROM "WorkMonitoring" WHERE "Kind" = 0 AND "TargetId" IN (SELECT "WorkId" FROM "MusicAlbums" WHERE "ArtistId" = {artistId})""",
            cancellationToken);
        var source = new MonitoringRelationSource(MonitoringRelationKind.Artist, artistId.ToString(), artist.Name, null, artist.AddedByProfileId ?? "owner");
        await commands.SetRelationAsync(source, mode != MusicMonitorMode.None, onlyFuture: mode == MusicMonitorMode.Future, cancellationToken);
    }

    public async Task SetAlbumMonitoredAsync(Guid workId, bool monitored, CancellationToken cancellationToken)
    {
        if (!await db.MusicAlbums.AnyAsync(candidate => candidate.WorkId == workId, cancellationToken))
        {
            throw new InvalidOperationException("The album no longer exists.");
        }

        await commands.SetAsync(MonitoringTargetKind.Work, workId, monitored, cancellationToken);
    }

    /// <summary>Stores the track list of an album when it has none yet. Returns false while the provider knows no tracks, so the caller waits instead of importing against an unknown list.</summary>
    public async Task<bool> EnsureTracksAsync(Guid workId, CancellationToken cancellationToken)
    {
        if (await db.WorkTracks.AnyAsync(track => track.WorkId == workId, cancellationToken))
        {
            return true;
        }

        var groupId = await db.MusicAlbums.Where(album => album.WorkId == workId).Select(album => album.MusicBrainzReleaseGroupId).FirstOrDefaultAsync(cancellationToken);
        if (groupId is null || await provider.GetTracksAsync(groupId, cancellationToken) is not { Tracks.Count: > 0 } tracks)
        {
            return false;
        }

        db.WorkTracks.AddRange(tracks.Tracks
            .GroupBy(track => (track.Disc, track.Number))
            .Select(group => group.First())
            .Select(track => new WorkTrack
            {
                WorkId = workId,
                Disc = track.Disc,
                Number = track.Number,
                Title = track.Title,
                DurationMs = track.DurationMs,
                MusicBrainzRecordingId = track.RecordingId
            }));
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
