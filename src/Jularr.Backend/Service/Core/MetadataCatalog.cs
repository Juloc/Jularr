using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Integrations.Data.AniList;
using Jularr.Integrations.Data.OpenLibrary;
using Jularr.Integrations.Data.Tmdb;
using Jularr.Integrations.Logic.OpenLibrary;
using Microsoft.Extensions.Caching.Memory;

namespace Jularr.Backend.Service.Core
{
    // Provider search and import of every media type, for the Works page and for Discover.
    internal sealed class MetadataCatalog(WorkMetadata metadata, BookMetadata books, AniListMetadata aniList, MusicMetadata music, GameMetadata games, IMemoryCache cache, ExternalFeedCache feeds)
    {
        // Ranked lists of one provider are read one after the other.
        private static readonly SemaphoreSlim BooksGate = new(1, 1);
        private static readonly SemaphoreSlim AniListGate = new(1, 1);

        public async Task<IReadOnlyList<MetadataSearchResultV1>> SearchAsync(LogicContext context, MediaType mediaType, MediaSubtype? subtype, string query, int? year, CancellationToken ct)
        {
            // Books come from Open Library, Manga and Light Novels also from AniList, albums from MusicBrainz, games from IGDB, video from TMDB.
            var results = new List<MetadataSearchResultV1>();
            if (BookMetadata.Handles(mediaType))
            {
                // One unreachable provider leaves the other's results.
                ServiceUnavailableException? failure = null;
                try
                {
                    results.AddRange((await books.SearchAsync(query, year, ct)).Select(Book));
                }
                catch (ServiceUnavailableException exception)
                {
                    failure = exception;
                }

                if (subtype is MediaSubtype.Manga or MediaSubtype.LightNovel)
                {
                    results.AddRange((await aniList.SearchBooksAsync(subtype.Value, query, year, ct)).Select(AniListBook));
                }
                else if (failure is not null)
                {
                    throw failure;
                }
            }
            else if (GameMetadata.Handles(mediaType))
            {
                var connection = await games.ConnectAsync(context, ct);
                results.AddRange((await games.SearchAsync(connection, query, year, ct)).Select(result => new MetadataSearchResultV1(GameMetadata.Namespace,
                    result.Id.ToString(CultureInfo.InvariantCulture), result.Name, null, result.Year is >= 1950 and <= 2200 ? result.Year : null,
                    TextLimit.Cut(string.Join(", ", result.Platforms.Select(platform => platform.Abbreviation ?? platform.Name)), 4000), null, TextLimit.Cut(string.Join(", ", result.Developers), 4000))));
            }
            else if (MusicMetadata.Handles(mediaType))
            {
                results.AddRange((await music.SearchAsync(query, year, ct)).Select(result => new MetadataSearchResultV1(MusicMetadata.Namespace, result.Id,
                    result.Title, null, result.Year is >= 1800 and <= 2200 ? result.Year : null, result.PrimaryType, null, result.Artist)));
            }
            else
            {
                var connection = await metadata.ConnectAsync(context, ct);
                results.AddRange((await metadata.SearchAsync(connection, mediaType, query, year, ct)).Select(result =>
                    new MetadataSearchResultV1(WorkMetadata.Namespace(mediaType), result.Id.ToString(CultureInfo.InvariantCulture), TextLimit.Cut(result.Title, 500)!,
                        TextLimit.Cut(result.OriginalTitle, 500), result.Year is >= 1800 and <= 2200 ? result.Year : null, TextLimit.Cut(result.Overview, 4000), null)));
            }

            return await MarkKnownAsync(context, results, ct);
        }

        // Discover's anime: AniList finds and shows seasons; Works, requests and every action use the TMDB series.
        public async Task<IReadOnlyList<MetadataSearchResultV1>> SearchAnimeAsync(string query, CancellationToken ct) =>
            [.. (await aniList.SearchAsync(AniListMediaType.Anime, AniListMatching.Formats(MediaType.Series, MediaSubtype.Anime), query, ct)).Select(AniListAnime)];

        private async Task<AniListMedia> ReadAnimeEntryAsync(int id, CancellationToken ct)
        {
            var media = await aniList.ReadAsync(AniListMediaType.Anime, id, ct);
            return media.Format is { } format && AniListMatching.Formats(MediaType.Series, MediaSubtype.Anime).Contains(format) ? media : throw new KeyNotFoundException("Not an anime series.");
        }

        // The series of an entry is the one of its first season, along the prequels; kept for a while either way.
        private async Task<int?> FindAnimeSeriesAsync(TmdbConnection connection, AniListMedia media, CancellationToken ct)
        {
            var key = "metadata.anime.series." + media.Id.ToString(CultureInfo.InvariantCulture);
            if (cache.TryGetValue(key, out int? cached))
            {
                return cached;
            }

            var formats = AniListMatching.Formats(MediaType.Series, MediaSubtype.Anime);
            var first = media;
            for (var step = 0; step < 10 && first.Prequels.Count > 0; step++)
            {
                AniListMedia? previous = null;
                foreach (var prequel in first.Prequels)
                {
                    try
                    {
                        var candidate = await aniList.ReadAsync(AniListMediaType.Anime, prequel, ct);
                        if (candidate.Format is { } format && formats.Contains(format))
                        {
                            previous = candidate;
                            break;
                        }
                    }
                    catch (KeyNotFoundException)
                    {
                        // A prequel AniList no longer knows is skipped.
                    }
                }

                if (previous is null)
                {
                    break;
                }

                first = previous;
            }

            int? found = null;
            foreach (var query in new[] { first.EnglishTitle, first.RomajiTitle }.OfType<string>().Distinct(StringComparer.Ordinal))
            {
                found = AniListMatching.ChooseSeries(await metadata.SearchAsync(connection, MediaType.Series, query, null, ct), first);
                if (found is not null)
                {
                    break;
                }
            }

            cache.Set(key, found, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6), Size = 1 });
            return found;
        }

        private static MetadataSearchResultV1 AniListAnime(AniListMedia result) => new(AniListMetadata.Namespace, result.Id.ToString(CultureInfo.InvariantCulture),
            TextLimit.Cut(AniListMetadata.Title(result), 500)!, TextLimit.Cut(result.RomajiTitle, 500), result.Year is >= 1800 and <= 2200 ? result.Year : null,
            TextLimit.Cut(result.Description, 4000), null, null, result.CoverUrl);

        // Ranked catalog lists: Open Library trends books, AniList ranks manga, light novels and anime. A kept list already names its Works.
        public async Task<IReadOnlyList<ExternalWorkData>?> ReadCachedFeedInBatchAsync(LogicContext context, MediaType mediaType, MediaSubtype? subtype, DiscoverFeedType feed, CancellationToken ct)
        {
            var ranking = Ranking(mediaType, subtype, feed);
            if (!await context.KeepsDiscoverTitlesAsync(ct))
            {
                return null;
            }
            var descriptor = new ExternalFeedData(ranking is null ? BookMetadata.Namespace : AniListMetadata.Namespace, mediaType, subtype, feed, 1, null, null);
            return (await ExternalFeedCache.ReadAsync(context, descriptor, ct)).Items;
        }

        // Without kept lists the titles shown still become Works, so each card opens one.
        public async Task<IReadOnlyList<ExternalWorkData>> ReadFeedAsync(LogicContext context, MediaType mediaType, MediaSubtype? subtype, DiscoverFeedType feed, CancellationToken ct)
        {
            var ranking = Ranking(mediaType, subtype, feed);
            var descriptor = new ExternalFeedData(ranking is null ? BookMetadata.Namespace : AniListMetadata.Namespace, mediaType, subtype, feed, 1, null, null);
            var keeps = await context.ExecuteBatchAsync((ctx, token) => ctx.KeepsDiscoverTitlesAsync(token), ct, mode: SqlAccessMode.ReadOnly);
            if (keeps)
            {
                return await feeds.LoadAsync(context, descriptor, FetchExternalFeedAsync, ct);
            }

            return await KeepWorksAsync(context, new ExternalWorkListData(descriptor.Namespace, mediaType, subtype), await FetchExternalFeedAsync(descriptor, ct), ct);
        }

        public async Task<IReadOnlyList<ExternalWorkData>> FetchExternalFeedAsync(ExternalFeedData feed, CancellationToken ct)
        {
            var ranking = Ranking(feed.MediaTypeId, feed.MediaSubtypeId, feed.DiscoverFeedTypeId!.Value);
            var gate = ranking is null ? BooksGate : AniListGate;
            var failedKey = ranking is null ? "metadata.catalog.failed.books" : "metadata.catalog.failed.anilist";
            await gate.WaitAsync(ct);
            try
            {
                if (cache.TryGetValue(failedKey, out _))
                {
                    throw new ServiceUnavailableException("metadata_unavailable");
                }
                try
                {
                    IReadOnlyList<MetadataSearchResultV1> results = ranking is not { } order ? [.. (await books.ReadTrendingAsync(ct)).Select(Book)]
                        : feed.MediaTypeId == MediaType.Series ? [.. (await aniList.ReadAnimeRankingAsync(order, ct)).Select(AniListAnime)]
                        : [.. (await aniList.ReadBookRankingAsync(feed.MediaSubtypeId!.Value, order, ct)).Select(AniListBook)];
                    return [.. results.Select(ExternalWork)];
                }
                catch (ServiceUnavailableException)
                {
                    cache.Set(failedKey, true, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60), Size = 1 });
                    throw;
                }
            }
            finally
            {
                gate.Release();
            }
        }

        private static AniListRanking? Ranking(MediaType mediaType, MediaSubtype? subtype, DiscoverFeedType feed) => (mediaType, subtype, feed) switch
        {
            (MediaType.Book, null, DiscoverFeedType.Trending) => null,
            (MediaType.Book, MediaSubtype.Manga or MediaSubtype.LightNovel, DiscoverFeedType.Trending) => AniListRanking.Trending,
            (MediaType.Book, MediaSubtype.Manga or MediaSubtype.LightNovel, DiscoverFeedType.Top) => AniListRanking.Score,
            (MediaType.Book, MediaSubtype.Manga or MediaSubtype.LightNovel, DiscoverFeedType.New) => AniListRanking.Newest,
            (MediaType.Series, MediaSubtype.Anime, DiscoverFeedType.Trending) => AniListRanking.Trending,
            (MediaType.Series, MediaSubtype.Anime, DiscoverFeedType.Top) => AniListRanking.Score,
            (MediaType.Series, MediaSubtype.Anime, DiscoverFeedType.New) => AniListRanking.Newest,
            _ => throw new DataValidationException("DiscoverFeedTypeId", "unknown_catalog_value")
        };

        // Every catalog title a search showed is kept as a Work, each catalog under its own name; the titles come back in their order with their Works.
        public static async Task<IReadOnlyList<ExternalWorkData>> KeepAsync(LogicContext context, MediaType mediaType, MediaSubtype? subtype, IReadOnlyList<MetadataSearchResultV1> results,
            CancellationToken ct)
        {
            var kept = new Dictionary<(string Namespace, string Value), ExternalWorkData>();
            foreach (var group in results.GroupBy(result => result.Namespace))
            {
                foreach (var work in await KeepWorksAsync(context, new ExternalWorkListData(group.Key, mediaType, subtype), [.. group.Select(ExternalWork)], ct))
                {
                    kept[(work.Namespace, work.Value)] = work;
                }
            }

            return [.. results.Select(result => kept.GetValueOrDefault((result.Namespace, result.ExternalId))).OfType<ExternalWorkData>()];
        }

        // Titles a provider showed become catalog Works, found again by their identity; each comes back with its Work, an anime season with its series and the season.
        public static async Task<IReadOnlyList<ExternalWorkData>> KeepWorksAsync(LogicContext context, ExternalWorkListData list, IReadOnlyList<ExternalWorkData> works, CancellationToken ct)
        {
            var saved = (await context.ExecuteBatchAsync((ctx, token) => ctx.SaveExternalWorksAsync(new ExternalWorkBatchData(list, works, false), token), ct))
                .ToDictionary(work => work.Value, StringComparer.Ordinal);
            return [.. works.Where(work => saved.ContainsKey(work.Value)).Select(work => new ExternalWorkData(work.Namespace, work.Value, work.Title, work.OriginalTitle, work.ReleaseYear,
                work.Overview, work.Creator, work.ImageUrl, work.BackdropUrl, work.FetchedAt, work.Rating, saved[work.Value].WorkId, saved[work.Value].SeasonNumber))];
        }

        private static ExternalWorkData ExternalWork(MetadataSearchResultV1 result) => new(result.Namespace, result.ExternalId, result.Title, result.OriginalTitle, result.ReleaseYear,
            result.Overview, result.Author, result.PosterUrl, null, null);

        private static MetadataSearchResultV1 Book(OpenLibraryResult result) => new(BookMetadata.Namespace, result.WorkId, TextLimit.Cut(result.Title, 500)!, null,
            result.Year is >= 1800 and <= 2200 ? result.Year : null, null, null, TextLimit.Cut(string.Join(", ", result.Authors), 4000), OpenLibraryClient.CoverUrl(result.CoverId));

        private static MetadataSearchResultV1 AniListBook(AniListMedia result) => new(AniListMetadata.Namespace, result.Id.ToString(CultureInfo.InvariantCulture),
            TextLimit.Cut(AniListMetadata.Title(result), 500)!, TextLimit.Cut(result.RomajiTitle, 500), result.Year is >= 1800 and <= 2200 ? result.Year : null,
            TextLimit.Cut(result.Description, 4000), null, TextLimit.Cut(string.Join(", ", result.Authors), 4000), result.CoverUrl);

        // Results that already are Works of the library carry their id, a season's through its series; a catalog title nobody opened is imported like a new one.
        private static Task<IReadOnlyList<MetadataSearchResultV1>> MarkKnownAsync(LogicContext context, IReadOnlyList<MetadataSearchResultV1> results, CancellationToken ct) =>
            context.ExecuteBatchAsync(async (ctx, token) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        identity."Namespace",
                        identity."Value",
                        identity."WorkId",
                        (
                            SELECT
                                fact."Value"
                            FROM
                                "WorkMetadataFacts" AS fact
                            WHERE
                                fact."WorkId" = identity."WorkId"
                                AND fact."FactKey" = 'Poster'
                                AND fact."Value" LIKE '/api/v1/covers/%'
                            ORDER BY
                                fact."IsManual" DESC
                            LIMIT 1
                        ) AS "PosterUrl"
                    FROM
                        "WorkExternalIdentities" AS identity
                        JOIN jsonb_to_recordset(@Values::jsonb) AS item ("Namespace" text, "Value" text) ON item."Namespace" = identity."Namespace" AND item."Value" = identity."Value"
                        JOIN "Works" AS work ON work."Id" = identity."WorkId"
                    WHERE
                        work."DetailedAt" IS NOT NULL
                        OR EXISTS (SELECT 1 FROM "WorkLibraryRelations" AS library WHERE library."WorkId" = work."Id")
                    UNION
                    SELECT
                        identity."Namespace",
                        identity."Value",
                        unit."WorkId",
                        NULL::text
                    FROM
                        "WorkUnitExternalIdentities" AS identity
                        JOIN "WorkUnits" AS unit ON unit."Id" = identity."WorkUnitId"
                        JOIN jsonb_to_recordset(@Values::jsonb) AS item ("Namespace" text, "Value" text) ON item."Namespace" = identity."Namespace" AND item."Value" = identity."Value";
                    """;
                sql.Parameters.Add(new WorkIdentityLookupData(JsonSerializer.Serialize(results.Select(result => new { result.Namespace, Value = result.ExternalId }))));
                var known = await ctx.ReadSql.ReadListAsync<WorkIdentityPosterData>(sql, token);
                var works = known.DistinctBy(identity => (identity.Namespace, identity.Value)).ToDictionary(identity => (identity.Namespace, identity.Value), identity => identity.WorkId);
                var posters = known.Where(identity => identity.PosterUrl is not null).GroupBy(identity => (identity.Namespace, identity.Value)).ToDictionary(group => group.Key, group => group.First().PosterUrl);
                return (IReadOnlyList<MetadataSearchResultV1>)[.. results.Select(result => new MetadataSearchResultV1(result.Namespace, result.ExternalId, result.Title, result.OriginalTitle,
                    result.ReleaseYear, result.Overview, works.TryGetValue((result.Namespace, result.ExternalId), out var workId) ? workId : null, result.Author,
                    posters.GetValueOrDefault((result.Namespace, result.ExternalId)) ?? result.PosterUrl))];
            }, ct, mode: SqlAccessMode.ReadOnly);

        public async Task<long> ImportAsync(LogicContext context, MediaType mediaType, MediaSubtype? subtype, string? @namespace, string externalId, CancellationToken ct)
        {
            // An anime season is requested as its TMDB series.
            if (mediaType == MediaType.Series && @namespace == AniListMetadata.Namespace)
            {
                var media = await ReadAnimeEntryAsync(int.TryParse(externalId, NumberStyles.None, CultureInfo.InvariantCulture, out var aniListId) ? aniListId
                    : throw new KeyNotFoundException("Invalid AniList id."), ct);
                var connection = await metadata.ConnectAsync(context, ct);
                return await FindAnimeSeriesAsync(connection, media, ct) is { } tmdbId
                    ? await metadata.ImportAsync(context, connection, MediaType.Series, MediaSubtype.Anime, tmdbId, ct)
                    : throw new KeyNotFoundException("TMDB does not know this anime.");
            }

            // Importing an existing mapping refreshes the same Work.
            if (BookMetadata.Handles(mediaType))
            {
                return @namespace == AniListMetadata.Namespace ? await aniList.ImportAsync(context, externalId, ct) : await books.ImportAsync(context, subtype, externalId, ct);
            }

            if (MusicMetadata.Handles(mediaType))
            {
                return await music.ImportAsync(context, externalId, ct);
            }

            if (GameMetadata.Handles(mediaType))
            {
                return await games.ImportAsync(context, await games.ConnectAsync(context, ct), externalId, ct);
            }

            return await metadata.ImportAsync(context, await metadata.ConnectAsync(context, ct), mediaType, subtype, int.Parse(externalId, CultureInfo.InvariantCulture), ct);
        }
    }
}
