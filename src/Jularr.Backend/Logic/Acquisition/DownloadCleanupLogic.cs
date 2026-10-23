using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Acquisition
{
    // The cleanup ledger is independent of finished Operations: pruning history cannot silently lose a remote deletion.
    internal sealed class DownloadCleanupTaskData : BaseData
    {
        public DownloadCleanupTaskData(long id, long acquisitionConnectionId, string externalId, string reason, int attemptCount)
        {
            Id = id;
            AcquisitionConnectionId = acquisitionConnectionId;
            ExternalId = externalId;
            Reason = reason;
            AttemptCount = attemptCount;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long Id { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long AcquisitionConnectionId { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.JobId))]
        public string ExternalId { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Action))]
        public string Reason { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Page))]
        public int AttemptCount { get; }
    }

    internal sealed class DownloadCleanupOperationData : BaseData
    {
        public DownloadCleanupOperationData(long operationId) => OperationId = operationId;
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long OperationId { get; }
    }

    internal sealed class DownloadCleanupKeyData : BaseData
    {
        public DownloadCleanupKeyData(long acquisitionConnectionId, string externalId, long? operationId = null, string? reason = null)
        {
            AcquisitionConnectionId = acquisitionConnectionId;
            ExternalId = externalId;
            OperationId = operationId;
            Reason = reason;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long AcquisitionConnectionId { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.JobId))]
        public string ExternalId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id), false)]
        public long? OperationId { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Action), false)]
        public string? Reason { get; }
    }

    internal sealed class DownloadCleanupAttemptData : BaseData
    {
        public DownloadCleanupAttemptData(long id, string? errorKey = null)
        {
            Id = id;
            ErrorKey = errorKey;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long Id { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Action), false)]
        public string? ErrorKey { get; }
    }

    internal static class DownloadCleanupLogic
    {
        // Insert only for the corresponding enabled policy; a repeated callback is idempotent.
        public static Task QueueImportedCleanupAsync(this LogicContext ctx, DownloadCleanupKeyData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO "DownloadClientCleanups" ("AcquisitionConnectionId", "ExternalId", "Reason", "OperationId")
                SELECT connection."Id", @ExternalId, 'imported', @OperationId
                FROM "AcquisitionConnections" AS connection
                WHERE connection."Id" = @AcquisitionConnectionId
                  AND connection."AcquisitionConnectionTypeId" = 2
                  AND connection."RemovesCompleted"
                ON CONFLICT ("AcquisitionConnectionId", "ExternalId") DO NOTHING;
                """;
            sql.Parameters.Add(data);
            return ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // Failure is already committed or recorded in the same batch. Never schedule deletion for a waiting import.
        public static Task QueueFailedCleanupAsync(this LogicContext ctx, DownloadCleanupOperationData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO "DownloadClientCleanups" ("AcquisitionConnectionId", "ExternalId", "Reason", "OperationId")
                SELECT connection."Id", operation."ExternalId", 'failed', operation."Id"
                FROM "Operations" AS operation
                JOIN "AcquisitionConnections" AS connection ON connection."Id" = operation."AcquisitionConnectionId"
                WHERE operation."Id" = @OperationId
                  AND operation."OperationTypeId" = 2
                  AND operation."OperationStatusTypeId" = 4
                  AND operation."ExternalId" IS NOT NULL
                  AND connection."AcquisitionConnectionTypeId" = 2
                  AND connection."RemovesFailed"
                ON CONFLICT ("AcquisitionConnectionId", "ExternalId") DO NOTHING;
                """;
            sql.Parameters.Add(data);
            return ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static Task<IReadOnlyList<DownloadCleanupTaskData>> ReadDueDownloadCleanupsAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT "Id", "AcquisitionConnectionId", "ExternalId", "Reason", "AttemptCount"
                FROM "DownloadClientCleanups"
                WHERE "CompletedAt" IS NULL
                  AND "RetryAt" <= now()
                  AND ("LeaseUntil" IS NULL OR "LeaseUntil" < now())
                ORDER BY "RetryAt", "Id"
                LIMIT 12;
                """;
            return ctx.ReadSql.ReadListAsync<DownloadCleanupTaskData>(sql, ct);
        }

        public static Task<bool> ClaimDownloadCleanupAsync(this LogicContext ctx, DownloadCleanupAttemptData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE "DownloadClientCleanups"
                SET "LeaseUntil" = now() + interval '2 minutes', "AttemptCount" = "AttemptCount" + 1
                WHERE "Id" = @Id AND "CompletedAt" IS NULL
                  AND "RetryAt" <= now() AND ("LeaseUntil" IS NULL OR "LeaseUntil" < now());
                """;
            sql.Parameters.Add(data);
            return ClaimedAsync(ctx, sql, ct);
        }

        private static async Task<bool> ClaimedAsync(LogicContext ctx, SqlStatement sql, CancellationToken ct) =>
            await ctx.ExecuteAsyncForAffectedRows(sql, ct) == 1;

        public static Task MarkDownloadCleanupDoneAsync(this LogicContext ctx, DownloadCleanupAttemptData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE "DownloadClientCleanups"
                SET "CompletedAt" = now(), "LeaseUntil" = NULL, "LastError" = NULL
                WHERE "Id" = @Id AND "CompletedAt" IS NULL;
                """;
            sql.Parameters.Add(data);
            return ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static Task RetryDownloadCleanupAsync(this LogicContext ctx, DownloadCleanupAttemptData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE "DownloadClientCleanups"
                SET "LastError" = @ErrorKey,
                    "LeaseUntil" = NULL,
                    "RetryAt" = now() + least(interval '1 minute' * power(2, least("AttemptCount", 8)), interval '4 hours')
                WHERE "Id" = @Id AND "CompletedAt" IS NULL;
                """;
            sql.Parameters.Add(data);
            return ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
