using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Library
{
    internal static class StoredFileLogic
    {
        // PostgreSQL keeps microseconds; compared times must match what was stored.
        public static DateTime StoredTime(DateTime value) => DateTime.UnixEpoch.AddTicks((value.Ticks - DateTime.UnixEpoch.Ticks) / 10 * 10);

        public static Task<int> UpsertStoredFilesAsync(this LogicContext ctx, StoredFileBatchData data, CancellationToken ct)
        {
            // Changed files lose their analysis and take the new release facts; unchanged ones keep theirs, also after a rename.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt", "ReleaseName", "ReleaseGroup", "QualityKey", "Edition", "Revision")
                SELECT
                    @LibraryRootId,
                    item."RelativePath",
                    item."SizeBytes",
                    item."ModifiedAt",
                    @SeenAt,
                    item."ReleaseName",
                    item."ReleaseGroup",
                    item."QualityKey",
                    item."Edition",
                    item."Revision"
                FROM
                    jsonb_to_recordset(@Files::jsonb) AS item("RelativePath" text, "SizeBytes" bigint, "ModifiedAt" timestamptz, "ReleaseName" text, "ReleaseGroup" text, "QualityKey" text,
                        "Edition" text, "Revision" smallint)
                ON CONFLICT ("LibraryRootId", "RelativePath") DO UPDATE
                SET
                    "SizeBytes" = EXCLUDED."SizeBytes",
                    "ModifiedAt" = EXCLUDED."ModifiedAt",
                    "LastSeenAt" = EXCLUDED."LastSeenAt",
                    "MissingSince" = NULL,
                    "ReleaseName" = CASE
                        WHEN "StoredFiles"."SizeBytes" = EXCLUDED."SizeBytes" AND "StoredFiles"."ModifiedAt" = EXCLUDED."ModifiedAt" AND "StoredFiles"."ReleaseName" IS NOT NULL THEN "StoredFiles"."ReleaseName"
                        ELSE EXCLUDED."ReleaseName"
                    END,
                    "ReleaseGroup" = CASE
                        WHEN "StoredFiles"."SizeBytes" = EXCLUDED."SizeBytes" AND "StoredFiles"."ModifiedAt" = EXCLUDED."ModifiedAt" AND "StoredFiles"."ReleaseName" IS NOT NULL THEN "StoredFiles"."ReleaseGroup"
                        ELSE EXCLUDED."ReleaseGroup"
                    END,
                    "QualityKey" = CASE
                        WHEN "StoredFiles"."SizeBytes" = EXCLUDED."SizeBytes" AND "StoredFiles"."ModifiedAt" = EXCLUDED."ModifiedAt" AND "StoredFiles"."ReleaseName" IS NOT NULL THEN "StoredFiles"."QualityKey"
                        ELSE EXCLUDED."QualityKey"
                    END,
                    "Edition" = CASE
                        WHEN "StoredFiles"."SizeBytes" = EXCLUDED."SizeBytes" AND "StoredFiles"."ModifiedAt" = EXCLUDED."ModifiedAt" AND "StoredFiles"."ReleaseName" IS NOT NULL THEN "StoredFiles"."Edition"
                        ELSE EXCLUDED."Edition"
                    END,
                    "Revision" = CASE
                        WHEN "StoredFiles"."SizeBytes" = EXCLUDED."SizeBytes" AND "StoredFiles"."ModifiedAt" = EXCLUDED."ModifiedAt" AND "StoredFiles"."ReleaseName" IS NOT NULL THEN "StoredFiles"."Revision"
                        ELSE EXCLUDED."Revision"
                    END,
                    "ProbedAt" = CASE
                        WHEN "StoredFiles"."SizeBytes" = EXCLUDED."SizeBytes" AND "StoredFiles"."ModifiedAt" = EXCLUDED."ModifiedAt" THEN "StoredFiles"."ProbedAt"
                    END,
                    "PageCount" = CASE
                        WHEN "StoredFiles"."SizeBytes" = EXCLUDED."SizeBytes" AND "StoredFiles"."ModifiedAt" = EXCLUDED."ModifiedAt" THEN "StoredFiles"."PageCount"
                    END,
                    "PagesCheckedAt" = CASE
                        WHEN "StoredFiles"."SizeBytes" = EXCLUDED."SizeBytes" AND "StoredFiles"."ModifiedAt" = EXCLUDED."ModifiedAt" THEN "StoredFiles"."PagesCheckedAt"
                    END,
                    "Sha1" = CASE
                        WHEN "StoredFiles"."SizeBytes" = EXCLUDED."SizeBytes" AND "StoredFiles"."ModifiedAt" = EXCLUDED."ModifiedAt" THEN "StoredFiles"."Sha1"
                    END,
                    "TrickplayFrames" = CASE
                        WHEN "StoredFiles"."SizeBytes" = EXCLUDED."SizeBytes" AND "StoredFiles"."ModifiedAt" = EXCLUDED."ModifiedAt" THEN "StoredFiles"."TrickplayFrames"
                    END,
                    "TrickplayCheckedAt" = CASE
                        WHEN "StoredFiles"."SizeBytes" = EXCLUDED."SizeBytes" AND "StoredFiles"."ModifiedAt" = EXCLUDED."ModifiedAt" THEN "StoredFiles"."TrickplayCheckedAt"
                    END,
                    "SubtitlesCheckedAt" = CASE
                        WHEN "StoredFiles"."SizeBytes" = EXCLUDED."SizeBytes" AND "StoredFiles"."ModifiedAt" = EXCLUDED."ModifiedAt" THEN "StoredFiles"."SubtitlesCheckedAt"
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

        // Looked at now; a file changed meanwhile is looked at again.
        public static async Task SaveSubtitlesCheckedAsync(this LogicContext ctx, SubtitleCheckData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "StoredFiles" AS file
                SET
                    "SubtitlesCheckedAt" = now()
                WHERE
                    file."Id" = @Id
                    AND file."SizeBytes" = @SizeBytes
                    AND file."ModifiedAt" = @ModifiedAt;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // A fetched subtitle belongs to its video's asset at once.
        public static async Task SaveSubtitleFileAsync(this LogicContext ctx, SubtitleFileData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt", "MediaAssetId")
                VALUES
                    (@LibraryRootId, @RelativePath, @SizeBytes, @ModifiedAt, now(), @MediaAssetId)
                ON CONFLICT ("LibraryRootId", "RelativePath") DO NOTHING;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // The pages of a comic archive; a file changed meanwhile is counted again by the next scan.
        public static async Task SaveStoredFilePagesAsync(this LogicContext ctx, StoredFilePagesData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "StoredFiles" AS file
                SET
                    "PageCount" = @PageCount,
                    "PagesCheckedAt" = now()
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
                    "MediaTracks" ("StoredFileId", "StreamIndex", "MediaTrackTypeId", "Codec", "Language", "Title", "Width", "Height", "Channels", "BitDepth", "DynamicRange", "Is3D", "IsDefault", "IsForced")
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
                    track."BitDepth",
                    track."DynamicRange",
                    coalesce(track."Is3D", FALSE),
                    track."IsDefault",
                    track."IsForced"
                FROM
                    jsonb_to_recordset(@Tracks::jsonb) AS track("StreamIndex" integer, "MediaTrackTypeId" smallint, "Codec" text, "Language" text, "Title" text,
                        "Width" integer, "Height" integer, "Channels" integer, "BitDepth" integer, "DynamicRange" text, "Is3D" boolean, "IsDefault" boolean, "IsForced" boolean);
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
