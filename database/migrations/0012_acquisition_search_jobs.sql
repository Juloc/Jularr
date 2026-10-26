INSERT INTO "OperationTypes" ("Id", "Key") VALUES (6, 'AcquisitionSearch');
CREATE UNIQUE INDEX "UX_Operations_ActiveSearch" ON "Operations" ("OperationTypeId") WHERE "OperationTypeId" = 6 AND "OperationStatusTypeId" IN (1, 2);

CREATE TABLE "AcquisitionSearchRuns" (
    "OperationId" bigint PRIMARY KEY REFERENCES "Operations" ("Id") ON DELETE CASCADE,
    "SelectionKey" varchar(64) NOT NULL,
    "TargetCount" integer NOT NULL DEFAULT 0 CHECK ("TargetCount" >= 0),
    "IsCancelled" boolean NOT NULL DEFAULT FALSE
);
CREATE TABLE "AcquisitionSearchTargets" (
    "OperationId" bigint NOT NULL REFERENCES "AcquisitionSearchRuns" ("OperationId") ON DELETE CASCADE,
    "WantedItemId" bigint NOT NULL REFERENCES "WantedItems" ("Id") ON DELETE CASCADE,
    "OutcomeKey" varchar(64) NULL,
    PRIMARY KEY ("OperationId", "WantedItemId")
);
CREATE INDEX "IX_AcquisitionSearchTargets_Pending" ON "AcquisitionSearchTargets" ("OperationId", "WantedItemId") WHERE "OutcomeKey" IS NULL;

-- Shared list facts keep filtered searches and the visible library in agreement.
CREATE VIEW "WorkListEntries" AS
SELECT
    work."Id",
    work."MediaTypeId",
    work."MediaSubtypeId",
    work."CanonicalTitle",
    work."ReleaseYear"::integer AS "ReleaseYear",
    work."CreatedAt",
    target."Id" AS "WantedItemId",
    coalesce(target."Targets", 0) AS "TargetCount",
    coalesce(target."IsMonitored", FALSE) AS "IsMonitored",
    target."AcquisitionProfileId",
    target."LibraryRootId",
    profile."Name" AS "ProfileName",
    coalesce(targetRoot."Name", fileRoot."Name") AS "LibraryName",
    coalesce(files."FileCount", 0) AS "FileCount",
    coalesce(files."SizeBytes", 0) AS "SizeBytes",
    CASE
        WHEN download."IsDownloading" THEN 3
        WHEN files."FileCount" > 0 AND NOT EXISTS (
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
                        JOIN "StoredFiles" AS present ON present."MediaAssetId" = asset."Id" AND present."MissingSince" IS NULL
                    WHERE
                        asset."WorkUnitId" = unit."Id"
                )
        ) THEN 1
        WHEN coalesce(target."IsMonitored", FALSE) THEN 2
        ELSE 4
    END::smallint AS "WorkListStatusId"
FROM
    "Works" AS work
    LEFT JOIN LATERAL (
        SELECT
            (array_agg(wanted."Id" ORDER BY wanted."Id"))[1] AS "Id",
            (array_agg(wanted."AcquisitionProfileId" ORDER BY wanted."Id"))[1] AS "AcquisitionProfileId",
            (array_agg(wanted."LibraryRootId" ORDER BY wanted."Id"))[1] AS "LibraryRootId",
            count(*)::integer AS "Targets",
            bool_or(NOT wanted."IsPaused") AS "IsMonitored"
        FROM
            "WantedItems" AS wanted
        WHERE
            wanted."WorkId" = work."Id"
        HAVING
            count(*) > 0
    ) AS target ON TRUE
    LEFT JOIN "AcquisitionProfiles" AS profile ON profile."Id" = target."AcquisitionProfileId"
    LEFT JOIN "LibraryRoots" AS targetRoot ON targetRoot."Id" = target."LibraryRootId"
    LEFT JOIN LATERAL (
        SELECT
            count(*)::integer AS "FileCount",
            sum(file."SizeBytes")::bigint AS "SizeBytes",
            (array_agg(file."LibraryRootId" ORDER BY file."Id"))[1] AS "LibraryRootId"
        FROM
            "MediaAssets" AS asset
            JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id" AND file."MissingSince" IS NULL AND NOT file."IsSubtitle"
        WHERE
            asset."WorkId" = work."Id"
    ) AS files ON TRUE
    LEFT JOIN "LibraryRoots" AS fileRoot ON fileRoot."Id" = files."LibraryRootId"
    LEFT JOIN LATERAL (
        SELECT
            EXISTS (
                SELECT
                    1
                FROM
                    "WantedItems" AS wanted
                    JOIN "AcquisitionReleases" AS release ON release."WantedItemId" = wanted."Id"
                    JOIN "Operations" AS operation ON operation."AcquisitionReleaseId" = release."Id"
                WHERE
                    wanted."WorkId" = work."Id"
                    AND operation."OperationStatusTypeId" IN (1, 2)
            ) AS "IsDownloading"
    ) AS download ON TRUE;
