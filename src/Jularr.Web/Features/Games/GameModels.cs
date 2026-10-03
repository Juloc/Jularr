namespace Jularr.Web.Features.Games;

/// <summary>Provider-independent canonical Game identity. Games deliberately stays outside MediaCore Work identity.</summary>
public sealed class Game
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CanonicalTitle { get; set; } = "";
    public string? Description { get; set; }
    public string? Developer { get; set; }
    public string? Publisher { get; set; }
    public int? ReleaseYear { get; set; }
    public DateOnly? ReleaseDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum GameTitleKind
{
    Alternate = 0,
    Native = 1,
    Localized = 2,
    Romanized = 3
}

/// <summary>An alternate/localized title; the canonical product title remains on <see cref="Game"/>.</summary>
public sealed class GameTitle
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GameId { get; set; }
    public string Value { get; set; } = "";
    public GameTitleKind Kind { get; set; } = GameTitleKind.Alternate;
    public string? LanguageTag { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Provider identity is matching/provenance evidence, never the canonical Game ID.</summary>
public sealed class GameExternalIdentity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GameId { get; set; }
    public string Provider { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastVerifiedAt { get; set; }
}

public enum GameArtworkKind
{
    Cover = 0,
    Backdrop = 1,
    Screenshot = 2,
    TitleScreen = 3,
    Logo = 4,
    Banner = 5
}

/// <summary>Reference to artwork; provider/cache fetching remains outside the canonical Game entity.</summary>
public sealed class GameArtwork
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GameId { get; set; }
    public GameArtworkKind Kind { get; set; }
    public string Uri { get; set; } = "";
    public string? Provider { get; set; }
    public string? ExternalId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Stable normalized platform identity. Runtime/core identifiers do not belong here.</summary>
public sealed class GamePlatform
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum GameReleaseKind
{
    Unknown = 0,
    Clean = 1,
    Modified = 2,
    Homebrew = 3,
    Prototype = 4
}

/// <summary>One concrete playable release/version of a canonical Game.</summary>
public sealed class GameRelease
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GameId { get; set; }
    public Guid GamePlatformId { get; set; }
    public GameReleaseKind Kind { get; set; }
    public string? Region { get; set; }
    public string? Revision { get; set; }
    public string? Version { get; set; }
    public string? Source { get; set; }
    public string? Format { get; set; }
    public string[] LanguageTags { get; set; } = [];
    public DateOnly? ReleaseDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum GameHashAlgorithm
{
    Crc32 = 0,
    Md5 = 1,
    Sha1 = 2,
    Sha256 = 3
}

/// <summary>Canonical release-level hash/serial-style evidence used for deterministic identification.</summary>
public sealed class GameReleaseHash
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GameReleaseId { get; set; }
    public GameHashAlgorithm Algorithm { get; set; }
    public string Value { get; set; } = "";
    public bool IsPrimary { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum GameReleaseFileRole
{
    Primary = 0,
    Disc = 1,
    Track = 2,
    Auxiliary = 3
}

/// <summary>
/// One physical file belonging to a release. LibraryRoot + RelativePath is physical placement only,
/// never canonical Game identity.
/// </summary>
public sealed class GameReleaseFile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GameReleaseId { get; set; }
    public Guid LibraryRootId { get; set; }
    public string RelativePath { get; set; } = "";
    public long SizeBytes { get; set; }
    public int? DiscNumber { get; set; }
    public int Sequence { get; set; }
    public GameReleaseFileRole Role { get; set; }
    public string? Crc32 { get; set; }
    public string? Md5 { get; set; }
    public string? Sha1 { get; set; }
    public string? Sha256 { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
