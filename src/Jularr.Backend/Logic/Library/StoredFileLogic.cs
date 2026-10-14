using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Library
{
    internal static class StoredFileLogic
    {
        public static Task<int> UpsertStoredFilesAsync(this LogicContext ctx, StoredFileBatchData data, CancellationToken ct)
        {
            // Changed files lose their analysis.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt")
                SELECT
                    @LibraryRootId,
                    item."RelativePath",
                    item."SizeBytes",
                    item."ModifiedAt",
                    @SeenAt
                FROM
                    jsonb_to_recordset(@Files::jsonb) AS item("RelativePath" text, "SizeBytes" bigint, "ModifiedAt" timestamptz)
                ON CONFLICT ("LibraryRootId", "RelativePath") DO UPDATE
                SET
                    "SizeBytes" = EXCLUDED."SizeBytes",
                    "ModifiedAt" = EXCLUDED."ModifiedAt",
                    "LastSeenAt" = EXCLUDED."LastSeenAt",
                    "MissingSince" = NULL,
                    "ProbedAt" = CASE
                        WHEN "StoredFiles"."SizeBytes" = EXCLUDED."SizeBytes" AND "StoredFiles"."ModifiedAt" = EXCLUDED."ModifiedAt" THEN "StoredFiles"."ProbedAt"
                    END,
                    "Sha1" = CASE
                        WHEN "StoredFiles"."SizeBytes" = EXCLUDED."SizeBytes" AND "StoredFiles"."ModifiedAt" = EXCLUDED."ModifiedAt" THEN "StoredFiles"."Sha1"
                    END,
                    "TrickplayFrames" = CASE
                        WHEN "StoredFiles"."SizeBytes" = EXCLUDED."SizeBytes" AND "StoredFiles"."ModifiedAt" = EXCLUDED."ModifiedAt" THEN "StoredFiles"."TrickplayFrames"
                    END,
                    "TrickplayCheckedAt" = CASE
                        WHEN "StoredFiles"."SizeBytes" = EXCLUDED."SizeBytes" AND "StoredFiles"."ModifiedAt" = EXCLUDED."ModifiedAt" THEN "StoredFiles"."TrickplayCheckedAt"
                    END;
                """;
            sql.Parameters.Add(data);
            return ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task CompleteStoredFileScanAsync(this LogicContext ctx, StoredFileBatchData data, CancellationToken ct)
        {
            // Unassigned files that vanished have no value.
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "StoredFiles" AS file
                WHERE
                    file."LibraryRootId" = @LibraryRootId
                    AND file."LastSeenAt" < @SeenAt
                    AND file."MediaAssetId" IS NULL;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            // Assigned files stay visible as missing.
            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "StoredFiles" AS file
                SET
                    "MissingSince" = @SeenAt
                WHERE
                    file."LibraryRootId" = @LibraryRootId
                    AND file."LastSeenAt" < @SeenAt
                    AND file."MissingSince" IS NULL;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task<bool> RenameStoredFileAsync(this LogicContext ctx, LibraryRenameData data, CancellationToken ct)
        {
            // The file keeps its identity, analysis and progress under the new name; a running scan keeps the old one.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "StoredFiles" AS file
                SET
                    "RelativePath" = @TargetPath
                WHERE
                    file."Id" = @StoredFileId
                    AND file."RelativePath" = @RelativePath
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "StoredFiles" AS other
                        WHERE
                            other."LibraryRootId" = file."LibraryRootId"
                            AND other."RelativePath" = @TargetPath
                    )
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "Operations" AS scan
                        WHERE
                            scan."LibraryRootId" = file."LibraryRootId"
                            AND scan."OperationTypeId" = 1
                            AND scan."OperationStatusTypeId" IN (1, 2)
                    );
                """;
            sql.Parameters.Add(data);
            return await ctx.ExecuteAsyncForAffectedRows(sql, ct) == 1;
        }

        public static async Task SaveTrickplayAsync(this LogicContext ctx, TrickplaySaveData data, CancellationToken ct)
        {
            // Frames of a file changed meanwhile are not kept; a failed attempt waits for a changed file.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "StoredFiles" AS file
                SET
                    "TrickplayFrames" = @TrickplayFrames,
                    "TrickplayCheckedAt" = now()
                WHERE
                    file."Id" = @Id
                    AND file."SizeBytes" = @SizeBytes
                    AND file."ModifiedAt" = @ModifiedAt;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task SaveStoredFileProbeAsync(this LogicContext ctx, StoredFileProbeData data, CancellationToken ct)
        {
            // Ignore analyses of files changed meanwhile.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "StoredFiles" AS file
                SET
                    "ProbedAt" = @ProbedAt,
                    "Container" = @Container,
                    "DurationMs" = @DurationMs,
                    "BitRate" = @BitRate
                WHERE
                    file."Id" = @Id
                    AND file."SizeBytes" = @SizeBytes
                    AND file."ModifiedAt" = @ModifiedAt;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                return;
            }

            sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "MediaTracks" AS track
                WHERE
                    track."StoredFileId" = @Id;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "MediaTracks" ("StoredFileId", "StreamIndex", "MediaTrackTypeId", "Codec", "Language", "Title", "Width", "Height", "Channels", "IsDefault", "IsForced")
                SELECT
                    @Id,
                    track."StreamIndex",
                    track."MediaTrackTypeId",
                    track."Codec",
                    track."Language",
                    track."Title",
                    track."Width",
                    track."Height",
                    track."Channels",
                    track."IsDefault",
                    track."IsForced"
                FROM
                    jsonb_to_recordset(@Tracks::jsonb) AS track("StreamIndex" integer, "MediaTrackTypeId" smallint, "Codec" text, "Language" text, "Title" text,
                        "Width" integer, "Height" integer, "Channels" integer, "IsDefault" boolean, "IsForced" boolean);
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
