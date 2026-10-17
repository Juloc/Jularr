using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    internal static class ArtworkSidecarLogic
    {
        // Only the Work identities already established by the canonical media inventory count.
        // A folder containing files from multiple Works is ambiguous and has no automatic image assignment.
        public static Task<int> UpsertSidecarsAsync(this LogicContext ctx, ArtworkScanBatchData batch, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH candidates AS (
                    SELECT
                        input."RelativePath",
                        input."Folder",
                        input."SlotId",
                        input."SeasonNumber"
                    FROM
                        jsonb_to_recordset(@Candidates::jsonb) AS input(
                            "RelativePath" text, "Folder" text, "SlotId" smallint, "SeasonNumber" integer
                        )
                    WHERE
                        length(input."RelativePath") BETWEEN 1 AND 2048
                        AND length(input."Folder") <= 2048
                        AND input."SlotId" BETWEEN 1 AND 5
                ),
                identified AS (
                    SELECT
                        image."RelativePath",
                        image."SlotId",
                        image."SeasonNumber",
                        matched."WorkId"
                    FROM
                        candidates AS image
                        JOIN LATERAL (
                            SELECT min(asset."WorkId") AS "WorkId"
                            FROM
                                "StoredFiles" AS file
                                JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                            WHERE
                                file."LibraryRootId" = @LibraryRootId
                                AND file."MissingSince" IS NULL
                                AND (
                                    image."Folder" = ''
                                    OR left(file."RelativePath", length(image."Folder") + 1) = image."Folder" || '/'
                                )
                            HAVING count(DISTINCT asset."WorkId") = 1
                        ) AS matched ON TRUE
                ),
                resolved AS (
                    SELECT
                        image."WorkId",
                        unit."Id" AS "WorkUnitId",
                        image."RelativePath",
                        image."SlotId"
                    FROM
                        identified AS image
                        LEFT JOIN "WorkUnits" AS unit
                            ON unit."WorkId" = image."WorkId"
                            AND unit."WorkUnitTypeId" = 1
                            AND unit."ParentWorkUnitId" IS NULL
                            AND unit."Number" = image."SeasonNumber"
                    WHERE
                        image."SeasonNumber" IS NULL OR unit."Id" IS NOT NULL
                )
                INSERT INTO
                    "WorkArtworks" ("WorkId", "WorkUnitId", "SlotId", "SourceKindId", "SourceKey", "LibraryRootId", "LastSeenAt")
                SELECT
                    image."WorkId", image."WorkUnitId", image."SlotId", 2, image."RelativePath", @LibraryRootId, @SeenAt
                FROM
                    resolved AS image
                ON CONFLICT ("WorkId", "WorkUnitId", "SlotId", "Locale", "SourceKindId", "SourceKey", "LibraryRootId")
                DO UPDATE SET
                    "LastSeenAt" = EXCLUDED."LastSeenAt"
                WHERE
                    "WorkArtworks"."LastSeenAt" IS DISTINCT FROM EXCLUDED."LastSeenAt";
                """;
            sql.Parameters.Add(batch);
            return ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // Do not prune after a partially failed or offline scan; keep pinned artwork even if files disappear.
        public static Task<int> CompleteSidecarScanAsync(this LogicContext ctx, ArtworkScanBatchData batch, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "WorkArtworks"
                WHERE
                    "SourceKindId" = 2
                    AND "LibraryRootId" = @LibraryRootId
                    AND NOT "IsPinned"
                    AND "LastSeenAt" IS DISTINCT FROM @SeenAt;
                """;
            sql.Parameters.Add(batch);
            return ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
