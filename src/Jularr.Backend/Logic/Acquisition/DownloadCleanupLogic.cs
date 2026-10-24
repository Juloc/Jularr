using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Acquisition
{
    // The cleanup ledger is independent of finished Operations: pruning history cannot silently lose a remote deletion.
    internal sealed class DownloadCleanupTaskData : BaseData
    {
        public DownloadCleanupTaskData(long id, long acquisitionConnectionId, string externalId, string reason, int attemptCount, string? sourceFingerprint)
        {
            Id = id;
            AcquisitionConnectionId = acquisitionConnectionId;
            ExternalId = externalId;
            Reason = reason;
            AttemptCount = attemptCount;
            SourceFingerprint = sourceFingerprint;
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

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Fingerprint), false)]
        public string? SourceFingerprint { get; }
    }

    internal sealed class DownloadCleanupOperationData : BaseData
    {
        public DownloadCleanupOperationData(long operationId) => OperationId = operationId;
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long OperationId { get; }
    }

    internal sealed class DownloadCleanupKeyData : BaseData
    {
        public DownloadCleanupKeyData(long acquisitionConnectionId, string externalId, long? operationId = null, string? reason = null, string? sourceFingerprint = null)
        {
            AcquisitionConnectionId = acquisitionConnectionId;
            ExternalId = externalId;
            OperationId = operationId;
            Reason = reason;
            SourceFingerprint = sourceFingerprint;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long AcquisitionConnectionId { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.JobId))]
        public string ExternalId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id), false)]
        public long? OperationId { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Action), false)]
        public string? Reason { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Fingerprint), false)]
        public string? SourceFingerprint { get; }
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

    internal sealed class DownloadCleanupClientData : BaseData
    {
        public DownloadCleanupClientData(long acquisitionConnectionId) => AcquisitionConnectionId = acquisitionConnectionId;
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long AcquisitionConnectionId { get; }
    }

    internal sealed class DownloadCleanupStatusData : BaseData
    {
        public DownloadCleanupStatusData(string externalId, string reason, int attemptCount, DateTime retryAt, DateTime? completedAt, string? lastError)
        {
            ExternalId = externalId;
            Reason = reason;
            AttemptCount = attemptCount;
            RetryAt = retryAt;
            CompletedAt = completedAt;
            LastError = lastError;
        }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.JobId))]
        public string ExternalId { get; }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Action))]
        public string Reason { get; }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Page))]
        public int AttemptCount { get; }
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.RetryAt))]
        public DateTime RetryAt { get; }
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.RetryAt), false)]
        public DateTime? CompletedAt { get; }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Status), false)]
        public string? LastError { get; }
    }

    internal static class DownloadCleanupLogic
    {
        public static Task<IReadOnlyList<DownloadCleanupStatusData>> ReadDownloadCleanupStatusesAsync(
            this LogicContext ctx, DownloadCleanupClientData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT "ExternalId", "Reason", "AttemptCount", "RetryAt", "CompletedAt", "LastError"
                FROM "DownloadClientCleanups"
                WHERE "AcquisitionConnectionId" = @AcquisitionConnectionId
                ORDER BY "Id" DESC
                LIMIT 100;
                """;
            sql.Parameters.Add(data);
            return ctx.ReadSql.ReadListAsync<DownloadCleanupStatusData>(sql, ct);
        }

        // Crash recovery: completion is committed before the caller can enqueue its remote cleanup.
        // Rebuild missing ledger rows from finished Operations, but never touch attention/waiting imports.
        public static Task QueueMissedDownloadCleanupsAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO "DownloadClientCleanups" ("AcquisitionConnectionId", "ExternalId", "Reason", "OperationId")
                SELECT operation."AcquisitionConnectionId", operation."ExternalId", 'failed', operation."Id"
                FROM "Operations" AS operation
                JOIN "AcquisitionConnections" AS connection ON connection."Id" = operation."AcquisitionConnectionId"
                WHERE operation."OperationTypeId" = 2
                  AND operation."OperationStatusTypeId" = 4
                  AND operation."ExternalId" IS NOT NULL
                  AND operation."FinishedAt" >= (
                      SELECT version."AppliedAt" FROM "DatabaseSchemaVersions" AS version WHERE version."Version" = 2
                  )
                  AND connection."AcquisitionConnectionTypeId" = 2
                  AND operation."ErrorKey" = 'download_failed'
                  AND connection."RemovesFailed"
                  AND NOT EXISTS (
                    SELECT 1 FROM "DownloadClientCleanups" AS cleanup
                    WHERE cleanup."AcquisitionConnectionId" = operation."AcquisitionConnectionId"
                      AND cleanup."ExternalId" = operation."ExternalId"
                  )
                ORDER BY operation."Id" DESC
                LIMIT 100
                ON CONFLICT ("AcquisitionConnectionId", "ExternalId") DO NOTHING;
                """;
            return ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // Insert only for the corresponding enabled policy; a repeated callback is idempotent.
        public static Task QueueImportedCleanupAsync(this LogicContext ctx, DownloadCleanupKeyData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO "DownloadClientCleanups" ("AcquisitionConnectionId", "ExternalId", "Reason", "OperationId", "SourceFingerprint")
                SELECT connection."Id", @ExternalId, 'imported', @OperationId, @SourceFingerprint
                FROM "AcquisitionConnections" AS connection
                WHERE connection."Id" = @AcquisitionConnectionId
                  AND connection."AcquisitionConnectionTypeId" = 2
                  AND connection."RemovesCompleted"
                  AND @SourceFingerprint IS NOT NULL
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
                  AND operation."ErrorKey" = 'download_failed'
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
                SELECT "Id", "AcquisitionConnectionId", "ExternalId", "Reason", "AttemptCount", "SourceFingerprint"
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
