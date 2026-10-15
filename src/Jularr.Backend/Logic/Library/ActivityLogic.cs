using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Library
{
    // A Work's history: each event is added once with the texts of the moment and only leaves by the history's days and size.
    internal static class ActivityLogic
    {
        public static async Task RecordActivityAsync(this LogicContext ctx, ActivityEventData data, CancellationToken ct)
        {
            // The file names its Work and unit, a download its release, client and the account that asked for it.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "ActivityEvents" ("ActivityEventTypeId", "WorkId", "WorkUnitId", "AcquisitionReleaseId", "StoredFileId", "AccountId", "WorkTitle", "WorkUnitTypeId",
                        "ParentUnitNumber", "UnitNumber", "ReleaseTitle", "QualityKey", "IndexerName", "ClientName", "Path", "PreviousPath", "ReasonKey")
                SELECT
                    @ActivityEventTypeId,
                    work."Id",
                    unit."Id",
                    release."Id",
                    file."Id",
                    COALESCE(@AccountId, operation."RequestedByAccountId"),
                    work."CanonicalTitle",
                    unit."WorkUnitTypeId",
                    parent."Number",
                    unit."Number",
                    release."Title",
                    COALESCE(file."QualityKey", release."QualityKey"),
                    indexer."Name",
                    client."Name",
                    CASE WHEN file."Id" IS NOT NULL THEN concat_ws('/', NULLIF(root."RelativePath", ''), file."RelativePath") END,
                    CASE WHEN root."Id" IS NOT NULL THEN concat_ws('/', NULLIF(root."RelativePath", ''), @PreviousPath) ELSE @PreviousPath END,
                    @ReasonKey
                FROM
                    (SELECT 1) AS one
                    LEFT JOIN "Operations" AS operation ON operation."Id" = @OperationId
                    LEFT JOIN "AcquisitionReleases" AS release ON release."Id" = COALESCE(@AcquisitionReleaseId, operation."AcquisitionReleaseId")
                    LEFT JOIN "WantedItems" AS wanted ON wanted."Id" = release."WantedItemId"
                    LEFT JOIN "StoredFiles" AS file ON file."Id" = @StoredFileId
                    LEFT JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                    LEFT JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                    LEFT JOIN "WorkUnits" AS unit ON unit."Id" = asset."WorkUnitId"
                    LEFT JOIN "WorkUnits" AS parent ON parent."Id" = unit."ParentWorkUnitId"
                    LEFT JOIN "AcquisitionConnections" AS indexer ON indexer."Id" = release."AcquisitionConnectionId"
                    LEFT JOIN "AcquisitionConnections" AS client ON client."Id" = operation."AcquisitionConnectionId"
                    JOIN "Works" AS work ON work."Id" = COALESCE(@WorkId, asset."WorkId", wanted."WorkId");
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static Task<int> PruneActivityEventsAsync(this LogicContext ctx, CancellationToken ct)
        {
            // Older events leave by age or once the newer ones fill the history's room.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH kept AS (
                    SELECT
                        event."Id",
                        event."OccurredAt",
                        sum(pg_column_size(event.*)) OVER (ORDER BY event."OccurredAt" DESC, event."Id" DESC) AS "Bytes"
                    FROM
                        "ActivityEvents" AS event
                )
                DELETE FROM
                    "ActivityEvents" AS event
                USING
                    kept,
                    "InstanceSettings" AS settings
                WHERE
                    event."Id" = kept."Id"
                    AND settings."Id" = 1
                    AND (
                        kept."OccurredAt" < now() - make_interval(days => settings."HistoryRetentionDays")
                        OR kept."Bytes" > settings."HistoryMegabytes"::bigint * 1000000
                    );
                """;
            return ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
