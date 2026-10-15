using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Integrations.Data.AniList;
using Jularr.Integrations.Data.Igdb;
using Jularr.Integrations.Data.MusicBrainz;
using Jularr.Integrations.Data.OpenLibrary;
using Jularr.Integrations.Data.Tmdb;
using Jularr.Integrations.Logic.Igdb;
using Jularr.Integrations.Logic.MusicBrainz;
using Jularr.Integrations.Logic.OpenLibrary;
using Jularr.Integrations.Logic.Tmdb;
using Microsoft.Extensions.Caching.Memory;

namespace Jularr.Backend.Service.Core
{
    // Provider search and import of every media type, for the Works page and for Discover.
    internal sealed class MetadataCatalog(WorkMetadata metadata, BookMetadata books, AniListMetadata aniList, MusicMetadata music, GameMetadata games, IMemoryCache cache)
    {
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
        public async Task<IReadOnlyList<MetadataSearchResultV1>> SearchAnimeAsync(LogicContext context, string query, CancellationToken ct) =>
            await MarkKnownAsync(context, [.. (await aniList.SearchAsync(AniListMediaType.Anime, AniListMatching.Formats(MediaType.Series, MediaSubtype.Anime), query, ct)).Select(AniListAnime)], ct);

        // One catalog title as its catalog describes it; its page never trusts what a link says.
        public async Task<DiscoverCatalogEntryV1> ReadEntryAsync(LogicContext context, MediaType mediaType, MediaSubtype? subtype, string @namespace, string externalId, CancellationToken ct)
        {
            if (mediaType == MediaType.Series && subtype == MediaSubtype.Anime && @namespace == AniListMetadata.Namespace)
            {
                return await ReadAnimeAsync(context, AniListId(externalId), ct);
            }

            var key = $"metadata.catalog.entry.{@namespace}.{externalId}";
            if (!cache.TryGetValue(key, out MetadataSearchResultV1? entry))
            {
                entry = (mediaType, @namespace) switch
                {
                    (MediaType.Book, BookMetadata.Namespace) => Book(await books.ReadAsync(externalId, ct)),
                    (MediaType.Book, AniListMetadata.Namespace) when subtype is MediaSubtype.Manga or MediaSubtype.LightNovel =>
                        AniListBook(await aniList.ReadAsync(AniListMediaType.Manga, AniListId(externalId), ct)),
                    (MediaType.Music, MusicMetadata.Namespace) => Album((await music.ReadAsync(externalId, ct)).Album),
                    (MediaType.Game, GameMetadata.Namespace) => Game(await games.ReadAsync(await games.ConnectAsync(context, ct), externalId, ct)),
                    _ => throw new KeyNotFoundException("The catalog does not hold this media type.")
                };
                cache.Set(key, entry, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6), Size = 1 });
            }

            var known = (await MarkKnownAsync(context, [entry!], ct))[0];
            return new DiscoverCatalogEntryV1(known.Title, known.ReleaseYear, known.Overview, known.PosterUrl, null, null, known.WorkId) { Author = known.Author };
        }

        private static int AniListId(string externalId) =>
            int.TryParse(externalId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : throw new KeyNotFoundException("Invalid AniList id.");

        private async Task<DiscoverCatalogEntryV1> ReadAnimeAsync(LogicContext context, int id, CancellationToken ct)
        {
            var media = await ReadAnimeEntryAsync(id, ct);

            // What AniList lacks for this season comes from the series.
            TmdbWork? series = null;
            if (media.BannerUrl is null || media.TrailerKey is null || media.Description is null || media.CoverUrl is null)
            {
                try
                {
                    var connection = await metadata.ConnectAsync(context, ct);
                    if (await FindAnimeSeriesAsync(connection, media, ct) is { } tmdbId)
                    {
                        series = await metadata.ReadWorkAsync(connection, MediaType.Series, tmdbId, ct);
                    }
                }
                catch (ServiceUnavailableException)
                {
                    // AniList alone still describes the season.
                }
            }

            var known = (await MarkKnownAsync(context, [AniListAnime(media)], ct))[0].WorkId;
            return new DiscoverCatalogEntryV1(TextLimit.Cut(AniListMetadata.Title(media), 500)!, media.Year is >= 1800 and <= 2200 ? media.Year : null,
                TextLimit.Cut(media.Description ?? series?.Overview, 4000), media.CoverUrl ?? TmdbClient.PosterUrl(series?.PosterPath), media.BannerUrl ?? TmdbClient.BackdropUrl(series?.BackdropPath),
                media.TrailerKey ?? series?.Details.TrailerKey, known);
        }

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

        // Ranked catalog lists: Open Library trends books, AniList ranks manga, light novels and anime.
        public async Task<IReadOnlyList<MetadataSearchResultV1>> ReadFeedAsync(LogicContext context, MediaType mediaType, MediaSubtype? subtype, DiscoverFeedType feed, CancellationToken ct)
        {
            var ranking = (mediaType, subtype, feed) switch
            {
                (MediaType.Book, null, DiscoverFeedType.Trending) => (AniListRanking?)null,
                (MediaType.Book, MediaSubtype.Manga or MediaSubtype.LightNovel, DiscoverFeedType.Trending) => AniListRanking.Trending,
                (MediaType.Book, MediaSubtype.Manga or MediaSubtype.LightNovel, DiscoverFeedType.Top) => AniListRanking.Score,
                (MediaType.Book, MediaSubtype.Manga or MediaSubtype.LightNovel, DiscoverFeedType.New) => AniListRanking.Newest,
                (MediaType.Series, MediaSubtype.Anime, DiscoverFeedType.Trending) => AniListRanking.Trending,
                (MediaType.Series, MediaSubtype.Anime, DiscoverFeedType.Top) => AniListRanking.Score,
                (MediaType.Series, MediaSubtype.Anime, DiscoverFeedType.New) => AniListRanking.Newest,
                _ => throw new DataValidationException("DiscoverFeedTypeId", "unknown_catalog_value")
            };

            // Ranked lists change slowly; a shared cache of a day spares the provider.
            var key = $"metadata.catalog.{mediaType}.{subtype}.{feed}.{DateTime.UtcNow:yyyy-MM-dd}";
            if (!cache.TryGetValue(key, out List<MetadataSearchResultV1>? results))
            {
                results = ranking is not { } order ? [.. (await books.ReadTrendingAsync(ct)).Select(Book)]
                    : mediaType == MediaType.Series ? [.. (await aniList.ReadAnimeRankingAsync(order, ct)).Select(AniListAnime)]
                    : [.. (await aniList.ReadBookRankingAsync(subtype!.Value, order, ct)).Select(AniListBook)];
                cache.Set(key, results, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6), Size = Math.Max(1, results.Count) });
            }

            return await MarkKnownAsync(context, results!, ct);
        }

        private static MetadataSearchResultV1 Game(IgdbGame game) => new(GameMetadata.Namespace, game.Id.ToString(CultureInfo.InvariantCulture), game.Name, null,
            game.Year is >= 1950 and <= 2200 ? game.Year : null, TextLimit.Cut(game.Summary, 4000), null, TextLimit.Cut(string.Join(", ", game.Developers), 4000),
            game.CoverImageId is { } cover ? IgdbClient.CoverUrl(cover) : null);

        private static MetadataSearchResultV1 Album(MusicBrainzAlbum album) => new(MusicMetadata.Namespace, album.Id, album.Title, null,
            album.Year is >= 1800 and <= 2200 ? album.Year : null, null, null, album.Artist, MusicBrainzClient.CoverUrl(album.Id));

        private static MetadataSearchResultV1 Book(OpenLibraryWork work) => new(BookMetadata.Namespace, work.WorkId, TextLimit.Cut(work.Title, 500)!, null,
            work.Year is >= 1800 and <= 2200 ? work.Year : null, TextLimit.Cut(work.Description, 4000), null, TextLimit.Cut(string.Join(", ", work.Authors), 4000),
            OpenLibraryClient.CoverUrl(work.CoverId));

        private static MetadataSearchResultV1 Book(OpenLibraryResult result) => new(BookMetadata.Namespace, result.WorkId, TextLimit.Cut(result.Title, 500)!, null,
            result.Year is >= 1800 and <= 2200 ? result.Year : null, null, null, TextLimit.Cut(string.Join(", ", result.Authors), 4000), OpenLibraryClient.CoverUrl(result.CoverId));

        private static MetadataSearchResultV1 AniListBook(AniListMedia result) => new(AniListMetadata.Namespace, result.Id.ToString(CultureInfo.InvariantCulture),
            TextLimit.Cut(AniListMetadata.Title(result), 500)!, TextLimit.Cut(result.RomajiTitle, 500), result.Year is >= 1800 and <= 2200 ? result.Year : null,
            TextLimit.Cut(result.Description, 4000), null, TextLimit.Cut(string.Join(", ", result.Authors), 4000), result.CoverUrl);

        // Results that already are Works carry their id.
        private async Task<IReadOnlyList<MetadataSearchResultV1>> MarkKnownAsync(LogicContext context, IReadOnlyList<MetadataSearchResultV1> results, CancellationToken ct)
        {
            var known = await context.ExecuteBatchAsync((ctx, token) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        identity."Namespace",
                        identity."Value",
                        identity."WorkId"
                    FROM
                        "WorkExternalIdentities" AS identity
                        JOIN jsonb_to_recordset(@Values::jsonb) AS item ("Namespace" text, "Value" text) ON item."Namespace" = identity."Namespace" AND item."Value" = identity."Value"
                    UNION
                    SELECT
                        identity."Namespace",
                        identity."Value",
                        unit."WorkId"
                    FROM
                        "WorkUnitExternalIdentities" AS identity
                        JOIN "WorkUnits" AS unit ON unit."Id" = identity."WorkUnitId"
                        JOIN jsonb_to_recordset(@Values::jsonb) AS item ("Namespace" text, "Value" text) ON item."Namespace" = identity."Namespace" AND item."Value" = identity."Value";
                    """;
                sql.Parameters.Add(new WorkIdentityLookupData(JsonSerializer.Serialize(results.Select(result => new { result.Namespace, Value = result.ExternalId })
                    .Concat(results.Select(result => (Result: result, Series: AnimeSeries(result))).Where(item => item.Series is not null)
                        .Select(item => new { Namespace = WorkMetadata.Namespace(MediaType.Series), Value = item.Series!.Value.ToString(CultureInfo.InvariantCulture) })))));
                return ctx.ReadSql.ReadListAsync<WorkIdentityData>(sql, token);
            }, ct);
            var works = known.DistinctBy(identity => (identity.Namespace, identity.Value)).ToDictionary(identity => (identity.Namespace, identity.Value), identity => identity.WorkId);
            return [.. results.Select(result => new MetadataSearchResultV1(result.Namespace, result.ExternalId, result.Title, result.OriginalTitle,
                result.ReleaseYear, result.Overview, works.TryGetValue((result.Namespace, result.ExternalId), out var workId) ? workId
                    : AnimeSeries(result) is { } series && works.TryGetValue((WorkMetadata.Namespace(MediaType.Series), series.ToString(CultureInfo.InvariantCulture)), out var seriesWork) ? seriesWork : null,
                result.Author, result.PosterUrl))];
        }

        // The TMDB series an anime entry was already resolved to.
        private int? AnimeSeries(MetadataSearchResultV1 result) =>
            result.Namespace == AniListMetadata.Namespace && cache.TryGetValue("metadata.anime.series." + result.ExternalId, out int? series) ? series : null;

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
