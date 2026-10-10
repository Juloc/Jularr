namespace Jularr.Web.Features.Novels;

public sealed class NovelWork
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SourceProvider { get; set; } = "";
    public string SourceKey { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Author { get; set; }
    public string? Description { get; set; }

    public string? MetadataProvider { get; set; }
    public string? MetadataExternalId { get; set; }
    public string? MetadataTitle { get; set; }
    public string? MetadataNativeTitle { get; set; }
    public string? MetadataDescription { get; set; }
    public string? CoverImageUrl { get; set; }
    public string? BannerImageUrl { get; set; }
    public string? Format { get; set; }
    public string? MetadataStatus { get; set; }
    public int? MetadataChapterCount { get; set; }
    public int? MetadataVolumeCount { get; set; }
    public string? MetadataGenresJson { get; set; }

    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A volume/book of a novel series. Every chapter belongs to exactly one
/// volume: web novels and single books have one implicit volume, EPUB light
/// novels have one volume per imported EPUB file.
/// </summary>
public sealed class NovelVolume
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkId { get; set; }
    /// <summary>Reading order within the series (1-based, unique per work).</summary>
    public int Number { get; set; }
    public string? Title { get; set; }
    public string Kind { get; set; } = NovelVolumeKinds.Web;
    /// <summary>
    /// Deterministic source identity within the series. Re-importing a source
    /// with the same identity refreshes this volume instead of adding one.
    /// </summary>
    public string SourceKey { get; set; } = "";
    public string? SourceFileName { get; set; }
    /// <summary>
    /// Absolute path of the original EPUB in the configured NAS library. Reader chapters and
    /// cached assets are derived from this file and can be rebuilt without retaining a second
    /// canonical copy under /data.
    /// </summary>
    public string? SourceStoragePath { get; set; }
    public string? SourceContentHash { get; set; }
    /// <summary>File name of the cached cover in the volume asset store.</summary>
    public string? CoverAsset { get; set; }
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One EPUB file that holds the content of a volume. A volume keeps every file it was imported from; the one whose hash is
/// <see cref="NovelVolume.SourceContentHash"/> is the version the reader shows.
/// </summary>
public sealed class NovelVolumeEdition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid VolumeId { get; set; }
    public string ContentHash { get; set; } = "";
    public string FileName { get; set; } = "";
    /// <summary>Where the file lives in the library, so a better edition can be shown again later; null for an upload that was not kept.</summary>
    public string? StoragePath { get; set; }
    /// <summary>The quality key of the profile's quality order this edition counts as.</summary>
    public string Quality { get; set; } = "EPUB";
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
}

public static class NovelVolumeKinds
{
    /// <summary>Chapter index of a web novel source (Narou/Ncode).</summary>
    public const string Web = "web";
    /// <summary>A single book imported through the Books catalog.</summary>
    public const string Book = "book";
    /// <summary>A user-provided EPUB light-novel volume.</summary>
    public const string Epub = "epub";
}

public sealed class NovelChapter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkId { get; set; }
    public Guid VolumeId { get; set; }
    /// <summary>Series-wide reading order (unique per work).</summary>
    public int Number { get; set; }
    /// <summary>Source identity of the chapter within its volume.</summary>
    public string SourceUrl { get; set; } = "";
    public string Title { get; set; } = "";
    public string OriginalText { get; set; } = "";
    /// <summary>
    /// Sanitized structured rendering of <see cref="OriginalText"/> (headings,
    /// emphasis, ruby, illustrations), see <see cref="NovelChapterDocument"/>.
    /// Null when the source is plain text.
    /// </summary>
    public string? ContentJson { get; set; }
    /// <summary>
    /// Optional section/group heading the chapter belongs to within its volume
    /// (e.g. "Extra", "Character Stories"), read from an EPUB's <c>nav</c>/
    /// <c>toc.ncx</c> nesting or a Narou chapter-index section heading (#512).
    /// Null for a chapter that is not part of a named group; it then renders
    /// as a flat list item, exactly as before groups existed.
    /// </summary>
    public string? GroupTitle { get; set; }
    public string SourceHash { get; set; } = "";
    public DateTime? PublishedAt { get; set; }
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool HasContent => OriginalText.Length > 0;
}

public sealed class NovelTranslation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChapterId { get; set; }
    public string TargetLanguage { get; set; } = "de";
    public string ProviderId { get; set; } = "";
    public int PromptVersion { get; set; }
    public string SourceHash { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class NovelProgress
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProfileId { get; set; } = "";
    public Guid WorkId { get; set; }
    public Guid ChapterId { get; set; }
    public int PositionPermille { get; set; }
    public string AnchorLanguage { get; set; } = "ja";
    public int? AnchorParagraphIndex { get; set; }
    public int AnchorOffset { get; set; }
    public string? AnchorText { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class NovelBookmark
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProfileId { get; set; } = "";
    public Guid WorkId { get; set; }
    public Guid ChapterId { get; set; }
    public int PositionPermille { get; set; }
    public string Language { get; set; } = "ja";
    public int? ParagraphIndex { get; set; }
    public int CharacterOffset { get; set; }
    public string? AnchorText { get; set; }
    public string? Label { get; set; }
    public string? Style { get; set; }
    public string? Color { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last-writer-wins clock for offline sync (Features/OfflineLibrary):
    /// the client timestamp of the event that most recently created/edited
    /// this bookmark. Distinct from <see cref="CreatedAt"/>, which never
    /// changes. Defaults to <see cref="CreatedAt"/> for bookmarks created
    /// through the normal (online) reader path.
    /// </summary>
    public DateTime SyncUpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Client-supplied idempotency key of the last applied offline sync event, if any.</summary>
    public Guid? ClientEventId { get; set; }
}

public sealed class NovelHighlight
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProfileId { get; set; } = "";
    public Guid WorkId { get; set; }
    public Guid ChapterId { get; set; }
    public string Language { get; set; } = "ja";
    public int ParagraphIndex { get; set; }
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public string Text { get; set; } = "";
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class NovelAnimeMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkId { get; set; }
    public int ChapterStart { get; set; }
    public int ChapterEnd { get; set; }
    public string AnimeProvider { get; set; } = "";
    public string AnimeExternalId { get; set; } = "";
    public int SeasonNumber { get; set; }
    public int EpisodeStart { get; set; }
    public int EpisodeEnd { get; set; }
    public string? Label { get; set; }
    public string Source { get; set; } = "manual";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// GroupTitle: the section heading the chapter is listed under on the source's chapter index, if any.
public sealed record NovelSourceChapterReference(
    int Number,
    string Title,
    string SourceUrl,
    DateTime? PublishedAt = null,
    string? GroupTitle = null);

public sealed record NovelSourceWorkSnapshot(
    string Provider,
    string SourceKey,
    string SourceUrl,
    string Title,
    string? Author,
    string? Description,
    IReadOnlyList<NovelSourceChapterReference> Chapters);

public sealed record NovelSourceChapterSnapshot(
    int Number,
    string Title,
    string SourceUrl,
    string OriginalText,
    DateTime? PublishedAt = null);

public interface INovelSourceProvider
{
    string Key { get; }
    bool CanHandle(Uri sourceUri);

    Task<NovelSourceWorkSnapshot> GetWorkAsync(
        Uri sourceUri,
        CancellationToken cancellationToken);

    Task<NovelSourceChapterSnapshot> GetChapterAsync(
        Uri sourceUri,
        CancellationToken cancellationToken);
}

public sealed record NovelListItem(
    Guid Id,
    string Title,
    string? NativeTitle,
    string? Author,
    string? Description,
    string? CoverImageUrl,
    string? BannerImageUrl,
    string? MetadataStatus,
    int? MetadataChapterCount,
    int? MetadataVolumeCount,
    int ChapterCount,
    int LoadedChapterCount,
    int TranslatedChapterCount,
    Guid? CurrentChapterId,
    int? CurrentChapterNumber,
    string? CurrentChapterTitle,
    int ProgressPermille,
    DateTime? LastReadAt,
    int VolumeCount,
    int? CurrentVolumeNumber)
{
    public bool HasProgress => CurrentChapterId is not null;
}

public sealed record NovelChapterItem(
    Guid Id,
    int Number,
    string Title,
    bool HasContent,
    bool HasTranslation,
    DateTime? PublishedAt,
    Guid VolumeId,
    string? GroupTitle = null);

/// <summary>A volume of a series with its chapter range.</summary>
public sealed record NovelVolumeItem(
    Guid Id,
    int Number,
    string? Title,
    string Kind,
    string? CoverUrl,
    string? SourceFileName,
    DateTime UpdatedAt)
{
    public bool IsEpub => Kind == NovelVolumeKinds.Epub;
}

public sealed record NovelWorkDetail(
    NovelWork Work,
    IReadOnlyList<NovelChapterItem> Chapters,
    IReadOnlyList<NovelAnimeMapping> Mappings,
    IReadOnlyList<NovelVolumeItem> Volumes)
{
    public bool IsEpubSeries => Work.SourceProvider == NovelEpubImportService.Provider;

    /// <summary>Series cover: the AniList cover, else the first volume cover.</summary>
    public string? CoverUrl =>
        Work.CoverImageUrl ?? Volumes.FirstOrDefault(x => x.CoverUrl is not null)?.CoverUrl;
}

public sealed record NovelMetadataCandidate(
    string Provider,
    string ExternalId,
    string PreferredTitle,
    string? NativeTitle,
    string? Description,
    string? CoverImageUrl,
    string? BannerImageUrl,
    string? Format,
    string? Status,
    int? ChapterCount,
    int? VolumeCount,
    IReadOnlyList<string>? Genres = null);

public interface INovelMetadataProvider
{
    string Key { get; }

    Task<IReadOnlyList<NovelMetadataCandidate>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken);

    Task<NovelMetadataCandidate?> GetAsync(
        string externalId,
        CancellationToken cancellationToken);
}
