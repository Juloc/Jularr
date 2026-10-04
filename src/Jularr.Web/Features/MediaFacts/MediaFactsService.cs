using Jularr.Web.Data;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.Learning.Courses;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Playback;
using Jularr.Web.Ui;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.MediaFacts;

/// <summary>
/// Builds the canonical <see cref="MediaFacts"/> projection for one work, for every media type
/// (#426). Every method computes strictly from data Jularr already has: the media inventory
/// (ffprobe-analyzed audio/subtitle streams), imported subtitle tracks, chapter/translation rows
/// and catalog edition metadata. Nothing here ever probes a file or calls a provider; it is safe
/// to call on every detail-page render.
/// </summary>
public sealed class MediaFactsService(AppDbContext db)
{
    public async Task<MediaFacts> GetAnimeFactsAsync(Guid animeId, CancellationToken cancellationToken)
    {
        var metadata = await db.AnimeMetadata
            .AsNoTracking()
            .Where(x => x.AnimeId == animeId)
            .Select(x => new { x.Status, x.SeasonYear, x.EpisodeCount, x.EpisodeDurationMinutes })
            .SingleOrDefaultAsync(cancellationToken);

        var localYear = metadata is null
            ? await db.AnimeLocalMetadata
                .AsNoTracking()
                .Where(x => x.AnimeId == animeId)
                .Select(x => (int?)x.Year)
                .SingleOrDefaultAsync(cancellationToken)
            : null;

        var episodes = await db.Episodes
            .AsNoTracking()
            .Where(x => x.AnimeId == animeId)
            .Select(x => new { x.Id, x.SeasonNumber })
            .ToListAsync(cancellationToken);

        var episodeCount = episodes.Count;
        var seasonCount = episodes
            .Select(x => x.SeasonNumber)
            .Where(x => x > 0)
            .Distinct()
            .Count();

        var languages = episodeCount == 0
            ? []
            : await BuildAnimeLanguageRowsAsync(
                [.. episodes.Select(x => x.Id)],
                episodeCount,
                cancellationToken);

        return new MediaFacts(
            MediaBannerKind.Anime,
            MediaBannerCardModel.MapStatus(metadata?.Status),
            metadata?.EpisodeCount ?? (episodeCount > 0 ? episodeCount : null),
            seasonCount > 0 ? seasonCount : null,
            metadata?.EpisodeDurationMinutes,
            metadata?.SeasonYear ?? localYear,
            languages);
    }

    // Embedded streams (canonical ffprobe-analyzed inventory) plus sidecar subtitle tracks,
    // exactly the two sources Library's Media Banner cards already union (LibraryMediaCardQuery)
    // -- but grouped per episode here so completeness (X of Y episodes) can be reported instead of
    // a plain presence list.
    private async Task<IReadOnlyList<MediaFactsLanguageRow>> BuildAnimeLanguageRowsAsync(
        IReadOnlyList<Guid> episodeIds,
        int episodeCount,
        CancellationToken cancellationToken)
    {
        var nullableEpisodeIds = episodeIds.Select(id => (Guid?)id).ToArray();
        var embeddedRows = await (
            from stream in db.MediaAnalysisStreams.AsNoTracking()
            join media in db.MediaFiles.AsNoTracking() on stream.MediaFileId equals media.Id
            where nullableEpisodeIds.Contains(media.EpisodeId) &&
                  stream.Language != null
            select new { media.EpisodeId, stream.Kind, stream.Language })
            .ToListAsync(cancellationToken);
        var embedded = embeddedRows
            .Where(x => x.EpisodeId.HasValue)
            .Select(x => new { EpisodeId = x.EpisodeId.Value, x.Kind, x.Language })
            .ToArray();

        var sidecar = await db.SubtitleTracks
            .AsNoTracking()
            .Where(x => episodeIds.Contains(x.EpisodeId))
            .Select(x => new { x.EpisodeId, Kind = MediaStreamKind.Subtitle, Language = (string?)x.Language })
            .ToListAsync(cancellationToken);

        return
        [
            .. embedded
                .Select(x => new { x.EpisodeId, x.Kind, Code = PlaybackLanguages.Normalize(x.Language) })
                .Concat(sidecar.Select(x => new { x.EpisodeId, x.Kind, Code = PlaybackLanguages.Normalize(x.Language) }))
                .Where(x => x.Code is not null && x.Code != PlaybackLanguages.SubtitlesOff)
                .GroupBy(x => (x.Kind, x.Code))
                .Select(group => new MediaFactsLanguageRow(
                    group.Key.Code!,
                    group.Key.Kind == MediaStreamKind.Audio
                        ? MediaFactsLanguageUsage.Audio
                        : MediaFactsLanguageUsage.Subtitle,
                    group.Select(x => x.EpisodeId).Distinct().Count(),
                    episodeCount))
                .OrderByDescending(x => x.AvailableUnits)
                .ThenBy(x => x.Language, StringComparer.Ordinal)
        ];
    }

    public async Task<MediaFacts> GetMangaFactsAsync(Guid seriesId, CancellationToken cancellationToken)
    {
        var series = await db.Database
            .SqlQueryRaw<MangaSeriesFactsRow>(
                """
                SELECT "MetadataStatus" AS "Status"
                FROM "MangaSeries"
                WHERE "Id" = {0}
                LIMIT 1
                """,
                seriesId.ToString())
            .SingleOrDefaultAsync(cancellationToken);

        if (series is null)
        {
            return MediaFacts.Empty(MediaBannerKind.Manga);
        }

        var chapters = await db.Database
            .SqlQueryRaw<MangaChapterFactsRow>(
                """
                SELECT "VolumeNumber", "PageCount"
                FROM "MangaChapters"
                WHERE "SeriesId" = {0}
                """,
                seriesId.ToString())
            .ToListAsync(cancellationToken);

        var chapterCount = chapters.Count;
        var volumeCount = chapters
            .Select(x => x.VolumeNumber)
            .Where(x => x is not null)
            .Distinct()
            .Count();

        IReadOnlyList<MediaFactsLanguageRow> languages = [];
        if (chapterCount > 0)
        {
            // Manga has no per-chapter language column: every imported chapter is the scanned
            // original, in whichever language this household's Learning setup targets (defaulting
            // to Japanese) -- the same canonical answer subtitle acquisition already uses, reused
            // here instead of assuming "ja" ourselves (#426).
            var language = await new LearningContentLanguageResolver(db)
                .ResolveTargetLanguageAsync(cancellationToken);
            languages =
            [
                new MediaFactsLanguageRow(
                    language,
                    MediaFactsLanguageUsage.Text,
                    chapters.Count(x => x.PageCount > 0),
                    chapterCount)
            ];
        }

        return new MediaFacts(
            MediaBannerKind.Manga,
            MediaBannerCardModel.MapStatus(series.Status),
            chapterCount > 0 ? chapterCount : null,
            volumeCount > 0 ? volumeCount : null,
            null,
            null,
            languages);
    }

    public async Task<MediaFacts> GetNovelFactsAsync(Guid workId, CancellationToken cancellationToken)
    {
        var work = await db.NovelWorks
            .AsNoTracking()
            .Where(x => x.Id == workId)
            .Select(x => new
            {
                x.Format,
                x.MetadataStatus,
                x.MetadataChapterCount,
                x.MetadataVolumeCount
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (work is null)
        {
            return MediaFacts.Empty(MediaBannerKind.LightNovel);
        }

        var chapters = await db.NovelChapters
            .AsNoTracking()
            .Where(x => x.WorkId == workId)
            .Select(x => new { x.Id, HasContent = x.OriginalText.Length > 0 })
            .ToListAsync(cancellationToken);

        var chapterCount = chapters.Count;
        var volumeCount = await db.NovelVolumes
            .AsNoTracking()
            .CountAsync(x => x.WorkId == workId, cancellationToken);

        var languages = new List<MediaFactsLanguageRow>();
        if (chapterCount > 0)
        {
            // Books ("EPUB:de"/"PDF:en") carry their real source language in NovelWork.Format;
            // every other novel source (web novels, EPUB light novels) has none, so it falls back
            // to the same canonical content-language answer Manga uses, instead of assuming "ja".
            var sourceLanguage = BookFileFormats.Language(work.Format)
                ?? await new LearningContentLanguageResolver(db).ResolveTargetLanguageAsync(cancellationToken);
            languages.Add(new MediaFactsLanguageRow(
                sourceLanguage,
                MediaFactsLanguageUsage.Text,
                chapters.Count(x => x.HasContent),
                chapterCount));

            var chapterIds = chapters.Select(x => x.Id).ToArray();
            var translationRows = await db.NovelTranslations
                .AsNoTracking()
                .Where(x => chapterIds.Contains(x.ChapterId))
                .Select(x => new { x.ChapterId, x.TargetLanguage })
                .Distinct()
                .ToListAsync(cancellationToken);

            foreach (var group in translationRows.GroupBy(x => x.TargetLanguage))
            {
                var normalized = PlaybackLanguages.Normalize(group.Key) ?? group.Key.Trim().ToLowerInvariant();
                if (string.Equals(normalized, sourceLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                languages.Add(new MediaFactsLanguageRow(
                    normalized,
                    MediaFactsLanguageUsage.Text,
                    group.Select(x => x.ChapterId).Distinct().Count(),
                    chapterCount));
            }
        }

        return new MediaFacts(
            MediaBannerKind.LightNovel,
            MediaBannerCardModel.MapStatus(work.MetadataStatus),
            work.MetadataChapterCount ?? (chapterCount > 0 ? chapterCount : null),
            work.MetadataVolumeCount ?? (volumeCount > 0 ? volumeCount : null),
            null,
            null,
            languages);
    }

    /// <summary>
    /// Books/Details shows a catalog work that may not be acquired yet, so its facts come from
    /// what the catalog record itself already carries (#405's merged editions) rather than a
    /// database lookup: no status, no episode/chapter counts, just the release year and the known
    /// editions' languages.
    /// </summary>
    public static MediaFacts CreateBookCatalogFacts(BookCatalogItem book)
    {
        var editions = book.Editions;
        IReadOnlyList<MediaFactsLanguageRow> languages;

        if (editions.Count > 0)
        {
            languages =
            [
                .. editions
                    .Where(x => !string.IsNullOrWhiteSpace(x.Language))
                    .Select(x => PlaybackLanguages.Normalize(x.Language) ?? x.Language!.Trim().ToLowerInvariant())
                    .GroupBy(x => x, StringComparer.Ordinal)
                    .Select(group => new MediaFactsLanguageRow(
                        group.Key,
                        MediaFactsLanguageUsage.Text,
                        group.Count(),
                        editions.Count))
                    .OrderByDescending(x => x.AvailableUnits)
                    .ThenBy(x => x.Language, StringComparer.Ordinal)
            ];
        }
        else if (!string.IsNullOrWhiteSpace(book.Language))
        {
            // A raw, unranked provider result (BookWorkSearch never merged it into editions): the
            // single edition this record itself describes is all that is known.
            var code = PlaybackLanguages.Normalize(book.Language) ?? book.Language.Trim().ToLowerInvariant();
            languages = [new MediaFactsLanguageRow(code, MediaFactsLanguageUsage.Text, 1, 1)];
        }
        else
        {
            languages = [];
        }

        return new MediaFacts(
            MediaBannerKind.Book,
            null,
            null,
            null,
            null,
            book.FirstPublishYear,
            languages);
    }

    private sealed record MangaSeriesFactsRow(string? Status);

    private sealed record MangaChapterFactsRow(int? VolumeNumber, int PageCount);
}
