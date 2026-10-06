using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Ai;
using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.Novels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Books;

public sealed record BookCatalogItem(
    string Id,
    string Title,
    string? Author,
    string? Summary,
    string? CoverImageUrl,
    IReadOnlyList<string> Subjects,
    int? FirstPublishYear,
    string? TextUrl,
    string? EpubUrl,
    string SourceUrl,
    string SourceName,
    string? TextSourceName)
{
    /// <summary>Every provider id of the same work (this record's <see cref="Id"/> included once merged).</summary>
    public IReadOnlyList<string> Identities { get; init; } = [];

    /// <summary>ISBN-13 of the work's editions (ISBN-10 converted), as the providers list them.</summary>
    public IReadOnlyList<string> Isbns { get; init; } = [];

    /// <summary>How many editions the provider knows; a popularity signal for ranking.</summary>
    public int? EditionCount { get; init; }

    /// <summary>Usable covers best first; <see cref="CoverImageUrl"/> is the chosen one.</summary>
    public IReadOnlyList<string> CoverCandidates { get; init; } = [];

    /// <summary>Publisher of the edition the record describes (Google Books volumes).</summary>
    public string? Publisher { get; init; }

    /// <summary>Publication date of the edition the record describes (Google Books volumes).</summary>
    public string? PublishedDate { get; init; }

    /// <summary>Language of the edition the record describes, as a short tag (e.g. "en", "id") when known.</summary>
    public string? Language { get; init; }

    /// <summary>
    /// The profile's reading-list state from a connected list provider, as a
    /// <see cref="BookListStates"/> code; never part of the shared catalog.
    /// </summary>
    public string? ExternalListState { get; init; }

    /// <summary>
    /// The provider records merged into this work (#405), each a distinct edition view: its own
    /// year, language, publisher, ISBN and format. Set by <see cref="BookWorkSearch"/> to one
    /// entry per merged record (at least one); a raw, unranked provider result leaves it empty.
    /// </summary>
    public IReadOnlyList<BookEditionSummary> Editions { get; init; } = [];

    /// <summary>
    /// A community rating (0-5) from a connected source (#371, currently only the optional
    /// Hardcover discovery integration); null whenever no source supplied one. Never a fabricated
    /// or derived number.
    /// </summary>
    public double? Rating { get; init; }

    public bool CanPreview => !string.IsNullOrWhiteSpace(TextUrl);
    public bool CanAcquire =>
        !string.IsNullOrWhiteSpace(EpubUrl)
        || Id.StartsWith(
            "wsid-",
            StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A Books discovery row backed by a real, honestly-labelled source (#371): <see cref="Trending"/>
/// is recent activity (Open Library trending), <see cref="Popular"/> is enduring, catalog-wide
/// popularity (Open Library edition count), <see cref="New"/> is recently published (Open Library
/// recent-subject data). None of these ever falls back to Gutenberg download counts.
/// </summary>
public enum BookBrowseMode
{
    Trending,
    Popular,
    New
}

public sealed partial class BookCatalogService(
    HttpClient httpClient,
    AppDbContext db,
    IBookTranslator translator,
    IConfiguration configuration,
    IDataProtectionProvider? dataProtectionProvider = null,
    // Test-only override of BookDiscoverySettingsStore's "/data" default; production callers
    // never pass this.
    DirectoryInfo? discoverySettingsDirectory = null,
    // Resolves the configured Books NAS library root (#389/#545); null (the default for tests
    // that do not exercise artwork placement) behaves exactly like no root being configured, so
    // covers keep using CoversPath -- no regression when nothing is set up.
    AnimeImportSettingsStore? importSettings = null)
{
    public const string ImportedBookProvider = "book-epub";
    public const int TranslationPromptVersion = 5;

    /// <summary>Beside-media artwork kind (issue #406): <c>cover.*</c> next to the EPUB/PDF.</summary>
    private const string CoverArtworkKind = "cover";

    private const int SearchLimit = 24;
    private const int DefaultSampleCharacters = 5500;
    private const int MaxRedirects = 5;
    private const int MaxEpubBytes = 100 * 1024 * 1024;
    private static readonly TimeSpan CatalogRequestTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan EpubDownloadTimeout = TimeSpan.FromSeconds(45);
    private static readonly SemaphoreSlim TranslationGate = new(1, 1);
    private readonly BesideMediaArtworkStore artwork = new(db);

    public async Task<IReadOnlyList<BookCatalogItem>> SearchAsync(
        string? query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            // The trending listing reports a catalog that did not answer (Discover shows that); this search has always meant "nothing".
            try
            {
                return await BrowseTrendingBooksAsync(cancellationToken);
            }
            catch (HttpRequestException)
            {
                return [];
            }
        }

        var normalizedQuery = query.Trim();
        var works = (await SearchPrimaryCatalogsCoreAsync(normalizedQuery, cancellationToken)).Items;

        if (works.Count > 0)
        {
            return works;
        }

        return BookWorkSearch.Rank(
            normalizedQuery,
            await SearchGutenbergCatalogAsync(
                normalizedQuery,
                cancellationToken));
    }

    /// <summary>
    /// Open Library, Google Books and Wikisource only. The application-level multi-source
    /// coordinator calls this beside Gutenberg/OPDS so no fallback provider is queried twice.
    /// </summary>
    public async Task<IReadOnlyList<BookCatalogItem>> SearchPrimaryCatalogsAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var result = await SearchPrimaryCatalogsCoreAsync(query, cancellationToken);
        return result.AllFailed ? throw new HttpRequestException("None of the book catalogs answered.") : result.Items;
    }

    private async Task<(IReadOnlyList<BookCatalogItem> Items, bool AllFailed)> SearchPrimaryCatalogsCoreAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = query.Trim();
        var openLibraryTask = CaptureCatalogResultAsync(
            token => SearchOpenLibraryAsync(normalizedQuery, token),
            cancellationToken);
        var googleTask = CaptureCatalogResultAsync(
            token => SearchGoogleBooksAsync(normalizedQuery, token),
            cancellationToken);
        var wikisourceTask = CaptureCatalogResultAsync(
            token => SearchIndonesianWikisourceAsync(
                normalizedQuery,
                token),
            cancellationToken);

        await Task.WhenAll(
            openLibraryTask,
            googleTask,
            wikisourceTask);

        // A provider that failed or timed out contributes nothing; the others still answer.
        var works = BookWorkSearch.Rank(
                normalizedQuery,
                wikisourceTask.Result.Items,
                openLibraryTask.Result.Items,
                googleTask.Result.Items)
            .Take(SearchLimit)
            .ToArray();
        return (works, wikisourceTask.Result.Failed && openLibraryTask.Result.Failed && googleTask.Result.Failed);
    }

    /// <summary>Project Gutenberg results for the application-level multi-source search.</summary>
    public Task<IReadOnlyList<BookCatalogItem>> SearchGutenbergCatalogAsync(
        string query,
        CancellationToken cancellationToken) =>
        CaptureCatalogAsync(
            token => SearchGutenbergAsync(query.Trim(), token),
            cancellationToken,
            fallbackToEmpty: true);

    /// <summary>
    /// One honestly-labelled Books discovery row (#371): each mode is backed by a source that
    /// actually offers that signal (Open Library trending/edition-count/recent-subject data), never
    /// a substitute for a provider that cannot supply it (Gutenberg download counts are never used
    /// as a Trending/Popular/New signal). When an owner Hardcover API key is configured (Settings →
    /// Books), matching items also get a Hardcover community rating; Hardcover never gates
    /// discovery itself off when it is unset or unreachable.
    /// </summary>
    public async Task<IReadOnlyList<BookCatalogItem>> BrowseAsync(
        BookBrowseMode mode,
        CancellationToken cancellationToken)
    {
        var items = mode switch
        {
            BookBrowseMode.Popular => await BrowseTopBooksAsync(cancellationToken),
            BookBrowseMode.New => await BrowseNewBooksAsync(cancellationToken),
            _ => await BrowseTrendingBooksAsync(cancellationToken)
        };

        if (dataProtectionProvider is null)
        {
            return items;
        }

        var settings = await new BookDiscoverySettingsStore(dataProtectionProvider, discoverySettingsDirectory)
            .LoadAsync(cancellationToken);

        return settings.HardcoverEnabled
            ? await EnrichHardcoverRatingsAsync(items, settings.HardcoverApiKey!, cancellationToken)
            : items;
    }

    public async Task<BookCatalogItem?> GetAsync(
        string id,
        CancellationToken cancellationToken)
    {
        if (TryParseGutenbergId(id, out var gutenbergId))
        {
            return await GetGutenbergAsync(
                gutenbergId,
                cancellationToken);
        }

        if (TryParseOpenLibraryId(id, out var workKey))
        {
            return await GetOpenLibraryAsync(
                workKey,
                cancellationToken);
        }

        if (TryParseGoogleBooksId(id, out var googleId))
        {
            return await GetGoogleBooksAsync(
                googleId,
                cancellationToken);
        }

        if (TryParseWikisourceId(id, out var wikisourcePageId))
        {
            return await GetIndonesianWikisourceAsync(
                wikisourcePageId,
                cancellationToken);
        }

        return null;
    }

    public async Task<IReadOnlyList<BookLibraryItem>> GetLibraryAsync(
        string profileId,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        targetLanguage = BookLanguageCatalog.Normalize(targetLanguage);

        var works = await db.NovelWorks
            .AsNoTracking()
            .Where(x => x.SourceProvider == ImportedBookProvider)
            .OrderBy(x => x.MetadataTitle ?? x.Title)
            .ToListAsync(cancellationToken);

        if (works.Count == 0)
        {
            return [];
        }

        var workIds = works.Select(x => x.Id).ToArray();

        var chapters = await db.NovelChapters
            .AsNoTracking()
            .Where(x => workIds.Contains(x.WorkId))
            .Select(x => new
            {
                x.Id,
                x.WorkId,
                x.SourceHash
            })
            .ToListAsync(cancellationToken);

        var chapterIds = chapters.Select(x => x.Id).ToArray();
        var cacheIdentity = TranslationCacheIdentity(
            await translator.GetTranslationModeAsync(cancellationToken));

        var translations = await db.NovelTranslations
            .AsNoTracking()
            .Where(x =>
                chapterIds.Contains(x.ChapterId)
                && x.TargetLanguage == targetLanguage
                && x.ProviderId == cacheIdentity
                && x.PromptVersion == TranslationPromptVersion)
            .Select(x => new
            {
                x.ChapterId,
                x.SourceHash
            })
            .ToListAsync(cancellationToken);

        var translated = translations
            .Select(x => (x.ChapterId, x.SourceHash))
            .ToHashSet();

        var progress = await db.NovelProgress
            .AsNoTracking()
            .Where(x =>
                x.ProfileId == profileId
                && workIds.Contains(x.WorkId))
            .ToDictionaryAsync(
                x => x.WorkId,
                cancellationToken);

        var chaptersByWork = chapters
            .GroupBy(x => x.WorkId)
            .ToDictionary(x => x.Key, x => x.ToArray());

        return works.Select(work =>
        {
            var workChapters = chaptersByWork.GetValueOrDefault(work.Id) ?? [];
            progress.TryGetValue(work.Id, out var current);

            return new BookLibraryItem(
                work.Id,
                work.MetadataTitle ?? work.Title,
                work.Author,
                work.MetadataDescription ?? work.Description,
                work.CoverImageUrl,
                ParseGenres(work.MetadataGenresJson),
                workChapters.Length,
                workChapters.Count(x =>
                    translated.Contains((x.Id, x.SourceHash))),
                current?.ChapterId,
                current?.PositionPermille ?? 0,
                current?.UpdatedAt);
        }).ToArray();
    }

    public async Task<BookLibraryDetail?> GetLibraryBookAsync(
        Guid workId,
        string profileId,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        targetLanguage = BookLanguageCatalog.Normalize(targetLanguage);

        var work = await db.NovelWorks
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Id == workId
                    && x.SourceProvider == ImportedBookProvider,
                cancellationToken);

        if (work is null)
        {
            return null;
        }

        var cacheIdentity = TranslationCacheIdentity(
            await translator.GetTranslationModeAsync(cancellationToken));

        var chapters = await db.NovelChapters
            .AsNoTracking()
            .Where(x => x.WorkId == workId)
            .OrderBy(x => x.Number)
            .Select(chapter => new BookChapterItem(
                chapter.Id,
                chapter.Number,
                chapter.Title,
                db.NovelTranslations.Any(translation =>
                    translation.ChapterId == chapter.Id
                    && translation.TargetLanguage == targetLanguage
                    && translation.ProviderId == cacheIdentity
                    && translation.PromptVersion == TranslationPromptVersion
                    && translation.SourceHash == chapter.SourceHash)))
            .ToListAsync(cancellationToken);

        var progress = await db.NovelProgress
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.ProfileId == profileId
                    && x.WorkId == workId,
                cancellationToken);

        var coverage = await (
                from translation in db.NovelTranslations.AsNoTracking()
                join chapter in db.NovelChapters.AsNoTracking()
                    on translation.ChapterId equals chapter.Id
                where chapter.WorkId == workId
                    && translation.ProviderId == cacheIdentity
                    && translation.PromptVersion == TranslationPromptVersion
                    && translation.SourceHash == chapter.SourceHash
                group translation.ChapterId by translation.TargetLanguage into languageGroup
                orderby languageGroup.Key
                select new BookTranslationCoverage(languageGroup.Key, languageGroup.Distinct().Count()))
            .ToListAsync(cancellationToken);

        return new BookLibraryDetail(
            work,
            ParseGenres(work.MetadataGenresJson),
            chapters,
            progress,
            coverage);
    }

    public async Task<IReadOnlyList<string>> GetCachedTranslationLanguagesAsync(Guid workId, CancellationToken cancellationToken)
    {
        var cacheIdentity = TranslationCacheIdentity(await translator.GetTranslationModeAsync(cancellationToken));
        return await (
                from translation in db.NovelTranslations.AsNoTracking()
                join chapter in db.NovelChapters.AsNoTracking()
                    on translation.ChapterId equals chapter.Id
                where chapter.WorkId == workId
                    && translation.ProviderId == cacheIdentity
                    && translation.PromptVersion == TranslationPromptVersion
                    && translation.SourceHash == chapter.SourceHash
                select translation.TargetLanguage)
            .Distinct()
            .OrderBy(language => language)
            .Take(12)
            .ToListAsync(cancellationToken);
    }

    public async Task<BookReaderChapter?> GetReaderChapterAsync(
        Guid chapterId,
        string profileId,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        targetLanguage = BookLanguageCatalog.Normalize(targetLanguage);

        var chapter = await db.NovelChapters
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Id == chapterId,
                cancellationToken);

        if (chapter is null)
        {
            return null;
        }

        var work = await db.NovelWorks
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Id == chapter.WorkId
                    && x.SourceProvider == ImportedBookProvider,
                cancellationToken);

        if (work is null)
        {
            return null;
        }

        var sourceLanguage = GetSourceLanguage(work);
        var translation = sourceLanguage.Equals(
                targetLanguage,
                StringComparison.OrdinalIgnoreCase)
            ? null
            : await GetCachedTranslationAsync(
                chapter.Id,
                targetLanguage,
                cancellationToken);

        var previous = await db.NovelChapters
            .AsNoTracking()
            .Where(x =>
                x.WorkId == work.Id
                && x.Number < chapter.Number)
            .OrderByDescending(x => x.Number)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var next = await db.NovelChapters
            .AsNoTracking()
            .Where(x =>
                x.WorkId == work.Id
                && x.Number > chapter.Number)
            .OrderBy(x => x.Number)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var progress = await db.NovelProgress
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.ProfileId == profileId
                    && x.WorkId == work.Id,
                cancellationToken);

        var bookmarks = await db.NovelBookmarks
            .AsNoTracking()
            .Where(x =>
                x.ProfileId == profileId
                && x.ChapterId == chapter.Id)
            .OrderBy(x => x.PositionPermille)
            .ToListAsync(cancellationToken);

        return new BookReaderChapter(
            work,
            chapter,
            translation,
            NovelTextLayout.SplitParagraphs(chapter.OriginalText),
            NovelTextLayout.SplitParagraphs(translation?.Text),
            previous,
            next,
            progress?.ChapterId == chapter.Id ? progress : null,
            bookmarks,
            sourceLanguage,
            targetLanguage);
    }

    public async Task<Guid> AcquireCatalogBookAsync(
        string catalogId,
        CancellationToken cancellationToken)
    {
        if (TryParseWikisourceId(
                catalogId,
                out var wikisourcePageId))
        {
            return await ImportIndonesianWikisourceAsync(
                wikisourcePageId,
                cancellationToken);
        }

        var catalog = await GetAsync(
            catalogId,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "Book could not be found in the catalog.");

        if (!catalog.CanAcquire)
        {
            var match = await FindGutenbergMatchAsync(
                catalog.Title,
                catalog.Author,
                cancellationToken);

            if (match is not null)
            {
                catalog = catalog with
                {
                    EpubUrl = match.EpubUrl,
                    TextUrl = match.TextUrl,
                    TextSourceName = "Project Gutenberg"
                };
            }
        }

        if (string.IsNullOrWhiteSpace(catalog.EpubUrl))
        {
            throw new InvalidOperationException(
                "No authorized downloadable EPUB source was found for this title. Upload your EPUB instead.");
        }

        var bytes = await DownloadGutenbergFileAsync(
            new Uri(catalog.EpubUrl, UriKind.Absolute),
            cancellationToken);

        using var stream = new MemoryStream(bytes, writable: false);
        var parsed = EpubBookParser.Parse(
            stream,
            catalog.Title + ".epub");

        return await ImportParsedBookAsync(
            parsed,
            sourceKey: CleanSourceKey(catalog.Id),
            sourceUrl: catalog.SourceUrl,
            metadataProvider: NormalizeProvider(catalog.SourceName),
            metadataExternalId: catalog.Id,
            coverImageUrl: catalog.CoverImageUrl,
            fallbackAuthor: catalog.Author,
            fallbackDescription: catalog.Summary,
            fallbackSubjects: catalog.Subjects,
            fileName: catalog.Title + ".epub",
            sourceKind: "gutenberg",
            contentHash: HashBytes(bytes),
            sizeBytes: bytes.LongLength,
            cancellationToken);
    }

    public Task<Guid> ImportUploadedEpubAsync(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken) =>
        ImportEpubStreamAsync(
            stream,
            fileName,
            "upload",
            "upload://" + Uri.EscapeDataString(fileName),
            cancellationToken);

    private async Task<Guid> ImportEpubStreamAsync(
        Stream stream,
        string fileName,
        string sourceKind,
        string sourceUrl,
        CancellationToken cancellationToken,
        string? storagePath = null,
        Guid? existingWorkId = null)
    {
        using var copy = await CopyToMemoryBoundedAsync(
            stream,
            MaxEpubBytes,
            cancellationToken);

        copy.Position = 0;

        var parsed = EpubBookParser.Parse(
            copy,
            fileName);
        var bytes = copy.ToArray();
        var contentHash = HashBytes(bytes);
        var sourceKey =
            "upload-" + BuildParsedBookIdentity(parsed);

        if (existingWorkId is null)
        {
            var existingId = await db.NovelWorks
                .AsNoTracking()
                .Where(x =>
                    x.SourceProvider == ImportedBookProvider
                    && x.SourceKey == sourceKey)
                .Select(x => (Guid?)x.Id)
                .SingleOrDefaultAsync(cancellationToken);

            if (existingId is Guid existing)
            {
                return existing;
            }
        }

        return await ImportParsedBookAsync(
            parsed,
            sourceKey: sourceKey,
            sourceUrl: sourceUrl,
            metadataProvider: null,
            metadataExternalId: null,
            coverImageUrl: null,
            fallbackAuthor: null,
            fallbackDescription: null,
            fallbackSubjects: [],
            fileName: fileName,
            sourceKind: sourceKind,
            contentHash: contentHash,
            sizeBytes: bytes.LongLength,
            cancellationToken,
            storagePath: storagePath,
            existingWorkId: existingWorkId);
    }

    public async Task<Guid> ImportRemoteEpubAsync(
        string sourceUrl,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(
                sourceUrl?.Trim(),
                UriKind.Absolute,
                out var initialUri))
        {
            throw new InvalidOperationException(
                "Enter a valid absolute EPUB URL.");
        }

        await ValidateExternalEpubUriAsync(
            initialUri,
            cancellationToken);

        var (bytes, finalUri) = await DownloadExternalEpubAsync(
            initialUri,
            cancellationToken);

        using var stream = new MemoryStream(
            bytes,
            writable: false);
        var fileName = Path.GetFileName(
            finalUri.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName)
            || !fileName.EndsWith(
                ".epub",
                StringComparison.OrdinalIgnoreCase))
        {
            fileName = "remote-book.epub";
        }

        var parsed = EpubBookParser.Parse(
            stream,
            fileName);
        var sourceKey =
            "remote-" + BuildParsedBookIdentity(parsed);

        var existingId = await db.NovelWorks
            .AsNoTracking()
            .Where(x =>
                x.SourceProvider == ImportedBookProvider
                && x.SourceKey == sourceKey)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(cancellationToken);

        if (existingId is Guid existing)
        {
            return existing;
        }

        return await ImportParsedBookAsync(
            parsed,
            sourceKey,
            finalUri.ToString(),
            metadataProvider: "direct-epub",
            metadataExternalId: null,
            coverImageUrl: null,
            fallbackAuthor: null,
            fallbackDescription: null,
            fallbackSubjects: [],
            fileName: fileName,
            sourceKind: "remote",
            contentHash: HashBytes(bytes),
            sizeBytes: bytes.LongLength,
            cancellationToken);
    }

    public async Task<NovelTranslation?> GetCachedTranslationAsync(
        Guid chapterId,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var mode = await translator.GetTranslationModeAsync(cancellationToken);
        return await GetCachedTranslationAsync(
            chapterId,
            targetLanguage,
            mode,
            cancellationToken);
    }

    private async Task<NovelTranslation?> GetCachedTranslationAsync(
        Guid chapterId,
        string targetLanguage,
        AiTranslationMode mode,
        CancellationToken cancellationToken)
    {
        targetLanguage = BookLanguageCatalog.Normalize(targetLanguage);
        var cacheIdentity = TranslationCacheIdentity(mode);

        return await (
            from translation in db.NovelTranslations.AsNoTracking()
            join chapter in db.NovelChapters.AsNoTracking()
                on translation.ChapterId equals chapter.Id
            where translation.ChapterId == chapterId
                && translation.TargetLanguage == targetLanguage
                && translation.ProviderId == cacheIdentity
                && translation.PromptVersion == TranslationPromptVersion
                && translation.SourceHash == chapter.SourceHash
            orderby translation.CreatedAt descending
            select translation
        ).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<NovelTranslation> TranslateChapterAsync(
        Guid chapterId,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        targetLanguage = BookLanguageCatalog.Normalize(targetLanguage);

        var chapter = await db.NovelChapters
            .SingleOrDefaultAsync(
                x => x.Id == chapterId,
                cancellationToken)
            ?? throw new InvalidOperationException(
                "Book chapter was not found.");

        var work = await db.NovelWorks
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Id == chapter.WorkId
                    && x.SourceProvider == ImportedBookProvider,
                cancellationToken)
            ?? throw new InvalidOperationException(
                "Imported book was not found.");

        var sourceLanguage = GetSourceLanguage(work);
        if (sourceLanguage.Equals(
            targetLanguage,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "This chapter is already in the selected language.");
        }

        var translationMode =
            await translator.GetTranslationModeAsync(cancellationToken);
        var cacheIdentity = TranslationCacheIdentity(translationMode);

        var cached = await GetCachedTranslationAsync(
            chapterId,
            targetLanguage,
            translationMode,
            cancellationToken);

        if (cached is not null)
        {
            if (translator is IAiUsageReporter usageReporter)
            {
                usageReporter.RecordCacheHit(AiOperations.BookTranslation);
            }

            return cached;
        }

        await TranslationGate.WaitAsync(cancellationToken);
        try
        {
            cached = await GetCachedTranslationAsync(
                chapterId,
                targetLanguage,
                translationMode,
                cancellationToken);
            if (cached is not null)
            {
                if (translator is IAiUsageReporter usageReporter)
                {
                    usageReporter.RecordCacheHit(AiOperations.BookTranslation);
                }

                return cached;
            }

            var previousChapter = await db.NovelChapters
                .AsNoTracking()
                .Where(x =>
                    x.WorkId == work.Id
                    && x.Number < chapter.Number)
                .OrderByDescending(x => x.Number)
                .FirstOrDefaultAsync(cancellationToken);

            string? previousTargetContext = null;
            if (previousChapter is not null)
            {
                previousTargetContext = await db.NovelTranslations
                    .AsNoTracking()
                    .Where(x =>
                        x.ChapterId == previousChapter.Id
                        && x.TargetLanguage == targetLanguage
                        && x.ProviderId == cacheIdentity
                        && x.PromptVersion == TranslationPromptVersion
                        && x.SourceHash == previousChapter.SourceHash)
                    .OrderByDescending(x => x.CreatedAt)
                    .Select(x => x.Text)
                    .FirstOrDefaultAsync(cancellationToken);
            }

            var nextContext = await db.NovelChapters
                .AsNoTracking()
                .Where(x =>
                    x.WorkId == work.Id
                    && x.Number > chapter.Number)
                .OrderBy(x => x.Number)
                .Select(x => x.OriginalText)
                .FirstOrDefaultAsync(cancellationToken);

            var memoryStore = CreateTranslationMemoryStore();
            var bible = await memoryStore.GetOrCreateAsync(
                work.Id,
                sourceLanguage,
                targetLanguage,
                async token =>
                {
                    if (translationMode != AiTranslationMode.Maximum)
                    {
                        return BookTranslationBibleSeed.Empty;
                    }

                    var sample = await BuildBookAnalysisSampleAsync(
                        work.Id,
                        token);

                    var seed = await translator.AnalyzeBookAsync(
                        new BookTranslationAnalysisRequest(
                            work.MetadataTitle ?? work.Title,
                            work.Author,
                            work.MetadataDescription ?? work.Description,
                            ParseGenres(work.MetadataGenresJson),
                            sourceLanguage,
                            targetLanguage,
                            sample),
                        token);

                    // The sample covers the opening chapters only; spoiler-safe
                    // story context uses the analysis after that point.
                    return seed with
                    {
                        AnalysisThroughChapter = await db.NovelChapters
                            .AsNoTracking()
                            .Where(x => x.WorkId == work.Id)
                            .OrderBy(x => x.Number)
                            .Take(3)
                            .MaxAsync(x => (int?)x.Number, token)
                    };
                },
                cancellationToken);

            var bookContext = BuildTranslationContext(
                work,
                chapter,
                previousChapter?.OriginalText,
                previousTargetContext,
                nextContext,
                targetLanguage);

            // The whole story memory before it is reduced to what a segment needs (#412), so AI
            // activity and usage show how much context the relevant-context projection saves.
            var fullBibleCharacters = BookTranslationMemoryStore.RenderContext(bible, int.MaxValue).Length;

            var chunkStore = CreateTranslationChunkStore();
            var translatedChunks = new List<string>();
            var chunks = NovelTranslationService.ChunkText(
                chapter.OriginalText,
                5200);

            for (var index = 0; index < chunks.Count; index++)
            {
                var chunk = chunks[index];
                var bibleContext =
                    BookTranslationMemoryStore.RenderRelevantContext(
                        bible,
                        chunk,
                        4200,
                        chapter.Number);

                var localContext = bookContext
                    + "\nCurrent segment: "
                    + (index + 1).ToString(CultureInfo.InvariantCulture)
                    + "/"
                    + chunks.Count.ToString(CultureInfo.InvariantCulture)
                    + "\n\n"
                    + bibleContext;

                if (translatedChunks.Count > 0)
                {
                    localContext +=
                        "\nPrevious final translated segment ending:\n"
                        + Tail(translatedChunks[^1], 900);
                }

                var cachedChunk = await chunkStore.TryLoadAsync(
                    work.Id,
                    chapter.Id,
                    targetLanguage,
                    chapter.SourceHash,
                    TranslationPromptVersion,
                    translationMode,
                    index,
                    chunk,
                    cancellationToken);

                if (!string.IsNullOrWhiteSpace(cachedChunk))
                {
                    if (translator is IAiUsageReporter usageReporter)
                    {
                        usageReporter.RecordResumedChunk(
                            AiOperations.BookTranslation);
                    }

                    translatedChunks.Add(cachedChunk);
                    continue;
                }

                using var segmentScope = AiWorkScope.Enter(
                    index + 1,
                    chunks.Count,
                    localContext.Length - bibleContext.Length + fullBibleCharacters);

                var draft = await translator.TranslateLiteraryAsync(
                    chunk,
                    sourceLanguage,
                    targetLanguage,
                    localContext,
                    cancellationToken);

                if (string.IsNullOrWhiteSpace(draft))
                {
                    throw new InvalidOperationException(
                        "AI translation returned an empty book segment.");
                }

                var candidate = draft.Trim();

                if (translationMode == AiTranslationMode.Maximum)
                {
                    candidate = await translator.EditLiteraryAsync(
                        new BookLiteraryEditRequest(
                            chunk,
                            candidate,
                            sourceLanguage,
                            targetLanguage,
                            localContext),
                        cancellationToken);

                    if (string.IsNullOrWhiteSpace(candidate))
                    {
                        throw new InvalidOperationException(
                            "Literary editor returned an empty book segment.");
                    }

                    candidate = candidate.Trim();
                }

                if (translationMode is AiTranslationMode.Quality
                    or AiTranslationMode.Maximum)
                {
                    var review = await translator.ReviewLiteraryAsync(
                        new BookTranslationQaRequest(
                            chunk,
                            candidate,
                            sourceLanguage,
                            targetLanguage,
                            localContext),
                        cancellationToken);

                    candidate = review.Accepted
                        ? candidate
                        : review.CorrectedTranslation?.Trim();

                    if (string.IsNullOrWhiteSpace(candidate))
                    {
                        throw new InvalidOperationException(
                            "Translation QA did not return usable final text.");
                    }
                }

                await chunkStore.SaveAsync(
                    work.Id,
                    chapter.Id,
                    targetLanguage,
                    chapter.SourceHash,
                    TranslationPromptVersion,
                    translationMode,
                    index,
                    chunk,
                    candidate,
                    cancellationToken);

                translatedChunks.Add(candidate);
            }

            var finalText = string.Join(
                "\n\n",
                translatedChunks);

            var memoryContext =
                BookTranslationMemoryStore.RenderRelevantContext(
                    bible,
                    chapter.OriginalText,
                    5000,
                    chapter.Number);

            BookTranslationMemoryDelta delta;
            using (AiWorkScope.Enter(null, null, fullBibleCharacters))
            {
                delta = await translator.ExtractTranslationMemoryAsync(
                    new BookTranslationMemoryRequest(
                        chapter.Number,
                        chapter.Title,
                        CompactMemorySample(chapter.OriginalText, 6000),
                        CompactMemorySample(finalText, 6000),
                        sourceLanguage,
                        targetLanguage,
                        memoryContext),
                    cancellationToken);
            }

            await memoryStore.ApplyChapterDeltaAsync(
                bible,
                chapter.Id,
                chapter.Number,
                chapter.Title,
                delta,
                cancellationToken,
                chapter.SourceHash);

            var completed = new NovelTranslation
            {
                ChapterId = chapter.Id,
                TargetLanguage = targetLanguage,
                ProviderId = cacheIdentity,
                PromptVersion = TranslationPromptVersion,
                SourceHash = chapter.SourceHash,
                Text = finalText,
                CreatedAt = DateTime.UtcNow
            };

            db.NovelTranslations.Add(completed);
            await db.SaveChangesAsync(cancellationToken);
            return completed;
        }
        finally
        {
            TranslationGate.Release();
        }
    }

    public async Task TranslateBookAsync(
        Guid workId,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        targetLanguage = BookLanguageCatalog.Normalize(targetLanguage);

        var chapterIds = await db.NovelChapters
            .AsNoTracking()
            .Where(x => x.WorkId == workId)
            .OrderBy(x => x.Number)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        if (chapterIds.Count == 0)
        {
            throw new InvalidOperationException(
                "Book has no chapters to translate.");
        }

        foreach (var chapterId in chapterIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cached = await GetCachedTranslationAsync(
                chapterId,
                targetLanguage,
                cancellationToken);

            if (cached is null)
            {
                await TranslateChapterAsync(
                    chapterId,
                    targetLanguage,
                    cancellationToken);
            }
        }
    }

    public async Task<int> ClearBookTranslationsAsync(
        Guid workId,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        targetLanguage = BookLanguageCatalog.Normalize(targetLanguage);

        var isBook = await db.NovelWorks
            .AsNoTracking()
            .AnyAsync(
                x => x.Id == workId
                    && x.SourceProvider == ImportedBookProvider,
                cancellationToken);

        if (!isBook)
        {
            throw new InvalidOperationException(
                "Imported book was not found.");
        }

        var chapterIds = await db.NovelChapters
            .AsNoTracking()
            .Where(x => x.WorkId == workId)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        var deleted = await db.NovelTranslations
            .Where(x =>
                chapterIds.Contains(x.ChapterId)
                && x.TargetLanguage == targetLanguage
                && x.PromptVersion == TranslationPromptVersion)
            .ExecuteDeleteAsync(cancellationToken);

        // ExecuteDelete bypasses the EF change tracker. Detach matching
        // cached translations so a later parent delete in this request
        // cannot try to delete an already-removed row.
        var chapterSet = chapterIds.ToHashSet();
        foreach (var entry in db.ChangeTracker
                     .Entries<NovelTranslation>()
                     .Where(x =>
                         chapterSet.Contains(x.Entity.ChapterId)
                         && x.Entity.TargetLanguage == targetLanguage
                         && x.Entity.PromptVersion == TranslationPromptVersion)
                     .ToArray())
        {
            entry.State = EntityState.Detached;
        }

        await CreateTranslationChunkStore().ClearAsync(
            workId,
            targetLanguage,
            cancellationToken);

        return deleted;
    }

    public async Task DeleteImportedBookAsync(
        Guid workId,
        CancellationToken cancellationToken)
    {
        var work = await db.NovelWorks
            .SingleOrDefaultAsync(
                x => x.Id == workId
                    && x.SourceProvider == ImportedBookProvider,
                cancellationToken)
            ?? throw new InvalidOperationException(
                "Imported book was not found.");

        var storedFiles = await StoredFilePathsAsync(workId, cancellationToken);
        db.NovelWorks.Remove(work);
        await db.SaveChangesAsync(cancellationToken);

        DeleteLocalCoverFiles(workId);
        await DeleteStoredFilesAsync(storedFiles, cancellationToken);
        await CreateTranslationMemoryStore().DeleteWorkAsync(
            workId,
            cancellationToken);
        await ChapterArtwork.ChapterArtworkService.DeleteWorkAsync(
            db,
            configuration,
            workId,
            cancellationToken);
    }

    private void DeleteLocalCoverFiles(Guid workId)
    {
        var directory = CoversPath;

        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(
                     directory,
                     workId.ToString("N") + ".*",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public async Task SaveProgressAsync(
        string profileId,
        Guid workId,
        Guid chapterId,
        int positionPermille,
        string language,
        CancellationToken cancellationToken)
    {
        var exists = await db.NovelChapters
            .AsNoTracking()
            .AnyAsync(
                x => x.Id == chapterId
                    && x.WorkId == workId,
                cancellationToken);

        if (!exists)
        {
            throw new InvalidOperationException(
                "Book chapter was not found.");
        }

        var progress = await db.NovelProgress
            .SingleOrDefaultAsync(
                x => x.ProfileId == profileId
                    && x.WorkId == workId,
                cancellationToken);

        if (progress is null)
        {
            progress = new NovelProgress
            {
                ProfileId = profileId,
                WorkId = workId
            };
            db.NovelProgress.Add(progress);
        }

        progress.ChapterId = chapterId;
        progress.PositionPermille = Math.Clamp(
            positionPermille,
            0,
            1000);
        progress.AnchorLanguage = NormalizeAnchorLanguage(language);
        progress.AnchorParagraphIndex = null;
        progress.AnchorOffset = 0;
        progress.AnchorText = null;
        progress.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<NovelBookmark> AddBookmarkAsync(
        string profileId,
        Guid workId,
        Guid chapterId,
        int positionPermille,
        string language,
        CancellationToken cancellationToken)
    {
        var exists = await db.NovelChapters
            .AsNoTracking()
            .AnyAsync(
                x => x.Id == chapterId
                    && x.WorkId == workId,
                cancellationToken);

        if (!exists)
        {
            throw new InvalidOperationException(
                "Book chapter was not found.");
        }

        var bookmark = new NovelBookmark
        {
            ProfileId = profileId,
            WorkId = workId,
            ChapterId = chapterId,
            PositionPermille = Math.Clamp(
                positionPermille,
                0,
                1000),
            Language = NormalizeAnchorLanguage(language),
            ParagraphIndex = null,
            CharacterOffset = 0,
            AnchorText = null,
            Label = null,
            CreatedAt = DateTime.UtcNow
        };

        db.NovelBookmarks.Add(bookmark);
        await db.SaveChangesAsync(cancellationToken);
        return bookmark;
    }

    public async Task RemoveBookmarkAsync(
        string profileId,
        Guid bookmarkId,
        CancellationToken cancellationToken)
    {
        await db.NovelBookmarks
            .Where(x =>
                x.Id == bookmarkId
                && x.ProfileId == profileId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<string> GetReadableSampleAsync(
        BookCatalogItem book,
        CancellationToken cancellationToken,
        int maxCharacters = DefaultSampleCharacters)
    {
        if (string.IsNullOrWhiteSpace(book.TextUrl))
        {
            throw new InvalidOperationException(
                "No readable text source is available for this catalog item.");
        }

        var raw = await GetTextFollowingRedirectsAsync(
            new Uri(book.TextUrl, UriKind.Absolute),
            cancellationToken);

        return ExtractReadableSample(
            raw,
            maxCharacters);
    }

    public static string ExtractReadableSample(
        string rawText,
        int maxCharacters = DefaultSampleCharacters)
    {
        if (maxCharacters < 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCharacters));
        }

        var text = rawText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var startMarker = text.IndexOf(
            "*** START OF",
            StringComparison.OrdinalIgnoreCase);
        if (startMarker >= 0)
        {
            var contentStart = text.IndexOf(
                '\n',
                startMarker);
            if (contentStart >= 0)
            {
                text = text[(contentStart + 1)..];
            }
        }

        var endMarker = text.IndexOf(
            "*** END OF",
            StringComparison.OrdinalIgnoreCase);
        if (endMarker >= 0)
        {
            text = text[..endMarker];
        }

        text = text.Trim();

        var chapterMatches = Regex.Matches(
            text,
            @"(?im)^[ \t]*chapter\s+(?:[ivxlcdm]+|\d+|one|two|three|four|five|six|seven|eight|nine|ten)\b[^\n]*");

        for (var index = 0;
             index < chapterMatches.Count;
             index++)
        {
            var current = chapterMatches[index];
            var nextIndex =
                index + 1 < chapterMatches.Count
                    ? chapterMatches[index + 1].Index
                    : text.Length;

            if (nextIndex - current.Index >= 900)
            {
                text = text[current.Index..]
                    .TrimStart();
                break;
            }
        }

        if (text.Length <= maxCharacters)
        {
            return text;
        }

        var candidate = text[..maxCharacters];
        var paragraphBreak = candidate.LastIndexOf(
            "\n\n",
            StringComparison.Ordinal);

        if (paragraphBreak >= maxCharacters / 2)
        {
            candidate = candidate[..paragraphBreak];
        }
        else
        {
            var sentenceBreak = candidate.LastIndexOfAny(
                ['.', '!', '?']);
            if (sentenceBreak >= maxCharacters / 2)
            {
                candidate = candidate[..(sentenceBreak + 1)];
            }
        }

        return candidate.Trim();
    }

    private async Task<Guid> ImportParsedBookAsync(
        ParsedEpubBook parsed,
        string sourceKey,
        string sourceUrl,
        string? metadataProvider,
        string? metadataExternalId,
        string? coverImageUrl,
        string? fallbackAuthor,
        string? fallbackDescription,
        IReadOnlyList<string> fallbackSubjects,
        string fileName,
        string sourceKind,
        string contentHash,
        long sizeBytes,
        CancellationToken cancellationToken,
        string fileFormat = "EPUB",
        string fileMediaType = "application/epub+zip",
        string? storagePath = null,
        Guid? existingWorkId = null)
    {
        sourceKey = CleanSourceKey(sourceKey);

        var targeted = existingWorkId is Guid;
        var work = targeted
            ? await db.NovelWorks.SingleOrDefaultAsync(
                x => x.Id == existingWorkId!.Value
                    && x.SourceProvider == ImportedBookProvider,
                cancellationToken)
            : await db.NovelWorks.SingleOrDefaultAsync(
                x => x.SourceProvider == ImportedBookProvider
                    && x.SourceKey == sourceKey,
                cancellationToken);

        if (targeted && work is null)
        {
            throw new InvalidOperationException("The target Books work no longer exists.");
        }

        if (work is null)
        {
            work = new NovelWork
            {
                SourceProvider = ImportedBookProvider,
                SourceKey = sourceKey,
                ImportedAt = DateTime.UtcNow
            };
            db.NovelWorks.Add(work);
        }

        var subjects = parsed.Subjects.Count > 0
            ? parsed.Subjects
            : fallbackSubjects;

        work.SourceUrl = Truncate(
            sourceUrl,
            2048);
        if (!targeted)
        {
            work.Title = Truncate(
                parsed.Title,
                500);
            work.Author = TruncateNullable(
                parsed.Author ?? fallbackAuthor,
                300);
            work.Description = TruncateNullable(
                parsed.Description ?? fallbackDescription,
                4000);
            work.MetadataProvider = TruncateNullable(
                metadataProvider,
                80);
            work.MetadataExternalId = TruncateNullable(
                metadataExternalId,
                200);
            work.MetadataTitle = work.Title;
            work.MetadataDescription = work.Description;
        }
        else if (string.IsNullOrWhiteSpace(work.Author))
        {
            work.Author = TruncateNullable(
                parsed.Author ?? fallbackAuthor,
                300);
        }

        // Library artwork is always local once a book is imported. Prefer a
        // current Google Books edition cover, then the actual EPUB cover, then
        // the catalog fallback. Existing local artwork survives provider outages.
        // storagePath (when set) is this import's NAS destination (#389/#545), known here
        // before the edition/file row exists, so the cover can land beside it right away
        // instead of waiting for the next refresh.
        var hadLocalCover = await GetLocalCoverPathAsync(work.Id, storagePath, cancellationToken) is not null;
        var storedCover = await TryPersistPreferredCoverAsync(
            work.Id,
            parsed,
            coverImageUrl,
            storagePath,
            cancellationToken);
        work.CoverImageUrl = storedCover || hadLocalCover
            ? $"/Books/Cover/{work.Id}"
            : targeted
                ? work.CoverImageUrl
                : null;

        work.Format = "EPUB:" + NormalizeSourceLanguage(
            parsed.Language);
        work.MetadataStatus = "IMPORTED";
        work.MetadataGenresJson = subjects.Count > 0
            ? JsonSerializer.Serialize(
                subjects.Take(32).ToArray())
            : null;
        work.UpdatedAt = DateTime.UtcNow;

        // A Books work is a series with one implicit volume; its chapters go
        // through the canonical Novel volume write path.
        var volume = await NovelVolumeContent.EnsureImplicitVolumeAsync(
            db,
            work,
            NovelVolumeKinds.Book,
            cancellationToken);

        await NovelVolumeContent.SyncChaptersAsync(
            db,
            work,
            volume,
            parsed.Chapters
                .OrderBy(x => x.Number)
                .Select(imported => new NovelVolumeChapterInput(
                    $"book://{work.Id:N}/{imported.Number.ToString(CultureInfo.InvariantCulture)}",
                    imported.Title,
                    imported.Text))
                .ToArray(),
            cancellationToken);

        await UpsertEditionAndFileAsync(
            work,
            BookEditionFacts.From(parsed),
            sourceKey,
            sourceUrl,
            metadataProvider,
            metadataExternalId,
            fileName,
            sourceKind,
            contentHash,
            sizeBytes,
            fileFormat,
            fileMediaType,
            cancellationToken,
            storagePath);

        return work.Id;
    }

    private async Task UpsertEditionAndFileAsync(
        NovelWork work,
        BookEditionFacts parsed,
        string sourceKey,
        string sourceUrl,
        string? metadataProvider,
        string? metadataExternalId,
        string fileName,
        string sourceKind,
        string contentHash,
        long sizeBytes,
        string fileFormat,
        string fileMediaType,
        CancellationToken cancellationToken,
        string? storagePath = null)
    {
        var editionKey = BuildEditionKey(
            parsed,
            metadataProvider,
            metadataExternalId,
            sourceKey);

        var edition = await db.BookEditions
            .SingleOrDefaultAsync(
                x => x.WorkId == work.Id
                    && x.EditionKey == editionKey,
                cancellationToken);

        if (edition is null)
        {
            await db.BookEditions
                .Where(x => x.WorkId == work.Id && x.IsPrimary)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        x => x.IsPrimary,
                        false),
                    cancellationToken);

            edition = new BookEdition
            {
                WorkId = work.Id,
                EditionKey = editionKey,
                CreatedAt = DateTime.UtcNow
            };
            db.BookEditions.Add(edition);
        }

        edition.Language = NormalizeSourceLanguage(parsed.Language);
        edition.Isbn10 = TruncateNullable(parsed.Isbn10, 10);
        edition.Isbn13 = TruncateNullable(parsed.Isbn13, 13);
        edition.Publisher = TruncateNullable(parsed.Publisher, 300);
        edition.PublishedDate = TruncateNullable(parsed.PublishedDate, 80);
        edition.Title = TruncateNullable(parsed.Title, 500);
        edition.Author = TruncateNullable(parsed.Author ?? work.Author, 300);
        edition.SourceProvider = TruncateNullable(metadataProvider, 80);
        edition.SourceExternalId = TruncateNullable(metadataExternalId, 200);
        edition.IsPrimary = true;
        edition.UpdatedAt = DateTime.UtcNow;

        var fileKey = "sha256-" + contentHash[..Math.Min(48, contentHash.Length)]
            .ToLowerInvariant();

        var file = await db.BookFiles
            .SingleOrDefaultAsync(
                x => x.EditionId == edition.Id
                    && x.FileKey == fileKey,
                cancellationToken);

        if (file is null)
        {
            await db.BookFiles
                .Where(x => x.EditionId == edition.Id && x.IsPrimary)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        x => x.IsPrimary,
                        false),
                    cancellationToken);

            file = new BookFile
            {
                EditionId = edition.Id,
                FileKey = fileKey,
                ImportedAt = DateTime.UtcNow
            };
            db.BookFiles.Add(file);
        }

        file.FileName = Truncate(
            string.IsNullOrWhiteSpace(fileName)
                ? "book.epub"
                : Path.GetFileName(fileName),
            500);
        file.Format = Truncate(
            string.IsNullOrWhiteSpace(fileFormat)
                ? "EPUB"
                : fileFormat,
            32);
        file.MediaType = Truncate(
            string.IsNullOrWhiteSpace(fileMediaType)
                ? "application/octet-stream"
                : fileMediaType,
            120);
        file.SourceKind = Truncate(
            string.IsNullOrWhiteSpace(sourceKind)
                ? "unknown"
                : sourceKind,
            80);
        file.SourceUrl = TruncateNullable(sourceUrl, 2048);
        file.ContentHash = Truncate(contentHash, 64);
        file.SizeBytes = Math.Max(0, sizeBytes);
        file.StoragePath = TruncateNullable(storagePath, 2048);
        file.IsPrimary = true;

        await db.SaveChangesAsync(cancellationToken);
    }

    private static string BuildEditionKey(
        BookEditionFacts parsed,
        string? metadataProvider,
        string? metadataExternalId,
        string sourceKey)
    {
        if (!string.IsNullOrWhiteSpace(parsed.Isbn13))
        {
            return "isbn13-" + parsed.Isbn13;
        }

        if (!string.IsNullOrWhiteSpace(parsed.Isbn10))
        {
            return "isbn10-" + parsed.Isbn10;
        }

        if (!string.IsNullOrWhiteSpace(metadataProvider)
            && !string.IsNullOrWhiteSpace(metadataExternalId))
        {
            return CleanSourceKey(
                metadataProvider + "-" + metadataExternalId);
        }

        return CleanSourceKey(
            NormalizeSourceLanguage(parsed.Language)
            + "-"
            + sourceKey);
    }

    private async Task<IReadOnlyList<BookCatalogItem>> SearchOpenLibraryAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(
            "https://openlibrary.org/search.json"
            + "?q=" + Uri.EscapeDataString(query)
            + "&fields=key,title,author_name,cover_i,first_publish_year,subject,isbn,edition_count,language"
            + $"&limit={SearchLimit}");

        var response = await GetJsonAsync<OpenLibrarySearchResponse>(
            uri,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "Open Library returned no data.");

        return response.Docs
            .Where(x =>
                !string.IsNullOrWhiteSpace(x.Key)
                && !string.IsNullOrWhiteSpace(x.Title)
                && x.Key.StartsWith(
                    "/works/",
                    StringComparison.Ordinal))
            .Take(SearchLimit)
            .Select(MapOpenLibrarySearch)
            .ToArray();
    }

    private async Task<IReadOnlyList<BookCatalogItem>> SearchGutenbergAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var response = await GetJsonAsync<GutendexListResponse>(
            new Uri(
                httpClient.BaseAddress!,
                "books?search="
                    + Uri.EscapeDataString(query)),
            cancellationToken)
            ?? throw new InvalidOperationException(
                "Project Gutenberg catalog returned no data.");

        return response.Results
            .Select(MapGutenberg)
            .Take(SearchLimit)
            .ToArray();
    }

    private async Task<BookCatalogItem?> GetGutenbergAsync(
        int id,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsyncWithTimeout(
            new HttpRequestMessage(
                HttpMethod.Get,
                $"books/{id}"),
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var book = await response.Content
            .ReadFromJsonAsync<GutendexBook>(
                cancellationToken: cancellationToken);

        return book is null
            ? null
            : MapGutenberg(book);
    }

    private async Task<BookCatalogItem?> GetOpenLibraryAsync(
        string workKey,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsyncWithTimeout(
            new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(
                    $"https://openlibrary.org/works/{Uri.EscapeDataString(workKey)}.json")),
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var work = await response.Content
            .ReadFromJsonAsync<OpenLibraryWork>(
                cancellationToken: cancellationToken);

        if (work is null
            || string.IsNullOrWhiteSpace(work.Title))
        {
            return null;
        }

        var author = await ResolveOpenLibraryAuthorsAsync(
            work.Authors,
            cancellationToken);

        var coverId = work.Covers?
            .FirstOrDefault(x => x > 0);

        return new BookCatalogItem(
            "ol-" + workKey,
            work.Title.Trim(),
            string.IsNullOrWhiteSpace(author)
                ? null
                : author,
            ExtractDescription(work.Description),
            coverId is > 0
                ? $"https://covers.openlibrary.org/b/id/{coverId}-L.jpg"
                : null,
            (work.Subjects ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Take(16)
                .ToArray(),
            ParseYear(work.FirstPublishDate),
            null,
            null,
            $"https://openlibrary.org/works/{workKey}",
            "Open Library",
            null);
    }

    private async Task<string?> ResolveOpenLibraryAuthorsAsync(
        OpenLibraryAuthorReference[]? authorReferences,
        CancellationToken cancellationToken)
    {
        var keys = (authorReferences ?? [])
            .Select(x => x.Author?.Key)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToArray();

        if (keys.Length == 0)
        {
            return null;
        }

        var names = new List<string>(keys.Length);
        foreach (var key in keys)
        {
            try
            {
                var author = await GetJsonAsync<OpenLibraryAuthor>(
                    new Uri(
                        $"https://openlibrary.org{key}.json"),
                    cancellationToken);

                if (!string.IsNullOrWhiteSpace(author?.Name))
                {
                    names.Add(author.Name.Trim());
                }
            }
            catch (Exception exception) when (
                exception is HttpRequestException
                    or TaskCanceledException)
            {
                // Optional metadata lookup.
            }
        }

        return names.Count == 0
            ? null
            : string.Join(", ", names);
    }

    private async Task<BookCatalogItem?> FindGutenbergMatchAsync(
        string title,
        string? author,
        CancellationToken cancellationToken)
    {
        var query = string.IsNullOrWhiteSpace(author)
            ? title
            : $"{title} {author}";

        try
        {
            // Gutendex is often slow; do not let it hold up adding a book for long.
            using var gutendexTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            gutendexTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            var response = await GetJsonAsync<GutendexListResponse>(
                new Uri(
                    httpClient.BaseAddress!,
                    "books?search="
                        + Uri.EscapeDataString(query)),
                gutendexTimeout.Token);

            return response?.Results
                .Where(x => IsLikelyMatch(
                    x,
                    title,
                    author))
                .Select(MapGutenberg)
                .FirstOrDefault(x => x.CanAcquire);
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested
            && exception is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
        {
            // Gutendex is a third-party mirror of the Gutenberg catalog; fall back to Project
            // Gutenberg's own OPDS search when it is slow or down.
            return await FindGutenbergOpdsMatchAsync(title, author, cancellationToken);
        }
    }

    private async Task<BookCatalogItem?> FindGutenbergOpdsMatchAsync(
        string title,
        string? author,
        CancellationToken cancellationToken)
    {
        // Gutenberg's search matches every word, so use the main title and the author's surname only.
        var mainTitle = title.Split([';', ':'], 2)[0].Trim();
        var surname = author?.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        var query = string.IsNullOrWhiteSpace(surname) ? mainTitle : $"{mainTitle} {surname}";
        using var response = await SendAsyncWithTimeout(
            new HttpRequestMessage(
                HttpMethod.Get,
                new Uri("https://www.gutenberg.org/ebooks/search.opds/?query=" + Uri.EscapeDataString(query))),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var document = System.Xml.Linq.XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        System.Xml.Linq.XNamespace atom = "http://www.w3.org/2005/Atom";
        var wanted = GutenbergOpds.NormalizeTitle(title);
        foreach (var entry in document.Descendants(atom + "entry"))
        {
            var entryTitle = entry.Element(atom + "title")?.Value ?? "";
            var id = GutenbergOpds.EbookId(entry, atom);
            var normalized = GutenbergOpds.NormalizeTitle(entryTitle);
            if (id is null || normalized.Length == 0
                || !(normalized.StartsWith(wanted, StringComparison.Ordinal) || wanted.StartsWith(normalized, StringComparison.Ordinal)))
            {
                continue;
            }

            return new BookCatalogItem(
                $"gutenberg-{id}",
                entryTitle.Trim(),
                author,
                null,
                $"https://www.gutenberg.org/cache/epub/{id}/pg{id}.cover.medium.jpg",
                [],
                null,
                $"https://www.gutenberg.org/ebooks/{id}.txt.utf-8",
                $"https://www.gutenberg.org/ebooks/{id}.epub3.images",
                $"https://www.gutenberg.org/ebooks/{id}",
                "Project Gutenberg",
                "Project Gutenberg");
        }

        return null;
    }

    private async Task<T?> GetJsonAsync<T>(
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsyncWithTimeout(
            new HttpRequestMessage(
                HttpMethod.Get,
                uri),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        return await response.Content
            .ReadFromJsonAsync<T>(
                cancellationToken: cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsyncWithTimeout(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        timeout.CancelAfter(CatalogRequestTimeout);

        return await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
    }

    private async Task<byte[]> DownloadGutenbergFileAsync(
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        if (!IsAllowedGutenbergUri(initialUri))
        {
            throw new InvalidOperationException(
                "The ebook download source is not trusted.");
        }

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        timeout.CancelAfter(EpubDownloadTimeout);

        var currentUri = initialUri;
        for (var redirect = 0;
             redirect <= MaxRedirects;
             redirect++)
        {
            using var response = await httpClient.SendAsync(
                new HttpRequestMessage(
                    HttpMethod.Get,
                    currentUri),
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            if (IsRedirect(response.StatusCode))
            {
                if (redirect == MaxRedirects
                    || response.Headers.Location is null)
                {
                    throw new InvalidOperationException(
                        "The ebook download redirected too many times.");
                }

                currentUri = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(
                        currentUri,
                        response.Headers.Location);

                if (!IsAllowedGutenbergUri(currentUri))
                {
                    throw new InvalidOperationException(
                        "The ebook download redirected to an untrusted host.");
                }

                continue;
            }

            response.EnsureSuccessStatusCode();

            var length = response.Content.Headers.ContentLength;
            if (length is > MaxEpubBytes)
            {
                throw new InvalidOperationException(
                    "EPUB exceeds the 100 MB import limit.");
            }

            await using var stream =
                await response.Content.ReadAsStreamAsync(
                    timeout.Token);
            using var memory = await CopyToMemoryBoundedAsync(
                stream,
                MaxEpubBytes,
                timeout.Token);
            return memory.ToArray();
        }

        throw new InvalidOperationException(
            "The ebook download could not be completed.");
    }

    private async Task<(byte[] Bytes, Uri FinalUri)> DownloadExternalEpubAsync(
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        timeout.CancelAfter(EpubDownloadTimeout);

        var currentUri = initialUri;
        for (var redirect = 0;
             redirect <= MaxRedirects;
             redirect++)
        {
            await ValidateExternalEpubUriAsync(
                currentUri,
                timeout.Token);

            using var response = await httpClient.SendAsync(
                new HttpRequestMessage(
                    HttpMethod.Get,
                    currentUri),
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            if (IsRedirect(response.StatusCode))
            {
                if (redirect == MaxRedirects
                    || response.Headers.Location is null)
                {
                    throw new InvalidOperationException(
                        "The EPUB download redirected too many times.");
                }

                currentUri = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(
                        currentUri,
                        response.Headers.Location);
                continue;
            }

            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is > MaxEpubBytes)
            {
                throw new InvalidOperationException(
                    "EPUB exceeds the 100 MB import limit.");
            }

            await using var stream =
                await response.Content.ReadAsStreamAsync(
                    timeout.Token);
            using var memory = await CopyToMemoryBoundedAsync(
                stream,
                MaxEpubBytes,
                timeout.Token);

            var bytes = memory.ToArray();
            if (bytes.Length < 4
                || bytes[0] != (byte)'P'
                || bytes[1] != (byte)'K')
            {
                throw new InvalidOperationException(
                    "The remote URL did not return an EPUB/ZIP file.");
            }

            return (bytes, currentUri);
        }

        throw new InvalidOperationException(
            "The EPUB download could not be completed.");
    }

    public static void ValidateExternalEpubUriSyntax(Uri uri)
    {
        if (!uri.Scheme.Equals(
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Remote EPUB imports require HTTPS.");
        }

        if (string.IsNullOrWhiteSpace(uri.Host)
            || uri.UserInfo.Length > 0)
        {
            throw new InvalidOperationException(
                "Remote EPUB URL is not allowed.");
        }

        if (IPAddress.TryParse(
                uri.Host,
                out var literal)
            && IsPrivateOrSpecialAddress(literal))
        {
            throw new InvalidOperationException(
                "Remote EPUB URL must not target a private or local address.");
        }
    }

    private static async Task ValidateExternalEpubUriAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        ValidateExternalEpubUriSyntax(uri);

        if (IPAddress.TryParse(
                uri.Host,
                out _))
        {
            return;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(
                uri.DnsSafeHost,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is System.Net.Sockets.SocketException
                or ArgumentException)
        {
            throw new InvalidOperationException(
                "Remote EPUB host could not be resolved.",
                exception);
        }

        if (addresses.Length == 0
            || addresses.Any(IsPrivateOrSpecialAddress))
        {
            throw new InvalidOperationException(
                "Remote EPUB host resolves to a private or local address.");
        }
    }

    private static bool IsPrivateOrSpecialAddress(
        IPAddress address)
    {
        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return IsPrivateOrSpecialAddress(
                address.MapToIPv4());
        }

        if (address.AddressFamily
            == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast
                || (bytes[0] & 0xfe) == 0xfc;
        }

        var octets = address.GetAddressBytes();
        if (octets.Length != 4)
        {
            return true;
        }

        return octets[0] == 0
            || octets[0] == 10
            || octets[0] == 127
            || (octets[0] == 100
                && octets[1] is >= 64 and <= 127)
            || (octets[0] == 169
                && octets[1] == 254)
            || (octets[0] == 172
                && octets[1] is >= 16 and <= 31)
            || (octets[0] == 192
                && octets[1] == 168)
            || (octets[0] == 198
                && octets[1] is 18 or 19)
            || octets[0] >= 224;
    }

    private async Task<string> GetTextFollowingRedirectsAsync(
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        if (!IsAllowedGutenbergUri(initialUri))
        {
            throw new InvalidOperationException(
                "The readable text source is not trusted.");
        }

        var currentUri = initialUri;

        for (var redirect = 0;
             redirect <= MaxRedirects;
             redirect++)
        {
            using var response = await SendAsyncWithTimeout(
                new HttpRequestMessage(
                    HttpMethod.Get,
                    currentUri),
                cancellationToken);

            if (IsRedirect(response.StatusCode))
            {
                if (redirect == MaxRedirects
                    || response.Headers.Location is null)
                {
                    throw new InvalidOperationException(
                        "The book text source redirected too many times.");
                }

                currentUri = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(
                        currentUri,
                        response.Headers.Location);

                if (!IsAllowedGutenbergUri(currentUri))
                {
                    throw new InvalidOperationException(
                        "The book text source redirected to an untrusted host.");
                }

                continue;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content
                .ReadAsStringAsync(cancellationToken);
        }

        throw new InvalidOperationException(
            "The book text source could not be loaded.");
    }

    private static BookCatalogItem MapOpenLibrarySearch(
        OpenLibrarySearchDoc book)
    {
        var workKey = book.Key!["/works/".Length..];
        var author = book.AuthorName is { Length: > 0 }
            ? string.Join(
                ", ",
                book.AuthorName.Where(x =>
                    !string.IsNullOrWhiteSpace(x)))
            : null;

        // "default=false" makes a missing cover fail instead of returning a blank
        // placeholder, so the next candidate takes over.
        IReadOnlyList<string> covers = book.CoverId is > 0
            ? [
                $"https://covers.openlibrary.org/b/id/{book.CoverId}-L.jpg?default=false",
                $"https://covers.openlibrary.org/b/id/{book.CoverId}-M.jpg?default=false"
            ]
            : [];

        return new BookCatalogItem(
            "ol-" + workKey,
            book.Title!.Trim(),
            string.IsNullOrWhiteSpace(author)
                ? null
                : author,
            null,
            covers.FirstOrDefault(),
            (book.Subjects ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Take(16)
                .ToArray(),
            book.FirstPublishYear,
            null,
            null,
            $"https://openlibrary.org/works/{workKey}",
            "Open Library",
            null)
        {
            // A work lists the ISBNs of all its editions; they identify it across providers.
            Isbns = (book.Isbns ?? [])
                .Select(BookWorkSearch.NormalizeIsbn)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Take(40)
                .ToArray(),
            EditionCount = book.EditionCount,
            CoverCandidates = covers,
            // Open Library lists language(s) across all editions of the work (e.g. "eng", "ind").
            Language = BookWorkSearch.NormalizeLanguageTag(book.Languages?.FirstOrDefault())
        };
    }

    /// <summary>
    /// Maps one <c>/subjects/{subject}.json</c> work (#371's "New" row) to a catalog item. Unlike
    /// <see cref="MapOpenLibrarySearch"/>'s <c>/search.json</c> shape, subjects responses carry
    /// authors as objects and the cover id under a different field name, and (per the unscoped-query
    /// spam found live) need a publish-year sanity check rather than trusting the value outright.
    /// </summary>
    private static BookCatalogItem MapOpenLibrarySubjectWork(
        OpenLibrarySubjectWork book,
        int currentYear)
    {
        var workKey = book.Key!["/works/".Length..];
        var author = book.Authors is { Length: > 0 }
            ? string.Join(
                ", ",
                book.Authors
                    .Select(x => x.Name?.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x)))
            : null;

        // Community-editable placeholder/spam rows sometimes carry an implausible year
        // (e.g. 9999); a year outside plausible print-history bounds is dropped, not shown.
        var year = book.FirstPublishYear is int publishYear
            && publishYear > 1450
            && publishYear <= currentYear + 1
                ? publishYear
                : (int?)null;

        IReadOnlyList<string> covers = book.CoverId is > 0
            ? [
                $"https://covers.openlibrary.org/b/id/{book.CoverId}-L.jpg?default=false",
                $"https://covers.openlibrary.org/b/id/{book.CoverId}-M.jpg?default=false"
            ]
            : [];

        return new BookCatalogItem(
            "ol-" + workKey,
            book.Title!.Trim(),
            string.IsNullOrWhiteSpace(author)
                ? null
                : author,
            null,
            covers.FirstOrDefault(),
            (book.Subjects ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Take(16)
                .ToArray(),
            year,
            null,
            null,
            $"https://openlibrary.org/works/{workKey}",
            "Open Library",
            null)
        {
            EditionCount = book.EditionCount,
            CoverCandidates = covers
        };
    }

    private static BookCatalogItem MapGutenberg(
        GutendexBook book)
    {
        var author = string.Join(
            ", ",
            book.Authors
                .Select(x => x.Name?.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x)));

        var cover = book.Formats
            .Where(x => x.Key.StartsWith(
                "image/",
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Key.Equals(
                "image/jpeg",
                StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Value)
            .FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(x));

        var textUrl = book.Formats
            .Where(x => x.Key.StartsWith(
                "text/plain",
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Key.Contains(
                "utf-8",
                StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Value)
            .FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(x));

        var epubUrl = book.Formats
            .Where(x => x.Key.Equals(
                "application/epub+zip",
                StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Value)
            .FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(x));

        var summary = book.Summaries
            .FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(x))
            ?.Trim();

        return new BookCatalogItem(
            book.Id.ToString(
                CultureInfo.InvariantCulture),
            book.Title.Trim(),
            string.IsNullOrWhiteSpace(author)
                ? null
                : author,
            summary,
            cover,
            book.Subjects
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Take(16)
                .ToArray(),
            null,
            textUrl,
            epubUrl,
            $"https://www.gutenberg.org/ebooks/{book.Id}",
            "Project Gutenberg",
            textUrl is null
                ? null
                : "Project Gutenberg");
    }

    private static bool IsLikelyMatch(
        GutendexBook candidate,
        string title,
        string? author)
    {
        var expectedTitle = NormalizeForMatch(title);
        var candidateTitle = NormalizeForMatch(
            candidate.Title);

        if (expectedTitle.Length == 0
            || (!candidateTitle.Contains(
                    expectedTitle,
                    StringComparison.Ordinal)
                && !expectedTitle.Contains(
                    candidateTitle,
                    StringComparison.Ordinal)))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(author))
        {
            return true;
        }

        var expectedAuthorParts =
            NormalizeForMatch(author)
                .Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries);
        var surname = expectedAuthorParts.LastOrDefault();

        return string.IsNullOrWhiteSpace(surname)
            || candidate.Authors.Any(x =>
                NormalizeForMatch(x.Name ?? "")
                    .Split(
                        ' ',
                        StringSplitOptions.RemoveEmptyEntries)
                    .Contains(
                        surname,
                        StringComparer.Ordinal));
    }

    private BookTranslationMemoryStore CreateTranslationMemoryStore() =>
        BookTranslationMemoryStore.FromConfiguration(configuration);

    private static string TranslationCacheIdentity(
        AiTranslationMode mode) =>
        $"book-v{TranslationPromptVersion}-{mode.ToString().ToLowerInvariant()}";

    private BookTranslationChunkStore CreateTranslationChunkStore()
    {
        var configured = configuration[
            "Books:Translation:ChunkCachePath"]?.Trim();

        if (string.IsNullOrWhiteSpace(configured))
        {
            var memoryPath = configuration[
                "Books:Translation:MemoryPath"]?.Trim();

            configured = string.IsNullOrWhiteSpace(memoryPath)
                ? BookTranslationChunkStore.DefaultRoot
                : Path.Combine(memoryPath, "chunks");
        }

        return new BookTranslationChunkStore(configured);
    }

    private async Task<string> BuildBookAnalysisSampleAsync(
        Guid workId,
        CancellationToken cancellationToken)
    {
        var chapters = await db.NovelChapters
            .AsNoTracking()
            .Where(x => x.WorkId == workId)
            .OrderBy(x => x.Number)
            .Take(3)
            .Select(x => new
            {
                x.Number,
                x.Title,
                x.OriginalText
            })
            .ToListAsync(cancellationToken);

        var builder = new StringBuilder();

        foreach (var item in chapters)
        {
            builder.AppendLine(
                $"Chapter {item.Number}: {item.Title}");
            builder.AppendLine(
                Head(
                    item.OriginalText,
                    3200));
            builder.AppendLine();

            if (builder.Length >= 9000)
            {
                break;
            }
        }

        var value = builder
            .ToString()
            .Trim();

        return value.Length <= 9000
            ? value
            : value[..9000];
    }

    private static string BuildTranslationContext(
        NovelWork work,
        NovelChapter chapter,
        string? previousSource,
        string? previousTarget,
        string? nextSource,
        string targetLanguage)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            $"Book: {work.MetadataTitle ?? work.Title}");
        if (!string.IsNullOrWhiteSpace(work.Author))
        {
            builder.AppendLine(
                $"Author: {work.Author}");
        }

        if (!string.IsNullOrWhiteSpace(work.Description))
        {
            builder.AppendLine(
                $"Book description: {Truncate(work.Description, 1800)}");
        }

        var genres = ParseGenres(
            work.MetadataGenresJson);
        if (genres.Count > 0)
        {
            builder.AppendLine(
                "Genres/themes: "
                + string.Join(", ", genres.Take(10)));
        }

        builder.AppendLine(
            $"Chapter {chapter.Number}: {chapter.Title}");

        if (targetLanguage.Equals(
                "id-modern",
                StringComparison.OrdinalIgnoreCase))
        {
            builder.AppendLine(
                "Target variant: Modern Indonesian. Use current Indonesian spelling and natural contemporary wording where the source is archaic, while preserving meaning, historical setting, names, relationships, tone, dialogue voice and literary atmosphere.");
        }

        if (!string.IsNullOrWhiteSpace(previousSource))
        {
            builder.AppendLine(
                "Previous source chapter ending (semantic context only; do not translate it):");
            builder.AppendLine(
                Tail(previousSource, 1600));
        }

        if (!string.IsNullOrWhiteSpace(previousTarget))
        {
            builder.AppendLine(
                $"Previously established {BookLanguageCatalog.GetName(targetLanguage)} translation ending "
                + "(translation-memory context only; do not repeat it):");
            builder.AppendLine(
                Tail(previousTarget, 1800));
        }

        if (!string.IsNullOrWhiteSpace(nextSource))
        {
            builder.AppendLine(
                "Next source chapter opening (disambiguation context only; do not translate it):");
            builder.AppendLine(
                Head(nextSource, 900));
        }

        return builder.ToString();
    }

    /// <summary>
    /// The cover to serve for a work (issue #406): beside its media on the Books NAS library root
    /// when one is configured and the book has been imported/refreshed onto it, else Jularr's own
    /// <c>/data/books/covers</c> copy -- unchanged behavior when no root is configured.
    /// </summary>
    public async Task<string?> GetLocalCoverPathAsync(
        Guid workId,
        string? knownStoragePath,
        CancellationToken cancellationToken)
    {
        var folder = await ResolveBesideMediaFolderAsync(workId, knownStoragePath, cancellationToken);
        if (folder is not null)
        {
            var resolved = await artwork.ResolveAsync(
                MediaArtworkScopes.Book, workId, CoverArtworkKind, folder, cancellationToken);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return GetLocalCoverPathFromData(workId);
    }

    private string? GetLocalCoverPathFromData(Guid workId)
    {
        var directory = CoversPath;

        if (!Directory.Exists(directory))
        {
            return null;
        }

        var prefix = workId.ToString("N") + ".";
        return Directory
            .EnumerateFiles(
                directory,
                prefix + "*",
                SearchOption.TopDirectoryOnly)
            .FirstOrDefault();
    }

    public static string GetCoverContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/jpeg"
        };

    /// <summary>
    /// The Books NAS library root's folder for a work's own media (issue #406), or null when no
    /// root is configured or the work has not been imported/refreshed onto one yet.
    /// <paramref name="knownStoragePath"/> lets a fresh import pass its just-computed destination
    /// before the file row that would otherwise carry it exists.
    /// </summary>
    private async Task<string?> ResolveBesideMediaFolderAsync(
        Guid workId,
        string? knownStoragePath,
        CancellationToken cancellationToken)
    {
        if (importSettings is null)
        {
            return null;
        }

        var libraryRoot = (await importSettings.LoadAsync(cancellationToken))
            .LibraryFor(MediaAcquisitionKind.Book)?.LibraryRoot;
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            return null;
        }

        var storagePath = knownStoragePath ?? await CanonicalStoragePathAsync(workId, cancellationToken);
        return BookArtworkFolders.Resolve(libraryRoot, storagePath);
    }

    /// <summary>The NAS path of the file Jularr currently keeps for a work, or null (no file kept,
    /// or it lives in Jularr's own <c>/data/books/files</c> copy rather than the NAS).</summary>
    private Task<string?> CanonicalStoragePathAsync(Guid workId, CancellationToken cancellationToken) =>
        (from file in db.BookFiles.AsNoTracking()
         join edition in db.BookEditions.AsNoTracking() on file.EditionId equals edition.Id
         where edition.WorkId == workId && file.StoragePath != null
         orderby edition.IsPrimary descending, file.IsPrimary descending, file.ImportedAt descending
         select file.StoragePath!).FirstOrDefaultAsync(cancellationToken);

    private async Task<string?> SaveLocalCoverAsync(
        Guid workId,
        byte[] bytes,
        string mediaType,
        string? identity,
        string? knownStoragePath,
        CancellationToken cancellationToken)
    {
        if (bytes.Length == 0 || bytes.Length > 10 * 1024 * 1024)
        {
            return null;
        }

        var extension = mediaType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/jpeg" or "image/jpg" => ".jpg",
            _ => null
        };

        if (extension is null)
        {
            return null;
        }

        var besideMediaFolder = await ResolveBesideMediaFolderAsync(workId, knownStoragePath, cancellationToken);
        if (besideMediaFolder is not null)
        {
            var outcome = await artwork.PersistAsync(
                MediaArtworkScopes.Book,
                workId,
                CoverArtworkKind,
                besideMediaFolder,
                bytes,
                MediaArtworkSources.Provider,
                identity,
                cancellationToken);
            if (outcome is ArtworkPersistOutcome.Saved or ArtworkPersistOutcome.Current or ArtworkPersistOutcome.KeptCustom)
            {
                var resolved = await artwork.ResolveAsync(
                    MediaArtworkScopes.Book, workId, CoverArtworkKind, besideMediaFolder, cancellationToken);
                if (resolved is not null)
                {
                    // The NAS copy is now canonical; a leftover /data copy from before the
                    // library root was configured would otherwise shadow it forever.
                    DeleteLocalCoverFiles(workId);
                    return resolved;
                }
            }

            // Unavailable/Rejected/Failed: fall through to /data so the cover is never lost while
            // the NAS root is briefly unreachable.
        }

        var directory = CoversPath;
        Directory.CreateDirectory(directory);

        foreach (var stale in Directory.EnumerateFiles(
                     directory,
                     workId.ToString("N") + ".*",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.Delete(stale);
            }
            catch (IOException)
            {
            }
        }

        var path = Path.Combine(
            directory,
            workId.ToString("N") + extension);

        await File.WriteAllBytesAsync(
            path,
            bytes,
            cancellationToken);
        return path;
    }

    private static string? FirstNonEmpty(
        string? primary,
        string? fallback)
    {
        var cleanPrimary = primary?.Trim();
        return string.IsNullOrWhiteSpace(cleanPrimary)
            ? fallback?.Trim()
            : cleanPrimary;
    }

    private static string BuildQuery(
        params (string Key, string Value)[] values) =>
        string.Join(
            "&",
            values
                .Where(x => !string.IsNullOrWhiteSpace(x.Value))
                .Select(x =>
                    Uri.EscapeDataString(x.Key)
                    + "="
                    + Uri.EscapeDataString(x.Value)));

    private static string GetSourceLanguage(
        NovelWork work) =>
        BookFileFormats.Language(work.Format) ?? "en";

    private static string NormalizeSourceLanguage(
        string? language)
    {
        var normalized = language?
            .Trim()
            .ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "en";
        }

        var separator = normalized.IndexOfAny(
            ['-', '_']);
        if (separator > 0)
        {
            normalized = normalized[..separator];
        }

        return normalized.Length <= 8
            ? normalized
            : "und";
    }

    private static string NormalizeAnchorLanguage(
        string? language)
    {
        var normalized = language?
            .Trim()
            .ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "original";
        }

        return normalized.Length <= 16
            ? normalized
            : normalized[..16];
    }

    private static IReadOnlyList<string> ParseGenres(
        string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json)
                ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string CleanSourceKey(string value)
    {
        var clean = Regex.Replace(
                value.Trim().ToLowerInvariant(),
                @"[^a-z0-9._-]+",
                "-")
            .Trim('-');

        if (clean.Length == 0)
        {
            clean = "book";
        }

        return clean.Length <= 80
            ? clean
            : clean[..80];
    }

    private static string NormalizeProvider(string value)
    {
        var normalized = Regex.Replace(
                value.Trim().ToLowerInvariant(),
                @"[^a-z0-9]+",
                "-")
            .Trim('-');

        return Truncate(
            "books-" + normalized,
            80);
    }


    private static string NormalizeForMatch(string value) =>
        Regex.Replace(
                value.ToLowerInvariant(),
                @"[^\p{L}\p{N}]+",
                " ")
            .Trim();

    private static bool TryParseGutenbergId(
        string id,
        out int gutenbergId)
    {
        if (id.StartsWith(
                "pg-",
                StringComparison.OrdinalIgnoreCase))
        {
            id = id[3..];
        }

        return int.TryParse(
            id,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out gutenbergId)
            && gutenbergId > 0;
    }

    private static bool TryParseOpenLibraryId(
        string id,
        out string workKey)
    {
        workKey = "";

        if (!id.StartsWith(
                "ol-",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var candidate = id[3..].Trim();
        if (!Regex.IsMatch(
                candidate,
                @"^OL\d+W$",
                RegexOptions.IgnoreCase))
        {
            return false;
        }

        workKey = candidate;
        return true;
    }

    private static string? ExtractDescription(
        JsonElement description)
    {
        if (description.ValueKind == JsonValueKind.String)
        {
            return description.GetString()?.Trim();
        }

        if (description.ValueKind == JsonValueKind.Object
            && description.TryGetProperty(
                "value",
                out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()?.Trim();
        }

        return null;
    }

    private static int? ParseYear(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = Regex.Match(
            value,
            @"\b(1[0-9]{3}|20[0-9]{2})\b");

        return match.Success
            && int.TryParse(
                match.Value,
                out var year)
            ? year
            : null;
    }

    private static bool IsRedirect(
        HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static bool IsAllowedGutenbergUri(Uri uri) =>
        uri.Scheme.Equals(
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase)
        && (uri.Host.Equals(
                "gutenberg.org",
                StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(
                ".gutenberg.org",
                StringComparison.OrdinalIgnoreCase));

    private static string BuildParsedBookIdentity(
        ParsedEpubBook book)
    {
        var canonical = new StringBuilder();
        canonical.Append(book.Title.Trim())
            .Append('\n')
            .Append(book.Author?.Trim() ?? "")
            .Append('\n')
            .Append(book.Language?.Trim().ToLowerInvariant() ?? "");

        foreach (var chapter in book.Chapters.OrderBy(x => x.Number))
        {
            canonical.Append('\n')
                .Append(chapter.Number)
                .Append('|')
                .Append(chapter.Title.Trim())
                .Append('|')
                .Append(Hash(chapter.Text));
        }

        var digest = Hash(canonical.ToString());
        return digest[..48].ToLowerInvariant();
    }

    private static string Hash(string value) =>
        Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(value)));

    private static string HashBytes(byte[] value) =>
        Convert.ToHexString(
            SHA256.HashData(value));

    private static string Head(
        string value,
        int maxLength) =>
        value.Length <= maxLength
            ? value
            : value[..maxLength];

    private static string CompactMemorySample(
        string value,
        int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var half = Math.Max(1, (maxLength - 40) / 2);
        return value[..half]
            + "\n\n[… middle omitted …]\n\n"
            + value[^half..];
    }

    private static string Tail(
        string value,
        int maxLength) =>
        value.Length <= maxLength
            ? value
            : value[^maxLength..];

    private static string Truncate(
        string value,
        int maxLength) =>
        value.Length <= maxLength
            ? value
            : value[..maxLength];

    private static string? TruncateNullable(
        string? value,
        int maxLength)
    {
        var clean = value?.Trim();
        if (string.IsNullOrWhiteSpace(clean))
        {
            return null;
        }

        return clean.Length <= maxLength
            ? clean
            : clean[..maxLength];
    }

    private static async Task<MemoryStream> CopyToMemoryBoundedAsync(
        Stream input,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var result = new MemoryStream();
        var buffer = new byte[81920];
        var total = 0;

        while (true)
        {
            var read = await input.ReadAsync(
                buffer,
                cancellationToken);
            if (read <= 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                result.Dispose();
                throw new InvalidOperationException(
                    "File exceeds the 100 MB import limit.");
            }

            await result.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }

        result.Position = 0;
        return result;
    }

    private sealed record GutendexListResponse(
        int Count,
        GutendexBook[] Results);

    private sealed record GutendexBook(
        int Id,
        string Title,
        string[] Subjects,
        GutendexPerson[] Authors,
        string[] Summaries,
        Dictionary<string, string> Formats,
        [property: JsonPropertyName("download_count")]
        int DownloadCount);

    private sealed record GutendexPerson(
        string? Name);

    private sealed record OpenLibrarySearchResponse(
        [property: JsonPropertyName("docs")]
        OpenLibrarySearchDoc[] Docs);

    private sealed record OpenLibrarySearchDoc(
        [property: JsonPropertyName("key")]
        string? Key,
        [property: JsonPropertyName("title")]
        string? Title,
        [property: JsonPropertyName("author_name")]
        string[]? AuthorName,
        [property: JsonPropertyName("cover_i")]
        int? CoverId,
        [property: JsonPropertyName("first_publish_year")]
        int? FirstPublishYear,
        [property: JsonPropertyName("subject")]
        string[]? Subjects,
        [property: JsonPropertyName("isbn")]
        string[]? Isbns = null,
        [property: JsonPropertyName("edition_count")]
        int? EditionCount = null,
        [property: JsonPropertyName("language")]
        string[]? Languages = null);

    /// <summary>The <c>/subjects/{subject}.json</c> envelope (#371's "New" row): a different shape
    /// than <c>/search.json</c> (authors as objects, <c>cover_id</c> instead of <c>cover_i</c>).</summary>
    private sealed record OpenLibrarySubjectResponse(
        [property: JsonPropertyName("works")]
        OpenLibrarySubjectWork[]? Works);

    private sealed record OpenLibrarySubjectWork(
        [property: JsonPropertyName("key")]
        string? Key,
        [property: JsonPropertyName("title")]
        string? Title,
        [property: JsonPropertyName("authors")]
        OpenLibrarySubjectAuthor[]? Authors,
        [property: JsonPropertyName("cover_id")]
        int? CoverId,
        [property: JsonPropertyName("first_publish_year")]
        int? FirstPublishYear,
        [property: JsonPropertyName("subject")]
        string[]? Subjects,
        [property: JsonPropertyName("edition_count")]
        int? EditionCount = null);

    private sealed record OpenLibrarySubjectAuthor(
        [property: JsonPropertyName("name")]
        string? Name);

    private sealed record OpenLibraryWork(
        [property: JsonPropertyName("title")]
        string Title,
        [property: JsonPropertyName("description")]
        JsonElement Description,
        [property: JsonPropertyName("subjects")]
        string[]? Subjects,
        [property: JsonPropertyName("covers")]
        int[]? Covers,
        [property: JsonPropertyName("first_publish_date")]
        string? FirstPublishDate,
        [property: JsonPropertyName("authors")]
        OpenLibraryAuthorReference[]? Authors);

    private sealed record OpenLibraryAuthorReference(
        [property: JsonPropertyName("author")]
        OpenLibraryKeyReference? Author);

    private sealed record OpenLibraryKeyReference(
        [property: JsonPropertyName("key")]
        string? Key);

    private sealed record OpenLibraryAuthor(
        [property: JsonPropertyName("name")]
        string? Name);
}

/// <summary>Helpers for Project Gutenberg's own OPDS search feed.</summary>
public static class GutenbergOpds
{
    public static string NormalizeTitle(string value)
    {
        // Gutenberg titles carry subtitles after ';' or ':' ("Frankenstein; or, the modern prometheus").
        var main = value.Split([';', ':'], 2)[0];
        return new string(main.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    public static int? EbookId(System.Xml.Linq.XElement entry, System.Xml.Linq.XNamespace atom)
    {
        foreach (var link in entry.Elements(atom + "link"))
        {
            var href = link.Attribute("href")?.Value ?? "";
            var match = System.Text.RegularExpressions.Regex.Match(href, @"/ebooks/(d+).opds$");
            if (match.Success)
            {
                return int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        var id = entry.Element(atom + "id")?.Value ?? "";
        var fromId = System.Text.RegularExpressions.Regex.Match(id, @"/ebooks/(d+).opds$");
        return fromId.Success ? int.Parse(fromId.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }
}
