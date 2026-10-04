using System.Globalization;
using System.Text.RegularExpressions;
using Jularr.Web.Features.Novels;

namespace Jularr.Web.Features.Books;

public static class BookLanguageCatalog
{
    private static readonly IReadOnlyDictionary<string, string> Names =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = "Indonesian",
            ["id-modern"] = "Modern Indonesian",
            ["de"] = "German",
            ["en"] = "English",
            ["fr"] = "French",
            ["es"] = "Spanish",
            ["it"] = "Italian",
            ["nl"] = "Dutch",
            ["pl"] = "Polish",
            ["pt"] = "Portuguese",
            ["tr"] = "Turkish",
            ["ja"] = "Japanese",
            ["ko"] = "Korean",
            ["zh"] = "Chinese",
            ["ru"] = "Russian"
        };

    public static IReadOnlyList<KeyValuePair<string, string>> Supported =>
        Names.ToArray();

    public static string Normalize(string? language, string fallback = "id")
    {
        var value = language?
            .Trim()
            .Replace('_', '-')
            .ToLowerInvariant();

        if (IsSupportedTag(value))
        {
            return value!;
        }

        var normalizedFallback = fallback
            .Trim()
            .Replace('_', '-')
            .ToLowerInvariant();

        return IsSupportedTag(normalizedFallback)
            ? normalizedFallback
            : "id";
    }

    public static string GetName(string? language)
    {
        var normalized = Normalize(language);

        if (Names.TryGetValue(normalized, out var name))
        {
            return name;
        }

        try
        {
            return CultureInfo.GetCultureInfo(normalized).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return normalized;
        }
    }

    private static bool IsSupportedTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 35)
        {
            return false;
        }

        if (Names.ContainsKey(value))
        {
            return true;
        }

        return Regex.IsMatch(
            value,
            @"^[a-z]{2,3}(?:-[a-z0-9]{2,8}){0,3}$",
            RegexOptions.CultureInvariant);
    }
}

public sealed record ImportedBookChapter(
    int Number,
    string Title,
    string Text)
{
    /// <summary>Normalized archive path of the spine document (EPUB only).</summary>
    public string? SourcePath { get; init; }

    /// <summary>
    /// Sanitized structured content whose text blocks equal the paragraphs of
    /// <see cref="Text"/>. Image blocks reference archive paths.
    /// </summary>
    public IReadOnlyList<NovelContentBlock> Blocks { get; init; } = [];

    /// <summary>
    /// Title of the nearest <c>nav</c>/<c>toc.ncx</c> entry this chapter is nested
    /// under, when that entry is not the volume's own top-level entry (#512).
    /// Null when the chapter is not part of a named group.
    /// </summary>
    public string? GroupTitle { get; init; }
}

/// <summary>An embedded raster image referenced by chapter content.</summary>
public sealed record ParsedEpubAsset(
    string Path,
    string MediaType,
    byte[] Bytes);

public sealed record ParsedEpubBook(
    string Title,
    string? Author,
    string? Description,
    string? Language,
    string? Isbn10,
    string? Isbn13,
    string? Publisher,
    string? PublishedDate,
    IReadOnlyList<string> Subjects,
    IReadOnlyList<ImportedBookChapter> Chapters,
    byte[]? CoverBytes,
    string? CoverMediaType)
{
    /// <summary>Value of the package's unique-identifier dc:identifier.</summary>
    public string? UniqueIdentifier { get; init; }

    /// <summary>Series name from calibre or EPUB 3 collection metadata.</summary>
    public string? SeriesTitle { get; init; }

    /// <summary>Position in the series from calibre or EPUB 3 metadata.</summary>
    public decimal? SeriesIndex { get; init; }

    /// <summary>Embedded images referenced by chapter blocks (only when requested).</summary>
    public IReadOnlyList<ParsedEpubAsset> Assets { get; init; } = [];
}

public sealed record BookLibraryItem(
    Guid WorkId,
    string Title,
    string? Author,
    string? Description,
    string? CoverImageUrl,
    IReadOnlyList<string> Subjects,
    int ChapterCount,
    int TranslatedChapterCount,
    Guid? CurrentChapterId,
    int ProgressPermille,
    DateTime? LastReadAt);

public sealed record BookChapterItem(
    Guid Id,
    int Number,
    string Title,
    bool HasTranslation);

/// <summary>
/// A run of a PDF book's pages (its chapters) for the book page: the first page's chapter to
/// open, the page numbers it spans and how many of its pages are translated.
/// </summary>
public sealed record BookPageRange(
    Guid FirstChapterId,
    int FirstPage,
    int LastPage,
    int PageCount,
    int TranslatedCount)
{
    private const int TargetRanges = 12;

    /// <summary>
    /// Groups pages into about a dozen ranges of a round size (10, 20, 30 …), so a long PDF is
    /// a short list instead of one row per page.
    /// </summary>
    public static IReadOnlyList<BookPageRange> Group(IReadOnlyList<BookChapterItem> pages)
    {
        var size = Math.Max(10, (int)Math.Ceiling(pages.Count / (double)TargetRanges / 10) * 10);
        return pages
            .Chunk(size)
            .Select(range => new BookPageRange(
                range[0].Id,
                range[0].Number,
                range[^1].Number,
                range.Length,
                range.Count(page => page.HasTranslation)))
            .ToArray();
    }
}

public sealed record BookLibraryDetail(
    NovelWork Work,
    IReadOnlyList<string> Subjects,
    IReadOnlyList<BookChapterItem> Chapters,
    NovelProgress? Progress,
    IReadOnlyList<BookTranslationCoverage> TranslationCoverage);

/// <summary>How many chapters of a work have a current cached translation in one target language.</summary>
public sealed record BookTranslationCoverage(string Language, int TranslatedChapters);

public sealed record BookReaderChapter(
    NovelWork Work,
    NovelChapter Chapter,
    NovelTranslation? Translation,
    IReadOnlyList<string> OriginalParagraphs,
    IReadOnlyList<string> TranslatedParagraphs,
    Guid? PreviousChapterId,
    Guid? NextChapterId,
    NovelProgress? Progress,
    IReadOnlyList<NovelBookmark> Bookmarks,
    string SourceLanguage,
    string TargetLanguage);
