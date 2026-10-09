namespace Jularr.Web.Features.MediaCore;

/// <summary>
/// The universal, provider-independent unit of the Jularr media core (#592, epic #556 §1). Every real
/// work — a movie, a series, an anime, a book, a manga or a light novel — is exactly one
/// <see cref="Work"/> with a stable internal id. External provider identities, titles, structure
/// (seasons/episodes, volumes/chapters), editions/versions, relations and per-field provenance all
/// hang off this id, so a work keeps the same Jularr identity no matter how many providers describe it.
/// </summary>
public sealed class Work
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public WorkMediaType MediaType { get; set; }

    /// <summary>
    /// Cached display title (the resolved primary <see cref="WorkTitle"/>). The authoritative,
    /// source-attributed value lives in <see cref="WorkTitle"/> / <see cref="WorkFieldProvenance"/>;
    /// this column exists only so list/grid queries avoid a join.
    /// </summary>
    public string CanonicalTitle { get; set; } = "";

    /// <summary>Release/publication year when known; provenance for it is tracked per field.</summary>
    public int? Year { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One title of a <see cref="Work"/> — original, English, romaji, native, localized, alternate or a
/// synonym. Alternate/localized titles carry a BCP-47 <see cref="Language"/>. Exactly one title per
/// work is <see cref="IsPrimary"/>.
/// </summary>
public sealed class WorkTitle
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkId { get; set; }

    public WorkTitleType TitleType { get; set; }

    /// <summary>BCP-47 language tag; <c>und</c> when the provider gives no language.</summary>
    public string Language { get; set; } = "und";

    public string Value { get; set; } = "";

    /// <summary>Case/diacritic/punctuation-folded form used for de-duplication and matching (never display).</summary>
    public string NormalizedValue { get; set; } = "";

    public bool IsPrimary { get; set; }

    /// <summary>Normalized provider that supplied this title (see <c>MappingProviders</c>), or a local source.</summary>
    public string Source { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A normalized external provider identity of a <see cref="Work"/> (#592 / #432). A work may carry
/// many: AniList, MAL, TMDB, TVDB, IMDb, Plex, Jellyfin and more. A given (provider, media type,
/// external id) resolves to at most one work (unique index), so the mapping is <b>correctable</b>:
/// moving <see cref="WorkId"/> reassigns the provider id to a different work without losing history.
/// </summary>
public sealed class WorkExternalIdentity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkId { get; set; }

    /// <summary>Provider's media namespace (a provider can use the same id space per media type).</summary>
    public WorkMediaType MediaType { get; set; }

    /// <summary>Normalized provider key (lowercase), e.g. <c>anilist</c>, <c>tmdb</c>, <c>imdb</c>.</summary>
    public string Provider { get; set; } = "";

    /// <summary>Provider's own id for the work, trimmed to a canonical form.</summary>
    public string ExternalId { get; set; } = "";

    /// <summary>The identity a feature should treat as canonical for this provider on this work.</summary>
    public bool IsPrimary { get; set; }

    /// <summary>Strength of the evidence, 0..1 (strong provider-id match vs weak title match).</summary>
    public double Confidence { get; set; } = 1.0;

    /// <summary>Short human-readable justification (never a filename alone; #556 never matches on filename only).</summary>
    public string Evidence { get; set; } = "";

    /// <summary>Owner correction: a provider refresh must never silently overwrite or remove it.</summary>
    public bool IsManualOverride { get; set; }

    public MappingReviewState ReviewState { get; set; } = MappingReviewState.Confirmed;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A typed directed edge between two resolved <see cref="Work"/> ids (#592).</summary>
public sealed class WorkRelation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FromWorkId { get; set; }
    public Guid ToWorkId { get; set; }

    public WorkRelationType RelationType { get; set; }

    /// <summary>Normalized provider/source that asserted the relation, or <c>owner</c> when curated.</summary>
    public string Source { get; set; } = "";

    /// <summary>Owner correction; protected from provider-refresh overwrite.</summary>
    public bool IsManualOverride { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A season of a <see cref="Work"/> (Series/Anime). Season <c>0</c> holds specials.</summary>
public sealed class WorkSeason
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkId { get; set; }

    /// <summary>0 for the specials season, 1-based otherwise.</summary>
    public int SeasonNumber { get; set; }

    public string? Title { get; set; }

    public bool IsSpecial { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// An episode of a <see cref="Work"/>. Supports both season numbering and anime <b>absolute</b>
/// numbering at once, and marks specials explicitly (#592: "anime absolute + season numbering + specials").
/// </summary>
public sealed class WorkEpisode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkId { get; set; }

    /// <summary>Optional link to the owning <see cref="WorkSeason"/>; null when only flat numbering is known.</summary>
    public Guid? SeasonId { get; set; }

    public int SeasonNumber { get; set; } = 1;

    public int EpisodeNumber { get; set; }

    /// <summary>Continuous anime absolute number across seasons, when the numbering scheme provides one.</summary>
    public int? AbsoluteNumber { get; set; }

    public bool IsSpecial { get; set; }

    public string? Title { get; set; }

    public DateTime? AiredAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A volume of a <see cref="Work"/> (Book/Manga/LightNovel).</summary>
public sealed class WorkVolume
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkId { get; set; }

    public int Number { get; set; }

    public string? Title { get; set; }

    /// <summary>The provider that identifies this volume and its id there; both null for a volume nothing external vouches for. Number and title never identify it.</summary>
    public string? Provider { get; set; }

    public string? ExternalId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The underlying audio recording, identified by its MusicBrainz recording id; one recording can be placed on many releases (albums). A track whose
/// recording id is unknown has no recording: recordings are never matched by title or position.
/// </summary>
public sealed class MusicRecording
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string MusicBrainzId { get; set; } = "";

    public string Title { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A track of an album <see cref="Work"/> (a release group): the placement of a recording on a release, at a disc and position. Logical content that exists
/// without a file, the music equivalent of an episode or chapter. Not to be confused with the technical stream <c>Track</c> below an Asset.
/// </summary>
public sealed class WorkTrack
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkId { get; set; }

    public int Disc { get; set; } = 1;

    public int Number { get; set; }

    public string Title { get; set; } = "";

    public int? DurationMs { get; set; }

    /// <summary>The recording placed here, when the provider named it; null while unresolved.</summary>
    public Guid? MusicRecordingId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A chapter of a <see cref="Work"/>; <see cref="Number"/> is a decimal so "10.5" specials fit.</summary>
public sealed class WorkChapter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkId { get; set; }

    /// <summary>Optional owning <see cref="WorkVolume"/>; null for web serialisation without volumes.</summary>
    public Guid? VolumeId { get; set; }

    public double Number { get; set; }

    public string? Title { get; set; }

    public bool IsSpecial { get; set; }

    /// <summary>The provider that identifies this chapter and its id there; both null for a chapter nothing external vouches for. Number and title never identify it.</summary>
    public string? Provider { get; set; }

    public string? ExternalId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum WorkUnitLocalKind : short
{
    NovelVolume = 0,
    MangaChapter = 1
}

/// <summary>
/// Says that a local reading unit (an imported light-novel volume or manga chapter) is a canonical <see cref="WorkVolume"/> or <see cref="WorkChapter"/> (exactly one of the two ids is set).
/// Only an owner mapping or the import of an acquisition for that unit writes it; a number or title never does.
/// </summary>
public sealed class WorkUnitBinding
{
    public long Id { get; set; }
    public Guid WorkId { get; set; }
    public WorkUnitLocalKind LocalKind { get; set; }

    /// <summary>The id of the local unit (a NovelVolume or MangaChapter id).</summary>
    public string LocalId { get; set; } = "";

    public Guid? WorkVolumeId { get; set; }
    public Guid? WorkChapterId { get; set; }

    /// <summary>Whether the owner mapped it or an import of an acquisition made for the unit; the owner's mapping is never replaced by an import.</summary>
    public bool IsOwnerMapping { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// An <b>edition</b> of a work — an editorial variant of the whole work: a book ISBN edition, a
/// translated release, a director's cut line. Deliberately separate from <see cref="WorkVersion"/>
/// (a concrete acquirable file variant) per #592 ("editions/versions modelled separately").
/// </summary>
public sealed class WorkEdition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkId { get; set; }

    /// <summary>Deterministic identity of the edition within the work (re-import refreshes, never duplicates).</summary>
    public string EditionKey { get; set; } = "";

    public string Language { get; set; } = "und";

    /// <summary>hardcover / paperback / ebook / bluray / web / … (free vocabulary per media type).</summary>
    public string? Format { get; set; }

    public string? Publisher { get; set; }

    public string? Isbn13 { get; set; }

    public string? Title { get; set; }

    public bool IsPrimary { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A concrete <b>version</b> — an acquirable/physical release variant of a unit of the work (a whole
/// movie, an episode identified by <see cref="UnitKey"/> like <c>S01E04</c>, a volume like <c>V03</c>).
/// Carries release/quality attributes. The full per-user requirement/track model is a later child
/// of #556 (§22); this is the identity anchor those build on.
/// </summary>
public sealed class WorkVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkId { get; set; }

    /// <summary>Optional owning <see cref="WorkEdition"/>.</summary>
    public Guid? EditionId { get; set; }

    /// <summary>Deterministic identity of the version within the work.</summary>
    public string VersionKey { get; set; } = "";

    /// <summary>Which unit this version covers, e.g. <c>S01E04</c> / <c>V03</c>; null = the whole work (a movie).</summary>
    public string? UnitKey { get; set; }

    public string? Quality { get; set; }
    public string? ReleaseGroup { get; set; }
    public string? Source { get; set; }
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Field-level metadata provenance for a <see cref="Work"/> (#435). Records, per important field,
/// where the displayed value came from, when it was fetched, its confidence, whether it is a manual
/// override and its fallback priority — so a provider refresh can update a field only when it does not
/// clobber a higher-priority or manually corrected source. One row per (work, field).
/// </summary>
public sealed class WorkFieldProvenance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkId { get; set; }

    /// <summary>Stable field key, e.g. <c>title</c>, <c>originalTitle</c>, <c>year</c>, <c>description</c>, <c>cover</c>.</summary>
    public string FieldKey { get; set; } = "";

    /// <summary>Normalized source: a provider key, or <c>owner</c> / <c>local</c> / <c>nfo</c> / <c>filename</c>.</summary>
    public string Source { get; set; } = "";

    public string? ProviderExternalId { get; set; }

    public double? Confidence { get; set; }

    /// <summary>True when the value is an owner correction; provider refreshes must not overwrite it.</summary>
    public bool IsManualOverride { get; set; }

    /// <summary>Effective precedence of the current source (lower wins). See <c>MetadataFieldSources</c>.</summary>
    public int FallbackPriority { get; set; }

    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A non-invasive bridge from a <see cref="Work"/> to an existing per-type record (Anime, NovelWork,
/// BookEdition, MangaSeries, Episode). It references the legacy primary key without modifying the
/// legacy table, so the universal core unifies the per-type models while every current feature keeps
/// working unchanged (#592: "do NOT rip out the per-type tables"). Each legacy record maps to exactly
/// one work (unique index on kind+id).
/// </summary>
public sealed class WorkSourceLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkId { get; set; }

    public WorkSourceKind SourceKind { get; set; }

    /// <summary>Primary key of the bridged legacy record.</summary>
    public Guid SourceId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
