-- Downloader completion is independent of library import; retain each real post-processing stage across restarts.
ALTER TABLE "Operations" DROP CONSTRAINT "Operations_DownloadStateTypeId_check";
ALTER TABLE "Operations" ADD COLUMN "DownloadCompletedAt" timestamptz NULL;
ALTER TABLE "Operations" ADD CONSTRAINT "Operations_DownloadStateTypeId_check" CHECK ("DownloadStateTypeId" BETWEEN 1 AND 10);

-- These retained import proposals prove that the downloader has already delivered its files.
UPDATE "Operations"
SET "DownloadStateTypeId" = 5, "RemainingBytes" = 0, "EtaSeconds" = NULL
WHERE "OperationTypeId" = 2 AND "OperationStatusTypeId" IN (1, 2)
    AND ("AttentionProposal" -> 'Source' IS NOT NULL OR "AttentionKey" IN ('import_path_unmapped', 'import_source_unavailable'));

CREATE OR REPLACE VIEW "OperationQueueStates" AS
SELECT
    operation."Id" AS "OperationId",
    (CASE
        WHEN operation."OperationStatusTypeId" = 4 AND operation."DismissedAt" IS NULL THEN 6
        WHEN operation."OperationStatusTypeId" IN (1, 2) AND operation."AttentionKey" IS NOT NULL THEN 4
        WHEN operation."OperationStatusTypeId" IN (1, 2) AND operation."DownloadStateTypeId" IN (4, 5, 6, 7, 8, 9) THEN 5
        WHEN operation."OperationStatusTypeId" IN (1, 2) THEN 2
        ELSE 3
    END)::smallint AS "ActivityFilterTypeId"
FROM "Operations" AS operation;
