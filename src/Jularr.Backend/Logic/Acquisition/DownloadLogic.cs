using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Notification;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Notification;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Logic.Notification;

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
                    work."MediaSubtypeId",
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
                            JOIN "StoredFiles" AS available ON available."MediaAssetId" = asset."Id" AND available."MissingSince" IS NULL
                        WHERE
                            asset."WorkId" = work."Id"
                            AND (
                                wanted."GamePlatformId" IS NULL
                                OR EXISTS (
                                    SELECT
                                        1
                                    FROM
                                        "GameReleases" AS game
                                    WHERE
                                        game."WorkVersionId" = asset."WorkVersionId"
                                        AND game."GamePlatformId" = wanted."GamePlatformId"
                                )
                            )
                    ) AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "WorkUnits" AS unit
                        WHERE
                            unit."WorkId" = work."Id"
                            AND unit."IsMonitored" AND (unit."WorkUnitTypeId" = 2 OR unit."WorkUnitTypeId" = 3 AND work."MediaSubtypeId" = 2)
                            AND (unit."ReleasedOn" <= current_date OR unit."WorkUnitTypeId" = 3 AND unit."ReleasedOn" IS NULL)
                            AND NOT EXISTS (
                                SELECT
                                    1
                                FROM
                                    "MediaAssets" AS asset
                                    JOIN "StoredFiles" AS available ON available."MediaAssetId" = asset."Id" AND available."MissingSince" IS NULL
                                WHERE
                                    asset."WorkUnitId" = unit."Id"
                            )
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
                    wanted."SearchCount",
                    ARRAY(
                        SELECT
                            identity."Namespace" || ':' || identity."Value"
                        FROM
                            "WorkExternalIdentities" AS identity
                        WHERE
                            identity."WorkId" = work."Id"
                    ) AS "ExternalIdentities",
                    ARRAY(
                        SELECT DISTINCT
                            locale."Locale"
                        FROM
                            "RequestTargets" AS target
                            JOIN "AcquisitionRequests" AS request ON request."Id" = target."AcquisitionRequestId"
                            JOIN "UiLocales" AS locale ON locale."Id" = request."UiLocaleId"
                        WHERE
                            target."WantedItemId" = wanted."Id"
                            AND request."AcquisitionRequestStatusTypeId" BETWEEN 2 AND 6
                    ) AS "RequestedLocales",
                    (
                        SELECT
                            creator."Creator"
                        FROM
                            "WorkCreators" AS creator
                        WHERE
                            creator."WorkId" = work."Id"
                    ) AS "Author",
                    platform."Abbreviation" AS "PlatformAbbreviation",
                    platform."IgdbId" AS "PlatformIgdbId",
                    wanted."CreatedAt" AS "WantedSince",
                    (
                        SELECT
                            available."QualityKey"
                        FROM
                            "MediaAssets" AS asset
                            JOIN "StoredFiles" AS available ON available."MediaAssetId" = asset."Id" AND available."MissingSince" IS NULL
                            JOIN jsonb_array_elements_text(profile."Definition" -> 'QualityOrder') WITH ORDINALITY AS quality ("Key", "Position")
                                ON lower(quality."Key") = lower(available."QualityKey")
                        WHERE
                            asset."WorkId" = work."Id"
                            AND asset."WorkUnitId" IS NULL
                        ORDER BY
                            quality."Position"
                        LIMIT 1
                    ) AS "InstalledQuality",
                    ARRAY(
                        SELECT DISTINCT
                            edition."Title"
                        FROM
                            "WorkEditions" AS edition
                        WHERE
                            edition."WorkId" = work."Id"
                            AND edition."LanguageCode" = ANY(requested."Languages")
                            AND edition."Title" IS NOT NULL
                            AND edition."Title" <> work."CanonicalTitle"
                    ) AS "EditionTitles",
                    ARRAY(
                        SELECT DISTINCT
                            identity."Value"
                        FROM
                            "WorkEditions" AS edition
                            JOIN "WorkEditionExternalIdentities" AS identity ON identity."WorkEditionId" = edition."Id" AND identity."Namespace" = 'isbn'
                        WHERE
                            edition."WorkId" = work."Id"
                            AND edition."LanguageCode" = ANY(requested."Languages")
                    ) AS "Isbns",
                    work."OriginalLanguage"
                FROM
                    "WantedItems" AS wanted
                    JOIN "Works" AS work ON work."Id" = wanted."WorkId"
                    LEFT JOIN "GamePlatforms" AS platform ON platform."Id" = wanted."GamePlatformId"
                    LEFT JOIN LATERAL (
                        SELECT
                            array_agg(DISTINCT lower(split_part(locale."Locale", '-', 1))) AS "Languages"
                        FROM
                            "RequestTargets" AS target
                            JOIN "AcquisitionRequests" AS request ON request."Id" = target."AcquisitionRequestId"
                            JOIN "UiLocales" AS locale ON locale."Id" = request."UiLocaleId"
                        WHERE
                            target."WantedItemId" = wanted."Id"
                            AND request."AcquisitionRequestStatusTypeId" BETWEEN 2 AND 6
                    ) AS requested ON TRUE
                    LEFT JOIN LATERAL (
                        SELECT
                            candidate."Id",
                            candidate."Name",
                            candidate."Definition"
                        FROM
                            "AcquisitionProfiles" AS candidate
                        WHERE
                            candidate."MediaTypeId" = work."MediaTypeId"
                            AND (candidate."Id" = wanted."AcquisitionProfileId"
                                OR wanted."AcquisitionProfileId" IS NULL AND candidate."IsDefault" AND (candidate."MediaSubtypeId" IS NULL OR candidate."MediaSubtypeId" = work."MediaSubtypeId"))
                        ORDER BY
                            candidate."MediaSubtypeId" IS NULL
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
            // Whole Works without media, or below their cutoff, without a running download, and Works with unclaimed missing episodes or volumes, oldest schedule first.
            // Only enabled types with a library of their own are searched.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    wanted."Id" AS "WantedItemId"
                FROM
                    "WantedItems" AS wanted
                    JOIN "Works" AS work ON work."Id" = wanted."WorkId"
                    JOIN "WorkClasses" AS class ON class."WorkId" = work."Id"
                WHERE
                    NOT wanted."IsPaused"
                    AND EXISTS (
                        SELECT
                            1
                        FROM
                            "LibraryRoots" AS root
                            JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId" AND NOT mount."IsReadOnly"
                        WHERE
                            root."Id" = wanted."LibraryRootId"
                            OR root."MediaTypeId" = class."MediaTypeId" AND root."MediaSubtypeId" IS NOT DISTINCT FROM class."MediaSubtypeId"
                    )
                    AND (wanted."NextSearchAt" IS NULL OR wanted."NextSearchAt" <= @Now)
                    AND (
                        work."MediaTypeId" IN (1, 3, 6, 7)
                        AND NOT EXISTS (
                            SELECT
                                1
                            FROM
                                "WorkUnits" AS volume
                            WHERE
                                volume."WorkId" = work."Id"
                                AND volume."WorkUnitTypeId" = 3
                                AND work."MediaSubtypeId" = 2
                        )
                        AND NOT EXISTS (
                            SELECT
                                1
                            FROM
                                "MediaAssets" AS asset
                                JOIN "StoredFiles" AS available ON available."MediaAssetId" = asset."Id" AND available."MissingSince" IS NULL
                            WHERE
                                asset."WorkId" = work."Id"
                                AND (
                                    wanted."GamePlatformId" IS NULL
                                    OR EXISTS (
                                        SELECT
                                            1
                                        FROM
                                            "GameReleases" AS game
                                        WHERE
                                            game."WorkVersionId" = asset."WorkVersionId"
                                            AND game."GamePlatformId" = wanted."GamePlatformId"
                                    )
                                )
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
                        OR work."MediaTypeId" IN (1, 3, 6, 7)
                        AND EXISTS (
                            SELECT
                                1
                            FROM
                                "WantedBelowCutoff" AS below
                            WHERE
                                below."WantedItemId" = wanted."Id"
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
                        OR (work."MediaTypeId" = 2 OR work."MediaSubtypeId" = 2)
                        AND EXISTS (
                            SELECT
                                1
                            FROM
                                "WorkUnits" AS unit
                                LEFT JOIN "WorkUnits" AS parent ON parent."Id" = unit."ParentWorkUnitId"
                            WHERE
                                unit."WorkId" = work."Id"
                                AND unit."IsMonitored" AND (unit."WorkUnitTypeId" = 2 OR unit."WorkUnitTypeId" = 3 AND work."MediaSubtypeId" = 2)
                                AND (unit."ReleasedOn" <= current_date OR unit."WorkUnitTypeId" = 3 AND unit."ReleasedOn" IS NULL)
                                AND NOT EXISTS (
                                    SELECT
                                        1
                                    FROM
                                        "MediaAssets" AS asset
                                        JOIN "StoredFiles" AS available ON available."MediaAssetId" = asset."Id" AND available."MissingSince" IS NULL
                                    WHERE
                                        asset."WorkUnitId" = unit."Id"
                                )
                                AND NOT EXISTS (
                                    SELECT
                                        1
                                    FROM
                                        "AcquisitionReleases" AS covering
                                        JOIN "Operations" AS download ON download."AcquisitionReleaseId" = covering."Id"
                                    WHERE
                                        covering."WantedItemId" = wanted."Id"
                                        AND download."OperationStatusTypeId" IN (1, 2)
                                        AND covering."ParentUnitNumber" IS NOT DISTINCT FROM parent."Number"
                                        AND (covering."UnitStart" IS NULL OR unit."Number" BETWEEN covering."UnitStart" AND covering."UnitEnd")
                                )
                        )
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

        public static async Task<long> ReserveDownloadAsync(this LogicContext ctx, AcquisitionGrabData data, CancellationToken ct)
        {
            // The target row serializes concurrent grabs.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    wanted."Id" AS "WantedItemId"
                FROM
                    "WantedItems" AS wanted
                WHERE
                    wanted."Id" = @WantedItemId
                FOR UPDATE;
                """;
            sql.Parameters.Add(data);
            await ctx.ReadRequiredAsync<WantedItemParameters>(sql, ct);

            // A whole Work downloads once; episode and volume downloads must not overlap.
            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    wanted."Id" AS "WantedItemId"
                FROM
                    "WantedItems" AS wanted
                    JOIN "Works" AS work ON work."Id" = wanted."WorkId"
                    JOIN "AcquisitionReleases" AS release ON release."WantedItemId" = wanted."Id"
                    JOIN "Operations" AS operation ON operation."AcquisitionReleaseId" = release."Id"
                WHERE
                    wanted."Id" = @WantedItemId
                    AND operation."OperationStatusTypeId" IN (1, 2)
                    AND (
                        work."MediaTypeId" <> 2 AND work."MediaSubtypeId" IS DISTINCT FROM 2
                        OR release."ParentUnitNumber" IS NULL AND release."UnitStart" IS NULL
                        OR @ParentUnitNumber::integer IS NULL AND @UnitStart::integer IS NULL
                        OR release."ParentUnitNumber" IS NOT DISTINCT FROM @ParentUnitNumber::integer
                        AND (release."UnitStart" IS NULL OR @UnitStart::integer IS NULL OR release."UnitStart" <= @UnitEnd AND @UnitStart <= release."UnitEnd")
                    )
                LIMIT 1;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ReadOptionalAsync<WantedItemParameters>(sql, ct) is not null)
            {
                throw new DataValidationException("CandidateId", "in_use");
            }

            // A retried release reuses its history row and finished download operation.
            sql = new SqlStatement();
            sql.Statement =
                """
                WITH release AS (
                    INSERT INTO
                        "AcquisitionReleases" ("WantedItemId", "AcquisitionConnectionId", "ReleaseIdentity", "Title", "SourceUrl", "QualityKey", "ParentUnitNumber", "UnitStart", "UnitEnd",
                            "AcquisitionReleaseStatusTypeId")
                    VALUES
                        (@WantedItemId, @IndexerConnectionId, @ReleaseIdentity, @Title, @SourceUrl, @QualityKey, @ParentUnitNumber, @UnitStart, @UnitEnd, 1)
                    ON CONFLICT ("WantedItemId", "ReleaseIdentity") DO UPDATE
                    SET
                        "SourceUrl" = EXCLUDED."SourceUrl",
                        "AcquisitionConnectionId" = EXCLUDED."AcquisitionConnectionId",
                        "Title" = EXCLUDED."Title",
                        "QualityKey" = EXCLUDED."QualityKey",
                        "ParentUnitNumber" = EXCLUDED."ParentUnitNumber",
                        "UnitStart" = EXCLUDED."UnitStart",
                        "UnitEnd" = EXCLUDED."UnitEnd",
                        "AcquisitionReleaseStatusTypeId" = 1,
                        "ReasonKey" = NULL,
                        "UpdatedAt" = now()
                    RETURNING
                        "Id"
                )
                INSERT INTO
                    "Operations" ("OperationTypeId", "OperationStatusTypeId", "AcquisitionReleaseId", "AcquisitionConnectionId", "RequestedByAccountId", "StartedAt")
                SELECT
                    2,
                    1,
                    release."Id",
                    @DownloadConnectionId,
                    @RequestedByAccountId,
                    now()
                FROM
                    release
                ON CONFLICT ("AcquisitionReleaseId") WHERE "OperationTypeId" = 2 DO UPDATE
                SET
                    "OperationStatusTypeId" = 1,
                    "AcquisitionConnectionId" = EXCLUDED."AcquisitionConnectionId",
                    "RequestedByAccountId" = EXCLUDED."RequestedByAccountId",
                    "ExternalId" = NULL,
                    "ProgressPercent" = NULL,
                    "SizeBytes" = NULL,
                    "RemainingBytes" = NULL,
                    "EtaSeconds" = NULL,
                    "DownloadStateTypeId" = NULL,
                    "ProcessedCount" = 0,
                    "ErrorKey" = NULL,
                    "AttentionKey" = NULL,
                    "AttentionUrl" = NULL,
                    "SourceLinkUrl" = NULL,
                    "DismissedAt" = NULL,
                    "StartedAt" = now(),
                    "FinishedAt" = NULL
                WHERE
                    "Operations"."OperationStatusTypeId" IN (3, 4)
                RETURNING
                    "Id" AS "OperationId";
                """;
            sql.Parameters.Add(data);
            var operation = await ctx.ReadOptionalAsync<DownloadOperationParameters>(sql, ct) ?? throw new DataValidationException("CandidateId", "in_use");
            await ctx.RecordActivityAsync(new ActivityEventData(ActivityEventType.Grabbed, operationId: operation.OperationId), ct);
            return operation.OperationId;
        }

        public static async Task StartDownloadAsync(this LogicContext ctx, DownloadStartData data, CancellationToken ct)
        {
            // The client accepted the reserved download.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH started AS (
                    UPDATE
                        "Operations" AS operation
                    SET
                        "OperationStatusTypeId" = 2,
                        "ExternalId" = @ExternalId
                    WHERE
                        operation."Id" = @OperationId
                        AND operation."OperationTypeId" = 2
                        AND operation."OperationStatusTypeId" = 1
                    RETURNING
                        operation."AcquisitionReleaseId"
                )
                UPDATE
                    "WantedItems" AS wanted
                SET
                    "NextSearchAt" = NULL,
                    "LastOutcomeKey" = 'grabbed'
                FROM
                    started
                    JOIN "AcquisitionReleases" AS release ON release."Id" = started."AcquisitionReleaseId"
                WHERE
                    wanted."Id" = release."WantedItemId"
                RETURNING
                    wanted."Id" AS "WantedItemId";
                """;
            sql.Parameters.Add(data);
            if (await ctx.ReadOptionalAsync<WantedItemParameters>(sql, ct) is { } wanted)
            {
                await ctx.AdvanceRequestsAsync(new RequestProgressData(wanted.WantedItemId, AcquisitionRequestStatusType.Downloading), ct);
            }
        }

        public static Task<IReadOnlyList<UnitStateData>> ReadUnitStatesAsync(this LogicContext ctx, long wantedItemId, CancellationToken ct)
        {
            // Episodes of regular seasons and Manga volumes; covered means a running download contains the unit.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    unit."Id" AS "WorkUnitId",
                    parent."Number"::integer AS "ParentNumber",
                    unit."Number"::integer AS "Number",
                    COALESCE(unit."ReleasedOn" <= current_date, unit."WorkUnitTypeId" = 3) AS "IsAvailable",
                    EXISTS (
                        SELECT
                            1
                        FROM
                            "MediaAssets" AS asset
                            JOIN "StoredFiles" AS available ON available."MediaAssetId" = asset."Id" AND available."MissingSince" IS NULL
                        WHERE
                            asset."WorkUnitId" = unit."Id"
                    ) AS "HasMedia",
                    EXISTS (
                        SELECT
                            1
                        FROM
                            "AcquisitionReleases" AS covering
                            JOIN "Operations" AS download ON download."AcquisitionReleaseId" = covering."Id"
                        WHERE
                            covering."WantedItemId" = wanted."Id"
                            AND download."OperationStatusTypeId" IN (1, 2)
                            AND covering."ParentUnitNumber" IS NOT DISTINCT FROM parent."Number"
                            AND (covering."UnitStart" IS NULL OR unit."Number" BETWEEN covering."UnitStart" AND covering."UnitEnd")
                    ) AS "IsCovered",
                    CASE WHEN unit."WorkUnitTypeId" = 2 AND parent."Number" > 0 THEN (
                        SELECT
                            count(*)::integer
                        FROM
                            "WorkUnits" AS earlier
                            JOIN "WorkUnits" AS season ON season."Id" = earlier."ParentWorkUnitId"
                        WHERE
                            earlier."WorkId" = work."Id"
                            AND earlier."WorkUnitTypeId" = 2
                            AND season."Number" > 0
                            AND (season."Number", earlier."Number") <= (parent."Number", unit."Number")
                    ) END AS "AbsoluteNumber",
                    unit."ReleasedOn"
                FROM
                    "WantedItems" AS wanted
                    JOIN "Works" AS work ON work."Id" = wanted."WorkId"
                    JOIN "WorkUnits" AS unit ON unit."WorkId" = work."Id"
                    LEFT JOIN "WorkUnits" AS parent ON parent."Id" = unit."ParentWorkUnitId"
                WHERE
                    wanted."Id" = @WantedItemId
                    AND unit."IsMonitored" AND (unit."WorkUnitTypeId" = 2 OR unit."WorkUnitTypeId" = 3 AND work."MediaSubtypeId" = 2)
                    AND unit."Number" = trunc(unit."Number")
                ORDER BY
                    parent."Number" NULLS FIRST,
                    unit."Number";
                """;
            sql.Parameters.Add(new WantedItemParameters(wantedItemId));
            return ctx.ReadSql.ReadListAsync<UnitStateData>(sql, ct);
        }

        public static Task<IReadOnlyList<DownloadData>> ReadActiveDownloadsAsync(this LogicContext ctx, CancellationToken ct)
        {
            // Grouped by client for one queue and history read each; a source that waits for a person is not asked again.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    operation."Id" AS "OperationId",
                    release."WantedItemId",
                    work."Id" AS "WorkId",
                    work."MediaTypeId",
                    work."MediaSubtypeId",
                    work."CanonicalTitle",
                    work."ReleaseYear"::integer AS "ReleaseYear",
                    operation."AcquisitionConnectionId",
                    operation."ExternalId",
                    release."Title",
                    operation."StartedAt",
                    release."QualityKey",
                    release."SourceUrl",
                    operation."SourceLinkUrl",
                    (
                        SELECT
                            count(*)::integer
                        FROM
                            "WorkUnits" AS track
                        WHERE
                            track."WorkId" = work."Id"
                            AND track."WorkUnitTypeId" = 5
                    ) AS "TrackCount",
                    platform."Id" AS "GamePlatformId",
                    platform."Name" AS "PlatformName",
                    (
                        SELECT
                            creator."Creator"
                        FROM
                            "WorkCreators" AS creator
                        WHERE
                            creator."WorkId" = work."Id"
                    ) AS "Author"
                FROM
                    "Operations" AS operation
                    JOIN "AcquisitionReleases" AS release ON release."Id" = operation."AcquisitionReleaseId"
                    JOIN "WantedItems" AS wanted ON wanted."Id" = release."WantedItemId"
                    JOIN "Works" AS work ON work."Id" = wanted."WorkId"
                    LEFT JOIN "GamePlatforms" AS platform ON platform."Id" = wanted."GamePlatformId"
                WHERE
                    operation."OperationTypeId" = 2
                    AND operation."OperationStatusTypeId" IN (1, 2)
                    AND operation."AcquisitionConnectionId" IS NOT NULL
                    AND operation."AttentionUrl" IS NULL
                ORDER BY
                    operation."AcquisitionConnectionId",
                    operation."Id";
                """;
            return ctx.ReadSql.ReadListAsync<DownloadData>(sql, ct);
        }

        // Client job states in Jularr terms.
        public static DownloadStateType ReadDownloadState(string? status) => status switch
        {
            "Paused" => DownloadStateType.Paused,
            "Queued" => DownloadStateType.Queued,
            _ => DownloadStateType.Downloading
        };

        public static async Task UpdateDownloadProgressAsync(this LogicContext ctx, DownloadProgressData data, CancellationToken ct)
        {
            // One write per client pass; unchanged state is not rewritten.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "Operations" AS operation
                SET
                    "ProgressPercent" = coalesce(item."ProgressPercent", operation."ProgressPercent"),
                    "SizeBytes" = coalesce(item."SizeBytes", operation."SizeBytes"),
                    "RemainingBytes" = item."RemainingBytes",
                    "EtaSeconds" = item."EtaSeconds",
                    "DownloadStateTypeId" = item."DownloadStateTypeId"
                FROM
                    jsonb_to_recordset(@Progress::jsonb) AS item ("OperationId" bigint, "ProgressPercent" smallint, "SizeBytes" bigint, "RemainingBytes" bigint, "EtaSeconds" integer,
                        "DownloadStateTypeId" smallint)
                WHERE
                    operation."Id" = item."OperationId"
                    AND operation."OperationTypeId" = 2
                    AND operation."OperationStatusTypeId" = 2
                    AND (operation."ProgressPercent", operation."SizeBytes", operation."RemainingBytes", operation."EtaSeconds", operation."DownloadStateTypeId")
                        IS DISTINCT FROM (coalesce(item."ProgressPercent", operation."ProgressPercent"), coalesce(item."SizeBytes", operation."SizeBytes"), item."RemainingBytes",
                        item."EtaSeconds", item."DownloadStateTypeId");
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
                        "FinishedAt" = now(),
                        "AttentionKey" = NULL,
                        "AttentionUrl" = NULL,
                        "SourceLinkUrl" = NULL
                    WHERE
                        operation."Id" = @OperationId
                        AND operation."OperationTypeId" = 2
                        AND operation."OperationStatusTypeId" IN (1, 2)
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
                    wanted."Id" = released."WantedItemId"
                RETURNING
                    wanted."WorkId",
                    (SELECT work."CanonicalTitle" FROM "Works" AS work WHERE work."Id" = wanted."WorkId") AS "Title";
                """;
            sql.Parameters.Add(data);
            if (await ctx.ReadOptionalAsync<FailedWorkData>(sql, ct) is { } failed)
            {
                await ctx.RecordActivityAsync(new ActivityEventData(ActivityEventType.DownloadFailed, operationId: data.OperationId, reasonKey: data.ReasonKey), ct);
                await ctx.NotifyStaffAsync(new StaffNotificationData(NotificationType.DownloadFailed, failed.WorkId, failed.Title, "/Admin/Activity"), ct);
            }
        }

        public static async Task SetDownloadAttentionAsync(this LogicContext ctx, DownloadAttentionData data, CancellationToken ct)
        {
            // A waiting import or source tells the Owner why; unchanged reasons are not rewritten.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "Operations" AS operation
                SET
                    "AttentionKey" = @AttentionKey,
                    "AttentionUrl" = @AttentionUrl,
                    "SourceLinkUrl" = NULL
                WHERE
                    operation."Id" = @OperationId
                    AND operation."OperationTypeId" = 2
                    AND operation."OperationStatusTypeId" IN (1, 2)
                    AND (
                        operation."AttentionKey" IS DISTINCT FROM @AttentionKey
                        OR operation."AttentionUrl" IS DISTINCT FROM @AttentionUrl
                        OR operation."SourceLinkUrl" IS NOT NULL
                    );
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task MarkAcquisitionReleaseAsync(this LogicContext ctx, AcquisitionReleaseMarkData data, CancellationToken ct)
        {
            // Blocked releases are never taken; allowed ones may be taken again.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AcquisitionReleases" AS release
                SET
                    "AcquisitionReleaseStatusTypeId" = @AcquisitionReleaseStatusTypeId,
                    "UpdatedAt" = now()
                WHERE
                    release."Id" = @AcquisitionReleaseId
                    AND release."WantedItemId" = @WantedItemId
                    AND CASE @AcquisitionReleaseStatusTypeId
                        WHEN 4 THEN release."AcquisitionReleaseStatusTypeId" IN (2, 3, 5)
                        WHEN 5 THEN release."AcquisitionReleaseStatusTypeId" = 4 OR release."AcquisitionReleaseStatusTypeId" = 3 AND release."ReasonKey" = 'download_failed'
                        ELSE FALSE
                    END;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.AcquisitionReleaseStatusTypeId), "invalid_transition");
            }
        }

        public static async Task SetSourceLinkAsync(this LogicContext ctx, SourceLinkData data, CancellationToken ct)
        {
            // Files are fetched over HTTPS only.
            if (!Uri.TryCreate(data.SourceLinkUrl, UriKind.Absolute, out var link) || link.Scheme != Uri.UriSchemeHttps)
            {
                throw new DataValidationException(nameof(data.SourceLinkUrl), "invalid_url");
            }

            // Only a download that waits for a person takes a link; a user only for their own request while instance and account allow it.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "Operations" AS operation
                SET
                    "AttentionKey" = NULL,
                    "AttentionUrl" = NULL,
                    "SourceLinkUrl" = @SourceLinkUrl
                WHERE
                    operation."Id" = @OperationId
                    AND operation."OperationTypeId" = 2
                    AND operation."OperationStatusTypeId" IN (1, 2)
                    AND operation."AttentionUrl" IS NOT NULL
                    AND (
                        @AcquisitionRequestId::bigint IS NULL
                        OR EXISTS (
                            SELECT
                                1
                            FROM
                                "AcquisitionReleases" AS release
                                JOIN "RequestTargets" AS target ON target."WantedItemId" = release."WantedItemId"
                                JOIN "AcquisitionRequests" AS request ON request."Id" = target."AcquisitionRequestId"
                                JOIN "ProfileWorks" AS visible ON visible."WorkId" = request."WorkId" AND visible."ProfileId" = request."RequestedByProfileId"
                                JOIN "InstanceSettings" AS settings ON settings."UsersConfirmSources"
                                JOIN "Accounts" AS account ON account."Id" = @AccountId AND account."MayConfirmSources"
                            WHERE
                                release."Id" = operation."AcquisitionReleaseId"
                                AND request."Id" = @AcquisitionRequestId
                                AND request."RequestedByProfileId" = @ProfileId
                        )
                    );
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw data.AcquisitionRequestId is null ? new DataValidationException(nameof(data.OperationId), "invalid_transition") : new KeyNotFoundException("No waiting download may be confirmed.");
            }
        }

        public static async Task<bool> IsDownloadFinishedAsync(this LogicContext ctx, DownloadOperationParameters data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    operation."OperationStatusTypeId" IN (3, 4) AS "IsFinished"
                FROM
                    "Operations" AS operation
                WHERE
                    operation."Id" = @OperationId;
                """;
            sql.Parameters.Add(data);
            return (await ctx.ReadSql.ReadOptionalAsync<DownloadFinishedData>(sql, ct))?.IsFinished ?? true;
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
                        "FinishedAt" = now(),
                        "AttentionKey" = NULL,
                        "AttentionUrl" = NULL,
                        "SourceLinkUrl" = NULL
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
            if (await ctx.ReadOptionalAsync<WantedItemParameters>(sql, ct) is not { } wanted)
            {
                return;
            }

            // Requesters hear about every arrival, not only the last one.
            await ctx.NotifyRequestersAsync(new WantedNotificationData(wanted.WantedItemId, NotificationType.MediaAvailable), ct);
            await ctx.CompleteRequestsAsync(wanted, ct);
        }

        // Requests complete once their Work has media and no monitored aired episode or volume is missing.
        internal static async Task CompleteRequestsAsync(this LogicContext ctx, WantedItemParameters data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AcquisitionRequests" AS request
                SET
                    "AcquisitionRequestStatusTypeId" = 6,
                    "UpdatedAt" = now(),
                    "Version" = request."Version" + 1
                FROM
                    "RequestTargets" AS target
                    JOIN "WantedItems" AS wanted ON wanted."Id" = target."WantedItemId"
                    JOIN "Works" AS work ON work."Id" = wanted."WorkId"
                WHERE
                    target."WantedItemId" = @WantedItemId
                    AND request."Id" = target."AcquisitionRequestId"
                    AND request."AcquisitionRequestStatusTypeId" BETWEEN 2 AND 5
                    AND EXISTS (
                        SELECT
                            1
                        FROM
                            "MediaAssets" AS asset
                            JOIN "StoredFiles" AS available ON available."MediaAssetId" = asset."Id" AND available."MissingSince" IS NULL
                        WHERE
                            asset."WorkId" = work."Id"
                    )
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "WorkUnits" AS unit
                        WHERE
                            unit."WorkId" = work."Id"
                            AND unit."IsMonitored" AND (unit."WorkUnitTypeId" = 2 OR unit."WorkUnitTypeId" = 3 AND work."MediaSubtypeId" = 2)
                            AND (unit."ReleasedOn" <= current_date OR unit."WorkUnitTypeId" = 3 AND unit."ReleasedOn" IS NULL)
                            AND NOT EXISTS (
                                SELECT
                                    1
                                FROM
                                    "MediaAssets" AS asset
                                    JOIN "StoredFiles" AS available ON available."MediaAssetId" = asset."Id" AND available."MissingSince" IS NULL
                                WHERE
                                    asset."WorkUnitId" = unit."Id"
                            )
                    );
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static Task<LibraryRootLocationData?> ReadImportRootAsync(this LogicContext ctx, long wantedItemId, CancellationToken ct)
        {
            // The target's chosen library, else the first one of its type and enabled subtype.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    root."Id",
                    root."MediaTypeId",
                    root."MediaSubtypeId",
                    root."StorageMountId",
                    mount."StorageProtocolTypeId",
                    mount."SharePath",
                    root."RelativePath"
                FROM
                    "WantedItems" AS wanted
                    JOIN "Works" AS work ON work."Id" = wanted."WorkId"
                    LEFT JOIN "WorkClasses" AS class ON class."WorkId" = work."Id"
                    JOIN "LibraryRoots" AS root ON root."MediaTypeId" = work."MediaTypeId"
                    JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                WHERE
                    wanted."Id" = @WantedItemId
                    AND NOT mount."IsReadOnly"
                    AND (root."Id" = wanted."LibraryRootId" OR root."MediaSubtypeId" IS NOT DISTINCT FROM CASE WHEN class."WorkId" IS NULL THEN work."MediaSubtypeId" ELSE class."MediaSubtypeId" END)
                ORDER BY
                    root."Id" = wanted."LibraryRootId" DESC NULLS LAST,
                    root."Id"
                LIMIT 1;
                """;
            sql.Parameters.Add(new WantedItemParameters(wantedItemId));
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
