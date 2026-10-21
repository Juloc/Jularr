using Jularr.Backend.Data.Logic.Worker;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Worker
{
    // What is kept of the background jobs: the Owner's choices, how the last pass went, and the notice of a job that fails.
    internal static class WorkerJobLogic
    {
        public static async Task<IReadOnlyList<WorkerJobRowData>> ReadWorkerJobsAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    job."Key",
                    job."IsPaused",
                    job."PausesWhileUsed" AS "PausesWhileUsedChoice",
                    job."LastStartedAt",
                    job."LastFinishedAt",
                    job."LastResultTypeId",
                    job."LastErrorKey",
                    job."NextRunAt"
                FROM
                    "WorkerJobs" AS job
                ORDER BY
                    job."Key";
                """;
            return await ctx.ReadSql.ReadListAsync<WorkerJobRowData>(sql, ct);
        }

        // A pass of a job: its outcome is kept, a failure leaves one notice until the job runs well again or a person dismisses it.
        public static async Task SaveWorkerJobRunAsync(this LogicContext ctx, WorkerJobRunData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "WorkerJobs" ("Key")
                VALUES
                    (@Key)
                ON CONFLICT ("Key") DO NOTHING;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            // Only the first failure in a row makes a notice; one a person dismissed does not come back with every pass.
            sql = new SqlStatement();
            sql.Statement =
                """
                WITH previous AS (
                    SELECT
                        job."LastResultTypeId"
                    FROM
                        "WorkerJobs" AS job
                    WHERE
                        job."Key" = @Key
                ), saved AS (
                    UPDATE
                        "WorkerJobs" AS job
                    SET
                        "LastStartedAt" = coalesce(@StartedAt, job."LastStartedAt"),
                        "LastFinishedAt" = @FinishedAt,
                        "LastResultTypeId" = @ResultTypeId,
                        "LastErrorKey" = @ErrorKey,
                        "NextRunAt" = @NextRunAt
                    WHERE
                        job."Key" = @Key
                    RETURNING
                        job."Key"
                ), noticed AS (
                    INSERT INTO
                        "Operations" ("OperationTypeId", "OperationStatusTypeId", "WorkerJobKey", "ErrorKey", "StartedAt", "FinishedAt")
                    SELECT
                        4,
                        4,
                        saved."Key",
                        @ErrorKey,
                        @StartedAt,
                        @FinishedAt
                    FROM
                        saved,
                        previous
                    WHERE
                        @ResultTypeId = 2
                        AND (
                            previous."LastResultTypeId" IS DISTINCT FROM 2
                            OR EXISTS (
                                SELECT
                                    1
                                FROM
                                    "Operations" AS existing
                                WHERE
                                    existing."WorkerJobKey" = saved."Key"
                                    AND existing."OperationTypeId" = 4
                                    AND existing."DismissedAt" IS NULL
                            )
                        )
                    ON CONFLICT ("WorkerJobKey") WHERE "OperationTypeId" = 4 AND "DismissedAt" IS NULL DO UPDATE
                    SET
                        "ErrorKey" = EXCLUDED."ErrorKey",
                        "FinishedAt" = EXCLUDED."FinishedAt"
                    RETURNING
                        "Id"
                )
                UPDATE
                    "Operations" AS notice
                SET
                    "DismissedAt" = now()
                FROM
                    saved
                WHERE
                    @ResultTypeId = 1
                    AND notice."WorkerJobKey" = saved."Key"
                    AND notice."OperationTypeId" = 4
                    AND notice."DismissedAt" IS NULL;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // The Owner pauses a job or sets whether it waits while Jularr is used; what is not given stays.
        public static async Task SaveWorkerJobChoiceAsync(this LogicContext ctx, WorkerJobChoiceData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "WorkerJobs" ("Key", "IsPaused", "PausesWhileUsed")
                VALUES
                    (@Key, coalesce(@IsPaused, FALSE), @PausesWhileUsedChoice)
                ON CONFLICT ("Key") DO UPDATE
                SET
                    "IsPaused" = coalesce(@IsPaused, "WorkerJobs"."IsPaused"),
                    "PausesWhileUsed" = coalesce(@PausesWhileUsedChoice, "WorkerJobs"."PausesWhileUsed");
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
