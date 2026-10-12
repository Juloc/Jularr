using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Acquisition
{
    internal static class WantedLogic
    {
        public static async Task RecordRequestTargetAsync(this LogicContext ctx, AcquisitionRequestData request, CancellationToken ct)
        {
            // One shared target per Work.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH target AS (
                    INSERT INTO "WantedItems" (
                        "WorkId"
                    ) VALUES (
                        @WorkId
                    )
                    ON CONFLICT ("WorkId") DO UPDATE SET
                        "WorkId" = EXCLUDED."WorkId"
                    RETURNING
                        "Id"
                )
                INSERT INTO "RequestTargets" (
                    "AcquisitionRequestId",
                    "WantedItemId"
                )
                SELECT
                    @Id,
                    target."Id"
                FROM
                    target;
                """;
            sql.Parameters.Add(request);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new InvalidOperationException("The request target was not recorded.");
            }
        }

        public static async Task UpdateWantedItemAsync(this LogicContext ctx, WantedItemUpdateData data, CancellationToken ct)
        {
            // Do not alter request decisions.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "WantedItems" AS wanted
                SET
                    "IsPaused" = @IsPaused,
                    "UpdatedAt" = now(),
                    "Version" = wanted."Version" + 1
                WHERE
                    wanted."Id" = @Id
                    AND wanted."Version" = @Version;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.Version), "invalid_transition");
            }
        }
    }
}
