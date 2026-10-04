using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Learning;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Operations;
using Jularr.Web.Infrastructure;
using Jularr.Web.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Web.Pages.Books;

public sealed class LibraryModel(
    AppDbContext db,
    BookCatalogService books,
    CurrentAccountContext account,
    BackgroundJobQueue jobs,
    BookTranslationJobs translationJobs,
    ILogger<LibraryModel> logger,
    IInstanceModuleService? instanceModules = null) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public BookLibraryDetail Book { get; private set; } = null!;
    public string TargetLanguage { get; private set; } = "id";
    public bool SourceIsTarget { get; private set; }
    public LanguageEditionSelectorModel LanguageEdition { get; private set; } = null!;
    public bool IsOwner => account.IsOwner;

    /// <summary>The latest whole-book translation run into <see cref="TargetLanguage"/>; idle for the original language.</summary>
    public BookTranslationJob TranslationJob { get; private set; } = BookTranslationJob.None;

    public string SourceLanguage => GetSourceLanguage(Book.Work);

    /// <summary>Chapters (pages of a PDF) with a current translation into <see cref="TargetLanguage"/>.</summary>
    public int TranslatedCount => Book.Chapters.Count(x => x.HasTranslation);

    /// <summary>The original and every language that has a translation, plus the selected one, for the Editions &amp; Languages card.</summary>
    public IReadOnlyList<LibraryEditionRow> EditionRows { get; private set; } = [];

    /// <summary>A PDF book: its chapters are its pages, which the page lists in ranges.</summary>
    public bool IsPdf => BookFileFormats.IsPdf(Book.Work);

    /// <summary>The derived PDF document of a PDF book, loaded for the owner's diagnostics line; null until analysed.</summary>
    public PdfDerivedDocument? PdfAnalysis { get; private set; }

    /// <summary>
    /// A PDF's pages in about a dozen ranges of a round size (10, 20, 30 …); listing every
    /// page as a chapter would bury the book under hundreds of rows.
    /// </summary>
    public IReadOnlyList<BookPageRange> PageRanges => BookPageRange.Group(Book.Chapters);

    /// <summary>
    /// Whether generating a whole-book translation (Translate/Regenerate) is
    /// allowed, resolved through <see cref="LearningModuleResolver.ResolveTranslationEnabledAsync"/>
    /// for this work's Book scope. The per-chapter "Translated" badge and the
    /// translated-count summary reflect the cache instead: reading an already
    /// available translated chapter is core reader behaviour and must not
    /// depend on this capability (#369).
    /// </summary>
    public bool TranslationEnabled { get; private set; }

    public async Task<IActionResult> OnGetAsync(
        Guid id,
        string? lang,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        TargetLanguage = BookLanguageCatalog.Normalize(lang);
        var detail = await books.GetLibraryBookAsync(
            id,
            account.ProfileId,
            TargetLanguage,
            cancellationToken);

        if (detail is null)
        {
            return NotFound();
        }

        Book = detail;
        SourceIsTarget = GetSourceLanguage(Book.Work)
            .Equals(
                TargetLanguage,
                StringComparison.OrdinalIgnoreCase);
        TranslationEnabled = await ResolveTranslationEnabledAsync(id, cancellationToken);
        if (IsPdf && IsOwner)
        {
            PdfAnalysis = await books.GetPdfAnalysisAsync(id, cancellationToken);
        }

        if (!SourceIsTarget)
        {
            TranslationJob = await translationJobs.GetAsync(id, TargetLanguage, cancellationToken);
        }

        EditionRows = Book.TranslationCoverage
            .Select(x => new LibraryEditionRow(x.Language, false, x.TranslatedChapters))
            .Where(x => !x.Code.Equals(SourceLanguage, StringComparison.OrdinalIgnoreCase))
            .Prepend(new LibraryEditionRow(SourceLanguage, true, Book.Chapters.Count))
            .ToList();
        if (!EditionRows.Any(x => x.Code.Equals(TargetLanguage, StringComparison.OrdinalIgnoreCase)))
        {
            EditionRows = [.. EditionRows, new LibraryEditionRow(TargetLanguage, false, 0)];
        }

        LanguageEdition = BookLanguageEditionSelectorFactory.Create(
            "book-language-edition",
            Book.Work.MetadataTitle ?? Book.Work.Title,
            GetSourceLanguage(Book.Work),
            TargetLanguage,
            Book.Chapters.Count,
            IsPdf,
            Book.TranslationCoverage,
            TranslationEnabled,
            language => $"/Books/Library/{id}?lang={Uri.EscapeDataString(language)}",
            $"/Books/Library/{id}?handler=TranslateBook",
            Ui);
        return Page();
    }

    /// <summary>Starts, continues or retries the translation: chapters and chunks that are already cached are kept and skipped.</summary>
    public Task<IActionResult> OnPostTranslateBookAsync(Guid id, string? lang, CancellationToken cancellationToken) =>
        QueueTranslationAsync(id, lang, discardExisting: false, "books.library.translationQueued", cancellationToken);

    /// <summary>Redo: deletes the cached translation of this book and language, then translates from the start.</summary>
    public async Task<IActionResult> OnPostRegenerateAsync(Guid id, string? lang, CancellationToken cancellationToken)
    {
        if (!account.IsOwner)
        {
            return Forbid();
        }

        return await QueueTranslationAsync(id, lang, discardExisting: true, "books.library.regenerateQueued", cancellationToken);
    }

    public async Task<IActionResult> OnPostReanalyzePdfAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        if (!account.IsOwner)
        {
            return Forbid();
        }

        var detail = await books.GetLibraryBookAsync(
            id,
            account.ProfileId,
            BookLanguageCatalog.Normalize(null),
            cancellationToken);

        if (detail is null || !BookFileFormats.IsPdf(detail.Work))
        {
            return NotFound();
        }

        await jobs.QueueAsync(
            new OperationDescriptor(
                "book-pdf-analysis",
                "Books",
                "Re-analyze PDF",
                detail.Work.MetadataTitle ?? detail.Work.Title,
                account.ProfileId,
                OperationLane.Maintenance,
                Retryable: true),
            async (operation, services, workerToken) =>
            {
                await operation.ReportAsync(5, "Analyzing PDF pages.", cancellationToken: workerToken);
                var service = services.GetRequiredService<BookCatalogService>();
                var document = await service.ReanalyzePdfAsync(id, workerToken);
                await operation.ReportAsync(
                    100,
                    $"{document.LogicalPages.Count} logical of {document.PhysicalPageCount} physical pages.",
                    cancellationToken: workerToken);
            },
            cancellationToken);

        TempData["Status"] = ui["books.library.pdfReanalyzeQueued"];
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        if (!account.IsOwner)
        {
            return Forbid();
        }

        try
        {
            await books.DeleteImportedBookAsync(
                id,
                cancellationToken);
            TempData["Status"] = ui["books.library.removed"];
            return RedirectToPage("/Books");
        }
        catch (InvalidOperationException exception)
        {
            logger.LogError(exception, "Deleting imported book {BookId} failed", id);
            TempData["Status"] = ui["books.library.deleteFailed"];
            return RedirectToPage(new { id });
        }
    }

    private async Task<IActionResult> QueueTranslationAsync(
        Guid id,
        string? lang,
        bool discardExisting,
        string queuedKey,
        CancellationToken cancellationToken)
    {
        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!await ResolveTranslationEnabledAsync(id, cancellationToken))
        {
            return Forbid();
        }

        var targetLanguage = BookLanguageCatalog.Normalize(lang);
        var detail = await books.GetLibraryBookAsync(id, account.ProfileId, targetLanguage, cancellationToken);
        if (detail is null)
        {
            return NotFound();
        }

        if (GetSourceLanguage(detail.Work).Equals(targetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            TempData["Status"] = ui["books.library.alreadyInLanguage"];
            return RedirectToPage(new { id, lang = targetLanguage });
        }

        var queued = await translationJobs.QueueAsync(
            id,
            detail.Work.MetadataTitle ?? detail.Work.Title,
            targetLanguage,
            account.ProfileId,
            discardExisting,
            cancellationToken);
        TempData["Status"] = queued
            ? ui.Format(queuedKey, ("language", BookLanguageCatalog.GetName(targetLanguage)))
            : ui["books.library.translationAlreadyRunning"];
        return RedirectToPage(new { id, lang = targetLanguage });
    }

    private static string GetSourceLanguage(
        Jularr.Web.Features.Novels.NovelWork work) =>
        BookFileFormats.Language(work.Format) ?? "en";

    /// <summary>
    /// Resolves the Translation capability for this book's Book/work scope.
    /// Shared with the Book reader through
    /// <see cref="LearningModuleResolver.ResolveTranslationEnabledAsync"/>.
    /// </summary>
    private Task<bool> ResolveTranslationEnabledAsync(
        Guid workId,
        CancellationToken cancellationToken) =>
        new LearningModuleResolver(db, instanceModules).ResolveTranslationEnabledAsync(
            account.ProfileId,
            LearningMediaType.Book,
            workId.ToString(),
            contentKey: null,
            cancellationToken);
}

/// <summary>One language of a book in the Editions &amp; Languages card; the original counts all of its chapters as available.</summary>
public sealed record LibraryEditionRow(string Code, bool IsOriginal, int TranslatedUnits);
