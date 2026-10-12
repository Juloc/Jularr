using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Acquisition
{
    internal static class DownloadLogic
    {
        // Failed releases are skipped automatically; interrupted downloads may be retried.
        public const string FailedReasonKey = "download_failed";
        public const string InterruptedReasonKey = "download_interrupted";

        public static Task<WantedTargetData> ReadWantedTargetAsync(this LogicContext ctx, long wantedItemId, CancellationToken ct)
        {
            // Search facts of one target.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    wanted."Id" AS "WantedItemId",
                    work."Id" AS "WorkId",
                    work."MediaTypeId",
                    work."CanonicalTitle",
                    work."ReleaseYear"::integer AS "ReleaseYear",
                    ARRAY(
                        SELECT DISTINCT
                            title."Title"
                        FROM
                            "WorkTitles" AS title
                        WHERE
                            title."WorkId" = work."Id"
                            AND title."Title" <> work."CanonicalTitle"
                        ORDER BY
                            title."Title"
                    ) AS "Titles",
                    profile."Id" AS "AcquisitionProfileId",
                    profile."Name" AS "ProfileName",
                    profile."Definition"::text AS "Definition",
                    ARRAY(
                        SELECT
                            release."ReleaseIdentity"
                        FROM
                            "AcquisitionReleases" AS release
                        WHERE
                            release."WantedItemId" = wanted."Id"
                            AND (release."AcquisitionReleaseStatusTypeId" = 4
                                OR (release."AcquisitionReleaseStatusTypeId" = 3 AND release."ReasonKey" = 'download_failed'))
                    ) AS "ReleaseIdentities",
                    wanted."IsPaused",
                    EXISTS (
                        SELECT
                            1
                        FROM
                            "MediaAssets" AS asset
                        WHERE
                            asset."WorkId" = work."Id"
                    ) AS "HasMedia",
                    EXISTS (
                        SELECT
                            1
                        FROM
                            "AcquisitionReleases" AS release
                            JOIN "Operations" AS operation ON operation."AcquisitionReleaseId" = release."Id"
                        WHERE
                            release."WantedItemId" = wanted."Id"
                            AND operation."OperationStatusTypeId" IN (1, 2)
                    ) AS "IsDownloading",
                    wanted."SearchCount"
                FROM
                    "WantedItems" AS wanted
                    JOIN "Works" AS work ON work."Id" = wanted."WorkId"
                    LEFT JOIN LATERAL (
                        SELECT
                            candidate."Id",
                            candidate."Name",
                            candidate."Definition"
                        FROM
                            "AcquisitionProfiles" AS candidate
                        WHERE
                            candidate."MediaTypeId" = work."MediaTypeId"
                            AND (candidate."Id" = wanted."AcquisitionProfileId" OR (wanted."AcquisitionProfileId" IS NULL AND candidate."IsDefault"))
                        LIMIT 1
                    ) AS profile ON TRUE
                WHERE
                    wanted."Id" = @WantedItemId;
                """;
            sql.Parameters.Add(new WantedItemParameters(wantedItemId));
            return ctx.ReadSql.ReadRequiredAsync<WantedTargetData>(sql, ct);
        }

        public static Task<IReadOnlyList<WantedItemParameters>> ReadDueWantedItemsAsync(this LogicContext ctx, WantedDueData data, CancellationToken ct)
        {
            // Missing movies without a running download, oldest schedule first.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    wanted."Id" AS "WantedItemId"
                FROM
                    "WantedItems" AS wanted
                    JOIN "Works" AS work ON work."Id" = wanted."WorkId"
                WHERE
                    NOT wanted."IsPaused"
                    AND (wanted."NextSearchAt" IS NULL OR wanted."NextSearchAt" <= @Now)
                    AND work."MediaTypeId" = 1
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "MediaAssets" AS asset
                        WHERE
                            asset."WorkId" = work."Id"
                    )
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "AcquisitionReleases" AS release
                            JOIN "Operations" AS operation ON operation."AcquisitionReleaseId" = release."Id"
                        WHERE
                            release."WantedItemId" = wanted."Id"
                            AND operation."OperationStatusTypeId" IN (1, 2)
                    )
                ORDER BY
                    wanted."NextSearchAt" NULLS FIRST,
                    wanted."Id"
                LIMIT @Limit;
                """;
            sql.Parameters.Add(data);
            return ctx.ReadSql.ReadListAsync<WantedItemParameters>(sql, ct);
        }

        public static async Task RecordWantedSearchAsync(this LogicContext ctx, WantedSearchOutcomeData data, CancellationToken ct)
        {
            // Search state is not an editorial change.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "WantedItems" AS wanted
                SET
                    "SearchCount" = wanted."SearchCount" + 1,
                    "LastSearchedAt" = now(),
                    "NextSearchAt" = @NextSearchAt,
                    "LastOutcomeKey" = @OutcomeKey
                WHERE
                    wanted."Id" = @WantedItemId;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            await ctx.AdvanceRequestsAsync(new RequestProgressData(data.WantedItemId, AcquisitionRequestStatusType.Searching), ct);
        }

        public static async Task<long> RecordGrabAsync(this LogicContext ctx, AcquisitionGrabData data, CancellationToken ct)
        {
            // A retried release reuses its history row and download operation.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH release AS (
                    INSERT INTO
                        "AcquisitionReleases" ("WantedItemId", "AcquisitionConnectionId", "ReleaseIdentity", "Title", "QualityKey", "AcquisitionReleaseStatusTypeId")
                    VALUES
                        (@WantedItemId, @IndexerConnectionId, @ReleaseIdentity, @Title, @QualityKey, 1)
                    ON CONFLICT ("WantedItemId", "ReleaseIdentity") DO UPDATE
                    SET
                        "AcquisitionConnectionId" = EXCLUDED."AcquisitionConnectionId",
                        "Title" = EXCLUDED."Title",
                        "QualityKey" = EXCLUDED."QualityKey",
                        "AcquisitionReleaseStatusTypeId" = 1,
                        "ReasonKey" = NULL,
                        "UpdatedAt" = now()
                    RETURNING
                        "Id"
                )
                INSERT INTO
                    "Operations" ("OperationTypeId", "OperationStatusTypeId", "AcquisitionReleaseId", "AcquisitionConnectionId", "ExternalId", "StartedAt")
                SELECT
                    2,
                    2,
                    release."Id",
                    @DownloadConnectionId,
                    @ExternalId,
                    now()
                FROM
                    release
                ON CONFLICT ("AcquisitionReleaseId") WHERE "OperationTypeId" = 2 DO UPDATE
                SET
                    "OperationStatusTypeId" = 2,
                    "AcquisitionConnectionId" = EXCLUDED."AcquisitionConnectionId",
                    "ExternalId" = EXCLUDED."ExternalId",
                    "ProgressPercent" = NULL,
                    "ProcessedCount" = 0,
                    "ErrorKey" = NULL,
                    "StartedAt" = now(),
                    "FinishedAt" = NULL
                RETURNING
                    "Id" AS "OperationId";
                """;
            sql.Parameters.Add(data);
            var operation = await ctx.ReadRequiredAsync<DownloadOperationParameters>(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "WantedItems" AS wanted
                SET
                    "NextSearchAt" = NULL,
                    "LastOutcomeKey" = 'grabbed'
                WHERE
                    wanted."Id" = @WantedItemId;
                """;
            sql.Parameters.Add(new WantedItemParameters(data.WantedItemId));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            await ctx.AdvanceRequestsAsync(new RequestProgressData(data.WantedItemId, AcquisitionRequestStatusType.Downloading), ct);
            return operation.OperationId;
        }

        public static Task<IReadOnlyList<DownloadData>> ReadActiveDownloadsAsync(this LogicContext ctx, CancellationToken ct)
        {
            // Grouped by client for one queue and history read each.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    operation."Id" AS "OperationId",
                    release."WantedItemId",
                    work."Id" AS "WorkId",
                    work."MediaTypeId",
                    work."CanonicalTitle",
                    work."ReleaseYear"::integer AS "ReleaseYear",
                    operation."AcquisitionConnectionId",
                    operation."ExternalId",
                    release."Title",
                    operation."StartedAt"
                FROM
                    "Operations" AS operation
                    JOIN "AcquisitionReleases" AS release ON release."Id" = operation."AcquisitionReleaseId"
                    JOIN "WantedItems" AS wanted ON wanted."Id" = release."WantedItemId"
                    JOIN "Works" AS work ON work."Id" = wanted."WorkId"
                WHERE
                    operation."OperationTypeId" = 2
                    AND operation."OperationStatusTypeId" = 2
                    AND operation."AcquisitionConnectionId" IS NOT NULL
                ORDER BY
                    operation."AcquisitionConnectionId",
                    operation."Id";
                """;
            return ctx.ReadSql.ReadListAsync<DownloadData>(sql, ct);
        }

        public static async Task UpdateDownloadProgressAsync(this LogicContext ctx, DownloadProgressData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "Operations" AS operation
                SET
                    "ProgressPercent" = @ProgressPercent
                WHERE
                    operation."Id" = @OperationId
                    AND operation."OperationTypeId" = 2
                    AND operation."OperationStatusTypeId" = 2;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task FailDownloadAsync(this LogicContext ctx, DownloadFailureData data, CancellationToken ct)
        {
            // The target is searched again; only release faults exclude the release.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH failed AS (
                    UPDATE
                        "Operations" AS operation
                    SET
                        "OperationStatusTypeId" = 4,
                        "ErrorKey" = @ReasonKey,
                        "FinishedAt" = now()
                    WHERE
                        operation."Id" = @OperationId
                        AND operation."OperationTypeId" = 2
                        AND operation."OperationStatusTypeId" = 2
                    RETURNING
                        operation."AcquisitionReleaseId"
                ), released AS (
                    UPDATE
                        "AcquisitionReleases" AS release
                    SET
                        "AcquisitionReleaseStatusTypeId" = 3,
                        "ReasonKey" = @ReasonKey,
                        "UpdatedAt" = now()
                    FROM
                        failed
                    WHERE
                        release."Id" = failed."AcquisitionReleaseId"
                    RETURNING
                        release."WantedItemId"
                )
                UPDATE
                    "WantedItems" AS wanted
                SET
                    "NextSearchAt" = @NextSearchAt,
                    "LastOutcomeKey" = @ReasonKey
                FROM
                    released
                WHERE
                    wanted."Id" = released."WantedItemId";
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task CompleteDownloadAsync(this LogicContext ctx, DownloadOperationParameters data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH completed AS (
                    UPDATE
                        "Operations" AS operation
                    SET
                        "OperationStatusTypeId" = 3,
                        "ProgressPercent" = 100,
                        "FinishedAt" = now()
                    WHERE
                        operation."Id" = @OperationId
                        AND operation."OperationTypeId" = 2
                        AND operation."OperationStatusTypeId" = 2
                    RETURNING
                        operation."AcquisitionReleaseId"
                ), imported AS (
                    UPDATE
                        "AcquisitionReleases" AS release
                    SET
                        "AcquisitionReleaseStatusTypeId" = 2,
                        "ReasonKey" = NULL,
                        "UpdatedAt" = now()
                    FROM
                        completed
                    WHERE
                        release."Id" = completed."AcquisitionReleaseId"
                    RETURNING
                        release."WantedItemId"
                )
                UPDATE
                    "WantedItems" AS wanted
                SET
                    "NextSearchAt" = NULL,
                    "LastOutcomeKey" = 'imported'
                FROM
                    imported
                WHERE
                    wanted."Id" = imported."WantedItemId"
                RETURNING
                    wanted."Id" AS "WantedItemId";
                """;
            sql.Parameters.Add(data);
            if (await ctx.ReadOptionalAsync<WantedItemParameters>(sql, ct) is { } wanted)
            {
                await ctx.AdvanceRequestsAsync(new RequestProgressData(wanted.WantedItemId, AcquisitionRequestStatusType.Completed), ct);
            }
        }

        public static Task<LibraryRootLocationData?> ReadImportRootAsync(this LogicContext ctx, long workId, CancellationToken ct)
        {
            // The first library of the media type receives downloads.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    root."Id",
                    root."MediaTypeId",
                    root."StorageMountId",
                    mount."StorageProtocolTypeId",
                    mount."SharePath",
                    root."RelativePath"
                FROM
                    "Works" AS work
                    JOIN "LibraryRoots" AS root ON root."MediaTypeId" = work."MediaTypeId"
                    JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                WHERE
                    work."Id" = @Id
                    AND NOT mount."IsReadOnly"
                ORDER BY
                    root."Id"
                LIMIT 1;
                """;
            sql.Parameters.Add(new WorkIdData(workId));
            return ctx.ReadSql.ReadOptionalAsync<LibraryRootLocationData>(sql, ct);
        }

        public static Task<StoredFileIdData?> ReadStoredFileIdAsync(this LogicContext ctx, LibraryFilePathData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    file."Id"
                FROM
                    "StoredFiles" AS file
                WHERE
                    file."LibraryRootId" = @LibraryRootId
                    AND file."RelativePath" = @RelativePath
                    AND file."MediaAssetId" IS NULL;
                """;
            sql.Parameters.Add(data);
            return ctx.ReadOptionalAsync<StoredFileIdData>(sql, ct);
        }

        private static async Task AdvanceRequestsAsync(this LogicContext ctx, RequestProgressData data, CancellationToken ct)
        {
            // Approved requests follow their shared target forward only.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AcquisitionRequests" AS request
                SET
                    "AcquisitionRequestStatusTypeId" = @AcquisitionRequestStatusTypeId,
                    "UpdatedAt" = now(),
                    "Version" = request."Version" + 1
                FROM
                    "RequestTargets" AS target
                WHERE
                    target."WantedItemId" = @WantedItemId
                    AND request."Id" = target."AcquisitionRequestId"
                    AND request."AcquisitionRequestStatusTypeId" BETWEEN 2 AND @AcquisitionRequestStatusTypeId - 1;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
