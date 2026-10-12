using Jularr.Backend.Data.Logic.Operation;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Operation
{
    internal static class OperationLogic
    {
        public static async Task<long> QueueLibraryScanAsync(this LogicContext ctx, OperationCreationData data, CancellationToken ct)
        {
            // One active scan per root.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH inserted AS (
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
                    ON CONFLICT DO NOTHING
                    RETURNING
                        "Id",
                        "OperationTypeId",
                        "LibraryRootId"
                )
                SELECT
                    inserted."Id",
                    inserted."OperationTypeId",
                    inserted."LibraryRootId"
                FROM
                    inserted
                UNION ALL
                SELECT
                    operation."Id",
                    operation."OperationTypeId",
                    operation."LibraryRootId"
                FROM
                    "Operations" AS operation
                WHERE
                    operation."LibraryRootId" = @LibraryRootId
                    AND operation."OperationTypeId" = @OperationTypeId
                    AND operation."OperationStatusTypeId" IN (1, 2)
                LIMIT 1;
                """;
            sql.Parameters.Add(data);
            return (await ctx.ReadOptionalAsync<OperationData>(sql, ct) ?? throw new KeyNotFoundException("The library root was not found.")).Id;
        }

        public static Task<OperationData?> ClaimOperationAsync(this LogicContext ctx, CancellationToken ct)
        {
            // Oldest queued work first.
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
                            queued."OperationStatusTypeId" = 1
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
                    AND operation."OperationStatusTypeId" = 2;
                """;
            sql.Parameters.Add(data);
            return ctx.ExecuteAsyncForAffectedRows(sql, ct);
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
                    operation."OperationStatusTypeId" = 2;
                """;
            return ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
