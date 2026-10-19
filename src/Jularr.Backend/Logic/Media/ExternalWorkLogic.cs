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
    }
}
