using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Acquisition
{
    internal static class WantedLogic
    {
        public static async Task RecordRequestTargetAsync(this LogicContext ctx, AcquisitionRequestData request, CancellationToken ct)
        {
            // One shared target per Work, per platform for games; a new target takes the requested quality, an existing one keeps its own.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH target AS (
                    INSERT INTO "WantedItems" (
                        "WorkId",
                        "GamePlatformId",
                        "AcquisitionProfileId"
                    )
                    SELECT
                        @WorkId,
                        @GamePlatformId,
                        request."AcquisitionProfileId"
                    FROM
                        "AcquisitionRequests" AS request
                    WHERE
                        request."Id" = @Id
                    ON CONFLICT ("WorkId", "GamePlatformId") DO UPDATE SET
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

        public static async Task EnsureWantedItemAsync(this LogicContext ctx, WorkIdData data, CancellationToken ct)
        {
            // A taken-over title becomes a target once.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO "WantedItems" (
                    "WorkId"
                ) VALUES (
                    @Id
                )
                ON CONFLICT ("WorkId", "GamePlatformId") DO NOTHING;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task UpdateWantedItemAsync(this LogicContext ctx, WantedItemUpdateData data, CancellationToken ct)
        {
            // Do not alter request decisions; a chosen profile and library fit the type and subtype.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "WantedItems" AS wanted
                SET
                    "IsPaused" = @IsPaused,
                    "AcquisitionProfileId" = @AcquisitionProfileId,
                    "LibraryRootId" = @LibraryRootId,
                    "UpdatedAt" = now(),
                    "Version" = wanted."Version" + 1
                FROM
                    "Works" AS work
                WHERE
                    wanted."Id" = @Id
                    AND wanted."Version" = @Version
                    AND work."Id" = wanted."WorkId"
                    AND (@AcquisitionProfileId::bigint IS NULL OR EXISTS (
                        SELECT
                            1
                        FROM
                            "AcquisitionProfiles" AS profile
                        WHERE
                            profile."Id" = @AcquisitionProfileId
                            AND profile."MediaTypeId" = work."MediaTypeId"
                            AND (profile."MediaSubtypeId" IS NULL OR profile."MediaSubtypeId" = work."MediaSubtypeId")
                    ))
                    AND (@LibraryRootId::bigint IS NULL OR EXISTS (
                        SELECT
                            1
                        FROM
                            "LibraryRoots" AS root
                        WHERE
                            root."Id" = @LibraryRootId
                            AND root."MediaTypeId" = work."MediaTypeId"
                            AND (root."MediaSubtypeId" IS NULL OR root."MediaSubtypeId" = work."MediaSubtypeId")
                    ));
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.Version), "invalid_transition");
            }
        }
    }
}
