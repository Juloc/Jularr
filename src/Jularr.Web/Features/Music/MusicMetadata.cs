namespace Jularr.Web.Features.Music;

public sealed record MusicArtistSummary(string MusicBrainzId, string Name, string SortName, string? Disambiguation, string? Country, string? Type);

public sealed record MusicReleaseGroupSummary(string MusicBrainzId, string Title, MusicAlbumType Type, DateTime? ReleaseDate, int? Year, bool HasSecondaryType);

/// <summary>An album found by a text search, with the artist the provider credits it to.</summary>
public sealed record MusicAlbumSearchHit(MusicReleaseGroupSummary Album, string ArtistId, string ArtistName);

public sealed record MusicTrackInfo(int Disc, int Number, string Title, int? DurationMs, string? RecordingId);

/// <summary>The track list of one album: the provider's earliest official release of the release group.</summary>
public sealed record MusicAlbumTracks(string ReleaseId, IReadOnlyList<MusicTrackInfo> Tracks);

/// <summary>The provider could not answer (unreachable, rate limited, malformed answer); the caller retries later and never treats it as "nothing exists".</summary>
public sealed class MusicMetadataException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// The metadata source of the Music media type. MusicBrainz is the only implementation; the interface keeps the library service
/// independent of it. Every call is read-only and bounded; a failure is a <see cref="MusicMetadataException"/>.
/// </summary>
public interface IMusicMetadataProvider
{
    Task<IReadOnlyList<MusicArtistSummary>> SearchArtistsAsync(string query, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<MusicAlbumSearchHit>> SearchAlbumsAsync(string query, int limit, CancellationToken cancellationToken);

    Task<MusicArtistSummary?> GetArtistAsync(string musicBrainzId, CancellationToken cancellationToken);

    /// <summary>Every album and EP release group of an artist, with the provider's release date and secondary types.</summary>
    Task<IReadOnlyList<MusicReleaseGroupSummary>> ListReleaseGroupsAsync(string artistMusicBrainzId, CancellationToken cancellationToken);

    /// <summary>The track list of a release group, or null when the provider knows no official release with tracks.</summary>
    Task<MusicAlbumTracks?> GetTracksAsync(string releaseGroupMusicBrainzId, CancellationToken cancellationToken);
}
