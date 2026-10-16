using Jularr.Backend.Data.Enums.Notification;
using Jularr.Backend.Data.Logic.Notification;
using Jularr.Backend.Data.Logic.Storage;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Notification;

namespace Jularr.Backend.Logic.Storage
{
    internal static class StorageMountStateLogic
    {
        // A failed mount is checked again after 1, 2, 5, 10 and then every 15 minutes; available resets the count, the Owner hears of the third failure.
        public static async Task SaveStorageMountProbeAsync(this LogicContext ctx, StorageMountProbeData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "StorageMountStates" AS state ("StorageMountId", "StorageAvailabilityTypeId", "DiagnosticCode", "CheckedAt", "AvailableAt", "FreeBytes", "TotalBytes", "FailedChecks", "NextCheckAt")
                VALUES
                    (@StorageMountId, @StorageAvailabilityTypeId, @DiagnosticCode, now(), CASE WHEN @StorageAvailabilityTypeId = 1 THEN now() END, @FreeBytes, @TotalBytes,
                        CASE WHEN @StorageAvailabilityTypeId = 1 THEN 0 ELSE 1 END, CASE WHEN @StorageAvailabilityTypeId <> 1 THEN now() + interval '1 minute' END)
                ON CONFLICT ("StorageMountId") DO UPDATE
                SET
                    "StorageAvailabilityTypeId" = EXCLUDED."StorageAvailabilityTypeId",
                    "DiagnosticCode" = EXCLUDED."DiagnosticCode",
                    "CheckedAt" = EXCLUDED."CheckedAt",
                    "AvailableAt" = coalesce(EXCLUDED."AvailableAt", state."AvailableAt"),
                    "FreeBytes" = EXCLUDED."FreeBytes",
                    "TotalBytes" = EXCLUDED."TotalBytes",
                    "FailedChecks" = CASE WHEN EXCLUDED."FailedChecks" = 0 THEN 0 ELSE state."FailedChecks" + 1 END,
                    "NextCheckAt" = CASE WHEN EXCLUDED."FailedChecks" > 0 THEN now() + make_interval(mins => (ARRAY[1, 2, 5, 10, 15])[least(state."FailedChecks" + 1, 5)]) END
                RETURNING
                    state."FailedChecks",
                    (SELECT mount."Name" FROM "StorageMounts" AS mount WHERE mount."Id" = state."StorageMountId") AS "Name";
                """;
            sql.Parameters.Add(data);
            var failure = await ctx.ReadRequiredAsync<StorageCheckFailureData>(sql, ct);
            if (failure.FailedChecks == 3)
            {
                await ctx.NotifyStaffAsync(new StaffNotificationData(NotificationType.StorageOffline, null, failure.Name, "/Admin/Storage?storageMountId=" + data.StorageMountId), ct);
            }
        }
    }
}
