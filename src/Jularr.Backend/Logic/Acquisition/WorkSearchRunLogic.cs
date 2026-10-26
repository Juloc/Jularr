using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Acquisition
{
    internal static class WorkSearchRunLogic
    {
        public static async Task<SearchRunStateData> QueueWorksSearchAsync(this LogicContext ctx, WorkSearchSelectionData data, CancellationToken ct)
        {
            // Serialize the short enqueue transaction; a duplicate click returns the same active run.
            var sql = new SqlStatement();
            sql.Statement = "SELECT pg_advisory_xact_lock(9461823);";
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    operation."Id" AS "OperationId",
                    run."TargetCount",
                    operation."ProcessedCount",
                    operation."OperationStatusTypeId",
                    run."IsCancelled",
                    run."SelectionKey"
                FROM "Operations" AS operation
                    JOIN "AcquisitionSearchRuns" AS run ON run."OperationId" = operation."Id"
                WHERE operation."OperationStatusTypeId" IN (1, 2)
                FOR UPDATE OF operation;
                """;
            if (await ctx.ReadOptionalAsync<SearchRunStateData>(sql, ct) is { } active)
            {
                if (active.SelectionKey != data.SelectionKey) throw new DataValidationException("WorkIds", "search_in_progress");
                return active;
            }

            // Snapshot matching targets in one statement, across all list pages. Monitoring stays unchanged.
            sql = new SqlStatement();
            sql.DisableJit = true;
            sql.Statement =
                """
                WITH operation AS (
                    INSERT INTO "Operations" ("OperationTypeId", "RequestedByAccountId")
                    VALUES (6, @AccountId)
                    RETURNING "Id"
                ), run AS (
                    INSERT INTO "AcquisitionSearchRuns" ("OperationId", "SelectionKey")
                    SELECT operation."Id", @SelectionKey FROM operation
                    RETURNING "OperationId"
                ), targets AS (
                    INSERT INTO "AcquisitionSearchTargets" ("OperationId", "WantedItemId")
                    SELECT run."OperationId", wanted."Id"
                    FROM run
                        CROSS JOIN "WorkListEntries" AS listed
                        JOIN "WantedItems" AS wanted ON wanted."WorkId" = listed."Id" AND NOT wanted."IsPaused"
                    WHERE (@WorkIds::bigint[] IS NULL OR listed."Id" = ANY(@WorkIds))
                        AND (@MediaTypeId::smallint IS NULL OR listed."MediaTypeId" = @MediaTypeId)
                        AND (@IsMonitored::boolean IS NULL OR listed."IsMonitored" = @IsMonitored)
                        AND (@WorkListStatusId::smallint IS NULL OR listed."WorkListStatusId" = @WorkListStatusId)
                        AND (@Query::text IS NULL OR strpos(lower(listed."CanonicalTitle"), lower(@Query)) > 0)
                        AND (@AcquisitionProfileId::bigint IS NULL OR listed."AcquisitionProfileId" = @AcquisitionProfileId)
                        AND (@LibraryRootId::bigint IS NULL OR listed."LibraryRootId" = @LibraryRootId OR EXISTS (
                            SELECT 1 FROM "MediaAssets" AS asset
                                JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id" AND file."MissingSince" IS NULL AND NOT file."IsSubtitle"
                            WHERE asset."WorkId" = listed."Id" AND file."LibraryRootId" = @LibraryRootId
                        ))
                    RETURNING "OperationId"
                )
                SELECT
                    run."OperationId",
                    count(targets."OperationId")::integer AS "TargetCount",
                    0::bigint AS "ProcessedCount",
                    1::smallint AS "OperationStatusTypeId",
                    FALSE AS "IsCancelled",
                    @SelectionKey AS "SelectionKey"
                FROM run
                    LEFT JOIN targets ON targets."OperationId" = run."OperationId"
                GROUP BY run."OperationId";
                """;
            sql.Parameters.Add(data);
            var queued = await ctx.ReadRequiredAsync<SearchRunStateData>(sql, ct);
            if (queued.TargetCount == 0) throw new DataValidationException("WorkIds", "no_search_targets");
            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE "AcquisitionSearchRuns" AS run
                SET "TargetCount" = (SELECT count(*) FROM "AcquisitionSearchTargets" AS target WHERE target."OperationId" = run."OperationId")
                WHERE run."OperationId" = @OperationId;
                """;
            sql.Parameters.Add(new SearchRunKeyData(queued.OperationId));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            return queued;
        }

        public static Task<SearchRunItemData?> ReadNextSearchTargetAsync(this LogicContext ctx, SearchRunKeyData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    target."WantedItemId",
                    account."Id" AS "AccountId",
                    account."AccountRoleTypeId",
                    account."IsEnabled"
                FROM "AcquisitionSearchTargets" AS target
                    JOIN "AcquisitionSearchRuns" AS run ON run."OperationId" = target."OperationId" AND NOT run."IsCancelled"
                    JOIN "Operations" AS operation ON operation."Id" = run."OperationId" AND operation."OperationStatusTypeId" = 2
                    JOIN "Accounts" AS account ON account."Id" = operation."RequestedByAccountId"
                WHERE target."OperationId" = @OperationId AND target."OutcomeKey" IS NULL
                ORDER BY target."WantedItemId"
                LIMIT 1;
                """;
            sql.Parameters.Add(data);
            return ctx.ReadSql.ReadOptionalAsync<SearchRunItemData>(sql, ct);
        }

        public static async Task RecordSearchTargetAsync(this LogicContext ctx, SearchRunProgressData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH finished AS (
                    UPDATE "AcquisitionSearchTargets" AS target
                    SET "OutcomeKey" = @OutcomeKey
                    WHERE target."OperationId" = @OperationId AND target."WantedItemId" = @WantedItemId AND target."OutcomeKey" IS NULL
                    RETURNING "OperationId"
                )
                UPDATE "Operations" AS operation
                SET "ProcessedCount" = operation."ProcessedCount" + 1,
                    "ProgressPercent" = LEAST(100, (operation."ProcessedCount" + 1) * 100 / GREATEST(1, run."TargetCount"))
                FROM finished
                    JOIN "AcquisitionSearchRuns" AS run ON run."OperationId" = finished."OperationId"
                WHERE operation."Id" = finished."OperationId" AND operation."OperationStatusTypeId" = 2;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task CancelWorksSearchAsync(this LogicContext ctx, SearchRunActionData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH cancelled AS (
                    UPDATE "AcquisitionSearchRuns" AS run
                    SET "IsCancelled" = TRUE
                    FROM "Operations" AS operation
                    WHERE run."OperationId" = @OperationId AND operation."Id" = run."OperationId" AND operation."RequestedByAccountId" = @AccountId
                        AND operation."OperationStatusTypeId" IN (1, 2)
                    RETURNING run."OperationId"
                )
                UPDATE "Operations" AS operation
                SET "OperationStatusTypeId" = 3,
                    "FinishedAt" = now()
                FROM cancelled
                WHERE operation."Id" = cancelled."OperationId";
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) == 0) throw new DataValidationException("OperationId", "invalid_transition");
        }

        public static Task<int> CompleteWorksSearchAsync(this LogicContext ctx, SearchRunKeyData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE "Operations" AS operation
                SET "OperationStatusTypeId" = 3, "FinishedAt" = now(),
                    "ProgressPercent" = 100
                WHERE operation."Id" = @OperationId AND operation."OperationTypeId" = 6 AND operation."OperationStatusTypeId" = 2;
                """;
            sql.Parameters.Add(data);
            return ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
