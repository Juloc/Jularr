-- A film or episode a person assigned from a waiting download was named by the file inside it, not by the release the download was grabbed for:
-- the stored release name lacked the release's language words and group, so the file counted as the original language only, scored lower than its own
-- release and was neither replaced by it nor protected from it. The history of such an assignment keeps the release; the file takes its name again.
UPDATE
    "StoredFiles" AS file
SET
    "ReleaseName" = left(assigned."ReleaseTitle", 500),
    "ReleaseGroup" = coalesce(file."ReleaseGroup", substring(assigned."ReleaseTitle" FROM '-([A-Za-z0-9]{2,32})$'))
FROM
    (
        SELECT DISTINCT ON (event."StoredFileId")
            event."StoredFileId",
            event."ReleaseTitle"
        FROM
            "ActivityEvents" AS event
        WHERE
            event."ActivityEventTypeId" = 7
            AND event."AcquisitionReleaseId" IS NOT NULL
            AND event."StoredFileId" IS NOT NULL
            AND event."ReleaseTitle" IS NOT NULL
        ORDER BY
            event."StoredFileId",
            event."OccurredAt" DESC,
            event."Id" DESC
    ) AS assigned,
    "MediaAssets" AS asset,
    "Works" AS work
WHERE
    file."Id" = assigned."StoredFileId"
    AND asset."Id" = file."MediaAssetId"
    AND work."Id" = asset."WorkId"
    AND work."MediaTypeId" IN (1, 2)
    AND file."ReleaseName" IS DISTINCT FROM left(assigned."ReleaseTitle", 500);
