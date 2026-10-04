using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Learning;
using Jularr.Web.Features.Learning.Courses;
using Jularr.Web.Features.Learning.LanguageAssistance;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.ReaderCore;
using Jularr.Web.Features.ReaderPreferences;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Novels;

public sealed record NovelReaderAnchor(
    string Language,
    int? ParagraphIndex,
    int Offset,
    int PositionPermille,
    string? AnchorText,
    bool Forced);

/// <summary>One text run of a furigana overlay; see <see cref="ReadModel.BuildFuriganaSegments"/>.</summary>
public sealed record FuriganaSegment(string Text, string? Reading);

/// <summary>
/// Novel reader page adapter. GET renders only cached content: a chapter
/// without downloaded text shows a preparation state whose action queues the
/// download through Operations instead of fetching from the provider inline.
/// </summary>
public sealed class ReadModel(
    NovelCatalogQueries catalog,
    NovelAnnotationService annotations,
    NovelProgressService progress,
    NovelImportService imports,
    NovelTranslationService translations,
    NovelMappingService mappings,
    NovelJobs jobs,
    LanguageTextAnalyzer languageAnalyzer,
    AppDbContext db,
    CurrentAccountContext account,
    OperationRunner operations,
    ILogger<ReadModel> logger,
    IInstanceModuleService? instanceModules = null) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public NovelReaderChapter Chapter { get; private set; } = null!;
    public IReadOnlyList<string> JapaneseParagraphs { get; private set; } = [];
    /// <summary>Cached German translation produced by the configured full AI pipeline.</summary>
    public IReadOnlyList<string> GermanParagraphs { get; private set; } = [];
    /// <summary>Cached text-only German translation produced locally by TranslateGemma.</summary>
    public IReadOnlyList<string> TranslateGemmaParagraphs { get; private set; } = [];
    /// <summary>Japanese content blocks: paragraphs, headings and illustrations.</summary>
    public IReadOnlyList<NovelReaderBlock> JapaneseBlocks { get; private set; } = [];
    public IReadOnlyList<NovelAnimeMapping> AnimeMappings { get; private set; } = [];
    public NovelChapterAnnotations Annotations { get; private set; } =
        new([], [], 0, 0);
    public NovelProgress? Progress { get; private set; }
    public NovelReaderAnchor InitialAnchor { get; private set; } =
        new(NovelReadingLanguage.Japanese, null, 0, 0, null, false);
    public ReaderDocumentDescriptor ReaderDocument { get; private set; } = null!;
    public ReaderSettingsSnapshot ReaderSettings { get; private set; } = null!;
    public OperationSnapshot? Preparation { get; private set; }
    public Guid? ReturnBookmarkId { get; private set; }
    public Guid? ReturnHighlightId { get; private set; }
    public bool IsOwner => account.IsOwner;

    /// <summary>
    /// Computed furigana is only offered when the content language's toolkit
    /// supports readings (Japanese today); the toggle and <see cref="OnGetFuriganaAsync"/>
    /// are both gated on it rather than a hardcoded language check.
    /// </summary>
    public bool FuriganaSupported { get; private set; }

    /// <summary>
    /// Whether generating (or regenerating) the chapter's German AI
    /// translation is allowed, resolved through the canonical Learning
    /// hierarchy (Novel media type → work → chapter). This only gates the
    /// translate/generation-status handlers and the "translate this chapter"
    /// prompt; it must never withhold an already cached translation, since
    /// reading available German text is core reader behaviour and must work
    /// with Learning off (#369).
    /// </summary>
    public bool TranslationEnabled { get; private set; }
    public bool TranslateGemmaConfigured { get; private set; }

    /// <summary>
    /// The reader renders TranslateGemma controls only when the local track can be read or
    /// generated; otherwise the chapter shows no trace of it (#487).
    /// </summary>
    public bool TranslateGemmaAvailable =>
        TranslateGemmaConfigured || TranslateGemmaParagraphs.Count > 0;

    private static bool FuriganaToolkitSupportsReadings =>
        LearningLanguageToolkitRegistry.Supports(
            NovelReadingLanguage.Japanese,
            LearningLanguageCapability.Readings);

    public async Task<IActionResult> OnGetAsync(
        Guid id,
        Guid? bookmark,
        Guid? highlight,
        int? paragraph,
        string? lang,
        Guid? prepare,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var chapter = await catalog.GetReaderChapterAsync(id, cancellationToken);
        if (chapter is null)
        {
            return NotFound();
        }

        Chapter = chapter;
        ReturnBookmarkId = bookmark;
        ReturnHighlightId = highlight;

        if (!chapter.HasContent)
        {
            Preparation = await GetPreparationAsync(prepare, cancellationToken);
            return Page();
        }

        var contentType = ReaderContentTypes.FromNovelMetadata(
            chapter.WorkFormat,
            chapter.SourceProvider);
        ReaderDocument = ReaderDocumentDescriptor.Create(
            chapter.WorkId,
            contentType,
            chapter.WorkTitle,
            ReaderPreferenceRules.ParseGenres(chapter.GenresJson),
            languages: [new("ja", "Japanisch"), new("de", "Deutsch")]);

        ReaderSettings = await ReaderPreferenceStore.GetAsync(
            db,
            account.ProfileId,
            chapter.WorkId,
            chapter.GenresJson,
            contentType,
            cancellationToken);

        // Readings only help Japanese text; an English book gets no furigana toggle.
        FuriganaSupported = FuriganaToolkitSupportsReadings &&
            JapaneseScript.Contains(chapter.OriginalText);
        TranslateGemmaConfigured = translations.TranslateGemmaConfigured;
        TranslationEnabled = await ResolveTranslationEnabledAsync(
            chapter.WorkId,
            id,
            cancellationToken);

        JapaneseParagraphs = NovelTextLayout.SplitParagraphs(chapter.OriginalText);
        // Cached German text is always readable (Original/German/Both):
        // reading an already available translation is core reader behaviour
        // and must not depend on the Learning Translation capability (#369).
        // Only generating a *new* translation is gated, through
        // TranslationEnabled below.
        GermanParagraphs = chapter.HasTranslation
            ? NovelTextLayout.SplitParagraphs(chapter.TranslationText)
            : [];
        TranslateGemmaParagraphs = chapter.HasTranslateGemmaTranslation
            ? NovelTextLayout.SplitParagraphs(chapter.TranslateGemmaTranslationText)
            : [];
        JapaneseBlocks = NovelChapterDocument.BuildReaderBlocks(
            chapter.OriginalText,
            chapter.ContentJson);

        AnimeMappings = await mappings.GetForChapterAsync(
            chapter.WorkId,
            chapter.Number,
            cancellationToken);

        var workProgress = await progress.GetProgressAsync(
            account.ProfileId,
            chapter.WorkId,
            cancellationToken);
        Progress = workProgress?.ChapterId == chapter.Id ? workProgress : null;

        Annotations = await annotations.GetChapterAnnotationsAsync(
            account.ProfileId,
            chapter.WorkId,
            chapter.Id,
            cancellationToken);

        InitialAnchor = ResolveInitialAnchor(bookmark, highlight, paragraph, lang);
        return Page();
    }

    public async Task<IActionResult> OnGetChaptersAsync(
        Guid id,
        string? q,
        int? after,
        int? before,
        CancellationToken cancellationToken)
    {
        var context = await catalog.GetChapterContextAsync(id, cancellationToken);
        if (context is null)
        {
            return NotFound();
        }

        var window = await catalog.GetChapterWindowAsync(
            context.WorkId,
            context.Number,
            q,
            after,
            before,
            NovelCatalogQueries.MaxChapterWindow,
            cancellationToken);

        return new JsonResult(window);
    }

    /// <summary>
    /// In-work text search (reader top bar), shared with the Books reader through
    /// <see cref="ReaderTextSearch"/>: Japanese source text plus German translations
    /// that still match their chapter source.
    /// </summary>
    public async Task<IActionResult> OnGetSearchAsync(
        Guid id,
        string? q,
        CancellationToken cancellationToken)
    {
        var context = await catalog.GetChapterContextAsync(id, cancellationToken);
        if (context is null)
        {
            return NotFound();
        }

        var hits = await ReaderTextSearch.SearchWorkAsync(
            db,
            context.WorkId,
            q,
            NovelReadingLanguage.German,
            cancellationToken);

        return new JsonResult(new { hits });
    }

    public async Task<IActionResult> OnGetWorkNotesAsync(
        Guid id,
        string? kind,
        int offset,
        CancellationToken cancellationToken)
    {
        var noteKind = kind?.Trim().ToLowerInvariant() switch
        {
            "bookmarks" => NovelNoteKind.Bookmark,
            "highlights" => NovelNoteKind.Highlight,
            _ => (NovelNoteKind?)null
        };

        if (noteKind is null)
        {
            return BadRequest("Unknown note kind.");
        }

        var context = await catalog.GetChapterContextAsync(id, cancellationToken);
        if (context is null)
        {
            return NotFound();
        }

        var page = await annotations.GetWorkNotesAsync(
            account.ProfileId,
            context.WorkId,
            context.ChapterId,
            noteKind.Value,
            offset,
            cancellationToken);

        return new JsonResult(new
        {
            items = page.Items,
            nextOffset = page.NextOffset,
            hasMore = page.HasMore
        });
    }

    /// <summary>
    /// Bounded, paged, profile-scoped search across the whole work's notes by
    /// bookmark label/anchor text or highlight text/note.
    /// </summary>
    public async Task<IActionResult> OnGetSearchNotesAsync(
        Guid id,
        string? kind,
        string? q,
        int offset,
        CancellationToken cancellationToken)
    {
        var noteKind = kind?.Trim().ToLowerInvariant() switch
        {
            "bookmarks" => NovelNoteKind.Bookmark,
            "highlights" => NovelNoteKind.Highlight,
            _ => (NovelNoteKind?)null
        };

        if (noteKind is null)
        {
            return BadRequest("Unknown note kind.");
        }

        if (string.IsNullOrWhiteSpace(q))
        {
            return BadRequest("A search query is required.");
        }

        var context = await catalog.GetChapterContextAsync(id, cancellationToken);
        if (context is null)
        {
            return NotFound();
        }

        var page = await annotations.SearchWorkNotesAsync(
            account.ProfileId,
            context.WorkId,
            noteKind.Value,
            q,
            offset,
            cancellationToken);

        return new JsonResult(new
        {
            items = page.Items,
            nextOffset = page.NextOffset,
            hasMore = page.HasMore
        });
    }

    /// <summary>
    /// The nearest bookmark before/after the caller's current chapter+position
    /// across the whole work, for previous/next bookmark navigation.
    /// </summary>
    public async Task<IActionResult> OnGetAdjacentBookmarkAsync(
        Guid id,
        bool forward,
        int positionPermille,
        CancellationToken cancellationToken)
    {
        var context = await catalog.GetChapterContextAsync(id, cancellationToken);
        if (context is null)
        {
            return NotFound();
        }

        var bookmark = await annotations.GetAdjacentBookmarkAsync(
            account.ProfileId,
            context.WorkId,
            context.Number,
            positionPermille,
            forward,
            cancellationToken);

        if (bookmark is null)
        {
            return new JsonResult(new { found = false });
        }

        return new JsonResult(new
        {
            found = true,
            bookmark.Id,
            bookmark.ChapterId,
            bookmark.ChapterNumber,
            bookmark.Label,
            bookmark.PositionPermille,
            isCurrentChapter = bookmark.ChapterId == id
        });
    }

    public async Task<IActionResult> OnPostPrepareChapterAsync(
        Guid id,
        Guid? bookmark,
        Guid? highlight,
        CancellationToken cancellationToken)
    {
        var context = await catalog.GetChapterContextAsync(id, cancellationToken);
        if (context is null)
        {
            return NotFound();
        }

        Guid? operationId = null;
        if (!context.HasContent)
        {
            operationId = await jobs.QueueChapterDownloadAsync(
                $"{context.WorkTitle} · Chapter {context.Number}",
                [context.ChapterId],
                account.ProfileId,
                cancellationToken);
        }

        return RedirectToPage(new { id, bookmark, highlight, prepare = operationId });
    }

    public async Task<IActionResult> OnGetChapterStatusAsync(
        Guid id,
        Guid? prepare,
        CancellationToken cancellationToken)
    {
        if (await catalog.HasContentAsync(id, cancellationToken))
        {
            return new JsonResult(new { ready = true });
        }

        var preparation = await GetPreparationAsync(prepare, cancellationToken);
        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        return new JsonResult(new
        {
            ready = false,
            status = preparation?.Status.ToString().ToLowerInvariant(),
            message = preparation?.Status is OperationStatus.Failed or OperationStatus.Interrupted
                ? ui["novels.read.prep.failed"]
                : null
        });
    }

    public async Task<IActionResult> OnPostTranslateAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!account.IsOwner)
        {
            return Forbid();
        }

        var context = await catalog.GetChapterContextAsync(id, cancellationToken);
        if (context is null)
        {
            return NotFound();
        }

        if (!await ResolveTranslationEnabledAsync(context.WorkId, id, cancellationToken))
        {
            return Forbid();
        }

        var cached = await translations.GetCachedAsync(
            id,
            NovelReadingLanguage.German,
            cancellationToken);

        if (cached is not null)
        {
            if (IsFetchRequest())
            {
                return new JsonResult(new { status = "ready" });
            }

            var uiCached = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
            TempData["Status"] = uiCached["novels.read.translationAlreadyCached"];
            return RedirectToPage(new { id });
        }

        await jobs.QueueTranslationAsync(
            id,
            $"{context.WorkTitle} · Chapter {context.Number}",
            account.ProfileId,
            cancellationToken);

        if (IsFetchRequest())
        {
            return new JsonResult(new { status = "queued" });
        }

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        TempData["Status"] = ui["novels.read.translationQueued"];
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostTranslateGemmaAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!account.IsOwner)
        {
            return Forbid();
        }

        var context = await catalog.GetChapterContextAsync(id, cancellationToken);
        if (context is null)
        {
            return NotFound();
        }

        if (!await ResolveTranslationEnabledAsync(context.WorkId, id, cancellationToken))
        {
            return Forbid();
        }

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!translations.TranslateGemmaConfigured)
        {
            return BadRequest(ui["novels.translation.localNotConfigured"]);
        }

        var cached = await translations.GetCachedAsync(
            id,
            NovelReadingLanguage.German,
            NovelTranslationEngine.TranslateGemma,
            cancellationToken);

        // A local translation made with another model, endpoint or algorithm stays readable,
        // but asking again queues a new one with the current configuration.
        if (cached is not null &&
            string.Equals(cached.ProviderId, translations.TranslateGemmaProviderId, StringComparison.Ordinal))
        {
            if (IsFetchRequest())
            {
                return new JsonResult(new
                {
                    status = "ready",
                    current = true,
                    paragraphs = NovelTextLayout.SplitParagraphs(cached.Text)
                });
            }

            TempData["Status"] = ui["novels.translation.localAlreadyCached"];
            return RedirectToPage(new { id });
        }

        await jobs.QueueTranslateGemmaAsync(
            id,
            $"{context.WorkTitle} · Chapter {context.Number}",
            account.ProfileId,
            cancellationToken);

        if (IsFetchRequest())
        {
            return new JsonResult(new { status = "queued" });
        }

        TempData["Status"] = ui["novels.translation.localQueued"];
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnGetTranslateGemmaStatusAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var context = await catalog.GetChapterContextAsync(id, cancellationToken);
        if (context is null)
        {
            return NotFound();
        }

        var cached = await translations.GetCachedAsync(
            id,
            NovelReadingLanguage.German,
            NovelTranslationEngine.TranslateGemma,
            cancellationToken);

        var currentProviderId = translations.TranslateGemmaProviderId;
        if (cached is not null)
        {
            // Cached text is readable without the Learning capability or a running endpoint.
            // Regeneration is only offered when the configuration changed since.
            var current = currentProviderId is null ||
                string.Equals(cached.ProviderId, currentProviderId, StringComparison.Ordinal);
            var canRegenerate =
                !current &&
                account.IsOwner &&
                await ResolveTranslationEnabledAsync(context.WorkId, id, cancellationToken);

            return new JsonResult(new
            {
                status = "ready",
                current,
                canRegenerate,
                paragraphs = NovelTextLayout.SplitParagraphs(cached.Text)
            });
        }

        if (currentProviderId is null)
        {
            return new JsonResult(new
            {
                status = "unavailable",
                canGenerate = false
            });
        }

        if (!await ResolveTranslationEnabledAsync(context.WorkId, id, cancellationToken))
        {
            return Forbid();
        }

        return new JsonResult(new
        {
            status = "pending",
            canGenerate = account.IsOwner
        });
    }

    public async Task<IActionResult> OnGetTranslationStatusAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var context = await catalog.GetChapterContextAsync(id, cancellationToken);
        if (context is null)
        {
            return NotFound();
        }

        var cached = await translations.GetCachedAsync(
            id,
            NovelReadingLanguage.German,
            cancellationToken);

        // Reading an already cached translation is core reader behaviour and
        // must not depend on the Learning capability (#369); only a *pending*
        // generation (no cached text yet) requires the resolved capability,
        // since it implies a translation would still need to be produced.
        if (cached is not null)
        {
            return new JsonResult(new
            {
                status = "ready",
                paragraphs = NovelTextLayout.SplitParagraphs(cached.Text)
            });
        }

        if (!await ResolveTranslationEnabledAsync(context.WorkId, id, cancellationToken))
        {
            return Forbid();
        }

        return new JsonResult(new { status = "pending" });
    }

    /// <summary>
    /// Computed furigana over kanji, per Japanese paragraph, for the reader's
    /// optional furigana toggle. Fetched on demand (never on the bounded
    /// reader GET) and applied client-side so toggling never reloads the
    /// page; native EPUB ruby a paragraph already carries is left alone
    /// (<see cref="FuriganaSegment.Reading"/> is null for it).
    /// </summary>
    public async Task<IActionResult> OnGetFuriganaAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!FuriganaToolkitSupportsReadings)
        {
            return NotFound();
        }

        var chapter = await catalog.GetReaderChapterAsync(id, cancellationToken);
        if (chapter is null || !chapter.HasContent)
        {
            return NotFound();
        }

        var blocks = NovelChapterDocument.BuildReaderBlocks(chapter.OriginalText, chapter.ContentJson);
        var paragraphs = blocks
            .Where(block => !block.IsImage)
            .ToDictionary(
                block => block.ParagraphIndex!.Value,
                block => BuildFuriganaSegments(block.Runs));

        return new JsonResult(new { paragraphs });
    }

    public async Task<IActionResult> OnPostRefreshSourceAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!account.IsOwner)
        {
            return Forbid();
        }

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        try
        {
            await operations.RunAsync(
                new OperationDescriptor(
                    "novel-chapter-refresh",
                    "Novels",
                    "Refresh novel chapter source",
                    ProfileId: account.ProfileId,
                    Lane: OperationLane.Normal,
                    IsDownload: true,
                    Retryable: false),
                async (operation, token) =>
                {
                    await operation.ReportAsync(
                        10,
                        "Refreshing novel chapter source.",
                        cancellationToken: token);

                    await imports.DownloadChapterContentAsync(
                        id,
                        forceRefresh: true,
                        token);
                },
                "Novel chapter source refreshed.",
                cancellationToken);

            TempData["Status"] = ui["novels.read.sourceRefreshed"];
        }
        catch (InvalidOperationException exception)
        {
            logger.LogError(exception, "Refreshing novel chapter source for chapter {ChapterId} failed", id);
            TempData["Status"] = ui["novels.read.sourceRefreshFailed"];
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostProgressAsync(
        Guid id,
        int positionPermille,
        string? anchorLanguage,
        int? anchorParagraphIndex,
        int anchorOffset,
        CancellationToken cancellationToken)
    {
        try
        {
            await progress.SaveProgressAsync(
                account.ProfileId,
                id,
                positionPermille,
                anchorLanguage,
                anchorParagraphIndex,
                anchorOffset,
                cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }

        return new OkResult();
    }

    public async Task<IActionResult> OnPostBookmarkAsync(
        Guid id,
        int positionPermille,
        string? language,
        int? paragraphIndex,
        int characterOffset,
        string? label,
        string? style,
        string? color,
        CancellationToken cancellationToken)
    {
        try
        {
            var bookmark = await annotations.AddBookmarkAsync(
                account.ProfileId,
                id,
                positionPermille,
                language,
                paragraphIndex,
                characterOffset,
                label,
                style,
                color,
                cancellationToken);

            return new JsonResult(new
            {
                bookmark.Id,
                bookmark.ChapterId,
                bookmark.PositionPermille,
                bookmark.Language,
                bookmark.ParagraphIndex,
                bookmark.CharacterOffset,
                bookmark.AnchorText,
                bookmark.Label,
                bookmark.Style,
                bookmark.Color
            });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    public async Task<IActionResult> OnPostRemoveBookmarkAsync(
        Guid id,
        Guid bookmarkId,
        CancellationToken cancellationToken)
    {
        await annotations.RemoveBookmarkAsync(
            account.ProfileId,
            bookmarkId,
            cancellationToken);

        return IsFetchRequest()
            ? new OkResult()
            : RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostReaderSettingsAsync(
        Guid id,
        string? scope,
        string? changedKey,
        string? genre,
        int genrePriority,
        bool resetField,
        bool resetScope,
        ReaderSettingsInput input,
        CancellationToken cancellationToken)
    {
        var context = await catalog.GetChapterContextAsync(id, cancellationToken);
        if (context is null)
        {
            return NotFound();
        }

        try
        {
            var contentType = ReaderContentTypes.FromNovelMetadata(
                context.WorkFormat,
                context.SourceProvider);
            var scopeKey = ReaderPreferenceScopes.ResolveTarget(
                scope,
                contentType,
                context.WorkId,
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
                    return BadRequest("A reader setting key is required.");
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
                    input,
                    cancellationToken);
            }

            var settings = await ReaderPreferenceStore.GetAsync(
                db,
                account.ProfileId,
                context.WorkId,
                context.GenresJson,
                contentType,
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
        CancellationToken cancellationToken)
    {
        var context = await catalog.GetChapterContextAsync(id, cancellationToken);
        if (context is null)
        {
            return NotFound();
        }

        await ReaderPreferenceStore.ResetWorkAsync(
            db,
            account.ProfileId,
            context.WorkId,
            cancellationToken);

        var contentType = ReaderContentTypes.FromNovelMetadata(
            context.WorkFormat,
            context.SourceProvider);
        var settings = await ReaderPreferenceStore.GetAsync(
            db,
            account.ProfileId,
            context.WorkId,
            context.GenresJson,
            contentType,
            cancellationToken);

        return new JsonResult(new { settings });
    }

    public async Task<IActionResult> OnPostBookmarkAppearanceAsync(
        Guid id,
        Guid bookmarkId,
        string? style,
        string? color,
        CancellationToken cancellationToken)
    {
        var bookmark = await annotations.UpdateBookmarkAppearanceAsync(
            account.ProfileId,
            bookmarkId,
            style,
            color,
            cancellationToken);

        if (bookmark is null)
        {
            return NotFound();
        }

        return new JsonResult(new
        {
            bookmark.Id,
            bookmark.Style,
            bookmark.Color
        });
    }

    public async Task<IActionResult> OnPostBookmarkLabelAsync(
        Guid id,
        Guid bookmarkId,
        string? label,
        CancellationToken cancellationToken)
    {
        var bookmark = await annotations.UpdateBookmarkLabelAsync(
            account.ProfileId,
            bookmarkId,
            label,
            cancellationToken);

        if (bookmark is null)
        {
            return NotFound();
        }

        return new JsonResult(new
        {
            bookmark.Id,
            bookmark.Label
        });
    }

    public async Task<IActionResult> OnPostHighlightNoteAsync(
        Guid id,
        Guid highlightId,
        string? note,
        CancellationToken cancellationToken)
    {
        var highlight = await annotations.UpdateHighlightNoteAsync(
            account.ProfileId,
            highlightId,
            note,
            cancellationToken);

        if (highlight is null)
        {
            return NotFound();
        }

        return new JsonResult(new
        {
            highlight.Id,
            highlight.Note
        });
    }

    public async Task<IActionResult> OnPostHighlightAsync(
        Guid id,
        string? language,
        int paragraphIndex,
        int startOffset,
        int endOffset,
        string? note,
        CancellationToken cancellationToken)
    {
        try
        {
            var highlight = await annotations.AddHighlightAsync(
                account.ProfileId,
                id,
                language,
                paragraphIndex,
                startOffset,
                endOffset,
                note,
                cancellationToken);

            return new JsonResult(new
            {
                highlight.Id,
                highlight.ChapterId,
                highlight.Language,
                highlight.ParagraphIndex,
                highlight.StartOffset,
                highlight.EndOffset,
                highlight.Text,
                highlight.Note
            });
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
        await annotations.RemoveHighlightAsync(
            account.ProfileId,
            highlightId,
            cancellationToken);

        return IsFetchRequest()
            ? new OkResult()
            : RedirectToPage(new { id });
    }

    /// <summary>
    /// Computed reading of each text run for the furigana overlay
    /// (<see cref="OnGetFuriganaAsync"/>): the concatenated <see cref="FuriganaSegment.Text"/>
    /// values equal the paragraph's plain text exactly, so the client can wrap
    /// them onto the live paragraph DOM by character offset regardless of
    /// existing highlight marks, the same way novel-annotations.js already
    /// wraps highlights. A run with its own native EPUB ruby is passed through
    /// with <see cref="FuriganaSegment.Reading"/> null: it already shows its
    /// own reading and must not be double-annotated.
    /// </summary>
    public IReadOnlyList<FuriganaSegment> BuildFuriganaSegments(IReadOnlyList<NovelInlineRun> runs)
    {
        var segments = new List<FuriganaSegment>(runs.Count);
        foreach (var run in runs)
        {
            if (run.Ruby is { Length: > 0 } || string.IsNullOrEmpty(run.Text))
            {
                segments.Add(new FuriganaSegment(run.Text, null));
                continue;
            }

            foreach (var token in languageAnalyzer.Analyze(run.Text, NovelReadingLanguage.Japanese))
            {
                var reading = token.Reading is { Length: > 0 } candidate
                    && JapaneseScript.Contains(token.Surface)
                        ? candidate
                        : null;
                segments.Add(new FuriganaSegment(token.Surface, reading));
            }
        }

        return segments;
    }

    private NovelReaderAnchor ResolveInitialAnchor(
        Guid? bookmarkId,
        Guid? highlightId,
        int? requestedParagraph,
        string? requestedLanguage)
    {
        var hasTranslation = GermanParagraphs.Count > 0;
        var hasTranslateGemmaTranslation = TranslateGemmaParagraphs.Count > 0;

        if (bookmarkId is Guid requestedBookmark &&
            Annotations.Bookmarks.FirstOrDefault(x => x.Id == requestedBookmark) is { } bookmark)
        {
            return new NovelReaderAnchor(
                bookmark.Language,
                bookmark.ParagraphIndex,
                bookmark.CharacterOffset,
                bookmark.PositionPermille,
                bookmark.AnchorText,
                Forced: true);
        }

        if (highlightId is Guid requestedHighlight &&
            Annotations.Highlights.FirstOrDefault(x => x.Id == requestedHighlight) is { } highlight)
        {
            var paragraphs = highlight.Language switch
            {
                NovelReadingLanguage.German => GermanParagraphs,
                NovelReadingLanguage.GermanTranslateGemma => TranslateGemmaParagraphs,
                _ => JapaneseParagraphs
            };
            var anchorText = highlight.ParagraphIndex < paragraphs.Count
                ? NovelTextLayout.CreateAnchorText(paragraphs[highlight.ParagraphIndex])
                : null;

            return new NovelReaderAnchor(
                highlight.Language,
                highlight.ParagraphIndex,
                highlight.StartOffset,
                0,
                anchorText,
                Forced: true);
        }

        // Round trip from a source context anchor (e.g. Learn/Review's "back to
        // chapter" link) that names a paragraph directly, without an actual
        // saved bookmark/highlight.
        if (requestedParagraph is int index && index >= 0)
        {
            var language = requestedLanguage switch
            {
                NovelReadingLanguage.German when hasTranslation =>
                    NovelReadingLanguage.German,
                NovelReadingLanguage.GermanTranslateGemma
                    when hasTranslateGemmaTranslation =>
                    NovelReadingLanguage.GermanTranslateGemma,
                _ => NovelReadingLanguage.Japanese
            };
            var paragraphs = language switch
            {
                NovelReadingLanguage.German => GermanParagraphs,
                NovelReadingLanguage.GermanTranslateGemma => TranslateGemmaParagraphs,
                _ => JapaneseParagraphs
            };
            var anchorText = index < paragraphs.Count
                ? NovelTextLayout.CreateAnchorText(paragraphs[index])
                : null;
            var positionPermille = paragraphs.Count > 0
                ? Math.Clamp(index * 1000 / paragraphs.Count, 0, 999)
                : 0;

            return new NovelReaderAnchor(
                language,
                index,
                0,
                positionPermille,
                anchorText,
                Forced: true);
        }

        if (Progress is not null)
        {
            return new NovelReaderAnchor(
                Progress.AnchorLanguage,
                Progress.AnchorParagraphIndex,
                Progress.AnchorOffset,
                Progress.PositionPermille,
                Progress.AnchorText,
                Forced: false);
        }

        return new NovelReaderAnchor(
            hasTranslation ? NovelReadingLanguage.German : NovelReadingLanguage.Japanese,
            null,
            0,
            0,
            null,
            Forced: false);
    }

    private async Task<OperationSnapshot?> GetPreparationAsync(
        Guid? operationId,
        CancellationToken cancellationToken)
    {
        if (operationId is not Guid id)
        {
            return null;
        }

        var snapshot = await new OperationStore(db).GetAsync(id, cancellationToken);
        return snapshot is not null &&
            snapshot.Kind == NovelJobs.ChapterDownloadKind &&
            snapshot.ProfileId == account.ProfileId
                ? snapshot
                : null;
    }

    private bool IsFetchRequest() =>
        string.Equals(
            Request.Headers["X-Requested-With"].ToString(),
            "fetch",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the Translation capability for this chapter's scope (profile →
    /// Novel media type → work → chapter) so cached German text and the
    /// translate/status handlers follow the canonical Learning hierarchy
    /// instead of only checking whether a translation happens to be cached.
    /// Shared with the Work chapter-list page through
    /// <see cref="LearningModuleResolver.ResolveTranslationEnabledAsync"/>.
    /// </summary>
    private Task<bool> ResolveTranslationEnabledAsync(
        Guid workId,
        Guid chapterId,
        CancellationToken cancellationToken) =>
        new LearningModuleResolver(db, instanceModules).ResolveTranslationEnabledAsync(
            account.ProfileId,
            LearningMediaType.Novel,
            workId.ToString(),
            chapterId.ToString(),
            cancellationToken);
}
