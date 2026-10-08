using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.Learning.Courses;
using Jularr.Web.Features.Mapping;
using Jularr.Web.Features.Playback;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Search;

/// <summary>
/// Loads the Media Facts that the search filters and result rows use, for a bounded set of candidate
/// records, with a fixed number of set-based queries per source type (never one query per title).
/// <para>
/// The definitions are the ones <c>MediaFactsService</c> (#426) uses for the same fact, batched: a
/// language is any normalised audio or subtitle track of an anime (embedded streams plus sidecar
/// subtitle tracks); a manga's text language is the household's Learning content language once it has
/// chapters; a novel or book has its source language (the <c>EPUB:de</c> format) or that content
/// language, plus every language a chapter was translated into. Local content means something
/// playable or readable exists on this server. Monitored and wanted come from the acquisition state
/// (the Wanted queue for anime, open requests for every type). Nothing is inferred: a fact a
/// source cannot know stays empty.
/// </para>
/// </summary>
internal sealed class MediaSearchFactsLoader(
    AppDbContext db,
    AnimeMonitoring animeMonitoring,
    AcquisitionAccessStore requests)
{
    private readonly record struct RequestKey(MediaAcquisitionKind Kind, string Provider, string ExternalId);

    public async Task<IReadOnlyDictionary<(MediaSearchType Type, Guid Id), MediaSearchFacts>> LoadAsync(
        IReadOnlyCollection<MediaSearchVariant> variants,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<(MediaSearchType, Guid), MediaSearchFacts>();
        if (variants.Count == 0)
        {
            return result;
        }

        var open = await OpenRequestsAsync(cancellationToken);
        foreach (var byType in variants.GroupBy(variant => variant.Type))
        {
            var ids = byType.Select(variant => variant.Id).Distinct().ToArray();
            var facts = byType.Key switch
            {
                MediaSearchType.Anime => await AnimeAsync(ids, open, cancellationToken),
                MediaSearchType.Manga => await MangaAsync(ids, open, cancellationToken),
                MediaSearchType.Novel or MediaSearchType.Book => await NovelsAsync(ids, byType.Key, open, cancellationToken),
                MediaSearchType.Movie => await MoviesAsync(ids, open, cancellationToken),
                MediaSearchType.Series => await SeriesAsync(ids, open, cancellationToken),
                MediaSearchType.Audiobook => await AudiobooksAsync(ids, open, cancellationToken),
                _ => new Dictionary<Guid, MediaSearchFacts>()
            };

            foreach (var (id, fact) in facts)
            {
                result[(byType.Key, id)] = fact;
            }
        }

        return result;
    }

    private async Task<Dictionary<Guid, MediaSearchFacts>> AnimeAsync(
        Guid[] ids,
        HashSet<RequestKey> open,
        CancellationToken cancellationToken)
    {
        var rows = await (
            from anime in db.Anime.AsNoTracking()
            where ids.Contains(anime.Id)
            join metadataValue in db.AnimeMetadata.AsNoTracking()
                on anime.Id equals metadataValue.AnimeId into metadataRows
            from metadata in metadataRows.DefaultIfEmpty()
            join localValue in db.AnimeLocalMetadata.AsNoTracking()
                on anime.Id equals localValue.AnimeId into localRows
            from local in localRows.DefaultIfEmpty()
            select new
            {
                anime.Id,
                anime.Key,
                Year = metadata != null && metadata.SeasonYear != null
                    ? metadata.SeasonYear
                    : local == null ? null : local.Year,
                Provider = metadata == null ? null : metadata.Provider,
                ExternalId = metadata == null ? null : metadata.ExternalId
            })
            .ToListAsync(cancellationToken);

        var withFiles = (await db.Episodes.AsNoTracking()
                .Where(episode => ids.Contains(episode.AnimeId) && db.MediaFiles.Any(file => file.EpisodeId == episode.Id))
                .Select(episode => episode.AnimeId)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var embedded = await (
            from stream in db.MediaAnalysisStreams.AsNoTracking()
            join file in db.MediaFiles.AsNoTracking() on stream.MediaFileId equals file.Id
            join episode in db.Episodes.AsNoTracking() on file.EpisodeId equals episode.Id
            where ids.Contains(episode.AnimeId) && stream.Language != null
            select new { episode.AnimeId, stream.Language })
            .Distinct()
            .ToListAsync(cancellationToken);

        var sidecar = await (
            from track in db.SubtitleTracks.AsNoTracking()
            join episode in db.Episodes.AsNoTracking() on track.EpisodeId equals episode.Id
            where ids.Contains(episode.AnimeId)
            select new { episode.AnimeId, track.Language })
            .Distinct()
            .ToListAsync(cancellationToken);

        var languages = embedded.Concat(sidecar)
            .Select(x => (x.AnimeId, Code: NormalizeLanguage(x.Language)))
            .Where(x => x.Code is not null)
            .ToLookup(x => x.AnimeId, x => x.Code!);

        var views = await animeMonitoring.LoadAsync([.. rows.Select(row => row.Key)], cancellationToken);
        var wantedKeys = (await AnimeCanonicalEpisodes.WantedAsync(db, null, cancellationToken))
            .Select(wanted => wanted.Key.AnimeKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return rows.ToDictionary(
            row => row.Id,
            row => new MediaSearchFacts(
                row.Year,
                DistinctSorted(languages[row.Id]),
                [],
                withFiles.Contains(row.Id),
                views[row.Key].IsWorkMonitored,
                wantedKeys.Contains(row.Key)
                || IsRequested(open, MediaAcquisitionKind.Anime, (row.Provider, row.ExternalId))));
    }

    private async Task<Dictionary<Guid, MediaSearchFacts>> MangaAsync(
        Guid[] ids,
        HashSet<RequestKey> open,
        CancellationToken cancellationToken)
    {
        var keys = ids.Select(id => id.ToString("D")).ToArray();
        var series = await db.Database
            .SqlQueryRaw<MangaSeriesRow>(
                """
                SELECT "Id", "MetadataProvider", "MetadataExternalId"
                FROM "MangaSeries"
                WHERE "Id" = ANY({0})
                """,
                (object)keys)
            .ToListAsync(cancellationToken);

        var chapters = (await db.Database
                .SqlQueryRaw<MangaChapterRow>(
                    """
                    SELECT "SeriesId", bool_or("PageCount" > 0) AS "HasPages"
                    FROM "MangaChapters"
                    WHERE "SeriesId" = ANY({0})
                    GROUP BY "SeriesId"
                    """,
                    (object)keys)
                .ToListAsync(cancellationToken))
            .ToDictionary(row => row.SeriesId, row => row.HasPages, StringComparer.OrdinalIgnoreCase);

        var contentLanguage = chapters.Count == 0
            ? null
            : NormalizeLanguage(await new LearningContentLanguageResolver(db).ResolveTargetLanguageAsync(cancellationToken));

        var result = new Dictionary<Guid, MediaSearchFacts>();
        foreach (var row in series)
        {
            if (!Guid.TryParse(row.Id, out var id))
            {
                continue;
            }

            var hasChapters = chapters.TryGetValue(row.Id, out var hasPages);
            result[id] = new MediaSearchFacts(
                null,
                hasChapters && contentLanguage is not null ? [contentLanguage] : [],
                [],
                hasChapters && hasPages,
                false,
                IsRequested(open, MediaAcquisitionKind.Manga, (row.MetadataProvider, row.MetadataExternalId)));
        }

        return result;
    }

    private async Task<Dictionary<Guid, MediaSearchFacts>> NovelsAsync(
        Guid[] ids,
        MediaSearchType type,
        HashSet<RequestKey> open,
        CancellationToken cancellationToken)
    {
        var works = await db.NovelWorks.AsNoTracking()
            .Where(work => ids.Contains(work.Id))
            .Select(work => new
            {
                work.Id,
                work.Format,
                work.MetadataGenresJson,
                work.MetadataProvider,
                work.MetadataExternalId
            })
            .ToListAsync(cancellationToken);

        // EXISTS stops at the first chapter, and "<> ''" compares stored sizes where length() would
        // read every chapter text.
        var chapters = (await db.NovelWorks.AsNoTracking()
                .Where(work => ids.Contains(work.Id))
                .Select(work => new
                {
                    work.Id,
                    HasChapters = db.NovelChapters.Any(chapter => chapter.WorkId == work.Id),
                    HasContent = db.NovelChapters.Any(chapter => chapter.WorkId == work.Id && chapter.OriginalText != "")
                })
                .ToListAsync(cancellationToken))
            .Where(x => x.HasChapters)
            .ToDictionary(x => x.Id, x => x.HasContent);

        var translated = (await (
                from translation in db.NovelTranslations.AsNoTracking()
                join chapter in db.NovelChapters.AsNoTracking() on translation.ChapterId equals chapter.Id
                where ids.Contains(chapter.WorkId)
                select new { chapter.WorkId, translation.TargetLanguage })
                .Distinct()
                .ToListAsync(cancellationToken))
            .Select(x => (x.WorkId, Code: NormalizeLanguage(x.TargetLanguage)))
            .Where(x => x.Code is not null)
            .ToLookup(x => x.WorkId, x => x.Code!);

        var contentLanguage = chapters.Count == 0
            ? null
            : NormalizeLanguage(await new LearningContentLanguageResolver(db).ResolveTargetLanguageAsync(cancellationToken));

        var kind = type == MediaSearchType.Book ? MediaAcquisitionKind.Book : MediaAcquisitionKind.LightNovel;
        var result = new Dictionary<Guid, MediaSearchFacts>();
        foreach (var work in works)
        {
            var hasChapters = chapters.TryGetValue(work.Id, out var hasContent);
            var languages = new List<string>();
            if (hasChapters)
            {
                if ((NormalizeLanguage(BookFileFormats.Language(work.Format)) ?? contentLanguage) is { } source)
                {
                    languages.Add(source);
                }

                languages.AddRange(translated[work.Id]);
            }

            result[work.Id] = new MediaSearchFacts(
                null,
                DistinctSorted(languages),
                ParseGenres(work.MetadataGenresJson),
                hasChapters && hasContent,
                false,
                IsRequested(open, kind, (work.MetadataProvider, work.MetadataExternalId)));
        }

        return result;
    }

    private async Task<Dictionary<Guid, MediaSearchFacts>> MoviesAsync(
        Guid[] ids,
        HashSet<RequestKey> open,
        CancellationToken cancellationToken) =>
        (await db.Movies.AsNoTracking()
            .Where(movie => ids.Contains(movie.Id))
            .Select(movie => new { movie.Id, movie.Year, movie.TmdbId, movie.ImdbId })
            .ToListAsync(cancellationToken))
        .ToDictionary(
            movie => movie.Id,
            movie => new MediaSearchFacts(
                movie.Year, [], [], false, false,
                IsRequested(
                    open,
                    MediaAcquisitionKind.Movie,
                    (MappingProviders.Tmdb, movie.TmdbId),
                    (MappingProviders.Imdb, movie.ImdbId))));

    private async Task<Dictionary<Guid, MediaSearchFacts>> SeriesAsync(
        Guid[] ids,
        HashSet<RequestKey> open,
        CancellationToken cancellationToken) =>
        (await db.TvSeries.AsNoTracking()
            .Where(series => ids.Contains(series.Id))
            .Select(series => new { series.Id, series.Year, series.TmdbId, series.TvdbId })
            .ToListAsync(cancellationToken))
        .ToDictionary(
            series => series.Id,
            series => new MediaSearchFacts(
                series.Year, [], [], false, false,
                IsRequested(
                    open,
                    MediaAcquisitionKind.Tv,
                    (MappingProviders.Tmdb, series.TmdbId),
                    (MappingProviders.Tvdb, series.TvdbId))));

    private async Task<Dictionary<Guid, MediaSearchFacts>> AudiobooksAsync(
        Guid[] ids,
        HashSet<RequestKey> open,
        CancellationToken cancellationToken)
    {
        var withFiles = (await db.AudiobookFiles.AsNoTracking()
                .Where(file => ids.Contains(file.AudiobookId))
                .Select(file => file.AudiobookId)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet();

        return (await db.Audiobooks.AsNoTracking()
                .Where(audiobook => ids.Contains(audiobook.Id))
                .Select(audiobook => new { audiobook.Id, audiobook.Year, audiobook.Asin })
                .ToListAsync(cancellationToken))
            .ToDictionary(
                audiobook => audiobook.Id,
                audiobook => new MediaSearchFacts(
                    audiobook.Year, [], [], withFiles.Contains(audiobook.Id), false,
                    IsRequested(open, MediaAcquisitionKind.Audiobook, ("audible", audiobook.Asin))));
    }

    // The bounded list of open requests; a title is "wanted" while one names one of its provider ids.
    private async Task<HashSet<RequestKey>> OpenRequestsAsync(CancellationToken cancellationToken) =>
        (await requests.ListAsync(null, null, openOnly: true, limit: 500, cancellationToken))
        .Select(request => new RequestKey(
            request.Kind,
            MappingProviders.Normalize(request.Provider),
            request.ExternalId.Trim()))
        .ToHashSet();

    private static bool IsRequested(
        HashSet<RequestKey> open,
        MediaAcquisitionKind kind,
        params (string? Provider, string? ExternalId)[] identities) =>
        open.Count > 0
        && identities.Any(identity =>
            !string.IsNullOrWhiteSpace(identity.Provider)
            && !string.IsNullOrWhiteSpace(identity.ExternalId)
            && open.Contains(new RequestKey(
                kind,
                MappingProviders.Normalize(identity.Provider),
                identity.ExternalId.Trim())));

    private static string? NormalizeLanguage(string? language) =>
        PlaybackLanguages.Normalize(language) is { } code && code != PlaybackLanguages.SubtitlesOff ? code : null;

    private static IReadOnlyList<string> DistinctSorted(IEnumerable<string> values) =>
        [.. values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static IReadOnlyList<string> ParseGenres(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return
            [
                .. (JsonSerializer.Deserialize<string[]>(json) ?? [])
                    .Select(genre => genre?.Trim())
                    .Where(genre => !string.IsNullOrEmpty(genre))
                    .Select(genre => genre!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
            ];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record MangaSeriesRow(string Id, string? MetadataProvider, string? MetadataExternalId);

    private sealed record MangaChapterRow(string SeriesId, bool HasPages);
}
