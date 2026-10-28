using System.Security;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    internal static class ExternalFeedLogic
    {
        public static async Task<IReadOnlyList<ExternalFeedData>> ReadMissingExternalFeedsAsync(this LogicContext ctx, ExternalFeedRefreshData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH candidates AS (
                    SELECT
                        entry."Namespace",
                        entry."MediaTypeId",
                        entry."MediaSubtypeId",
                        entry."DiscoverFeedTypeId",
                        entry."Page",
                        entry."SourceValue"
                    FROM
                        jsonb_to_recordset(@Feeds::jsonb) AS entry (
                            "Namespace" varchar(16), "MediaTypeId" smallint, "MediaSubtypeId" smallint,
                            "DiscoverFeedTypeId" smallint, "Page" integer, "SourceValue" varchar(64)
                        )
                    UNION ALL
                    SELECT
                        identity."Namespace",
                        CASE WHEN identity."Namespace" = 'tmdb_movie' THEN 1 ELSE 2 END::smallint,
                        NULL::smallint,
                        NULL::smallint,
                        1,
                        identity."Value"
                    FROM
                        "WorkExternalIdentities" AS identity
                        JOIN "Works" AS work ON work."Id" = identity."WorkId"
                    WHERE
                        identity."Namespace" IN ('tmdb_movie', 'tmdb_tv')
                        AND CASE WHEN identity."Value" ~ '^[1-9][0-9]{0,9}$' THEN identity."Value"::bigint BETWEEN 1 AND 2147483647 ELSE FALSE END
                        -- Recommendations are prepared for Works with details or a tie to the library, not for every catalog title.
                        AND (work."DetailedAt" IS NOT NULL OR EXISTS (SELECT 1 FROM "WorkLibraryRelations" AS library WHERE library."WorkId" = work."Id"))
                ), localized AS (
                    SELECT
                        candidate."Namespace",
                        candidate."MediaTypeId",
                        candidate."MediaSubtypeId",
                        candidate."DiscoverFeedTypeId",
                        candidate."Page",
                        candidate."SourceValue",
                        CASE WHEN candidate."Namespace" IN ('tmdb_movie', 'tmdb_tv') THEN locale."Locale" END AS "Language"
                    FROM
                        candidates AS candidate
                        JOIN "MediaTypes" AS type ON type."Id" = candidate."MediaTypeId" AND type."IsEnabled"
                        LEFT JOIN "MediaSubtypes" AS subtype ON subtype."Id" = candidate."MediaSubtypeId"
                        JOIN "Providers" AS provider ON provider."Key" = CASE WHEN candidate."Namespace" IN ('tmdb_movie', 'tmdb_tv') THEN 'tmdb' ELSE candidate."Namespace" END
                        CROSS JOIN "InstanceSettings" AS settings
                        JOIN "UiLocales" AS locale ON locale."Id" = settings."UiLocaleId"
                    WHERE
                        (candidate."MediaSubtypeId" IS NULL OR subtype."IsEnabled")
                        AND provider."IsEnabled"
                        AND (provider."Key" <> 'tmdb' OR provider."ProtectedSecret" IS NOT NULL)
                        AND (candidate."SourceValue" IS NOT NULL OR candidate."Namespace" <> ALL(@WholeCatalogs))
                )
                SELECT
                    candidate."Namespace",
                    candidate."MediaTypeId",
                    candidate."MediaSubtypeId",
                    candidate."DiscoverFeedTypeId",
                    candidate."Page",
                    candidate."SourceValue",
                    candidate."Language"
                FROM
                    localized AS candidate
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM "ExternalWorkFeeds" AS feed
                    WHERE
                        feed."Namespace" = candidate."Namespace"
                        AND feed."MediaTypeId" = candidate."MediaTypeId"
                        AND feed."MediaSubtypeId" IS NOT DISTINCT FROM candidate."MediaSubtypeId"
                        AND feed."DiscoverFeedTypeId" IS NOT DISTINCT FROM candidate."DiscoverFeedTypeId"
                        AND feed."Page" = candidate."Page"
                        AND feed."SourceValue" IS NOT DISTINCT FROM candidate."SourceValue"
                        AND feed."Language" IS NOT DISTINCT FROM candidate."Language"
                )
                ORDER BY
                    candidate."SourceValue" NULLS FIRST,
                    candidate."Namespace",
                    candidate."MediaSubtypeId" NULLS FIRST,
                    candidate."DiscoverFeedTypeId"
                LIMIT @Limit;
                """;
            sql.Parameters.Add(data);
            return await ctx.ReadSql.ReadListAsync<ExternalFeedData>(sql, ct);
        }

        // Only the refresh service calls this selection. Cache reads never compare timestamps.
        public static async Task<IReadOnlyList<ExternalFeedData>> ReadDueExternalFeedsAsync(this LogicContext ctx, ExternalFeedRefreshData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    feed."Namespace",
                    feed."MediaTypeId",
                    feed."MediaSubtypeId",
                    feed."DiscoverFeedTypeId",
                    feed."Page",
                    feed."SourceValue",
                    feed."Language"
                FROM
                    "ExternalWorkFeeds" AS feed
                    JOIN "MediaTypes" AS type ON type."Id" = feed."MediaTypeId" AND type."IsEnabled"
                    LEFT JOIN "MediaSubtypes" AS subtype ON subtype."Id" = feed."MediaSubtypeId"
                    JOIN "Providers" AS provider ON provider."Key" = CASE WHEN feed."Namespace" IN ('tmdb_movie', 'tmdb_tv') THEN 'tmdb' ELSE feed."Namespace" END
                    CROSS JOIN "InstanceSettings" AS settings
                    JOIN "UiLocales" AS locale ON locale."Id" = settings."UiLocaleId"
                WHERE
                    (feed."MediaSubtypeId" IS NULL OR subtype."IsEnabled")
                    AND provider."IsEnabled"
                    AND (provider."Key" <> 'tmdb' OR provider."ProtectedSecret" IS NOT NULL)
                    AND (provider."Key" <> 'tmdb' OR feed."Language" = locale."Locale")
                    AND (feed."SourceValue" IS NOT NULL OR feed."Namespace" <> ALL(@WholeCatalogs))
                    AND feed."RetryAfter" <= @Now
                    AND (
                        feed."RefreshedAt" IS NULL
                        OR feed."RefreshedAt" <= @Now - CASE
                            WHEN feed."SourceValue" IS NOT NULL THEN interval '12 hours'
                            WHEN feed."Namespace" NOT IN ('tmdb_movie', 'tmdb_tv') OR (feed."DiscoverFeedTypeId" = 1 AND feed."Page" = 1) THEN interval '6 hours'
                            ELSE interval '3 minutes'
                        END
                    )
                ORDER BY
                    feed."RetryAfter",
                    feed."Id"
                LIMIT @Limit;
                """;
            sql.Parameters.Add(data);
            return await ctx.ReadSql.ReadListAsync<ExternalFeedData>(sql, ct);
        }

        public static async Task<IReadOnlyList<ExternalWorkData>?> ReadExternalFeedAsync(this LogicContext ctx, ExternalFeedData feed, CancellationToken ct)
        {
            if (!await ctx.KeepsDiscoverListsAsync(ct))
            {
                throw new SecurityException("Discover keeps no lists without the Caching module.");
            }

            var state = new SqlStatement();
            state.Statement =
                """
                SELECT
                    feed."Id" AS "FeedId"
                FROM
                    "ExternalWorkFeeds" AS feed
                WHERE
                    feed."Key" = @Key
                    AND feed."RefreshedAt" IS NOT NULL;
                """;
            state.Parameters.Add(feed);
            if (await ctx.ReadSql.ReadOptionalAsync<ExternalFeedStateData>(state, ct) is null)
            {
                return null;
            }

            // Each Work under the feed's own identity of it; an anime season under the entry of that season. A Work's own cover comes before the catalog's.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    feed."Namespace",
                    coalesce(season."Value", identity."Value") AS "Value",
                    work."CanonicalTitle" AS "Title",
                    work."OriginalTitle",
                    work."ReleaseYear"::integer AS "ReleaseYear",
                    work."Overview",
                    work."Creator",
                    coalesce(cover."PosterUrl", work."ImageUrl") AS "ImageUrl",
                    coalesce(work."BackdropUrl", cover."BackdropUrl") AS "BackdropUrl",
                    work."FetchedAt",
                    work."Rating",
                    work."Id" AS "WorkId",
                    item."SeasonNumber"::integer AS "SeasonNumber"
                FROM
                    "ExternalWorkFeeds" AS feed
                    JOIN "ExternalWorkFeedItems" AS item ON item."FeedId" = feed."Id"
                    JOIN "Works" AS work ON work."Id" = item."WorkId"
                    LEFT JOIN "WorkExternalIdentities" AS identity ON identity."WorkId" = work."Id" AND identity."Namespace" = feed."Namespace"
                    LEFT JOIN LATERAL (
                        SELECT
                            mapped."Value"
                        FROM
                            "WorkUnits" AS unit
                            JOIN "WorkUnitExternalIdentities" AS mapped ON mapped."WorkUnitId" = unit."Id" AND mapped."Namespace" = feed."Namespace"
                        WHERE
                            item."SeasonNumber" IS NOT NULL
                            AND unit."WorkId" = work."Id"
                            AND unit."WorkUnitTypeId" = 1
                            AND unit."Number" = item."SeasonNumber"
                        ORDER BY
                            mapped."EpisodeOffset",
                            mapped."Id"
                        LIMIT 1
                    ) AS season ON TRUE
                    LEFT JOIN LATERAL (
                        SELECT
                            (array_agg(fact."Value" ORDER BY fact."Value" LIKE '/api/v1/covers/%' DESC, fact."IsManual" DESC) FILTER (WHERE fact."FactKey" = 'Poster'))[1] AS "PosterUrl",
                            (array_agg(fact."Value" ORDER BY fact."Value" LIKE '/api/v1/covers/%' DESC, fact."IsManual" DESC) FILTER (WHERE fact."FactKey" = 'Backdrop'))[1] AS "BackdropUrl"
                        FROM
                            "WorkMetadataFacts" AS fact
                        WHERE
                            fact."WorkId" = work."Id"
                            AND fact."FactKey" IN ('Poster', 'Backdrop')
                    ) AS cover ON TRUE
                WHERE
                    feed."Key" = @Key
                    AND coalesce(season."Value", identity."Value") IS NOT NULL
                ORDER BY
                    item."Position";
                """;
            sql.Parameters.Add(feed);
            return await ctx.ReadSql.ReadListAsync<ExternalWorkData>(sql, ct);
        }

        public static async Task SaveExternalFeedAsync(this LogicContext ctx, ExternalFeedData feed, IReadOnlyList<ExternalWorkData> works, CancellationToken ct)
        {
            if (!await ctx.KeepsDiscoverListsAsync(ct))
            {
                throw new SecurityException("Discover keeps no lists without the Caching module.");
            }

            var list = new ExternalWorkListData(feed.Namespace, feed.MediaTypeId, feed.MediaSubtypeId);
            var saved = await ctx.SaveExternalWorksAsync(new ExternalWorkBatchData(list, works, feed.DiscoverFeedTypeId == DiscoverFeedType.Trending && feed.Page == 1 && feed.SourceValue is null), ct);
            var data = new ExternalFeedUpdateData(feed, works, saved);

            var upsert = new SqlStatement();
            upsert.Statement =
                """
                INSERT INTO "ExternalWorkFeeds" (
                    "Key",
                    "Namespace",
                    "MediaTypeId",
                    "MediaSubtypeId",
                    "DiscoverFeedTypeId",
                    "Page",
                    "SourceValue",
                    "Language",
                    "RefreshedAt"
                ) VALUES (
                    @Key,
                    @Namespace,
                    @MediaTypeId,
                    @MediaSubtypeId,
                    @DiscoverFeedTypeId,
                    @Page,
                    @SourceValue,
                    @Language,
                    statement_timestamp()
                )
                ON CONFLICT ("Key") DO UPDATE SET
                    "RefreshedAt" = statement_timestamp(),
                    "RetryAfter" = statement_timestamp();
                """;
            upsert.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(upsert, ct) != 1)
            {
                throw new InvalidOperationException("The feed was not saved.");
            }

            var remove = new SqlStatement();
            remove.Statement =
                """
                DELETE FROM "ExternalWorkFeedItems" AS item
                USING "ExternalWorkFeeds" AS feed
                WHERE
                    feed."Key" = @Key
                    AND item."FeedId" = feed."Id";
                """;
            remove.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(remove, ct);

            var insert = new SqlStatement();
            insert.Statement =
                """
                INSERT INTO "ExternalWorkFeedItems" (
                    "FeedId",
                    "Position",
                    "WorkId",
                    "SeasonNumber"
                )
                SELECT
                    feed."Id",
                    entry."Position",
                    entry."WorkId",
                    entry."SeasonNumber"
                FROM
                    "ExternalWorkFeeds" AS feed
                    CROSS JOIN jsonb_to_recordset(@Items::jsonb) AS entry ("Position" integer, "WorkId" bigint, "SeasonNumber" smallint)
                WHERE
                    feed."Key" = @Key;
                """;
            insert.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(insert, ct) != data.Count)
            {
                throw new InvalidOperationException("The feed contains an unknown provider identity.");
            }
        }

        public static async Task PostponeExternalFeedAsync(this LogicContext ctx, ExternalFeedRetryData data, CancellationToken ct)
        {
            if (!await ctx.KeepsDiscoverListsAsync(ct))
            {
                throw new SecurityException("Discover keeps no lists without the Caching module.");
            }
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO "ExternalWorkFeeds" (
                    "Key",
                    "Namespace",
                    "MediaTypeId",
                    "MediaSubtypeId",
                    "DiscoverFeedTypeId",
                    "Page",
                    "SourceValue",
                    "Language",
                    "RetryAfter"
                ) VALUES (
                    @Key,
                    @Namespace,
                    @MediaTypeId,
                    @MediaSubtypeId,
                    @DiscoverFeedTypeId,
                    @Page,
                    @SourceValue,
                    @Language,
                    @RetryAfter
                )
                ON CONFLICT ("Key") DO UPDATE SET
                    "RetryAfter" = EXCLUDED."RetryAfter";
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new InvalidOperationException("The feed retry was not saved.");
            }
        }

        // With the Caching module's sub-option on, the catalogs whose Discover lists get their details in advance, each with its own request budget; none while it is off.
        // A catalog without settings of its own has no budget. The refresh runs only while Discover keeps its lists.
        public static async Task<IReadOnlyList<CatalogPrefetchData>> ReadCatalogPrefetchesAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    sync."Namespace",
                    sync."RequestsPerMinute"::integer AS "RequestsPerMinute",
                    sync."PausesWhileUsed"
                FROM
                    "ExternalWorkSyncs" AS sync
                    JOIN "InstanceSettings" AS settings ON settings."Id" = 1 AND settings."DetailsDiscoverAhead"
                ORDER BY
                    sync."Namespace";
                """;
            return await ctx.ReadSql.ReadListAsync<CatalogPrefetchData>(sql, ct);
        }

        // The next titles of a catalog's Discover lists that have no details yet and a catalog entry that can give them, in the lists' order.
        // Recommendations of a Work are no Discover list.
        public static async Task<IReadOnlyList<WorkIdData>> ReadUndetailedDiscoverWorksAsync(this LogicContext ctx, CatalogPrefetchQueryData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    listed."Id"
                FROM (
                    SELECT DISTINCT ON (work."Id")
                        work."Id",
                        item."Position",
                        feed."Id" AS "FeedId"
                    FROM
                        "ExternalWorkFeeds" AS feed
                        JOIN "ExternalWorkFeedItems" AS item ON item."FeedId" = feed."Id"
                        JOIN "Works" AS work ON work."Id" = item."WorkId"
                    WHERE
                        feed."Namespace" = @Namespace
                        AND feed."SourceValue" IS NULL
                        AND work."DetailedAt" IS NULL
                        AND work."Id" <> ALL(@Skipped)
                        AND EXISTS (
                            SELECT 1
                            FROM
                                "WorkExternalIdentities" AS identity
                            WHERE
                                identity."WorkId" = work."Id"
                                AND (identity."Namespace" IN ('tmdb_movie', 'tmdb_tv', 'openlibrary', 'musicbrainz', 'igdb') OR identity."Namespace" = 'anilist' AND work."MediaTypeId" = 3))
                    ORDER BY
                        work."Id",
                        item."Position",
                        feed."Id"
                ) AS listed
                ORDER BY
                    listed."Position",
                    listed."FeedId",
                    listed."Id"
                LIMIT @Limit;
                """;
            sql.Parameters.Add(data);
            return await ctx.ReadSql.ReadListAsync<WorkIdData>(sql, ct);
        }
    }
}
