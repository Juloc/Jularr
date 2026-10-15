using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Integrations.Data.AniList;
using Jularr.Integrations.Data.OpenLibrary;
using Jularr.Integrations.Logic.OpenLibrary;
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

        // Ranked catalog lists: Open Library trends books, AniList ranks manga and light novels.
        public async Task<IReadOnlyList<MetadataSearchResultV1>> ReadFeedAsync(LogicContext context, MediaType mediaType, MediaSubtype? subtype, DiscoverFeedType feed, CancellationToken ct)
        {
            var ranking = (mediaType, subtype, feed) switch
            {
                (MediaType.Book, null, DiscoverFeedType.Trending) => (AniListRanking?)null,
                (MediaType.Book, MediaSubtype.Manga or MediaSubtype.LightNovel, DiscoverFeedType.Trending) => AniListRanking.Trending,
                (MediaType.Book, MediaSubtype.Manga or MediaSubtype.LightNovel, DiscoverFeedType.Top) => AniListRanking.Score,
                (MediaType.Book, MediaSubtype.Manga or MediaSubtype.LightNovel, DiscoverFeedType.New) => AniListRanking.Newest,
                _ => throw new DataValidationException("DiscoverFeedTypeId", "unknown_catalog_value")
            };

            // Ranked lists change slowly; a shared cache of a day spares the provider.
            var key = $"metadata.catalog.{mediaType}.{subtype}.{feed}.{DateTime.UtcNow:yyyy-MM-dd}";
            if (!cache.TryGetValue(key, out List<MetadataSearchResultV1>? results))
            {
                results = ranking is { } order ? [.. (await aniList.ReadBookRankingAsync(subtype!.Value, order, ct)).Select(AniListBook)] : [.. (await books.ReadTrendingAsync(ct)).Select(Book)];
                cache.Set(key, results, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6), Size = Math.Max(1, results.Count) });
            }

            return await MarkKnownAsync(context, results!, ct);
        }

        private static MetadataSearchResultV1 Book(OpenLibraryResult result) => new(BookMetadata.Namespace, result.WorkId, TextLimit.Cut(result.Title, 500)!, null,
            result.Year is >= 1800 and <= 2200 ? result.Year : null, null, null, TextLimit.Cut(string.Join(", ", result.Authors), 4000), OpenLibraryClient.CoverUrl(result.CoverId));

        private static MetadataSearchResultV1 AniListBook(AniListMedia result) => new(AniListMetadata.Namespace, result.Id.ToString(CultureInfo.InvariantCulture),
            TextLimit.Cut(AniListMetadata.Title(result), 500)!, TextLimit.Cut(result.RomajiTitle, 500), result.Year is >= 1800 and <= 2200 ? result.Year : null,
            TextLimit.Cut(result.Description, 4000), null, TextLimit.Cut(string.Join(", ", result.Authors), 4000), result.CoverUrl);

        // Results that already are Works carry their id.
        private static async Task<IReadOnlyList<MetadataSearchResultV1>> MarkKnownAsync(LogicContext context, IReadOnlyList<MetadataSearchResultV1> results, CancellationToken ct)
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
                        JOIN jsonb_to_recordset(@Values::jsonb) AS item ("Namespace" text, "Value" text) ON item."Namespace" = identity."Namespace" AND item."Value" = identity."Value";
                    """;
                sql.Parameters.Add(new WorkIdentityLookupData(JsonSerializer.Serialize(results.Select(result => new { result.Namespace, Value = result.ExternalId }))));
                return ctx.ReadSql.ReadListAsync<WorkIdentityData>(sql, token);
            }, ct);
            var works = known.ToDictionary(identity => (identity.Namespace, identity.Value), identity => identity.WorkId);
            return [.. results.Select(result => new MetadataSearchResultV1(result.Namespace, result.ExternalId, result.Title, result.OriginalTitle,
                result.ReleaseYear, result.Overview, works.TryGetValue((result.Namespace, result.ExternalId), out var workId) ? workId : null, result.Author, result.PosterUrl))];
        }

        public async Task<long> ImportAsync(LogicContext context, MediaType mediaType, MediaSubtype? subtype, string? @namespace, string externalId, CancellationToken ct)
        {
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
