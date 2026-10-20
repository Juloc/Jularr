using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    // The one owner of a profile marking its own progress as watched or unwatched; game play time is not watching and stays.
    internal static class ProgressLogic
    {
        public static async Task MarkWatchedAsync(this LogicContext ctx, ProgressChangeData data, CancellationToken ct)
        {
            await ctx.RequireProgressTargetAsync(data, ct);

            // A progress of video or audio completes; a book's only where the profile has one (it needs a position to continue from).
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH RECURSIVE targets AS (
                    SELECT
                        unit."Id" AS "WorkUnitId"
                    FROM
                        "WorkUnits" AS unit
                    WHERE
                        unit."WorkId" = @WorkId
                        AND (@WorkUnitId::bigint IS NULL OR unit."Id" = @WorkUnitId)
                    UNION ALL
                    SELECT
                        child."Id"
                    FROM
                        "WorkUnits" AS child
                        JOIN targets ON child."ParentWorkUnitId" = targets."WorkUnitId"
                    WHERE
                        @WorkUnitId::bigint IS NOT NULL
                )
                UPDATE
                    "MediaProgress" AS progress
                SET
                    "IsCompleted" = TRUE,
                    "CompletedAt" = coalesce(progress."CompletedAt", now()),
                    "PositionMs" = CASE WHEN progress."ProgressPositionTypeId" = 1 THEN 0 ELSE progress."PositionMs" END,
                    "Progression" = CASE WHEN progress."ProgressPositionTypeId" = 2 THEN 1 ELSE progress."Progression" END,
                    "UpdatedAt" = now()
                WHERE
                    progress."ProfileId" = @ProfileId
                    AND progress."WorkId" = @WorkId
                    AND progress."ProgressPositionTypeId" IN (1, 2)
                    AND (@WorkUnitId::bigint IS NULL OR progress."WorkUnitId" IN (SELECT "WorkUnitId" FROM targets));
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            // Episodes that have aired (or are here) and tracks complete even without a progress; so does the film itself.
            sql = new SqlStatement();
            sql.Statement =
                """
                WITH RECURSIVE targets AS (
                    SELECT
                        unit."Id" AS "WorkUnitId",
                        unit."WorkUnitTypeId",
                        unit."ReleasedOn"
                    FROM
                        "WorkUnits" AS unit
                    WHERE
                        unit."WorkId" = @WorkId
                        AND (@WorkUnitId::bigint IS NULL OR unit."Id" = @WorkUnitId)
                    UNION ALL
                    SELECT
                        child."Id",
                        child."WorkUnitTypeId",
                        child."ReleasedOn"
                    FROM
                        "WorkUnits" AS child
                        JOIN targets ON child."ParentWorkUnitId" = targets."WorkUnitId"
                    WHERE
                        @WorkUnitId::bigint IS NOT NULL
                )
                INSERT INTO
                    "MediaProgress" ("ProfileId", "WorkId", "WorkUnitId", "ProgressPositionTypeId", "PositionMs", "IsCompleted", "CompletedAt")
                SELECT
                    @ProfileId,
                    @WorkId,
                    target."WorkUnitId",
                    1,
                    0,
                    TRUE,
                    now()
                FROM
                    targets AS target
                WHERE
                    target."WorkUnitTypeId" IN (2, 5)
                    AND (
                        coalesce(target."ReleasedOn" <= current_date, TRUE)
                        OR EXISTS (SELECT 1 FROM "MediaAssets" AS asset WHERE asset."WorkUnitId" = target."WorkUnitId")
                    )
                UNION ALL
                SELECT
                    @ProfileId,
                    work."Id",
                    NULL,
                    1,
                    0,
                    TRUE,
                    now()
                FROM
                    "Works" AS work
                WHERE
                    work."Id" = @WorkId
                    AND work."MediaTypeId" = 1
                    AND @WorkUnitId::bigint IS NULL
                ON CONFLICT ("ProfileId", "WorkId", "WorkUnitId") DO NOTHING;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // Nothing watched remains: neither the position nor the completion; a later play begins at the start.
        public static async Task MarkUnwatchedAsync(this LogicContext ctx, ProgressChangeData data, CancellationToken ct)
        {
            await ctx.RequireProgressTargetAsync(data, ct);

            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH RECURSIVE targets AS (
                    SELECT
                        unit."Id" AS "WorkUnitId"
                    FROM
                        "WorkUnits" AS unit
                    WHERE
                        unit."WorkId" = @WorkId
                        AND (@WorkUnitId::bigint IS NULL OR unit."Id" = @WorkUnitId)
                    UNION ALL
                    SELECT
                        child."Id"
                    FROM
                        "WorkUnits" AS child
                        JOIN targets ON child."ParentWorkUnitId" = targets."WorkUnitId"
                    WHERE
                        @WorkUnitId::bigint IS NOT NULL
                )
                DELETE FROM
                    "MediaProgress" AS progress
                WHERE
                    progress."ProfileId" = @ProfileId
                    AND progress."WorkId" = @WorkId
                    AND progress."ProgressPositionTypeId" IN (1, 2)
                    AND (@WorkUnitId::bigint IS NULL OR progress."WorkUnitId" IN (SELECT "WorkUnitId" FROM targets));
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // Only a Work the profile may open, and a unit of that Work.
        private static async Task RequireProgressTargetAsync(this LogicContext ctx, ProgressChangeData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id"
                FROM
                    "ProfileWorks" AS visible
                    JOIN "Works" AS work ON work."Id" = visible."WorkId"
                WHERE
                    visible."ProfileId" = @ProfileId
                    AND work."Id" = @WorkId
                    AND (
                        @WorkUnitId::bigint IS NULL
                        OR EXISTS (SELECT 1 FROM "WorkUnits" AS unit WHERE unit."WorkId" = work."Id" AND unit."Id" = @WorkUnitId)
                    )
                FOR SHARE OF work;
                """;
            sql.Parameters.Add(data);
            _ = await ctx.ReadRequiredAsync<ProgressWorkData>(sql, ct);
        }
    }
}
