using System.Security;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Instance;

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
                    WHERE
                        identity."Namespace" IN ('tmdb_movie', 'tmdb_tv')
                        AND CASE WHEN identity."Value" ~ '^[1-9][0-9]{0,9}$' THEN identity."Value"::bigint BETWEEN 1 AND 2147483647 ELSE FALSE END
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
            var state = new SqlStatement();
            state.Statement =
                """
                SELECT
                    module."IsEnabled",
                    CASE WHEN feed."RefreshedAt" IS NOT NULL THEN feed."Id" END AS "FeedId"
                FROM
                    "InstanceModules" AS module
                    LEFT JOIN "ExternalWorkFeeds" AS feed ON feed."Key" = @Key
                WHERE
                    module."Key" = 'DiscoverCache';
                """;
            state.Parameters.Add(feed);
            var current = await ctx.ReadSql.ReadOptionalAsync<ExternalFeedStateData>(state, ct);
            if (current is not { IsEnabled: true })
            {
                throw new SecurityException("The Discover cache is unavailable.");
            }
            if (current.FeedId is null)
            {
                return null;
            }

            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Namespace",
                    work."Value",
                    work."Title",
                    work."OriginalTitle",
                    work."ReleaseYear"::integer AS "ReleaseYear",
                    work."Overview",
                    work."Creator",
                    work."ImageUrl",
                    work."BackdropUrl",
                    work."FetchedAt",
                    work."Rating"
                FROM
                    "ExternalWorkFeeds" AS feed
                    JOIN "ExternalWorkFeedItems" AS item ON item."FeedId" = feed."Id"
                    JOIN "ExternalWorks" AS work ON work."Id" = item."ExternalWorkId"
                WHERE
                    feed."Key" = @Key
                ORDER BY
                    item."Position";
                """;
            sql.Parameters.Add(feed);
            return await ctx.ReadSql.ReadListAsync<ExternalWorkData>(sql, ct);
        }

        public static async Task SaveExternalFeedAsync(this LogicContext ctx, ExternalFeedData feed, IReadOnlyList<ExternalWorkData> works, CancellationToken ct)
        {
            var modules = await ctx.ReadInstanceModulesAsync(new InstanceModuleParameters(["DiscoverCache"]), ct);
            if (modules.Count != 1 || !modules[0].IsEnabled)
            {
                throw new SecurityException("The Discover cache is unavailable.");
            }

            var list = new ExternalWorkListData(feed.Namespace, feed.MediaTypeId, feed.MediaSubtypeId);
            await ctx.SaveExternalWorksAsync(new ExternalWorkBatchData(list, works, feed.DiscoverFeedTypeId == DiscoverFeedType.Trending && feed.Page == 1 && feed.SourceValue is null), ct);
            var data = new ExternalFeedUpdateData(feed, works);

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
                    "ExternalWorkId"
                )
                SELECT
                    feed."Id",
                    entry."Position"::integer,
                    work."Id"
                FROM
                    "ExternalWorkFeeds" AS feed
                    CROSS JOIN unnest(@Values::varchar[]) WITH ORDINALITY AS entry ("Value", "Position")
                    JOIN "ExternalWorks" AS work ON work."Namespace" = @Namespace AND work."Value" = entry."Value"
                WHERE
                    feed."Key" = @Key;
                """;
            insert.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(insert, ct) != data.Values.Length)
            {
                throw new InvalidOperationException("The feed contains an unknown provider identity.");
            }
        }

        public static async Task PostponeExternalFeedAsync(this LogicContext ctx, ExternalFeedRetryData data, CancellationToken ct)
        {
            var modules = await ctx.ReadInstanceModulesAsync(new InstanceModuleParameters(["DiscoverCache"]), ct);
            if (modules.Count != 1 || !modules[0].IsEnabled)
            {
                throw new SecurityException("The Discover cache is unavailable.");
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
    }
}
