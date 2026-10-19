using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    // The provider catalog Discover keeps: every title it loaded once, the current trending lists in their order.
    internal static class ExternalWorkLogic
    {
        // Trending lists change slowly; within this time the catalog answers instead of the provider.
        private static readonly TimeSpan TrendingLifetime = TimeSpan.FromHours(6);

        public static bool IsFresh(IReadOnlyList<ExternalWorkData> trending, DateTimeOffset now) =>
            trending.Count > 0 && now - trending.Max(work => new DateTimeOffset(work.FetchedAt, TimeSpan.Zero)) < TrendingLifetime;

        // Returns how many titles were saved.
        public static async Task<int> SaveExternalWorksAsync(this LogicContext ctx, ExternalWorkBatchData data, CancellationToken ct)
        {
            if (data.Values.Length == 0) return 0;

            // A new trending list replaces the old order of its type.
            var sql = new SqlStatement();
            if (data.IsTrending)
            {
                sql.Statement =
                    """
                    UPDATE
                        "ExternalWorks" AS work
                    SET
                        "TrendingRank" = NULL
                    WHERE
                        work."Namespace" = @Namespace
                        AND work."MediaTypeId" = @MediaTypeId
                        AND work."MediaSubtypeId" IS NOT DISTINCT FROM @MediaSubtypeId::smallint
                        AND work."TrendingRank" IS NOT NULL;
                    """;
                sql.Parameters.Add(data);
                await ctx.ExecuteAsyncForAffectedRows(sql, ct);
                sql = new SqlStatement();
            }

            // A list or search that knows less than an earlier read keeps what was known; only secure image addresses are kept.
            sql.Statement =
                """
                INSERT INTO
                    "ExternalWorks" AS work ("Namespace", "Value", "MediaTypeId", "MediaSubtypeId", "Title", "OriginalTitle", "ReleaseYear", "Overview", "Creator", "ImageUrl", "BackdropUrl", "TrendingRank", "FetchedAt")
                SELECT DISTINCT ON (item."Value")
                    @Namespace,
                    item."Value",
                    @MediaTypeId,
                    @MediaSubtypeId::smallint,
                    item."Title",
                    item."OriginalTitle",
                    nullif(item."ReleaseYear", 0),
                    item."Overview",
                    item."Creator",
                    CASE WHEN item."ImageUrl" ~ '^https://' AND length(item."ImageUrl") <= 500 THEN item."ImageUrl" END,
                    CASE WHEN item."BackdropUrl" ~ '^https://' AND length(item."BackdropUrl") <= 500 THEN item."BackdropUrl" END,
                    CASE WHEN @IsTrending THEN item."Rank"::integer END,
                    now()
                FROM
                    unnest(@Values, @Titles, @OriginalTitles, @ReleaseYears, @Overviews, @Creators, @ImageUrls, @BackdropUrls) WITH ORDINALITY
                        AS item("Value", "Title", "OriginalTitle", "ReleaseYear", "Overview", "Creator", "ImageUrl", "BackdropUrl", "Rank")
                ORDER BY
                    item."Value",
                    item."Rank"
                ON CONFLICT ("Namespace", "Value") DO UPDATE
                SET
                    "MediaTypeId" = EXCLUDED."MediaTypeId",
                    "MediaSubtypeId" = EXCLUDED."MediaSubtypeId",
                    "Title" = EXCLUDED."Title",
                    "OriginalTitle" = coalesce(EXCLUDED."OriginalTitle", work."OriginalTitle"),
                    "ReleaseYear" = coalesce(EXCLUDED."ReleaseYear", work."ReleaseYear"),
                    "Overview" = coalesce(EXCLUDED."Overview", work."Overview"),
                    "Creator" = coalesce(EXCLUDED."Creator", work."Creator"),
                    "ImageUrl" = coalesce(EXCLUDED."ImageUrl", work."ImageUrl"),
                    "BackdropUrl" = coalesce(EXCLUDED."BackdropUrl", work."BackdropUrl"),
                    "TrendingRank" = coalesce(EXCLUDED."TrendingRank", work."TrendingRank"),
                    "FetchedAt" = now();
                """;
            sql.Parameters.Add(data);
            return await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // The current trending list of a type in its order, with when it was loaded.
        public static async Task<IReadOnlyList<ExternalWorkData>> ReadTrendingExternalWorksAsync(this LogicContext ctx, ExternalWorkListData list, CancellationToken ct)
        {
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
                    work."FetchedAt"
                FROM
                    "ExternalWorks" AS work
                WHERE
                    work."Namespace" = @Namespace
                    AND work."MediaTypeId" = @MediaTypeId
                    AND work."MediaSubtypeId" IS NOT DISTINCT FROM @MediaSubtypeId
                    AND work."TrendingRank" IS NOT NULL
                ORDER BY
                    work."TrendingRank";
                """;
            sql.Parameters.Add(list);
            return await ctx.ReadSql.ReadListAsync<ExternalWorkData>(sql, ct);
        }
        // A full sync: the provider's id list once a day, its changes every six hours, then this many entries per pass.
        public static readonly TimeSpan ChangesInterval = TimeSpan.FromHours(6);
        public const int EntriesPerPass = 240;

        public static bool IsListDue(ExternalWorkSyncData sync, DateTimeOffset now) => sync.ListedOn != DateOnly.FromDateTime(now.UtcDateTime) && now.UtcDateTime.Hour >= 9;

        public static bool AreChangesDue(ExternalWorkSyncData sync, DateTimeOffset now) =>
            sync.ChangesReadAt is not { } read || now - new DateTimeOffset(read, TimeSpan.Zero) >= ChangesInterval;

        public static async Task<ExternalWorkSyncData> ReadExternalWorkSyncAsync(this LogicContext ctx, string @namespace, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    sync."Namespace",
                    sync."ListedOn",
                    sync."ChangesReadAt"
                FROM
                    "ExternalWorkSyncs" AS sync
                WHERE
                    sync."Namespace" = @Namespace;
                """;
            sql.Parameters.Add(new ExternalWorkSyncData(@namespace, null, null));
            return await ctx.ReadSql.ReadOptionalAsync<ExternalWorkSyncData>(sql, ct) ?? new ExternalWorkSyncData(@namespace, null, null);
        }

        public static async Task<int> SaveExternalWorkSyncAsync(this LogicContext ctx, ExternalWorkSyncData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "ExternalWorkSyncs" AS sync ("Namespace", "ListedOn", "ChangesReadAt")
                VALUES
                    (@Namespace, @ListedOn, @ChangesReadAt)
                ON CONFLICT ("Namespace") DO UPDATE
                SET
                    "ListedOn" = EXCLUDED."ListedOn",
                    "ChangesReadAt" = EXCLUDED."ChangesReadAt",
                    "UpdatedAt" = now();
                """;
            sql.Parameters.Add(data);
            return await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // A provider's id list adds the titles not yet known; known ones only take its popularity, their titles stay.
        public static async Task<int> ListExternalWorksAsync(this LogicContext ctx, ExternalWorkEntriesData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "ExternalWorks" AS work ("Namespace", "Value", "MediaTypeId", "MediaSubtypeId", "Title", "OriginalTitle", "Popularity", "FetchedAt")
                SELECT DISTINCT ON (entry."Value")
                    @Namespace,
                    entry."Value",
                    @MediaTypeId,
                    @MediaSubtypeId::smallint,
                    left(entry."Title", 500),
                    left(entry."Title", 500),
                    greatest(entry."Popularity", 0),
                    now()
                FROM
                    jsonb_to_recordset(@Entries::jsonb) AS entry ("Value" text, "Title" text, "Popularity" real)
                WHERE
                    length(btrim(entry."Title")) > 0
                ORDER BY
                    entry."Value"
                ON CONFLICT ("Namespace", "Value") DO UPDATE
                SET
                    "Popularity" = EXCLUDED."Popularity"
                WHERE
                    work."Popularity" IS DISTINCT FROM EXCLUDED."Popularity";
                """;
            sql.Parameters.Add(data);
            return await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task<IReadOnlyList<string>> ReadUndetailedExternalWorksAsync(this LogicContext ctx, ExternalWorkQueueData queue, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Value"
                FROM
                    "ExternalWorks" AS work
                WHERE
                    work."Namespace" = @Namespace
                    AND work."DetailedAt" IS NULL
                ORDER BY
                    work."Popularity" DESC NULLS LAST,
                    work."Id"
                LIMIT @Limit;
                """;
            sql.Parameters.Add(queue);
            return [.. (await ctx.ReadSql.ReadListAsync<ExternalWorkValueData>(sql, ct)).Select(row => row.Value)];
        }

        // Entries just read replace what the catalog knew; entries the provider no longer has leave it.
        public static async Task<int> DetailExternalWorksAsync(this LogicContext ctx, ExternalWorkEntriesData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "ExternalWorks" AS work
                WHERE
                    work."Namespace" = @Namespace
                    AND work."Value" = ANY(@Gone);
                """;
            sql.Parameters.Add(data);
            var changed = await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "ExternalWorks" AS work
                SET
                    "Title" = left(entry."Title", 500),
                    "OriginalTitle" = left(entry."OriginalTitle", 500),
                    "ReleasedOn" = entry."ReleasedOn",
                    "ReleaseYear" = CASE WHEN extract(year FROM entry."ReleasedOn") BETWEEN 1800 AND 2200 THEN extract(year FROM entry."ReleasedOn")::smallint END,
                    "Overview" = left(entry."Overview", 4000),
                    "ImageUrl" = CASE WHEN entry."ImageUrl" ~ '^https://' AND length(entry."ImageUrl") <= 500 THEN entry."ImageUrl" END,
                    "BackdropUrl" = CASE WHEN entry."BackdropUrl" ~ '^https://' AND length(entry."BackdropUrl") <= 500 THEN entry."BackdropUrl" END,
                    "Popularity" = coalesce(greatest(entry."Popularity", 0), work."Popularity"),
                    "Rating" = CASE WHEN entry."Rating" BETWEEN 0 AND 10 THEN round(entry."Rating"::numeric, 2) END,
                    "ImdbId" = CASE WHEN entry."ImdbId" ~ '^tt[0-9]{7,10}$' THEN entry."ImdbId" END,
                    "IsAdult" = entry."IsAdult",
                    "FetchedAt" = now(),
                    "DetailedAt" = now()
                FROM
                    jsonb_to_recordset(@Entries::jsonb) AS entry ("Value" text, "Title" text, "OriginalTitle" text, "ReleasedOn" date, "Overview" text, "ImageUrl" text,
                        "BackdropUrl" text, "Popularity" real, "Rating" double precision, "ImdbId" text, "IsAdult" boolean)
                WHERE
                    work."Namespace" = @Namespace
                    AND work."Value" = entry."Value"
                    AND length(btrim(entry."Title")) > 0;
                """;
            sql.Parameters.Add(data);
            return changed + await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // Entries the provider changed are read again.
        public static async Task<int> MarkChangedExternalWorksAsync(this LogicContext ctx, ExternalWorkValuesData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "ExternalWorks" AS work
                SET
                    "DetailedAt" = NULL
                WHERE
                    work."Namespace" = @Namespace
                    AND work."Value" = ANY(@Values)
                    AND work."DetailedAt" IS NOT NULL;
                """;
            sql.Parameters.Add(data);
            return await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
