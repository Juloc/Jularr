using System.Globalization;
using Jularr.Web.Features.Novels;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Books;

public sealed partial class BookCatalogService
{
    /// <summary>Where derived PDF documents are kept (<c>Books:DerivedPath</c>, default <c>/data/books/derived</c>).</summary>
    private PdfDerivedDocumentStore CreateDerivedDocumentStore() =>
        new(FirstNonEmpty(configuration["Books:DerivedPath"], null) ?? PdfDerivedDocumentStore.DefaultRoot);

    /// <summary>
    /// The stable identity of the chapter of a logical PDF page: the work and the first physical page that shows it.
    /// A chapter keeps this key across re-analysis, so progress, bookmarks and translations stay attached.
    /// </summary>
    private static string PdfPageChapterKey(Guid workId, int firstPhysicalPage) =>
        $"book://{workId:N}/{firstPhysicalPage.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The newest derived document of a PDF work's stored file, or null when none was built yet.</summary>
    public async Task<PdfDerivedDocument?> GetPdfAnalysisAsync(Guid workId, CancellationToken cancellationToken)
    {
        var file = await GetStoredFileAsync(workId, cancellationToken);
        return file is null ? null : await CreateDerivedDocumentStore().TryLoadLatestAsync(file.ContentHash, cancellationToken);
    }

    /// <summary>
    /// Re-reads the stored PDF, rebuilds its derived document and resyncs the work's chapters to the logical pages.
    /// The PDF is never touched. Chapters of the first occurrence of each logical page stay (their translations are
    /// re-pointed when stripped furniture changed the text), chapters of repeated physical pages are merged into
    /// them. A PDF that cannot be read leaves the work unchanged.
    /// </summary>
    public async Task<PdfDerivedDocument> ReanalyzePdfAsync(Guid workId, CancellationToken cancellationToken)
    {
        var work = await db.NovelWorks.SingleOrDefaultAsync(x => x.Id == workId && x.SourceProvider == ImportedBookProvider, cancellationToken)
            ?? throw new InvalidOperationException("The Books work no longer exists.");
        var file = BookFileFormats.IsPdf(work) ? await GetStoredFileAsync(workId, cancellationToken) : null;
        if (file is null)
        {
            throw new InvalidOperationException("The work has no stored PDF to analyse.");
        }

        if (file.SizeBytes > PdfDocumentReader.MaxReadBytes)
        {
            throw new InvalidOperationException("The PDF is too large to analyse.");
        }

        var content = PdfDocumentReader.Read(await File.ReadAllBytesAsync(file.Path, cancellationToken));
        if (content.Pages.Count == 0)
        {
            throw new InvalidOperationException("The PDF could not be read; the work was left unchanged.");
        }

        var analysis = PdfDocumentAnalyzer.Analyze(content.Pages, file.ContentHash);
        var volume = await NovelVolumeContent.EnsureImplicitVolumeAsync(db, work, NovelVolumeKinds.Book, cancellationToken);
        await CreateDerivedDocumentStore().SaveAsync(analysis.Document, cancellationToken);
        await SyncPdfChaptersAsync(work, volume, analysis, cancellationToken);
        work.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return analysis.Document;
    }

    /// <summary>
    /// The chapter shown by every physical page of the PDF, for the Original view: pages that repeat an earlier page
    /// share its chapter. Falls back to the chapters in reading order (one per page, the layout of works imported
    /// before logical pages) when no derived document matches the work's chapters.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> GetPdfPageChapterIdsAsync(Guid workId, CancellationToken cancellationToken)
    {
        var chapters = await db.NovelChapters
            .AsNoTracking()
            .Where(x => x.WorkId == workId)
            .OrderBy(x => x.Number)
            .Select(x => new { x.Id, x.SourceUrl })
            .ToListAsync(cancellationToken);
        var readingOrder = chapters.Select(x => x.Id).ToArray();
        var document = await GetPdfAnalysisAsync(workId, cancellationToken);
        if (document is null)
        {
            return readingOrder;
        }

        var idByKey = chapters.GroupBy(x => x.SourceUrl, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Id, StringComparer.Ordinal);
        var logicalByPage = document.LogicalPageByPhysicalPage();
        var pageChapters = new Guid[document.PhysicalPageCount];
        for (var page = 0; page < pageChapters.Length; page++)
        {
            var key = PdfPageChapterKey(workId, document.LogicalPages[logicalByPage[page] - 1].FirstPhysicalPage);
            if (!idByKey.TryGetValue(key, out pageChapters[page]))
            {
                return readingOrder;
            }
        }

        return pageChapters;
    }

    /// <summary>
    /// Writes one chapter per logical page through the canonical volume sync. Chapters already imported for the work
    /// are matched by their page key; before the sync removes chapters of repeated pages their translations, progress,
    /// bookmarks and highlights move to the chapter that survives.
    /// </summary>
    private async Task SyncPdfChaptersAsync(NovelWork work, NovelVolume volume, PdfDocumentAnalysis analysis, CancellationToken cancellationToken)
    {
        var logicalPages = analysis.Document.LogicalPages;
        var inputs = logicalPages
            .Select((page, index) => new NovelVolumeChapterInput(
                PdfPageChapterKey(work.Id, page.FirstPhysicalPage),
                PageTitle(page.Number, analysis.LogicalPageTexts[index]),
                analysis.LogicalPageTexts[index]))
            .ToArray();

        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        if (db.Entry(volume).State != EntityState.Added)
        {
            await CarryOverPdfChapterDataAsync(work, volume, logicalPages, inputs, cancellationToken);
        }

        await NovelVolumeContent.SyncChaptersAsync(db, work, volume, inputs, cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private async Task CarryOverPdfChapterDataAsync(
        NovelWork work,
        NovelVolume volume,
        IReadOnlyList<PdfLogicalPage> logicalPages,
        IReadOnlyList<NovelVolumeChapterInput> inputs,
        CancellationToken cancellationToken)
    {
        var existing = await db.NovelChapters
            .AsNoTracking()
            .Where(x => x.VolumeId == volume.Id)
            .Select(x => new { x.Id, x.SourceUrl, x.SourceHash })
            .ToListAsync(cancellationToken);
        var chapterByKey = existing.GroupBy(x => x.SourceUrl, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var oldHash = existing.ToDictionary(x => x.Id, x => x.SourceHash);
        var newHash = new Dictionary<Guid, string>();
        var survivorOfDuplicate = new Dictionary<Guid, Guid>();
        for (var index = 0; index < logicalPages.Count; index++)
        {
            if (!chapterByKey.TryGetValue(inputs[index].SourceKey, out var survivor))
            {
                continue;
            }

            newHash[survivor.Id] = NovelVolumeContent.Hash(inputs[index].Text);
            foreach (var physicalPage in logicalPages[index].PhysicalPages.Skip(1))
            {
                if (chapterByKey.TryGetValue(PdfPageChapterKey(work.Id, physicalPage), out var duplicate))
                {
                    survivorOfDuplicate[duplicate.Id] = survivor.Id;
                }
            }
        }

        var chapterIds = newHash.Keys.Concat(survivorOfDuplicate.Keys).ToArray();
        var translations = await db.NovelTranslations.Where(x => chapterIds.Contains(x.ChapterId)).ToListAsync(cancellationToken);
        var occupied = translations.Select(TranslationSlot).ToHashSet();

        // The translated text of a chapter stays valid when only page furniture was stripped from its source,
        // so a translation made for the old text is re-pointed to the new hash instead of being regenerated.
        foreach (var translation in translations.Where(x => newHash.ContainsKey(x.ChapterId) && x.SourceHash == oldHash[x.ChapterId]))
        {
            Reassign(translation, translation.ChapterId, newHash[translation.ChapterId]);
        }

        foreach (var translation in translations.Where(x => survivorOfDuplicate.ContainsKey(x.ChapterId) && x.SourceHash == oldHash[x.ChapterId]))
        {
            var survivorId = survivorOfDuplicate[translation.ChapterId];
            Reassign(translation, survivorId, newHash[survivorId]);
        }

        var duplicateIds = survivorOfDuplicate.Keys.ToArray();
        foreach (var progress in await db.NovelProgress.Where(x => x.WorkId == work.Id && duplicateIds.Contains(x.ChapterId)).ToListAsync(cancellationToken))
        {
            progress.ChapterId = survivorOfDuplicate[progress.ChapterId];
        }

        foreach (var bookmark in await db.NovelBookmarks.Where(x => x.WorkId == work.Id && duplicateIds.Contains(x.ChapterId)).ToListAsync(cancellationToken))
        {
            bookmark.ChapterId = survivorOfDuplicate[bookmark.ChapterId];
        }

        foreach (var highlight in await db.NovelHighlights.Where(x => x.WorkId == work.Id && duplicateIds.Contains(x.ChapterId)).ToListAsync(cancellationToken))
        {
            highlight.ChapterId = survivorOfDuplicate[highlight.ChapterId];
        }

        await db.SaveChangesAsync(cancellationToken);

        void Reassign(NovelTranslation translation, Guid chapterId, string sourceHash)
        {
            var slot = (chapterId, translation.TargetLanguage, translation.ProviderId, translation.PromptVersion, sourceHash);
            // The target chapter already has this translation: keep it, the other one is dropped with its old chapter.
            if (!occupied.Add(slot))
            {
                return;
            }

            occupied.Remove(TranslationSlot(translation));
            translation.ChapterId = chapterId;
            translation.SourceHash = sourceHash;
        }
    }

    private static (Guid ChapterId, string Language, string ProviderId, int PromptVersion, string SourceHash) TranslationSlot(NovelTranslation translation) =>
        (translation.ChapterId, translation.TargetLanguage, translation.ProviderId, translation.PromptVersion, translation.SourceHash);
}
