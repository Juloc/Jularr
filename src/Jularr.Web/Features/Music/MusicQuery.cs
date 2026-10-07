using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Providers;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Music;

/// <summary>Where an album stands, derived from its files, its monitoring and the one shared request lifecycle; it is never stored.</summary>
public enum MusicAlbumState
{
    /// <summary>Not monitored and nothing in the library.</summary>
    Unmonitored,

    /// <summary>Monitored, nothing in the library and no request yet (the next Wanted pass requests it).</summary>
    Missing,

    /// <summary>An approved request waits for a release.</summary>
    Requested,

    Downloading,

    /// <summary>The request ended without files (gave up or was rejected); it waits for the owner.</summary>
    Failed,

    /// <summary>Some tracks have files.</summary>
    Partial,

    /// <summary>Every known track has a file.</summary>
    Available
}

public sealed record MusicArtistRow(Guid Id, string Name, MusicMonitorMode Monitor, int Albums, int Monitored, int Available, DateTime? LastRefreshedAt);

public sealed record MusicAlbumRow(
    Guid WorkId,
    string Title,
    int? Year,
    MusicAlbumType Type,
    DateTime? ReleaseDate,
    bool Monitored,
    MusicAlbumState State,
    int Tracks,
    int TracksWithFiles,
    string? RequestMessage);

public sealed record MusicArtistView(MusicArtist Artist, IReadOnlyList<MusicAlbumRow> Albums);

public sealed record MusicTrackRow(Guid Id, int Disc, int Number, string Title, int? DurationMs, string? Path);

public sealed record MusicAlbumView(MusicAlbumRow Album, MusicArtist Artist, string? MusicBrainzReleaseGroupId, IReadOnlyList<MusicTrackRow> Tracks, Guid? RequestId, AcquisitionRequestStatus? RequestStatus);

/// <summary>
/// The read model of the Music media type, shared by the Admin and consumer pages: artists with their counts, an artist's albums and one album with its tracks. Every state is
/// derived from canonical records (assets and files, the album's monitoring, the album's request) in set-based queries, so the pages never
/// keep a second copy of what is wanted or available.
/// </summary>
public sealed class MusicQuery(AppDbContext db, AcquisitionAccessStore requests)
{
    public async Task<IReadOnlyList<MusicArtistRow>> ListArtistsAsync(CancellationToken cancellationToken)
    {
        var withFiles = AlbumsWithFiles();
        return await db.MusicArtists.AsNoTracking()
            .OrderBy(artist => artist.SortName)
            .Select(artist => new MusicArtistRow(
                artist.Id,
                artist.Name,
                artist.Monitor,
                db.MusicAlbums.Count(album => album.ArtistId == artist.Id),
                db.MusicAlbums.Count(album => album.ArtistId == artist.Id && album.Monitored),
                db.MusicAlbums.Count(album => album.ArtistId == artist.Id && withFiles.Contains(album.WorkId)),
                artist.LastRefreshedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<MusicArtistView?> GetArtistAsync(Guid artistId, CancellationToken cancellationToken)
    {
        var artist = await db.MusicArtists.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == artistId, cancellationToken);
        if (artist is null)
        {
            return null;
        }

        var albums = await db.MusicAlbums.AsNoTracking()
            .Where(album => album.ArtistId == artistId)
            .Join(db.Works.AsNoTracking(), album => album.WorkId, work => work.Id, (album, work) => new AlbumBase(album.WorkId, work.CanonicalTitle, work.Year, album.Type, album.ReleaseDate, album.Monitored, album.MusicBrainzReleaseGroupId))
            .ToListAsync(cancellationToken);
        return new MusicArtistView(artist, await ToRowsAsync(albums, cancellationToken));
    }

    public async Task<MusicAlbumView?> GetAlbumAsync(Guid workId, CancellationToken cancellationToken)
    {
        var row = await (
                from detail in db.MusicAlbums.AsNoTracking()
                join work in db.Works.AsNoTracking() on detail.WorkId equals work.Id
                join artist in db.MusicArtists.AsNoTracking() on detail.ArtistId equals artist.Id
                where detail.WorkId == workId
                select new { Base = new AlbumBase(detail.WorkId, work.CanonicalTitle, work.Year, detail.Type, detail.ReleaseDate, detail.Monitored, detail.MusicBrainzReleaseGroupId), Artist = artist })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var files = await (
                from asset in db.MediaAssets.AsNoTracking()
                join file in db.StoredFiles.AsNoTracking() on (Guid?)asset.Id equals file.MediaAssetId
                where asset.WorkId == workId && asset.Kind == MediaAssetKind.Audio && asset.WorkTrackId != null
                select new { TrackId = asset.WorkTrackId!.Value, file.Path })
            .ToListAsync(cancellationToken);
        var pathByTrack = files.GroupBy(file => file.TrackId).ToDictionary(group => group.Key, group => group.OrderBy(file => file.Path, StringComparer.Ordinal).First().Path);
        var tracks = (await db.WorkTracks.AsNoTracking().Where(track => track.WorkId == workId).OrderBy(track => track.Disc).ThenBy(track => track.Number).ToListAsync(cancellationToken))
            .Select(track => new MusicTrackRow(track.Id, track.Disc, track.Number, track.Title, track.DurationMs, pathByTrack.GetValueOrDefault(track.Id)))
            .ToArray();
        var album = (await ToRowsAsync([row.Base], cancellationToken)).Single();
        var request = row.Base.GroupId is null ? null : await requests.FindLatestAsync(MediaAcquisitionKind.Music, ProviderKeys.MusicBrainz, row.Base.GroupId, cancellationToken);
        return new MusicAlbumView(album, row.Artist, row.Base.GroupId, tracks, request?.Id, request?.Status);
    }

    private IQueryable<Guid> AlbumsWithFiles() =>
        db.MediaAssets.AsNoTracking()
            .Where(asset => asset.Kind == MediaAssetKind.Audio && db.StoredFiles.Any(file => file.MediaAssetId == asset.Id))
            .Select(asset => asset.WorkId);

    private async Task<IReadOnlyList<MusicAlbumRow>> ToRowsAsync(IReadOnlyList<AlbumBase> albums, CancellationToken cancellationToken)
    {
        if (albums.Count == 0)
        {
            return [];
        }

        var workIds = albums.Select(album => album.WorkId).ToArray();
        var trackCounts = await db.WorkTracks.AsNoTracking().Where(track => workIds.Contains(track.WorkId)).GroupBy(track => track.WorkId)
            .Select(group => new { WorkId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.WorkId, item => item.Count, cancellationToken);
        var fileCounts = await (
                from asset in db.MediaAssets.AsNoTracking()
                where workIds.Contains(asset.WorkId) && asset.Kind == MediaAssetKind.Audio && asset.WorkTrackId != null && db.StoredFiles.Any(file => file.MediaAssetId == asset.Id)
                group asset by asset.WorkId into byWork
                select new { WorkId = byWork.Key, Count = byWork.Select(asset => asset.WorkTrackId).Distinct().Count() })
            .ToDictionaryAsync(item => item.WorkId, item => item.Count, cancellationToken);
        var groupIds = albums.Select(album => album.GroupId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var latest = await requests.ListLatestAsync(MediaAcquisitionKind.Music, ProviderKeys.MusicBrainz, groupIds, cancellationToken);

        return [.. albums
            .OrderByDescending(album => album.ReleaseDate ?? DateTime.MinValue)
            .ThenBy(album => album.Title, StringComparer.OrdinalIgnoreCase)
            .Select(album =>
            {
                var tracks = trackCounts.GetValueOrDefault(album.WorkId);
                var withFiles = fileCounts.GetValueOrDefault(album.WorkId);
                var request = album.GroupId is null ? null : latest.GetValueOrDefault(album.GroupId);
                return new MusicAlbumRow(album.WorkId, album.Title, album.Year, album.Type, album.ReleaseDate, album.Monitored, StateOf(album.Monitored, tracks, withFiles, request?.Status), tracks, withFiles, request?.StatusMessage);
            })];
    }

    private static MusicAlbumState StateOf(bool monitored, int tracks, int withFiles, AcquisitionRequestStatus? request)
    {
        if (withFiles > 0)
        {
            return tracks > 0 && withFiles < tracks ? MusicAlbumState.Partial : MusicAlbumState.Available;
        }

        return request switch
        {
            AcquisitionRequestStatus.Approved or AcquisitionRequestStatus.Searching or AcquisitionRequestStatus.Pending => MusicAlbumState.Requested,
            AcquisitionRequestStatus.Downloading or AcquisitionRequestStatus.Importing => MusicAlbumState.Downloading,
            AcquisitionRequestStatus.Failed or AcquisitionRequestStatus.Rejected => MusicAlbumState.Failed,
            _ => monitored ? MusicAlbumState.Missing : MusicAlbumState.Unmonitored
        };
    }

    private sealed record AlbumBase(Guid WorkId, string Title, int? Year, MusicAlbumType Type, DateTime? ReleaseDate, bool Monitored, string? GroupId);
}

/// <summary>How the Admin and the Library pages say what state an album is in, so both read the same derived state the same way.</summary>
public static class MusicAlbumPresentation
{
    /// <summary>The tone of the outlined tag of a state (<c>admin-tag-*</c>); the state's name always sits next to it.</summary>
    public static string Tone(MusicAlbumState state) =>
        state switch
        {
            MusicAlbumState.Available => "success",
            MusicAlbumState.Partial or MusicAlbumState.Requested or MusicAlbumState.Downloading => "warning",
            MusicAlbumState.Failed => "danger",
            _ => "text"
        };

    /// <summary>A request that ended without files is a matter for the owner; a Library visitor sees an album that is missing and can be requested again.</summary>
    public static MusicAlbumState ForLibrary(MusicAlbumState state) => state == MusicAlbumState.Failed ? MusicAlbumState.Missing : state;
}
