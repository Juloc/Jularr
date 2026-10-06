namespace Jularr.Web.Features.Music;

/// <summary>How an artist's albums become wanted: nothing, every album the artist ever released, or only albums released after the artist was added.</summary>
public enum MusicMonitorMode
{
    None = 0,
    All = 1,
    Future = 2
}

public enum MusicAlbumType
{
    Album = 0,
    Ep = 1,
    Single = 2,
    Compilation = 3,
    Live = 4,
    Soundtrack = 5,
    Other = 6
}

/// <summary>
/// An artist the owner manages: the owner of a set of album Works, the way a Series owns episodes. It is not a Work itself.
/// <see cref="MonitorFromUtc"/> anchors "Future": only albums released after it become wanted.
/// </summary>
public sealed class MusicArtist
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public string SortName { get; set; } = "";

    /// <summary>The provider identity (MusicBrainz artist id); unique when set.</summary>
    public string? MusicBrainzId { get; set; }

    public MusicMonitorMode Monitor { get; set; } = MusicMonitorMode.All;

    public DateTime MonitorFromUtc { get; set; } = DateTime.UtcNow;

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the discography was last read from the provider; the shared Wanted pass refreshes artists whose value is old.</summary>
    public DateTime? LastRefreshedAt { get; set; }
}

/// <summary>The album-specific facts of a Work with <c>MediaType = Music</c>; the Work row itself carries title and year.</summary>
public sealed class MusicAlbum
{
    /// <summary>The Work this row describes (primary key and foreign key).</summary>
    public Guid WorkId { get; set; }

    public Guid ArtistId { get; set; }

    public MusicAlbumType Type { get; set; }

    public DateTime? ReleaseDate { get; set; }

    /// <summary>The provider identity (MusicBrainz release group id); unique when set.</summary>
    public string? MusicBrainzReleaseGroupId { get; set; }

    /// <summary>Whether a missing album is wanted. The artist's monitor mode sets it when the album is added; the owner can switch it per album.</summary>
    public bool Monitored { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
