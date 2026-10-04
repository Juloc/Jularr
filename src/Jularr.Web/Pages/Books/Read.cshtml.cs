using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.ReaderCore;
using Jularr.Web.Features.ReaderPreferences;
using Jularr.Web.Data;
using Jularr.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Web.Pages.Books;

/// <summary>
/// The PDF of a PDF book for the reader: its authenticated file URL (null when the
/// stored file is gone) and the chapter of every page, in page order.
/// </summary>
public sealed record BookPdfReaderDocument(string? FileUrl, IReadOnlyList<Guid> PageChapterIds);

public sealed class ReadModel(
    BookCatalogService books,
    CurrentAccountContext account,
    BackgroundJobQueue jobs,
    AppDbContext db) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public BookReaderChapter Reader { get; private set; } = null!;
    public ReaderDocumentDescriptor ReaderDocument { get; private set; } = null!;
    public ReaderSettingsSnapshot ReaderSettings { get; private set; } = null!;
    public IReadOnlyList<BookReaderHighlightItem> CurrentHighlights { get; private set; } = [];
    public int? RequestedPositionPermille { get; private set; }
    public int? RequestedParagraph { get; private set; }
    public int ChapterCount { get; private set; }

    /// <summary>
    /// Where the chapter opens: the requested position, else the saved position
    /// when the saved progress is in this chapter, else its start.
    /// </summary>
    public int InitialPositionPermille =>
        RequestedPositionPermille
        ?? (Reader.Progress is { } progress && progress.ChapterId == Reader.Chapter.Id
            ? progress.PositionPermille
            : 0);

    /// <summary>
    /// Set for PDF books: the reader renders the stored PDF's pages in the paper
    /// frame instead of the chapter text.
    /// </summary>
    public BookPdfReaderDocument? Pdf { get; private set; }

    /// <summary>
    /// Other current translated editions cached for this work. Translations are shared content,
    /// not profile/Learning state; the language menu links to them.
    /// </summary>
    public IReadOnlyList<string> CachedTranslationLanguages { get; private set; } = [];

    /// <summary>
    /// Whether this book already has at least one current cached translation in
    /// the requested target language. This is work-level state: it keeps the
    /// Reader language affordance stable while a whole-book translation is only
    /// partially complete and the currently open chapter is not translated yet.
    /// </summary>
    public bool HasWorkTranslationLanguage { get; private set; }

    public string? RequestedView { get; private set; }
    public bool IsOwner => account.IsOwner;

    public bool HasTranslation =>
        Reader.Translation is not null
        || Reader.SourceLanguage.Equals(
            Reader.TargetLanguage,
            StringComparison.OrdinalIgnoreCase);

    public async Task<IActionResult> OnGetAsync(
        Guid id,
        string? lang,
        int? pos,
        int? p,
        string? view,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var target = BookLanguageCatalog.Normalize(lang);
        var reader = await books.GetReaderChapterAsync(
            id,
            account.ProfileId,
            target,
            cancellationToken);

        if (reader is null)
        {
            return NotFound();
        }

        Reader = reader;
        RequestedPositionPermille = pos is null
            ? null
            : Math.Clamp(pos.Value, 0, 1000);
        RequestedParagraph = p is >= 0 ? p : null;
        RequestedView = NormalizeRequestedView(view);
        var availableTranslationLanguages = await books.GetCachedTranslationLanguagesAsync(reader.Work.Id, cancellationToken);
        HasWorkTranslationLanguage = availableTranslationLanguages.Any(language => language.Equals(reader.TargetLanguage, StringComparison.OrdinalIgnoreCase));
        CachedTranslationLanguages = availableTranslationLanguages
            .Where(language => !language.Equals(reader.SourceLanguage, StringComparison.OrdinalIgnoreCase)
                && !language.Equals(reader.TargetLanguage, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var documentLanguages = new[] { reader.SourceLanguage, reader.TargetLanguage }
            .Concat(availableTranslationLanguages)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(language => new ReaderDocumentLanguage(language, BookLanguageCatalog.GetName(language)))
            .ToList();

        if (BookFileFormats.IsPdf(reader.Work))
        {
            // A PDF book is one document whose logical pages are the work's chapters; the
            // reader shows the physical pages, so it needs every physical page's chapter.
            var pages = await books.GetPdfPageChapterIdsAsync(reader.Work.Id, cancellationToken);
            var file = await books.GetStoredFileAsync(reader.Work.Id, cancellationToken);
            Pdf = new BookPdfReaderDocument(
                file is null ? null : $"/Books/File/{reader.Work.Id}",
                pages);
            ChapterCount = pages.Count;
            ReaderDocument = ReaderDocumentDescriptor.Create(
                reader.Work.Id,
                ReaderContentType.Book,
                reader.Work.MetadataTitle ?? reader.Work.Title,
                ReaderPreferenceRules.ParseGenres(reader.Work.MetadataGenresJson),
                ReaderLayoutKind.FixedPages,
                documentLanguages);
        }
        else
        {
            ChapterCount = await db.NovelChapters
                .AsNoTracking()
                .CountAsync(x => x.WorkId == reader.Work.Id, cancellationToken);
            ReaderDocument = ReaderDocumentDescriptor.Create(
                reader.Work.Id,
                ReaderContentType.Book,
                reader.Work.MetadataTitle ?? reader.Work.Title,
                ReaderPreferenceRules.ParseGenres(reader.Work.MetadataGenresJson),
                languages: documentLanguages);
        }

        ReaderSettings = await ReaderPreferenceStore.GetAsync(
            db,
            account.ProfileId,
            reader.Work.Id,
            reader.Work.MetadataGenresJson,
            ReaderContentType.Book,
            cancellationToken);
        if (Pdf is not null)
        {
            // PDF translation is page-level; fixed pages do not use paragraph highlights.
            return Page();
        }

        CurrentHighlights = await BookReaderAnnotationStore.GetChapterHighlightsAsync(
            db,
            account.ProfileId,
            reader.Chapter.Id,
            cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostReaderSettingsAsync(
        Guid id,
        string? lang,
        string? scope,
        string? changedKey,
        string? genre,
        int genrePriority,
        bool resetField,
        bool resetScope,
        ReaderSettingsInput input,
        CancellationToken cancellationToken)
    {
        var target = BookLanguageCatalog.Normalize(lang);
        var reader = await books.GetReaderChapterAsync(
            id,
            account.ProfileId,
            target,
            cancellationToken);

        if (reader is null)
        {
            return NotFound();
        }

        try
        {
            var scopeKey = ReaderPreferenceScopes.ResolveTarget(
                scope,
                ReaderContentType.Book,
                reader.Work.Id,
                genre,
                genrePriority == 0
                    ? ReaderPreferenceScopes.DefaultGenrePriority
                    : genrePriority);

            if (resetScope)
            {
                await ReaderPreferenceStore.ResetScopeAsync(
                    db,
                    account.ProfileId,
                    scopeKey,
                    cancellationToken);
            }
            else if (resetField)
            {
                if (string.IsNullOrWhiteSpace(changedKey))
                {
                    return BadRequest("Reader setting key is required.");
                }

                await ReaderPreferenceStore.ResetScopeFieldAsync(
                    db,
                    account.ProfileId,
                    scopeKey,
                    changedKey,
                    cancellationToken);
            }
            else if (!string.IsNullOrWhiteSpace(changedKey))
            {
                await ReaderPreferenceStore.SaveScopeFieldAsync(
                    db,
                    account.ProfileId,
                    scopeKey,
                    changedKey,
                    input,
                    cancellationToken);
            }
            else
            {
                await ReaderPreferenceStore.SaveScopeAsync(
                    db,
                    account.ProfileId,
                    scopeKey,
                    scopeKey.StartsWith("work:", StringComparison.OrdinalIgnoreCase)
                        ? reader.Work.Id
                        : null,
                    input,
                    cancellationToken);
            }

            var settings = await ReaderPreferenceStore.GetAsync(
                db,
                account.ProfileId,
                reader.Work.Id,
                reader.Work.MetadataGenresJson,
                ReaderContentType.Book,
                cancellationToken);

            return new JsonResult(new { settings });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    public async Task<IActionResult> OnPostResetReaderSettingsAsync(
        Guid id,
        string? lang,
        CancellationToken cancellationToken)
    {
        var target = BookLanguageCatalog.Normalize(lang);
        var reader = await books.GetReaderChapterAsync(
            id,
            account.ProfileId,
            target,
            cancellationToken);

        if (reader is null)
        {
            return NotFound();
        }

        await ReaderPreferenceStore.ResetBookAsync(
            db,
            account.ProfileId,
            reader.Work.Id,
            cancellationToken);

        var settings = await ReaderPreferenceStore.GetAsync(
            db,
            account.ProfileId,
            reader.Work.Id,
            reader.Work.MetadataGenresJson,
            ReaderContentType.Book,
            cancellationToken);

        return new JsonResult(new { settings });
    }

    public async Task<IActionResult> OnPostTranslateAsync(
        Guid id,
        string? lang,
        CancellationToken cancellationToken)
    {
        var target = BookLanguageCatalog.Normalize(lang);

        var reader = await books.GetReaderChapterAsync(
            id,
            account.ProfileId,
            target,
            cancellationToken);

        if (reader is null)
        {
            return NotFound();
        }

        if (reader.SourceLanguage.Equals(
                target,
                StringComparison.OrdinalIgnoreCase)
            || reader.Translation is not null)
        {
            return new JsonResult(new { status = "ready" });
        }

        await jobs.QueueAsync(
            new OperationDescriptor(
                "book-chapter-translation",
                "Translation",
                "Translate book chapter",
                $"Target language: {target}",
                account.ProfileId,
                OperationLane.Normal,
                Retryable: true),
            async (operation, services, workerToken) =>
            {
                await operation.ReportAsync(
                    5,
                    "Translating chapter.",
                    cancellationToken: workerToken);

                var service = services.GetRequiredService<BookCatalogService>();
                await service.TranslateChapterAsync(
                    id,
                    target,
                    workerToken);

                await operation.ReportAsync(
                    100,
                    "Chapter translation completed.",
                    cancellationToken: workerToken);
            },
            cancellationToken);

        return new JsonResult(new { status = "queued" });
    }

    public async Task<IActionResult> OnGetTranslationStatusAsync(
        Guid id,
        string? lang,
        CancellationToken cancellationToken)
    {
        var target = BookLanguageCatalog.Normalize(lang);
        var reader = await books.GetReaderChapterAsync(
            id,
            account.ProfileId,
            target,
            cancellationToken);

        if (reader is null)
        {
            return NotFound();
        }

        if (reader.SourceLanguage.Equals(
                target,
                StringComparison.OrdinalIgnoreCase))
        {
            return new JsonResult(new
            {
                status = "ready",
                paragraphs = reader.OriginalParagraphs
            });
        }

        if (reader.Translation is not null)
        {
            return new JsonResult(new
            {
                status = "ready",
                paragraphs = reader.TranslatedParagraphs
            });
        }

        return new JsonResult(new { status = "pending" });
    }

    public async Task<IActionResult> OnGetChaptersAsync(
        Guid id,
        string? q,
        string? lang,
        CancellationToken cancellationToken)
    {
        var reader = await books.GetReaderChapterAsync(
            id,
            account.ProfileId,
            BookLanguageCatalog.Normalize(lang),
            cancellationToken);

        if (reader is null)
        {
            return NotFound();
        }

        var chapters = await BookReaderAnnotationStore.GetChaptersAsync(
            db,
            reader.Work.Id,
            q,
            cancellationToken);

        return new JsonResult(new
        {
            currentChapterId = reader.Chapter.Id,
            chapters
        });
    }

    public async Task<IActionResult> OnGetAnnotationsAsync(
        Guid id,
        string? lang,
        CancellationToken cancellationToken)
    {
        var reader = await books.GetReaderChapterAsync(
            id,
            account.ProfileId,
            BookLanguageCatalog.Normalize(lang),
            cancellationToken);

        if (reader is null)
        {
            return NotFound();
        }

        var annotations = await BookReaderAnnotationStore.GetAnnotationsAsync(
            db,
            account.ProfileId,
            reader.Work.Id,
            cancellationToken);

        return new JsonResult(annotations);
    }

    public async Task<IActionResult> OnPostHighlightAsync(
        Guid id,
        string? lang,
        string? anchorLanguage,
        int paragraphIndex,
        int startOffset,
        int endOffset,
        string? note,
        CancellationToken cancellationToken)
    {
        var reader = await books.GetReaderChapterAsync(
            id,
            account.ProfileId,
            BookLanguageCatalog.Normalize(lang),
            cancellationToken);

        if (reader is null)
        {
            return NotFound();
        }

        try
        {
            var highlight = await BookReaderAnnotationStore.AddHighlightAsync(
                db,
                account.ProfileId,
                reader,
                anchorLanguage,
                paragraphIndex,
                startOffset,
                endOffset,
                note,
                cancellationToken);

            return new JsonResult(highlight);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    public async Task<IActionResult> OnPostRemoveHighlightAsync(
        Guid id,
        Guid highlightId,
        CancellationToken cancellationToken)
    {
        var removed = await BookReaderAnnotationStore.RemoveHighlightAsync(
            db,
            account.ProfileId,
            highlightId,
            cancellationToken);

        return removed
            ? new OkResult()
            : NotFound();
    }

    public async Task<IActionResult> OnPostProgressAsync(
        Guid id,
        int positionPermille,
        string? lang,
        CancellationToken cancellationToken)
    {
        var reader = await books.GetReaderChapterAsync(
            id,
            account.ProfileId,
            BookLanguageCatalog.Normalize(lang),
            cancellationToken);

        if (reader is null)
        {
            return NotFound();
        }

        await books.SaveProgressAsync(
            account.ProfileId,
            reader.Work.Id,
            reader.Chapter.Id,
            positionPermille,
            reader.TargetLanguage,
            cancellationToken);

        return new OkResult();
    }

    public async Task<IActionResult> OnPostBookmarkAsync(
        Guid id,
        int positionPermille,
        string? lang,
        string? anchorLanguage,
        CancellationToken cancellationToken)
    {
        var reader = await books.GetReaderChapterAsync(
            id,
            account.ProfileId,
            BookLanguageCatalog.Normalize(lang),
            cancellationToken);

        if (reader is null)
        {
            return NotFound();
        }

        var bookmark = await books.AddBookmarkAsync(
            account.ProfileId,
            reader.Work.Id,
            reader.Chapter.Id,
            positionPermille,
            string.Equals(
                anchorLanguage,
                "original",
                StringComparison.OrdinalIgnoreCase)
                ? "original"
                : reader.TargetLanguage,
            cancellationToken);

        return new JsonResult(new
        {
            bookmark.Id,
            bookmark.PositionPermille
        });
    }

    public async Task<IActionResult> OnPostRemoveBookmarkAsync(
        Guid id,
        Guid bookmarkId,
        CancellationToken cancellationToken)
    {
        await books.RemoveBookmarkAsync(
            account.ProfileId,
            bookmarkId,
            cancellationToken);

        return new OkResult();
    }

    /// <summary>
    /// In-book search (reader top bar). Bounded and profile independent: it only
    /// reads the work's chapter text and the translations the reader could show.
    /// </summary>
    public async Task<IActionResult> OnGetSearchAsync(
        Guid id,
        string? q,
        string? lang,
        CancellationToken cancellationToken)
    {
        var reader = await books.GetReaderChapterAsync(
            id,
            account.ProfileId,
            BookLanguageCatalog.Normalize(lang),
            cancellationToken);

        if (reader is null)
        {
            return NotFound();
        }

        var translationLanguage = reader.SourceLanguage.Equals(
            reader.TargetLanguage,
            StringComparison.OrdinalIgnoreCase)
            ? null
            : reader.TargetLanguage;

        var hits = await ReaderTextSearch.SearchWorkAsync(
            db,
            reader.Work.Id,
            q,
            translationLanguage,
            cancellationToken);

        return new JsonResult(new { hits });
    }

    private static string? NormalizeRequestedView(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is "original" or "translated" or "both"
            ? normalized
            : null;
    }

}
