using System.Globalization;
using Jularr.Web.Data;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Progress;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Tracking;

/// <summary>
/// Newest canonical local progress checkpoint of one work for one profile.
/// <see cref="CompletionMarker"/> identifies the furthest completed unit
/// (episode, chapter) using the same completion rules as the canonical
/// progress resolver; it changes only when a unit is finished.
/// </summary>
public sealed record AniListSyncCheckpoint(
    string MediaKind,
    Guid LocalId,
    string Title,
    DateTime UpdatedAt,
    string? CompletionMarker);

/// <summary>
/// Read-only view of the canonical local progress tables (MediaProgress,
/// MangaProgress, NovelProgress) for automatic AniList sync. It never writes
/// progress and never decides AniList values; those always come from
/// <see cref="AniListAccountService"/>.
/// </summary>
public static class AniListSyncCheckpoints
{
    public const string Anime = "anime";
    public const string Manga = "manga";
    public const string Novel = "novel";

    // Mirrors the completedThreshold the canonical novel resolver uses in
    // AniListAccountService (a chapter counts as read from 95% on).
    private const int NovelChapterCompletedPermille = 950;

    /// <summary>Checkpoints of the profile changed after <paramref name="sinceUtc"/>.</summary>
    public static async Task<IReadOnlyList<AniListSyncCheckpoint>> LoadChangedAsync(
        AppDbContext db,
        string profileId,
        DateTime sinceUtc,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? enabledMediaKinds = null)
    {
        var checkpoints = new List<AniListSyncCheckpoint>();
        if (enabledMediaKinds is null || enabledMediaKinds.Contains(Anime))
        {
            checkpoints.AddRange(await LoadAnimeAsync(db, profileId, sinceUtc, cancellationToken));
        }

        if (enabledMediaKinds is null || enabledMediaKinds.Contains(Manga))
        {
            checkpoints.AddRange(await LoadMangaAsync(db, profileId, sinceUtc, cancellationToken));
        }

        if (enabledMediaKinds is null || enabledMediaKinds.Contains(Novel))
        {
            checkpoints.AddRange(await LoadNovelsAsync(db, profileId, sinceUtc, cancellationToken));
        }

        return checkpoints;
    }

    private static async Task<IEnumerable<AniListSyncCheckpoint>> LoadAnimeAsync(
        AppDbContext db,
        string profileId,
        DateTime sinceUtc,
        CancellationToken cancellationToken)
    {
        // The completion marker is the canonical contiguous CompletedThrough episode (specials excluded), so an
        // in-progress or non-contiguous episode never changes it.
        var completions = await new VideoProgressService(db).GetCompletedThroughAsync(profileId, cancellationToken: cancellationToken);
        if (completions.Count == 0)
        {
            return [];
        }

        var workIds = completions.Select(x => x.WorkId).ToArray();
        var animeByWork = await (
            from link in db.WorkSourceLinks.AsNoTracking()
            join anime in db.Anime.AsNoTracking()
                on link.SourceId equals anime.Id
            where link.SourceKind == WorkSourceKind.Anime &&
                  workIds.Contains(link.WorkId)
            select new
            {
                link.WorkId,
                AnimeId = anime.Id,
                anime.Title
            })
            .ToListAsync(cancellationToken);

        return completions
            .Join(
                animeByWork,
                completion => completion.WorkId,
                anime => anime.WorkId,
                (completion, anime) => new AniListSyncCheckpoint(
                    Anime,
                    anime.AnimeId,
                    anime.Title,
                    AsUtc(completion.UpdatedAt),
                    completion.CompletedThrough is { } through
                        ? $"S{through.SeasonNumber}E{through.EpisodeNumber}"
                        : null))
            .Where(x => x.UpdatedAt > sinceUtc);
    }

    private static async Task<IEnumerable<AniListSyncCheckpoint>> LoadMangaAsync(
        AppDbContext db,
        string profileId,
        DateTime sinceUtc,
        CancellationToken cancellationToken)
    {
        // Manga progress is owned by MangaRepository (raw SQL tables). One row
        // per profile and series holds the current reader position.
        var rows = await db.Database
            .SqlQuery<MangaCheckpointRow>(
                $"""
                SELECT
                    p."SeriesId" AS "SeriesId",
                    COALESCE(s."MetadataTitle", s."Title") AS "Title",
                    c."Number" AS "ChapterNumber",
                    p."PageIndex" AS "PageIndex",
                    c."PageCount" AS "PageCount",
                    p."UpdatedAt" AS "UpdatedAt"
                FROM "MangaProgress" p
                JOIN "MangaSeries" s ON s."Id" = p."SeriesId"
                JOIN "MangaChapters" c ON c."Id" = p."ChapterId" AND c."SeriesId" = p."SeriesId"
                WHERE p."ProfileId" = {profileId}
                """)
            .ToListAsync(cancellationToken);

        return rows
            .Select(row =>
            {
                var completedChapter = row.PageIndex >= Math.Max(0, row.PageCount - 1)
                    ? row.ChapterNumber
                    : row.ChapterNumber - 1;
                return new AniListSyncCheckpoint(
                    Manga,
                    Guid.Parse(row.SeriesId),
                    row.Title,
                    DateTime.Parse(
                        row.UpdatedAt,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                    completedChapter > 0
                        ? completedChapter.ToString("0.###", CultureInfo.InvariantCulture)
                        : null);
            })
            .Where(x => x.UpdatedAt > sinceUtc);
    }

    private static async Task<IEnumerable<AniListSyncCheckpoint>> LoadNovelsAsync(
        AppDbContext db,
        string profileId,
        DateTime sinceUtc,
        CancellationToken cancellationToken)
    {
        var rows = await (
            from progress in db.NovelProgress.AsNoTracking()
            join chapter in db.NovelChapters.AsNoTracking()
                on progress.ChapterId equals chapter.Id
            join work in db.NovelWorks.AsNoTracking()
                on progress.WorkId equals work.Id
            where progress.ProfileId == profileId &&
                  chapter.WorkId == progress.WorkId &&
                  progress.UpdatedAt > sinceUtc
            select new
            {
                progress.WorkId,
                Title = work.MetadataTitle ?? work.Title,
                chapter.Number,
                progress.PositionPermille,
                progress.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        return rows.Select(row =>
        {
            var completedChapter = row.PositionPermille >= NovelChapterCompletedPermille
                ? row.Number
                : row.Number - 1;
            return new AniListSyncCheckpoint(
                Novel,
                row.WorkId,
                row.Title,
                AsUtc(row.UpdatedAt),
                completedChapter > 0
                    ? completedChapter.ToString(CultureInfo.InvariantCulture)
                    : null);
        });
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private sealed class MangaCheckpointRow
    {
        public string SeriesId { get; set; } = "";
        public string Title { get; set; } = "";
        public double ChapterNumber { get; set; }
        public int PageIndex { get; set; }
        public int PageCount { get; set; }
        public string UpdatedAt { get; set; } = "";
    }
}
