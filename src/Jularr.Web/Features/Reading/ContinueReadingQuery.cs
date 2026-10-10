using System.Globalization;
using Jularr.Web.Data;
using Jularr.Web.Features.Books;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Reading;

public enum ContinueReadingKind
{
    Novel,
    Book,
    Manga
}

/// <summary>
/// One resumable reading item. <see cref="ResumeUrl"/> opens the medium's
/// reader on the saved chapter; the reader restores the saved position from
/// the same canonical progress row.
/// </summary>
public sealed record ContinueReadingItem(
    ContinueReadingKind Kind,
    Guid WorkId,
    string Title,
    string? CoverImageUrl,
    Guid ChapterId,
    double ChapterNumber,
    int ProgressPercent,
    int? PageNumber,
    int? PageCount,
    DateTime LastReadAt,
    string ResumeUrl)
{
    public string ChapterLabel =>
        ChapterNumber.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>
/// Profile-scoped "Continue reading" read model across Novels, Books and
/// Manga. It reads the canonical progress tables directly with bounded
/// queries (one per progress store, each limited to <c>limit</c> rows) and
/// merges them newest first.
/// <para>
/// Mapping to the owning features:
/// <list type="bullet">
/// <item>Novels and Books share <c>NovelProgress</c> (one row per profile and
/// work, written by <c>NovelProgressService</c> and
/// <c>BookCatalogService.SaveProgressAsync</c>). A work is a Book when its
/// <c>SourceProvider</c> is <see cref="BookCatalogService.ImportedBookProvider"/>,
/// otherwise a Novel. Resume: <c>/Novels/Read/{chapterId}</c> or
/// <c>/Books/Read/{chapterId}?lang={anchorLanguage}</c>; both readers restore
/// the saved position when the progress row points at that chapter.</item>
/// <item>Manga uses <c>MangaProgress</c> (written by
/// <c>MangaRepository.SaveProgressAsync</c>, zero-based page index). Resume:
/// <c>/Manga/Read/{chapterId}?page={pageIndex}</c>.</item>
/// </list>
/// </para>
/// <para>
/// Finished items are excluded. A work is finished when the saved chapter is
/// its last chapter by number and that chapter is complete: for Novels and
/// Books <c>PositionPermille &gt;= 950</c>
/// (<see cref="BookRecommendationProgress.FinishedPositionPermille"/>, the same
/// chapter-complete threshold the novel AniList sync uses); for Manga the
/// saved page is the chapter's last page (the manga AniList sync threshold).
/// A finished work reappears automatically when a later chapter is imported.
/// </para>
/// <para>
/// Ordering: last read time descending, then work id ascending (canonical
/// GUID text order) as deterministic tie-break.
/// </para>
/// </summary>
public sealed class ContinueReadingQuery(AppDbContext db)
{
    public const int DefaultLimit = 12;
    public const int MaxLimit = 50;
    public const int FinishedPositionPermille =
        BookRecommendationProgress.FinishedPositionPermille;

    public async Task<IReadOnlyList<ContinueReadingItem>> GetAsync(
        string profileId,
        int limit = DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);

        var textual = await LoadNovelProgressAsync(profileId, limit, cancellationToken);
        var manga = await LoadMangaProgressAsync(profileId, limit, cancellationToken);

        return textual
            .Concat(manga)
            .OrderByDescending(x => x.LastReadAt)
            .ThenBy(x => x.WorkId.ToString("D"), StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();
    }

    private async Task<IEnumerable<ContinueReadingItem>> LoadNovelProgressAsync(
        string profileId,
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await (
            from progress in db.NovelProgress.AsNoTracking()
            join work in db.NovelWorks.AsNoTracking()
                on progress.WorkId equals work.Id
            join chapter in db.NovelChapters.AsNoTracking()
                on progress.ChapterId equals chapter.Id
            where progress.ProfileId == profileId
                && !(progress.PositionPermille >= FinishedPositionPermille
                    && !db.NovelChapters.Any(next =>
                        next.WorkId == progress.WorkId
                        && next.Number > chapter.Number))
            orderby progress.UpdatedAt descending, progress.WorkId
            select new
            {
                progress.WorkId,
                Title = work.MetadataTitle ?? work.Title,
                work.CoverImageUrl,
                work.SourceProvider,
                progress.ChapterId,
                chapter.Number,
                progress.PositionPermille,
                progress.AnchorLanguage,
                progress.UpdatedAt
            })
            .Take(limit)
            .ToListAsync(cancellationToken);

        return rows.Select(row =>
        {
            var isBook = row.SourceProvider == BookCatalogService.ImportedBookProvider;
            return new ContinueReadingItem(
                isBook ? ContinueReadingKind.Book : ContinueReadingKind.Novel,
                row.WorkId,
                row.Title,
                row.CoverImageUrl,
                row.ChapterId,
                row.Number,
                Math.Clamp(row.PositionPermille / 10, 0, 100),
                null,
                null,
                AsUtc(row.UpdatedAt),
                isBook
                    ? $"/Books/Read/{row.ChapterId}?lang={Uri.EscapeDataString(BookLanguageCatalog.Normalize(row.AnchorLanguage))}"
                    : $"/Novels/Read/{row.ChapterId}");
        });
    }

    private async Task<IEnumerable<ContinueReadingItem>> LoadMangaProgressAsync(
        string profileId,
        int limit,
        CancellationToken cancellationToken)
    {
        // Manga tables are owned by MangaRepository through raw SQL (no EF
        // entities). "UpdatedAt" is round-trip ("O") UTC text, so text order
        // equals time order.
        var rows = await db.Database
            .SqlQuery<MangaProgressRow>($"""
                SELECT
                    p."SeriesId" AS "SeriesId",
                    COALESCE(s."MetadataTitle", s."Title") AS "Title",
                    s."CoverImageUrl" AS "CoverImageUrl",
                    (SELECT c0."Id" FROM "MangaChapters" c0
                        WHERE c0."SeriesId" = s."Id" AND c0."SupersededById" IS NULL
                        ORDER BY c0."Number" LIMIT 1) AS "PreviewChapterId",
                    p."ChapterId" AS "ChapterId",
                    c."Number" AS "Number",
                    p."PageIndex" AS "PageIndex",
                    c."PageCount" AS "PageCount",
                    p."UpdatedAt" AS "UpdatedAt"
                FROM "MangaProgress" p
                JOIN "MangaSeries" s ON s."Id" = p."SeriesId"
                JOIN "MangaChapters" c ON c."Id" = p."ChapterId"
                WHERE p."ProfileId" = {profileId}
                  AND NOT (
                      p."PageIndex" >= c."PageCount" - 1
                      AND NOT EXISTS (
                          SELECT 1 FROM "MangaChapters" later
                          WHERE later."SeriesId" = p."SeriesId" AND later."SupersededById" IS NULL
                            AND later."Number" > c."Number"))
                ORDER BY p."UpdatedAt" DESC, lower(p."SeriesId")
                LIMIT {limit}
                """)
            .ToListAsync(cancellationToken);

        return rows.Select(row =>
        {
            var chapterId = Guid.Parse(row.ChapterId);
            var cover = !string.IsNullOrWhiteSpace(row.CoverImageUrl)
                ? row.CoverImageUrl
                : row.PreviewChapterId is null
                    ? null
                    : $"/Manga/Read/{Guid.Parse(row.PreviewChapterId)}?handler=Page&page=0";
            var percent = row.PageCount <= 0
                ? 0
                : Math.Clamp(
                    (int)Math.Round((row.PageIndex + 1) * 100d / row.PageCount),
                    0,
                    100);

            return new ContinueReadingItem(
                ContinueReadingKind.Manga,
                Guid.Parse(row.SeriesId),
                row.Title,
                cover,
                chapterId,
                row.Number,
                percent,
                row.PageIndex + 1,
                row.PageCount,
                AsUtc(DateTime.Parse(
                    row.UpdatedAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind)),
                $"/Manga/Read/{chapterId}?page={row.PageIndex}");
        });
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    private sealed class MangaProgressRow
    {
        public string SeriesId { get; set; } = "";
        public string Title { get; set; } = "";
        public string? CoverImageUrl { get; set; }
        public string? PreviewChapterId { get; set; }
        public string ChapterId { get; set; } = "";
        public double Number { get; set; }
        public int PageIndex { get; set; }
        public int PageCount { get; set; }
        public string UpdatedAt { get; set; } = "";
    }
}
