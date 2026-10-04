using Jularr.Web.Features.ReaderCore;

namespace Jularr.Web.Features.Manga;

/// <summary>
/// Adapts Manga/comic chapter pages to the canonical image-sequence Reader
/// document. Canonical MediaCore Work/Volume/Chapter identity is supplied by
/// the caller; Manga legacy ids remain source identifiers only.
/// </summary>
public static class MangaReaderDocumentAdapter
{
    public const string OriginalVariantKey = "original";

    public static ReaderDocument Create(
        MangaChapterRead chapter,
        ReaderDocumentIdentity identity,
        IReadOnlyList<MangaPageItem>? pages = null)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        ArgumentNullException.ThrowIfNull(identity);

        var pageDirection = ParsePageDirection(chapter.Direction);
        var descriptor = ReaderDocumentDescriptor.Create(
            identity.WorkId,
            ReaderContentType.Manga,
            chapter.SeriesTitle,
            layoutKind: ReaderLayoutKind.ImageSequence,
            pageDirection: pageDirection);

        var variant = new ReaderDocumentVariant(
            OriginalVariantKey,
            "und",
            ReaderVariantKind.Original,
            supportsSourceMapping: true);
        var documentIdentity = identity.WithVariant(variant.Key);
        var pageItems = ResolvePages(chapter, pages);
        var content = pageItems
            .Select(page => (ReaderContentNode)new ReaderImageContentNode(
                $"page:{page.PageIndex}",
                $"manga:{chapter.Id:N}:{page.PageIndex}",
                source: new ReaderSourcePageReference(
                    page.PageIndex,
                    sourceItemId: $"legacy-manga-page:{chapter.Id:N}:{page.PageIndex}",
                    sourceDocument: documentIdentity)))
            .ToArray();

        return ReaderDocument.Create(
            descriptor,
            documentIdentity,
            content,
            sourceLanguage: "und",
            variant,
            [variant]);
    }

    public static ReaderPageDirection ParsePageDirection(string? direction) =>
        direction?.Trim().ToLowerInvariant() switch
        {
            "rtl" or "right-to-left" or "right_to_left" =>
                ReaderPageDirection.RightToLeft,
            "ltr" or "left-to-right" or "left_to_right" =>
                ReaderPageDirection.LeftToRight,
            _ => ReaderPageDirection.Auto
        };

    private static IReadOnlyList<MangaPageItem> ResolvePages(
        MangaChapterRead chapter,
        IReadOnlyList<MangaPageItem>? pages)
    {
        if (pages is null || pages.Count == 0)
        {
            return Enumerable.Range(0, chapter.PageCount)
                .Select(index => new MangaPageItem(
                    chapter.Id,
                    index,
                    "",
                    "application/octet-stream"))
                .ToArray();
        }

        var ordered = pages.OrderBy(page => page.PageIndex).ToArray();
        if (ordered.Any(page => page.ChapterId != chapter.Id)
            || ordered.Select(page => page.PageIndex).Distinct().Count() != ordered.Length
            || ordered.Any(page => page.PageIndex < 0 || page.PageIndex >= chapter.PageCount))
        {
            throw new ArgumentException(
                "Manga pages must belong to the chapter and have unique in-range page indexes.",
                nameof(pages));
        }

        return ordered;
    }
}
