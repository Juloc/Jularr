using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    // The AniBridge mappings: where AniList's anime entries lie at TheTVDB. A release replaces all rows at once; matching reads them.
    internal static class AnimeMappingLogic
    {
        public static Task<AnimeMappingSourceData> ReadAnimeMappingSourceAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    source."ETag",
                    (SELECT count(*) FROM "AnimeMappings") AS "Rows"
                FROM
                    "AnimeMappingSources" AS source;
                """;
            return ctx.ReadSql.ReadRequiredAsync<AnimeMappingSourceData>(sql, ct);
        }

        // The rows of the new release replace the old ones, and the release is the loaded one.
        public static async Task SaveAnimeMappingsAsync(this LogicContext ctx, AnimeMappingLoadData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                LOCK TABLE "AnimeMappings" IN EXCLUSIVE MODE;

                DELETE FROM
                    "AnimeMappings";

                INSERT INTO
                    "AnimeMappings" ("AniListId", "TvdbId", "SeasonNumber", "AniListEpisode", "FirstEpisode", "LastEpisode")
                SELECT
                    item."AniListId",
                    item."TvdbId",
                    item."SeasonNumber",
                    item."AniListEpisode",
                    item."FirstEpisode",
                    item."LastEpisode"
                FROM
                    jsonb_to_recordset(@Mappings::jsonb) AS item ("AniListId" integer, "TvdbId" integer, "SeasonNumber" smallint, "AniListEpisode" integer, "FirstEpisode" integer, "LastEpisode" integer)
                ON CONFLICT DO NOTHING;

                UPDATE
                    "AnimeMappingSources"
                SET
                    "ETag" = @ETag,
                    "SchemaVersion" = @SchemaVersion,
                    "GeneratedAt" = @GeneratedAt,
                    "CheckedAt" = now(),
                    "LoadedAt" = now()
                WHERE
                    "Id";
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // The loaded release is still the current one.
        public static async Task MarkAnimeMappingsCheckedAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AnimeMappingSources"
                SET
                    "CheckedAt" = now()
                WHERE
                    "Id";
                """;
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // The TheTVDB series an AniList entry lies in.
        public static Task<IReadOnlyList<AnimeMappingSeriesData>> ReadAnimeMappingSeriesAsync(this LogicContext ctx, int aniListId, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT DISTINCT
                    mapping."TvdbId"
                FROM
                    "AnimeMappings" AS mapping
                WHERE
                    mapping."AniListId" = @AniListId
                ORDER BY
                    mapping."TvdbId"
                LIMIT 10;
                """;
            sql.Parameters.Add(new AnimeMappingLookupData(aniListId));
            return ctx.ReadSql.ReadListAsync<AnimeMappingSeriesData>(sql, ct);
        }

        // Every AniList entry in the seasons of a Work's TheTVDB series, and whether the entry names another series too.
        public static Task<IReadOnlyList<AnimeMappingData>> ReadAnimeMappingsOfWorkAsync(this LogicContext ctx, long workId, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    mapping."AniListId",
                    mapping."SeasonNumber"::integer AS "SeasonNumber",
                    mapping."AniListEpisode",
                    mapping."FirstEpisode",
                    mapping."LastEpisode",
                    EXISTS (
                        SELECT
                            1
                        FROM
                            "AnimeMappings" AS other
                        WHERE
                            other."AniListId" = mapping."AniListId"
                            AND other."TvdbId" <> mapping."TvdbId"
                    ) AS "IsAmbiguous"
                FROM
                    "WorkExternalIdentities" AS identity
                    JOIN "AnimeMappings" AS mapping ON mapping."TvdbId" = CASE WHEN identity."Value" ~ '^[1-9][0-9]{0,8}$' THEN identity."Value"::integer END
                WHERE
                    identity."WorkId" = @Id
                    AND identity."Namespace" = 'tvdb'
                ORDER BY
                    mapping."SeasonNumber",
                    mapping."FirstEpisode",
                    mapping."AniListId"
                LIMIT 1000;
                """;
            sql.Parameters.Add(new WorkIdData(workId));
            return ctx.ReadSql.ReadListAsync<AnimeMappingData>(sql, ct);
        }
    }
}
