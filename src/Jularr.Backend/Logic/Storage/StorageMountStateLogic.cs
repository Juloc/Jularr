using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Logic.Storage;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Storage
{
    internal static class StorageMountStateLogic
    {
        // Retry delays after failed reconnects.
        private static readonly int[] ReconnectMinutes = [1, 2, 5, 10, 15];

        public static async Task SaveStorageMountProbeAsync(this LogicContext ctx, StorageMountProbeData data, CancellationToken ct)
        {
            // Available resets the backoff.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "StorageMountStates" ("StorageMountId", "StorageAvailabilityTypeId", "DiagnosticCode", "CheckedAt", "AvailableAt", "FreeBytes", "TotalBytes")
                VALUES
                    (@StorageMountId, @StorageAvailabilityTypeId, @DiagnosticCode, now(), CASE WHEN @StorageAvailabilityTypeId = 1 THEN now() END, @FreeBytes, @TotalBytes)
                ON CONFLICT ("StorageMountId") DO UPDATE
                SET
                    "StorageAvailabilityTypeId" = EXCLUDED."StorageAvailabilityTypeId",
                    "DiagnosticCode" = EXCLUDED."DiagnosticCode",
                    "CheckedAt" = EXCLUDED."CheckedAt",
                    "AvailableAt" = coalesce(EXCLUDED."AvailableAt", "StorageMountStates"."AvailableAt"),
                    "FreeBytes" = EXCLUDED."FreeBytes",
                    "TotalBytes" = EXCLUDED."TotalBytes",
                    "ReconnectAttempts" = CASE WHEN EXCLUDED."StorageAvailabilityTypeId" = 1 THEN 0 ELSE "StorageMountStates"."ReconnectAttempts" END,
                    "ReconnectAt" = CASE WHEN EXCLUDED."StorageAvailabilityTypeId" = 1 THEN NULL ELSE "StorageMountStates"."ReconnectAt" END;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // Disconnect forgets the observed state.
        public static async Task RequestStorageConnectionAsync(this LogicContext ctx, StorageConnectRequestData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "StorageMountStates" ("StorageMountId", "IsConnectRequested")
                VALUES
                    (@StorageMountId, @IsConnectRequested)
                ON CONFLICT ("StorageMountId") DO UPDATE
                SET
                    "IsConnectRequested" = EXCLUDED."IsConnectRequested",
                    "StorageAvailabilityTypeId" = CASE WHEN EXCLUDED."IsConnectRequested" THEN "StorageMountStates"."StorageAvailabilityTypeId" END,
                    "AvailableAt" = CASE WHEN EXCLUDED."IsConnectRequested" THEN "StorageMountStates"."AvailableAt" END,
                    "DiagnosticCode" = CASE WHEN EXCLUDED."IsConnectRequested" THEN "StorageMountStates"."DiagnosticCode" END,
                    "CheckedAt" = CASE WHEN EXCLUDED."IsConnectRequested" THEN "StorageMountStates"."CheckedAt" END,
                    "FreeBytes" = CASE WHEN EXCLUDED."IsConnectRequested" THEN "StorageMountStates"."FreeBytes" END,
                    "TotalBytes" = CASE WHEN EXCLUDED."IsConnectRequested" THEN "StorageMountStates"."TotalBytes" END,
                    "ReconnectAttempts" = 0,
                    "ReconnectAt" = NULL;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // Only while the Owner still wants the connection.
        public static async Task<bool> SaveStorageReconnectAsync(this LogicContext ctx, StorageMountProbeData probe, int failedAttempts, CancellationToken ct)
        {
            var available = probe.StorageAvailabilityTypeId == StorageAvailabilityType.Available;
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "StorageMountStates"
                SET
                    "StorageAvailabilityTypeId" = @StorageAvailabilityTypeId,
                    "DiagnosticCode" = @DiagnosticCode,
                    "CheckedAt" = now(),
                    "AvailableAt" = CASE WHEN @StorageAvailabilityTypeId = 1 THEN now() ELSE "AvailableAt" END,
                    "FreeBytes" = @FreeBytes,
                    "TotalBytes" = @TotalBytes,
                    "ReconnectAttempts" = @ReconnectAttempts,
                    "ReconnectAt" = CASE WHEN @ReconnectAttempts > 0 THEN now() + make_interval(mins => @DelayMinutes) END
                WHERE
                    "StorageMountId" = @StorageMountId
                    AND "IsConnectRequested";
                """;
            sql.Parameters.Add(probe);
            sql.Parameters.Add(new StorageReconnectAttemptData(available ? 0 : failedAttempts, ReconnectMinutes[Math.Clamp(failedAttempts, 1, ReconnectMinutes.Length) - 1]));
            return await ctx.ExecuteAsyncForAffectedRows(sql, ct) == 1;
        }
    }
}
