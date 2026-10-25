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
            // Serialize target changes by canonical Work, so concurrent requests cannot lose a season floor.
            var sql = new SqlStatement();
            sql.Statement = "SELECT \"Id\" FROM \"Works\" WHERE \"Id\" = @Id FOR UPDATE;";
            sql.Parameters.Add(new WorkIdData(request.WorkId));
            _ = await ctx.ReadRequiredAsync<WorkIdData>(sql, ct);

            sql = new SqlStatement();
            sql.Statement = """
                SELECT wanted."MinimumSeasonNumber"
                FROM "WantedItems" AS wanted
                WHERE wanted."WorkId" = @WorkId
                    AND wanted."GamePlatformId" IS NOT DISTINCT FROM @GamePlatformId;
                """;
            sql.Parameters.Add(request);
            var prior = await ctx.ReadOptionalAsync<ExistingSeasonScope>(sql, ct);

            // The requested earliest season is retained during moderation and only applied on approval.
            sql = new SqlStatement();
            sql.Statement =
                """
                WITH target AS (
                    INSERT INTO "WantedItems" (
                        "WorkId",
                        "GamePlatformId",
                        "AcquisitionProfileId",
                        "MinimumSeasonNumber"
                    )
                    SELECT
                        @WorkId,
                        @GamePlatformId,
                        request."AcquisitionProfileId",
                        coalesce(request."StartSeasonNumber", 1)
                    FROM
                        "AcquisitionRequests" AS request
                    WHERE
                        request."Id" = @Id
                    ON CONFLICT ("WorkId", "GamePlatformId") DO UPDATE SET
                        "WorkId" = EXCLUDED."WorkId",
                        "MinimumSeasonNumber" = least("WantedItems"."MinimumSeasonNumber", EXCLUDED."MinimumSeasonNumber")
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

            sql = new SqlStatement();
            sql.Statement = """
                SELECT "MinimumSeasonNumber" FROM "WantedItems"
                WHERE "WorkId" = @WorkId AND "GamePlatformId" IS NOT DISTINCT FROM @GamePlatformId;
                """;
            sql.Parameters.Add(request);
            var now = await ctx.ReadRequiredAsync<ExistingSeasonScope>(sql, ct);

            if (prior is null && now.MinimumSeasonNumber > 1)
            {
                // Fresh target: imported older seasons were initially marked as monitored by default.
                // There is no prior Wanted target with manually selected older seasons to disturb.
                sql = new SqlStatement();
                sql.Statement = """
                    UPDATE "WorkUnits" AS episode SET "IsMonitored" = FALSE
                    FROM "WorkUnits" AS season
                    WHERE episode."WorkId" = @WorkId AND episode."ParentWorkUnitId" = season."Id"
                        AND episode."WorkUnitTypeId" = 2 AND season."WorkUnitTypeId" = 1
                        AND season."Number" > 0 AND season."Number" < @MinimumSeasonNumber;
                    """;
                sql.Parameters.Add(new { request.WorkId, now.MinimumSeasonNumber });
                await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            }
            else if (prior is { } existing && now.MinimumSeasonNumber < existing.MinimumSeasonNumber)
            {
                // A second explicit requester broadens the shared acquisition intent.
                // Do not touch seasons outside this newly requested range.
                sql = new SqlStatement();
                sql.Statement = """
                    UPDATE "WorkUnits" AS episode SET "IsMonitored" = TRUE
                    FROM "WorkUnits" AS season
                    WHERE episode."WorkId" = @WorkId AND episode."ParentWorkUnitId" = season."Id"
                        AND episode."WorkUnitTypeId" = 2 AND season."WorkUnitTypeId" = 1
                        AND season."Number" >= @MinimumSeasonNumber
                        AND season."Number" < @OldMinimumSeasonNumber;
                    """;
                sql.Parameters.Add(new { request.WorkId, now.MinimumSeasonNumber, OldMinimumSeasonNumber = existing.MinimumSeasonNumber });
                await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            }
        }

        private sealed record ExistingSeasonScope(int MinimumSeasonNumber);

        // A target for a title that has none; one that exists stays as it is. Without the data asking for monitoring, a new target starts paused and the Owner unpauses it later.
        public static async Task<bool> EnsureWantedItemAsync(this LogicContext ctx, WantedTakeoverData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO "WantedItems" (
                    "WorkId",
                    "AcquisitionProfileId",
                    "LibraryRootId",
                    "IsPaused"
                )
                SELECT
                    work."Id",
                    profile."Id",
                    root."Id",
                    @IsPaused
                FROM
                    "Works" AS work
                    LEFT JOIN "AcquisitionProfiles" AS profile ON profile."Id" = @AcquisitionProfileId
                        AND profile."MediaTypeId" = work."MediaTypeId"
                        AND (profile."MediaSubtypeId" IS NULL OR profile."MediaSubtypeId" = work."MediaSubtypeId")
                    LEFT JOIN "LibraryRoots" AS root ON root."Id" = @LibraryRootId
                        AND root."MediaTypeId" = work."MediaTypeId"
                WHERE
                    work."Id" = @WorkId
                ON CONFLICT ("WorkId", "GamePlatformId") DO NOTHING;
                """;
            sql.Parameters.Add(data);
            return await ctx.ExecuteAsyncForAffectedRows(sql, ct) == 1;
        }

        public static async Task<bool> UpdateWantedItemAsync(this LogicContext ctx, WantedItemUpdateData data, CancellationToken ct)
        {
            if (data.TagIds is not null) await ctx.RequireAcquisitionTagsAsync(new AcquisitionTagSetData(data.TagIds), ct);

            // Optimistic version validation below guards the paused state used for this transition.
            var resumed = false;
            if (!data.IsPaused)
            {
                var current = new SqlStatement();
                current.Statement =
                    """
                    SELECT "IsPaused"
                    FROM "WantedItems"
                    WHERE "Id" = @Id AND "Version" = @Version;
                    """;
                current.Parameters.Add(data);
                resumed = await ctx.ReadSql.ExecuteScalarAsync(current, ct) is true;
            }

            // Do not alter request decisions; a chosen profile and library fit the type and subtype.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "WantedItems" AS wanted
                SET
                    "IsPaused" = @IsPaused,
                    "NextSearchAt" = CASE WHEN wanted."IsPaused" AND NOT @IsPaused THEN NULL ELSE wanted."NextSearchAt" END,
                    "AcquisitionProfileId" = @AcquisitionProfileId,
                    "LibraryRootId" = @LibraryRootId,
                    "MonitorsNewUnits" = coalesce(@MonitorsNewUnits, wanted."MonitorsNewUnits"),
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
            if (data.TagIds is null) return resumed;
            sql = new SqlStatement();
            sql.Statement =
                """
                WITH target AS MATERIALIZED (
                    SELECT work."Id" FROM "Works" AS work JOIN "WantedItems" AS wanted ON wanted."WorkId" = work."Id"
                    WHERE wanted."Id" = @Id FOR UPDATE OF work
                ), removed AS (
                    DELETE FROM "WorkTags" AS tagged USING target
                    WHERE tagged."WorkId" = target."Id" AND NOT (tagged."TagId" = ANY(@TagIds))
                )
                INSERT INTO "WorkTags" ("WorkId", "TagId")
                SELECT target."Id", tag FROM target CROSS JOIN unnest(@TagIds::bigint[]) AS tag
                ON CONFLICT ("WorkId", "TagId") DO NOTHING;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            return resumed;
        }

        // Priority is search state, not an editorial change: the targets keep their version and their schedule.
        public static async Task SetWantedPriorityAsync(this LogicContext ctx, WantedPriorityData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "WantedItems" AS wanted
                SET
                    "WantedPriorityTypeId" = @WantedPriorityTypeId
                WHERE
                    wanted."Id" = ANY(@WantedItemIds);
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != data.WantedItemIds.Length)
            {
                throw new KeyNotFoundException("A wanted target does not exist.");
            }
        }

        // The chosen targets that are not paused are due now, the highest priority first when the search runs; no choice leaves the due ones as they are.
        public static async Task StartWantedSearchAsync(this LogicContext ctx, WantedSearchStartData data, CancellationToken ct)
        {
            if (data.WantedItemIds.Length == 0)
            {
                return;
            }

            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "WantedItems" AS wanted
                SET
                    "NextSearchAt" = CASE WHEN wanted."IsPaused" THEN wanted."NextSearchAt" ELSE NULL END
                WHERE
                    wanted."Id" = ANY(@WantedItemIds);
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != data.WantedItemIds.Length)
            {
                throw new KeyNotFoundException("A wanted target does not exist.");
            }
        }

        // A season sets all its episodes; units of other Works and other unit types are not touched.
        public static async Task SetUnitMonitoringAsync(this LogicContext ctx, WantedUnitMonitoringData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "WorkUnits" AS unit
                SET
                    "IsMonitored" = @IsMonitored
                FROM
                    "WantedItems" AS wanted
                WHERE
                    wanted."Id" = @WantedItemId
                    AND unit."WorkId" = wanted."WorkId"
                    AND (unit."Id" = @WorkUnitId AND unit."WorkUnitTypeId" IN (1, 2, 3) OR unit."ParentWorkUnitId" = @WorkUnitId AND unit."WorkUnitTypeId" = 2);
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) == 0)
            {
                throw new KeyNotFoundException("The unit is not part of this wanted Work.");
            }

            // Newly monitored units are searched in the next cycle, not after the old backoff; unmonitoring may leave nothing missing.
            if (!data.IsMonitored)
            {
                await ctx.CompleteRequestsAsync(new WantedItemParameters(data.WantedItemId), ct);
            }
            else
            {
                sql = new SqlStatement();
                sql.Statement =
                    """
                    UPDATE
                        "WantedItems" AS wanted
                    SET
                        "NextSearchAt" = NULL
                    WHERE
                        wanted."Id" = @WantedItemId;
                    """;
                sql.Parameters.Add(data);
                await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            }
        }
    }
}
