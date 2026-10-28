using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Integrations.Data.AniList;
using Jularr.Integrations.Data.Narou;
using Jularr.Integrations.Data.OpenLibrary;
using Jularr.Integrations.Logic.Narou;
using Jularr.Integrations.Logic.OpenLibrary;
using Microsoft.Extensions.Caching.Memory;

namespace Jularr.Backend.Service.Core
{
    // Provider search and import of every media type, for the Works page and for Discover.
    internal sealed class MetadataCatalog(WorkMetadata metadata, BookMetadata books, AniListMetadata aniList, MusicMetadata music, GameMetadata games, AniListMatcher matcher,
        NarouMetadata narou, IMemoryCache cache, ExternalFeedCache feeds)
    {
        // Ranked lists of one provider are read one after the other.
        private static readonly SemaphoreSlim BooksGate = new(1, 1);
        private static readonly SemaphoreSlim AniListGate = new(1, 1);

        // The details of one Work are read by one caller at a time; a caller that waited finds them stored.
        private static readonly SemaphoreSlim[] DetailGates = [.. Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1))];

        // Narō is asked for the Japanese titles of only the first few light novel entries a search finds.
        private const int EditionLookups = 3;

        // The language names a linked web novel by its edition's English, romanized or Japanese title.
        public async Task<IReadOnlyList<MetadataSearchResultV1>> SearchAsync(LogicContext context, MediaType mediaType, MediaSubtype? subtype, string query, int? year, CancellationToken ct,
            string? language = null)
        {
            // Books come from Open Library, Manga and Light Novels from AniList only, albums from MusicBrainz, games from IGDB, films from TMDB, series from TheTVDB.
            var results = new List<MetadataSearchResultV1>();
            if (BookMetadata.Handles(mediaType))
            {
                // Manga and Light Novels are AniList data only; other books come from Open Library.
                if (subtype is MediaSubtype.Manga or MediaSubtype.LightNovel)
                {
                    var editions = await aniList.SearchBooksAsync(subtype.Value, query, year, ct);

                    // Narō's web novels by title or ncode, each its own title beside the published editions; one that is clearly an edition's original follows it under its name.
                    IReadOnlyList<(NarouNovel Novel, AniListMedia? Edition)> novels = [];
                    if (subtype == MediaSubtype.LightNovel && await NarouMetadata.IsEnabledAsync(context, ct))
                    {
                        try
                        {
                            novels = await SearchWebNovelsAsync(query, editions, ct);
                        }
                        catch (ServiceUnavailableException)
                        {
                            // Narō's failure leaves the other catalogs' results.
                        }
                    }

                    foreach (var edition in editions)
                    {
                        results.Add(AniListBook(edition));
                        results.AddRange(novels.Where(item => item.Edition == edition).Select(item => WebNovel(item.Novel, item.Edition, language)));
                    }

                    results.AddRange(novels.Where(item => item.Edition is null).Select(item => WebNovel(item.Novel, null, language)));
                }
                else
                {
                    results.AddRange((await books.SearchAsync(query, year, ct)).Select(Book));
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
            else if (mediaType == MediaType.Series)
            {
                results.AddRange((await metadata.SearchSeriesAsync(context, query, year, ct)).Select(result => new MetadataSearchResultV1(result.Namespace, result.Value, result.Title,
                    result.OriginalTitle, result.ReleaseYear, result.Overview, null, null, result.ImageUrl)));
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

        // The query's novels and, for an English or romanized query, those under the Japanese titles of the editions found; a novel found only through an edition shows when it clearly is its original.
        private async Task<IReadOnlyList<(NarouNovel Novel, AniListMedia? Edition)>> SearchWebNovelsAsync(string query, IReadOnlyList<AniListMedia> editions, CancellationToken ct)
        {
            var found = new List<NarouNovel>(await narou.SearchAsync(query, ct));
            if (!NarouClient.IsNcode(query.Trim()))
            {
                var asked = AniListMatching.Normalize(query);
                foreach (var title in editions.Select(edition => edition.NativeTitle).OfType<string>().Select(AniListMatching.Normalize)
                    .Where(title => title.Length > 0 && title != asked).Distinct(StringComparer.Ordinal).Take(EditionLookups))
                {
                    found.AddRange((await narou.SearchAsync(title, ct)).Where(novel => AniListMatching.LinkWebNovel(editions, novel.Title, novel.Writer).Match is not null));
                }
            }

            return [.. found.DistinctBy(novel => novel.Ncode).Select(novel => (novel, AniListMatching.LinkWebNovel(editions, novel.Title, novel.Writer).Match))];
        }

        // TheTVDB's series search kept as Works; its IMDb and TMDB ids find a series of the library that does not hold its TheTVDB id yet.
        public async Task<IReadOnlyList<ExternalWorkData>> SearchAndKeepSeriesAsync(LogicContext context, MediaSubtype? subtype, string query, CancellationToken ct) =>
            await KeepWorksAsync(context, new ExternalWorkListData("tvdb", MediaType.Series, subtype), await metadata.SearchSeriesAsync(context, query, null, ct), ct);

        // Discover's anime: AniList finds and shows seasons; Works, requests and every action use the series of TheTVDB.
        public async Task<IReadOnlyList<MetadataSearchResultV1>> SearchAnimeAsync(string query, CancellationToken ct) =>
            [.. (await aniList.SearchAsync(AniListMediaType.Anime, AniListMatching.Formats(MediaType.Series, MediaSubtype.Anime), query, ct)).Select(AniListAnime)];

        private async Task<AniListMedia> ReadAnimeEntryAsync(int id, CancellationToken ct)
        {
            var media = await aniList.ReadAsync(AniListMediaType.Anime, id, ct);
            return media.Format is { } format && AniListMatching.Formats(MediaType.Series, MediaSubtype.Anime).Contains(format) ? media : throw new KeyNotFoundException("Not an anime series.");
        }

        // The TheTVDB series of an entry is the one the AniBridge mappings name for it; without one, the one a title search names for its first season, along the
        // prequels, kept for a while. Where the mappings name several series, the title search may only choose among them.
        private async Task<int?> FindAnimeSeriesAsync(LogicContext context, AniListMedia media, CancellationToken ct)
        {
            var mapped = await ReadMappedSeriesAsync(context, media.Id, ct);
            if (mapped is [var only])
            {
                return only;
            }

            var key = "metadata.anime.tvdb." + media.Id.ToString(CultureInfo.InvariantCulture);
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

            // The first season may be mapped where the entry itself is not.
            if (mapped.Count == 0 && first.Id != media.Id && await ReadMappedSeriesAsync(context, first.Id, ct) is [var series])
            {
                return series;
            }

            // Without a mapping, TheTVDB's title search names the series.
            int? found = null;
            foreach (var query in new[] { first.EnglishTitle, first.RomajiTitle }.OfType<string>().Distinct(StringComparer.Ordinal))
            {
                var results = await metadata.SearchSeriesAsync(context, query, null, ct);
                found = AniListMatching.ChooseSeries(mapped.Count == 0 ? results
                    : [.. results.Where(result => mapped.Any(id => id.ToString(CultureInfo.InvariantCulture) == result.Value))], first);
                if (found is not null)
                {
                    break;
                }
            }

            cache.Set(key, found, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6), Size = 1 });
            return found;
        }

        private static async Task<IReadOnlyList<int>> ReadMappedSeriesAsync(LogicContext context, int aniListId, CancellationToken ct) =>
            [.. (await context.ExecuteBatchAsync((ctx, token) => ctx.ReadAnimeMappingSeriesAsync(aniListId, token), ct, mode: SqlAccessMode.ReadOnly)).Select(series => series.TvdbId)];

        private static MetadataSearchResultV1 AniListAnime(AniListMedia result) => new(AniListMetadata.Namespace, result.Id.ToString(CultureInfo.InvariantCulture),
            TextLimit.Cut(AniListMetadata.Title(result), 500)!, TextLimit.Cut(result.RomajiTitle, 500), result.Year is >= 1800 and <= 2200 ? result.Year : null,
            TextLimit.Cut(result.Description, 4000), null, null, result.CoverUrl);

        // Ranked catalog lists: Open Library trends books, AniList ranks manga, light novels and anime. A kept list already names its Works.
        public async Task<IReadOnlyList<ExternalWorkData>?> ReadCachedFeedInBatchAsync(LogicContext context, MediaType mediaType, MediaSubtype? subtype, DiscoverFeedType feed, CancellationToken ct)
        {
            var ranking = Ranking(mediaType, subtype, feed);
            if (!await context.KeepsDiscoverListsAsync(ct))
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
            var keeps = await context.ExecuteBatchAsync((ctx, token) => ctx.KeepsDiscoverListsAsync(token), ct, mode: SqlAccessMode.ReadOnly);
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

        // Every catalog title a search showed is kept as a Work, each catalog under its own name; the titles come back in their order with their Works, named as the search named them.
        // A web novel's Work keeps its Japanese name: the edition's name a search lends it depends on the profile's language.
        public static async Task<IReadOnlyList<ExternalWorkData>> KeepAsync(LogicContext context, MediaType mediaType, MediaSubtype? subtype, IReadOnlyList<MetadataSearchResultV1> results,
            CancellationToken ct)
        {
            var kept = new Dictionary<(string Namespace, string Value), ExternalWorkData>();
            foreach (var group in results.GroupBy(result => result.Namespace))
            {
                var works = group.Select(result => result.Namespace == NarouMetadata.Namespace && result.OriginalTitle is { } japanese
                    ? new ExternalWorkData(result.Namespace, result.ExternalId, japanese, null, result.ReleaseYear, result.Overview, result.Author, result.PosterUrl, null, null)
                    : ExternalWork(result));
                foreach (var work in await KeepWorksAsync(context, new ExternalWorkListData(group.Key, mediaType, subtype), [.. works], ct))
                {
                    kept[(work.Namespace, work.Value)] = work;
                }
            }

            return [.. results.Select(result => kept.GetValueOrDefault((result.Namespace, result.ExternalId)) is { } work
                ? new ExternalWorkData(work.Namespace, work.Value, result.Title, work.OriginalTitle, work.ReleaseYear, work.Overview, work.Creator, work.ImageUrl, work.BackdropUrl,
                    work.FetchedAt, work.Rating, work.WorkId, work.SeasonNumber)
                : null).OfType<ExternalWorkData>()];
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

        // A web novel goes by its own Japanese title, or by its edition's name with the Japanese one beside it.
        private static MetadataSearchResultV1 WebNovel(NarouNovel novel, AniListMedia? edition, string? language) => new(NarouMetadata.Namespace, novel.Ncode,
            TextLimit.Cut(edition is null ? novel.Title : AniListMetadata.Title(edition, language), 500)!, edition is null ? null : TextLimit.Cut(novel.Title, 500),
            novel.FirstYear is >= 1800 and <= 2200 ? novel.FirstYear : null, TextLimit.Cut(novel.Story, 4000), null, TextLimit.Cut(novel.Writer, 4000));

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
            // An anime season is requested as its series.
            if (mediaType == MediaType.Series && @namespace == AniListMetadata.Namespace)
            {
                var media = await ReadAnimeEntryAsync(int.TryParse(externalId, NumberStyles.None, CultureInfo.InvariantCulture, out var aniListId) ? aniListId
                    : throw new KeyNotFoundException("Invalid AniList id."), ct);
                return await FindAnimeSeriesAsync(context, media, ct) is { } tvdbId
                    ? await metadata.ImportSeriesAsync(context, MediaSubtype.Anime, tvdbId, null, ct)
                    : throw new KeyNotFoundException("TheTVDB does not know this anime.");
            }

            // Every other series comes from TheTVDB, also one a TMDB search or list named.
            if (mediaType == MediaType.Series)
            {
                return await metadata.ImportSeriesAsync(context, subtype, @namespace ?? WorkMetadata.Namespace(MediaType.Series), externalId, ct);
            }

            // Importing an existing mapping refreshes the same Work.
            if (BookMetadata.Handles(mediaType))
            {
                if (@namespace == NarouMetadata.Namespace)
                {
                    return subtype == MediaSubtype.LightNovel && await NarouMetadata.IsEnabledAsync(context, ct)
                        ? await narou.ImportAsync(context, externalId, ct)
                        : throw new KeyNotFoundException("Narō's web novels are light novels of an enabled provider.");
                }

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

        // A Work the profile may open (any Work for a background pass without a profile) that has no details yet takes them from its catalog entry once, into the same Work.
        // False while the catalog cannot give them now; nothing is retried, the next open asks again. A Work no catalog entry describes, such as an anime season no series
        // holds yet, keeps its row.
        public async Task<bool> DetailAsync(LogicContext context, long workId, long? profileId, CancellationToken ct)
        {
            var gate = DetailGates[(int)(workId % DetailGates.Length)];
            await gate.WaitAsync(ct);
            try
            {
                var (work, identity) = await ReadDetailStateAsync(context, workId, profileId, ct);
                if (work is not { IsDetailed: false } || identity is null)
                {
                    return true;
                }

                try
                {
                    await ImportAsync(context, work.MediaTypeId, work.MediaSubtypeId, identity.Namespace, identity.Value, ct);
                    return true;
                }
                catch (Exception exception) when (exception is ServiceUnavailableException or KeyNotFoundException or DataValidationException)
                {
                    return false;
                }
            }
            finally
            {
                gate.Release();
            }
        }

        // The Work a request names. A catalog title takes its details first, so the request knows its episodes, volumes or platforms; an anime season no series holds yet
        // is requested as its series, which then takes the season's Work in. While the catalog cannot answer, the request names the Work as it is and its details follow later.
        public async Task<long> PrepareRequestAsync(LogicContext context, long workId, long profileId, CancellationToken ct)
        {
            var (work, identity) = await ReadDetailStateAsync(context, workId, profileId, ct);
            if (work is not { IsDetailed: false })
            {
                return workId;
            }

            if (identity is null && work.AniListValue is { } season)
            {
                try
                {
                    return await ResolveAnimeSeasonAsync(context, season, ct);
                }
                catch (Exception exception) when (exception is ServiceUnavailableException or KeyNotFoundException or DataValidationException)
                {
                    return workId;
                }
            }

            await DetailAsync(context, workId, profileId, ct);
            return workId;
        }

        // The series comes from its catalog and, if it was never matched, gets its seasons' AniList entries; the season's Work goes into the series once a season holds its entry.
        private async Task<long> ResolveAnimeSeasonAsync(LogicContext context, string aniListId, CancellationToken ct)
        {
            var seriesId = await ImportAsync(context, MediaType.Series, MediaSubtype.Anime, AniListMetadata.Namespace, aniListId, ct);
            var target = await context.ExecuteBatchAsync((ctx, token) => AniListMatcher.ReadUnmatchedSeriesAsync(ctx, seriesId, token), ct, mode: SqlAccessMode.ReadOnly);
            IdentityMatchResultData? match = null;
            if (target is not null)
            {
                try
                {
                    var (seasons, mappings) = await context.ExecuteBatchAsync(async (ctx, token) =>
                        (await AniListMatcher.ReadSeasonsAsync(ctx, seriesId, token), await ctx.ReadAnimeMappingsOfWorkAsync(seriesId, token)), ct, mode: SqlAccessMode.ReadOnly);
                    match = await matcher.MatchAsync(target, seasons, mappings, null, ct);
                }
                catch (ServiceUnavailableException)
                {
                    // The matching job maps the seasons later and the season's Work goes then.
                }
            }

            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                if (match is not null)
                {
                    await ctx.SaveIdentityMatchAsync(match, token);
                }
                else
                {
                    await ctx.MergeAnimePlaceholdersAsync(seriesId, token);
                }

                return true;
            }, ct);
            return seriesId;
        }

        private static Task<(WorkDetailStateData? Work, WorkIdentityData? Identity)> ReadDetailStateAsync(LogicContext context, long workId, long? profileId, CancellationToken ct) =>
            context.ExecuteBatchAsync(async (ctx, token) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        work."Id",
                        work."MediaTypeId",
                        work."MediaSubtypeId",
                        work."DetailedAt" IS NOT NULL AS "IsDetailed",
                        (
                            SELECT
                                identity."Value"
                            FROM
                                "WorkExternalIdentities" AS identity
                            WHERE
                                identity."WorkId" = work."Id"
                                AND identity."Namespace" = 'anilist'
                                AND work."MediaTypeId" = 2
                        ) AS "AniListValue"
                    FROM
                        "Works" AS work
                    WHERE
                        work."Id" = @Id
                        AND (@ProfileId::bigint IS NULL OR EXISTS (SELECT 1 FROM "ProfileWorks" AS visible WHERE visible."ProfileId" = @ProfileId AND visible."WorkId" = work."Id"));
                    """;
                sql.Parameters.Add(new WorkDetailLookupData(workId, profileId));
                var work = await ctx.ReadSql.ReadOptionalAsync<WorkDetailStateData>(sql, token);
                return (work, work is null ? null : await WorkMetadataQueries.ReadProviderIdentityAsync(ctx, workId, token));
            }, ct, mode: SqlAccessMode.ReadOnly);
    }
}
