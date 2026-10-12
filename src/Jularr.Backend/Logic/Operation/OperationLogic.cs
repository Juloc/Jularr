using Jularr.Backend.Data.Logic.Operation;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Operation
{
    internal static class OperationLogic
    {
        public static async Task<long> QueueLibraryScanAsync(this LogicContext ctx, OperationCreationData data, CancellationToken ct)
        {
            // One active scan per root; the upsert always returns it.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "Operations" ("OperationTypeId", "LibraryRootId", "RequestedByAccountId")
                SELECT
                    @OperationTypeId,
                    root."Id",
                    @RequestedByAccountId
                FROM
                    "LibraryRoots" AS root
                WHERE
                    root."Id" = @LibraryRootId
                ON CONFLICT ("LibraryRootId") WHERE "OperationTypeId" = 1 AND "OperationStatusTypeId" IN (1, 2) DO UPDATE
                SET
                    "LibraryRootId" = EXCLUDED."LibraryRootId"
                RETURNING
                    "Id",
                    "OperationTypeId",
                    "LibraryRootId";
                """;
            sql.Parameters.Add(data);
            return (await ctx.ReadOptionalAsync<OperationData>(sql, ct) ?? throw new KeyNotFoundException("The library root was not found.")).Id;
        }

        public static Task<OperationData?> ClaimOperationAsync(this LogicContext ctx, CancellationToken ct)
        {
            // Oldest queued scan first; downloads are tracked by their client.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "Operations" AS operation
                SET
                    "OperationStatusTypeId" = 2,
                    "StartedAt" = now()
                WHERE
                    operation."Id" = (
                        SELECT
                            queued."Id"
                        FROM
                            "Operations" AS queued
                        WHERE
                            queued."OperationTypeId" = 1
                            AND queued."OperationStatusTypeId" = 1
                        ORDER BY
                            queued."CreatedAt",
                            queued."Id"
                        LIMIT 1
                        FOR UPDATE SKIP LOCKED
                    )
                RETURNING
                    operation."Id",
                    operation."OperationTypeId",
                    operation."LibraryRootId";
                """;
            return ctx.ReadOptionalAsync<OperationData>(sql, ct);
        }

        public static Task<int> UpdateOperationAsync(this LogicContext ctx, OperationProgressData data, CancellationToken ct)
        {
            // Finished states are final.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "Operations" AS operation
                SET
                    "OperationStatusTypeId" = @OperationStatusTypeId,
                    "ProcessedCount" = @ProcessedCount,
                    "ErrorKey" = @ErrorKey,
                    "FinishedAt" = CASE WHEN @OperationStatusTypeId IN (3, 4) THEN now() END
                WHERE
                    operation."Id" = @Id
                    AND operation."OperationTypeId" = 1
                    AND operation."OperationStatusTypeId" = 2;
                """;
            sql.Parameters.Add(data);
            return ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task DismissOperationAsync(this LogicContext ctx, OperationActionData data, CancellationToken ct)
        {
            // Only failures leave the To-Do list.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "Operations" AS operation
                SET
                    "DismissedAt" = now()
                WHERE
                    operation."Id" = @OperationId
                    AND operation."OperationStatusTypeId" = 4
                    AND operation."DismissedAt" IS NULL;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) == 0)
            {
                throw new KeyNotFoundException("The failed operation was not found.");
            }
        }

        public static async Task RetryOperationAsync(this LogicContext ctx, OperationActionData data, CancellationToken ct)
        {
            // Scans run again; downloads search their target now.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH failed AS (
                    SELECT
                        operation."Id",
                        operation."OperationTypeId",
                        operation."LibraryRootId",
                        operation."AcquisitionReleaseId"
                    FROM
                        "Operations" AS operation
                    WHERE
                        operation."Id" = @OperationId
                        AND operation."OperationStatusTypeId" = 4
                        AND operation."DismissedAt" IS NULL
                    FOR UPDATE
                ), scanned AS (
                    INSERT INTO
                        "Operations" ("OperationTypeId", "LibraryRootId", "RequestedByAccountId")
                    SELECT
                        1,
                        failed."LibraryRootId",
                        @AccountId
                    FROM
                        failed
                    WHERE
                        failed."OperationTypeId" = 1
                    ON CONFLICT ("LibraryRootId") WHERE "OperationTypeId" = 1 AND "OperationStatusTypeId" IN (1, 2) DO NOTHING
                ), searched AS (
                    UPDATE
                        "WantedItems" AS wanted
                    SET
                        "NextSearchAt" = now()
                    FROM
                        failed
                        JOIN "AcquisitionReleases" AS release ON release."Id" = failed."AcquisitionReleaseId"
                    WHERE
                        wanted."Id" = release."WantedItemId"
                )
                UPDATE
                    "Operations" AS operation
                SET
                    "DismissedAt" = now()
                FROM
                    failed
                WHERE
                    operation."Id" = failed."Id";
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) == 0)
            {
                throw new KeyNotFoundException("The failed operation was not found.");
            }
        }

        public static Task<int> RequeueInterruptedOperationsAsync(this LogicContext ctx, CancellationToken ct)
        {
            // A restarted host resumes interrupted work.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "Operations" AS operation
                SET
                    "OperationStatusTypeId" = 1,
                    "StartedAt" = NULL
                WHERE
                    operation."OperationTypeId" = 1
                    AND operation."OperationStatusTypeId" = 2;
                """;
            return ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
