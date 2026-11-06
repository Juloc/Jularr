-- Pictures and pages of prepared books no longer live in the database: an EPUB's pictures and a CBZ's pages are read from the book file when
-- they are shown, pictures and pages the server draws (PDF, books read through a copy, CBR/CB7, DjVu) are kept on disk under the data folder
-- and drawn again when they are missing. The reading text, structure, translations and every other non-picture resource stay byte for byte.
--
-- The table is written anew with the rows it keeps: rows deleted in the update's own transaction cannot be reclaimed by VACUUM FULL or CLUSTER
-- (neither runs on them before the commit), while TRUNCATE gives the table and its TOAST new files and frees the old ones at the commit.
CREATE TEMPORARY TABLE "ReadingResourcesKept" ON COMMIT DROP AS
SELECT
    resource."ReadingDocumentId",
    resource."Path",
    resource."MediaType",
    resource."Bytes"
FROM
    "ReadingResources" AS resource
WHERE
    resource."MediaType" NOT LIKE 'image/%';

TRUNCATE "ReadingResources";

INSERT INTO
    "ReadingResources" ("ReadingDocumentId", "Path", "MediaType", "Bytes")
SELECT
    kept."ReadingDocumentId",
    kept."Path",
    kept."MediaType",
    kept."Bytes"
FROM
    "ReadingResourcesKept" AS kept;

-- The database cache counts what is left of each document.
UPDATE
    "ReadingDocuments" AS document
SET
    "CacheBytes" = coalesce((
        SELECT
            sum(length(resource."Bytes"))
        FROM
            "ReadingResources" AS resource
        WHERE
            resource."ReadingDocumentId" = document."Id"
    ), 0)
WHERE
    document."CacheBytes" > 0;
