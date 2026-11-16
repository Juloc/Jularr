using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Library
{
    // An administrator deletes a file as Sonarr does: at once, the unit is missing again and searched again unless the setting switches it off.
    internal static class FileDeletionLogic
    {
        public static Task<IReadOnlyList<DeletableFileData>> ReadDeletableFilesAsync(this LogicContext ctx, DeletableFilesQueryData data, CancellationToken ct)
        {
            // Files on read-only connections stay; a chosen media file takes the subtitles that extend its name with it.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    file."Id" AS "StoredFileId",
                    mount."Id" AS "StorageMountId",
                    root."RelativePath" AS "RootPath",
                    file."RelativePath",
                    file."MissingSince" IS NOT NULL AS "IsMissing",
                    NOT file."IsSubtitle" AS "IsMedia"
                FROM
                    "StoredFiles" AS chosen
                    JOIN "StoredFiles" AS file ON file."Id" = chosen."Id"
                        OR (
                            NOT chosen."IsSubtitle"
                            AND chosen."MediaAssetId" IS NOT NULL
                            AND file."IsSubtitle"
                            AND file."LibraryRootId" = chosen."LibraryRootId"
                            AND file."MediaAssetId" = chosen."MediaAssetId"
                            AND starts_with(file."RelativePath", regexp_replace(chosen."RelativePath", '\.[^./]*$', '') || '.')
                        )
                    JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                    JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                WHERE
                    chosen."Id" = ANY (@StoredFileIds)
                    AND NOT mount."IsReadOnly"
                ORDER BY
                    file."Id";
                """;
            sql.Parameters.Add(data);
            return ctx.ReadSql.ReadListAsync<DeletableFileData>(sql, ct);
        }

        // The media files that lie on a unit or, for a season, on its episodes; a file a scan no longer finds is left out, as the page does not count it.
        public static Task<IReadOnlyList<WorkUnitFileData>> ReadWorkUnitFilesAsync(this LogicContext ctx, WorkUnitFilesQueryData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    file."Id" AS "StoredFileId"
                FROM
                    "WorkUnits" AS unit
                    JOIN "MediaAssets" AS asset ON asset."WorkUnitId" = unit."Id"
                    JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id" AND NOT file."IsSubtitle" AND file."MissingSince" IS NULL
                WHERE
                    unit."WorkId" = @WorkId
                    AND (unit."Id" = @WorkUnitId OR unit."ParentWorkUnitId" = @WorkUnitId)
                ORDER BY
                    file."Id";
                """;
            sql.Parameters.Add(data);
            return ctx.ReadSql.ReadListAsync<WorkUnitFileData>(sql, ct);
        }

        // The unit must belong to the Work; its type tells whether it has a switch, the target is the one that wants the Work.
        public static async Task<WorkUnitTargetData> ReadWorkUnitTargetAsync(this LogicContext ctx, WorkUnitFilesQueryData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    unit."WorkUnitTypeId",
                    wanted."Id" AS "WantedItemId"
                FROM
                    "WorkUnits" AS unit
                    LEFT JOIN LATERAL (
                        SELECT
                            target."Id"
                        FROM
                            "WantedItems" AS target
                        WHERE
                            target."WorkId" = unit."WorkId"
                        ORDER BY
                            target."Id"
                        LIMIT 1
                    ) AS wanted ON TRUE
                WHERE
                    unit."Id" = @WorkUnitId
                    AND unit."WorkId" = @WorkId;
                """;
            sql.Parameters.Add(data);
            return await ctx.ReadSql.ReadOptionalAsync<WorkUnitTargetData>(sql, ct) ?? throw new KeyNotFoundException("The unit does not belong to the title.");
        }

        public static async Task ForgetDeletedFilesAsync(this LogicContext ctx, DeletedFilesData data, CancellationToken ct)
        {
            // A unit left without a file is missing again: its target is searched at once, and the unit is switched off when the setting says so.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH recycled AS (
                    INSERT INTO
                        "RecycledFiles" ("StorageMountId", "FolderPath", "RelativePath")
                    SELECT
                        item."StorageMountId",
                        item."FolderPath",
                        item."RecycledPath"
                    FROM
                        jsonb_to_recordset(@Files::jsonb) AS item("StorageMountId" bigint, "FolderPath" text, "RecycledPath" text)
                    WHERE
                        item."RecycledPath" IS NOT NULL
                    ON CONFLICT ("StorageMountId", "FolderPath", "RelativePath") DO UPDATE
                    SET
                        "RecycledAt" = now()
                ),
                gone AS (
                    SELECT
                        file."Id" AS "StoredFileId",
                        file."MediaAssetId"
                    FROM
                        jsonb_to_recordset(@Files::jsonb) AS item("StoredFileId" bigint)
                        JOIN "StoredFiles" AS file ON file."Id" = item."StoredFileId"
                ),
                removed AS (
                    DELETE FROM
                        "StoredFiles" AS file
                    USING
                        gone
                    WHERE
                        file."Id" = gone."StoredFileId"
                ),
                emptied AS (
                    SELECT
                        asset."Id",
                        asset."WorkId",
                        asset."WorkUnitId"
                    FROM
                        "MediaAssets" AS asset
                    WHERE
                        asset."Id" IN (
                            SELECT
                                gone."MediaAssetId"
                            FROM
                                gone
                            WHERE
                                gone."MediaAssetId" IS NOT NULL
                        )
                        AND NOT EXISTS (
                            SELECT
                                1
                            FROM
                                "StoredFiles" AS remaining
                            WHERE
                                remaining."MediaAssetId" = asset."Id"
                                AND remaining."Id" NOT IN (
                                    SELECT
                                        gone."StoredFileId"
                                    FROM
                                        gone
                                )
                        )
                ),
                assets AS (
                    DELETE FROM
                        "MediaAssets" AS asset
                    USING
                        emptied
                    WHERE
                        asset."Id" = emptied."Id"
                ),
                unmonitored AS (
                    UPDATE
                        "WorkUnits" AS unit
                    SET
                        "IsMonitored" = FALSE
                    FROM
                        emptied
                    WHERE
                        @UnmonitorsDeletedUnits
                        AND unit."Id" = emptied."WorkUnitId"
                )
                UPDATE
                    "WantedItems" AS wanted
                SET
                    "NextSearchAt" = now()
                WHERE
                    wanted."WorkId" IN (
                        SELECT
                            emptied."WorkId"
                        FROM
                            emptied
                    );
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
