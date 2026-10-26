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

        public static Task<int> QueueDueLibraryScansAsync(this LogicContext ctx, CancellationToken ct)
        {
            // The instance's interval; storage last probed offline or denied is not scanned, so a sleeping NAS stays asleep.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "Operations" ("OperationTypeId", "LibraryRootId")
                SELECT
                    1,
                    root."Id"
                FROM
                    "LibraryRoots" AS root
                    JOIN "InstanceSettings" AS settings ON settings."Id" = 1 AND settings."LibraryScanIntervalHours" > 0
                    LEFT JOIN "StorageMountStates" AS state ON state."StorageMountId" = root."StorageMountId"
                WHERE
                    coalesce(state."StorageAvailabilityTypeId", 1) = 1
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "Operations" AS operation
                        WHERE
                            operation."LibraryRootId" = root."Id"
                            AND operation."OperationTypeId" = 1
                            AND operation."CreatedAt" > now() - make_interval(hours => settings."LibraryScanIntervalHours")
                    )
                ON CONFLICT ("LibraryRootId") WHERE "OperationTypeId" = 1 AND "OperationStatusTypeId" IN (1, 2) DO NOTHING;
                """;
            return ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static Task<int> PruneFinishedOperationsAsync(this LogicContext ctx, CancellationToken ct)
        {
            // Finished work leaves the history by age or once the newer entries fill its room; running, queued and unacknowledged failed work stays.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH finished AS (
                    SELECT
                        operation."Id",
                        operation."FinishedAt",
                        sum(pg_column_size(operation.*)) OVER (ORDER BY operation."FinishedAt" DESC, operation."Id" DESC) AS "Bytes"
                    FROM
                        "Operations" AS operation
                    WHERE
                        operation."OperationStatusTypeId" = 3
                        OR operation."OperationStatusTypeId" = 4 AND operation."DismissedAt" IS NOT NULL
                )
                DELETE FROM
                    "Operations" AS operation
                USING
                    finished,
                    "InstanceSettings" AS settings
                WHERE
                    operation."Id" = finished."Id"
                    AND settings."Id" = 1
                    AND (
                        finished."FinishedAt" < now() - make_interval(days => settings."HistoryRetentionDays")
                        OR finished."Bytes" > settings."HistoryMegabytes"::bigint * 1000000
                    );
                """;
            return ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task<bool> QueueBackupAsync(this LogicContext ctx, OperationCreationData data, CancellationToken ct)
        {
            // One backup at a time.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "Operations" ("OperationTypeId", "RequestedByAccountId")
                VALUES
                    (3, @RequestedByAccountId)
                ON CONFLICT ("OperationTypeId") WHERE "OperationTypeId" = 3 AND "OperationStatusTypeId" IN (1, 2) DO NOTHING;
                """;
            sql.Parameters.Add(data);
            return await ctx.ExecuteAsyncForAffectedRows(sql, ct) == 1;
        }

        public static async Task<bool> QueueTranscodeBenchmarkAsync(this LogicContext ctx, OperationCreationData data, CancellationToken ct)
        {
            // One benchmark at a time.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "Operations" ("OperationTypeId", "RequestedByAccountId")
                VALUES
                    (5, @RequestedByAccountId)
                ON CONFLICT ("OperationTypeId") WHERE "OperationTypeId" = 5 AND "OperationStatusTypeId" IN (1, 2) DO NOTHING;
                """;
            sql.Parameters.Add(data);
            return await ctx.ExecuteAsyncForAffectedRows(sql, ct) == 1;
        }

        public static Task<OperationData?> ClaimOperationAsync(this LogicContext ctx, OperationClaimData data, CancellationToken ct)
        {
            // Oldest queued operation of the lane's kind first (scans and backups are claimed apart); downloads are tracked by their client.
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
                            queued."OperationTypeId" = @OperationTypeId
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
            sql.Parameters.Add(data);
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
                    "ProcessedCount" = CASE WHEN operation."OperationTypeId" = 6 AND @OperationStatusTypeId = 4 THEN operation."ProcessedCount" ELSE @ProcessedCount END,
                    "ErrorKey" = @ErrorKey,
                    "FinishedAt" = CASE WHEN @OperationStatusTypeId IN (3, 4) THEN now() END
                WHERE
                    operation."Id" = @Id
                    AND operation."OperationTypeId" IN (1, 3, 5, 6)
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
            // Scans and backups run again; downloads search their target now.
            var sql = new SqlStatement();
            sql.Statement = "SELECT pg_advisory_xact_lock(9461823) FROM \"Operations\" WHERE \"Id\" = @OperationId AND \"OperationTypeId\" = 6;";
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            sql = new SqlStatement();
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
                ), backedUp AS (
                    INSERT INTO
                        "Operations" ("OperationTypeId", "RequestedByAccountId")
                    SELECT
                        3,
                        @AccountId
                    FROM
                        failed
                    WHERE
                        failed."OperationTypeId" = 3
                    ON CONFLICT ("OperationTypeId") WHERE "OperationTypeId" = 3 AND "OperationStatusTypeId" IN (1, 2) DO NOTHING
                ), benchmarked AS (
                    INSERT INTO
                        "Operations" ("OperationTypeId", "RequestedByAccountId")
                    SELECT
                        5,
                        @AccountId
                    FROM
                        failed
                    WHERE
                        failed."OperationTypeId" = 5
                    ON CONFLICT ("OperationTypeId") WHERE "OperationTypeId" = 5 AND "OperationStatusTypeId" IN (1, 2) DO NOTHING
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
                    "DismissedAt" = CASE WHEN operation."OperationTypeId" = 6 THEN NULL ELSE now() END,
                    "OperationStatusTypeId" = CASE WHEN operation."OperationTypeId" = 6 THEN 1 ELSE operation."OperationStatusTypeId" END,
                    "StartedAt" = CASE WHEN operation."OperationTypeId" = 6 THEN NULL ELSE operation."StartedAt" END,
                    "FinishedAt" = CASE WHEN operation."OperationTypeId" = 6 THEN NULL ELSE operation."FinishedAt" END,
                    "ErrorKey" = CASE WHEN operation."OperationTypeId" = 6 THEN NULL ELSE operation."ErrorKey" END
                FROM
                    failed
                WHERE
                    operation."Id" = failed."Id"
                    AND (operation."OperationTypeId" <> 6 OR operation."RequestedByAccountId" = @AccountId AND NOT EXISTS (
                        SELECT 1 FROM "Operations" AS active WHERE active."OperationTypeId" = 6 AND active."OperationStatusTypeId" IN (1, 2)
                    ));
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
                    operation."OperationTypeId" IN (1, 3, 5, 6)
                    AND operation."OperationStatusTypeId" = 2;
                """;
            return ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
