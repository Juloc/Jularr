-- Step 2 of one title table (#1651): every catalog title is a Work and ExternalWorks goes. The catalog is a cache that fills again;
-- only what people made moves: the titles relations point at, and the catalog fields of titles that already are Works.

-- A Work's catalog entry fills only what the Work leaves empty; the entry of the Work's own type and subtype comes first.
-- FetchedAt is when the catalog's own entry was read (the entry's former DetailedAt); a trending rank is list state and comes again with its list.
WITH entry AS (
    SELECT DISTINCT ON (identity."WorkId")
        identity."WorkId",
        listed."Namespace",
        listed."OriginalTitle",
        listed."Overview",
        listed."Creator",
        listed."ImageUrl",
        listed."BackdropUrl",
        listed."Popularity",
        listed."Rating",
        listed."Votes",
        listed."IsAdult",
        listed."DetailedAt"
    FROM
        "ExternalWorks" AS listed
        JOIN "WorkExternalIdentities" AS identity ON identity."Namespace" = listed."Namespace" AND identity."Value" = listed."Value"
        JOIN "Works" AS work ON work."Id" = identity."WorkId"
    ORDER BY
        identity."WorkId",
        (listed."MediaTypeId" = work."MediaTypeId" AND coalesce(listed."MediaSubtypeId", 0) = coalesce(work."MediaSubtypeId", 0)) DESC,
        listed."DetailedAt" IS NOT NULL DESC,
        listed."Votes" DESC NULLS LAST,
        listed."Id"
)
UPDATE
    "Works" AS work
SET
    "CatalogNamespace" = coalesce(work."CatalogNamespace", entry."Namespace"),
    "OriginalTitle" = coalesce(work."OriginalTitle", entry."OriginalTitle"),
    "Overview" = coalesce(work."Overview", entry."Overview"),
    "Creator" = coalesce(work."Creator", entry."Creator"),
    "ImageUrl" = coalesce(work."ImageUrl", entry."ImageUrl"),
    "BackdropUrl" = coalesce(work."BackdropUrl", entry."BackdropUrl"),
    "Popularity" = coalesce(work."Popularity", entry."Popularity"),
    "Rating" = coalesce(work."Rating", entry."Rating"),
    "Votes" = coalesce(work."Votes", entry."Votes"),
    "IsAdult" = work."IsAdult" OR entry."IsAdult",
    "FetchedAt" = coalesce(work."FetchedAt", entry."DetailedAt")
FROM
    entry
WHERE
    work."Id" = entry."WorkId";

-- The catalog titles relations point at: an anime season's series, the Work that holds their identity, or a new minimal Work.
CREATE TEMPORARY TABLE "RelatedCatalogWorks" ON COMMIT DROP AS
SELECT
    listed."Id" AS "ExternalWorkId",
    CASE
        WHEN season."WorkId" IS NOT NULL THEN season."WorkId"
        WHEN identity."WorkId" IS NOT NULL THEN identity."WorkId"
        ELSE nextval(pg_get_serial_sequence('"Works"', 'Id'))
    END AS "WorkId",
    identity."WorkId" IS NULL AND season."WorkId" IS NULL AS "IsNew"
FROM
    "ExternalWorks" AS listed
    LEFT JOIN "WorkExternalIdentities" AS identity ON identity."Namespace" = listed."Namespace" AND identity."Value" = listed."Value"
    LEFT JOIN LATERAL (
        SELECT
            unit."WorkId"
        FROM
            "WorkUnitExternalIdentities" AS mapped
            JOIN "WorkUnits" AS unit ON unit."Id" = mapped."WorkUnitId"
        WHERE
            listed."Namespace" = 'anilist'
            AND listed."MediaTypeId" = 2
            AND listed."MediaSubtypeId" = 1
            AND mapped."Namespace" = 'anilist'
            AND mapped."Value" = listed."Value"
    ) AS season ON TRUE
WHERE
    EXISTS (
        SELECT
            1
        FROM
            "WorkRelations" AS relation
        WHERE
            relation."ExternalWorkId" = listed."Id"
    );

INSERT INTO
    "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "OriginalTitle", "ReleaseYear", "ReleasedOn", "Overview", "Creator", "ImageUrl", "BackdropUrl",
        "Popularity", "Rating", "Votes", "IsAdult", "CatalogNamespace", "FetchedAt")
SELECT
    related."WorkId",
    listed."MediaTypeId",
    listed."MediaSubtypeId",
    listed."Title",
    listed."OriginalTitle",
    listed."ReleaseYear",
    listed."ReleasedOn",
    listed."Overview",
    listed."Creator",
    listed."ImageUrl",
    listed."BackdropUrl",
    listed."Popularity",
    listed."Rating",
    listed."Votes",
    listed."IsAdult",
    listed."Namespace",
    listed."DetailedAt"
FROM
    "RelatedCatalogWorks" AS related
    JOIN "ExternalWorks" AS listed ON listed."Id" = related."ExternalWorkId"
WHERE
    related."IsNew";

INSERT INTO
    "WorkExternalIdentities" ("WorkId", "Namespace", "Value")
SELECT
    related."WorkId",
    listed."Namespace",
    listed."Value"
FROM
    "RelatedCatalogWorks" AS related
    JOIN "ExternalWorks" AS listed ON listed."Id" = related."ExternalWorkId"
WHERE
    related."IsNew";

-- An IMDb id joins a new Work only while no other Work holds it; nothing is merged.
INSERT INTO
    "WorkExternalIdentities" ("WorkId", "Namespace", "Value")
SELECT DISTINCT ON (listed."ImdbId")
    related."WorkId",
    'imdb',
    listed."ImdbId"
FROM
    "RelatedCatalogWorks" AS related
    JOIN "ExternalWorks" AS listed ON listed."Id" = related."ExternalWorkId"
WHERE
    related."IsNew"
    AND listed."ImdbId" IS NOT NULL
ORDER BY
    listed."ImdbId",
    listed."Votes" DESC NULLS LAST,
    listed."Id"
ON CONFLICT DO NOTHING;

-- A relation that now points at its own Work says nothing; of relations that now say the same, one stays: a person's, a hidden one, one that already named a Work.
DELETE FROM
    "WorkRelations" AS relation
USING
    "RelatedCatalogWorks" AS related
WHERE
    relation."ExternalWorkId" = related."ExternalWorkId"
    AND related."WorkId" = relation."WorkId";

WITH ranked AS (
    SELECT
        relation."Id",
        row_number() OVER (
            PARTITION BY relation."WorkId", coalesce(relation."RelatedWorkId", related."WorkId"), relation."WorkRelationTypeId"
            ORDER BY relation."SourceKey" = 'manual' DESC, relation."IsSuppressed" DESC, relation."RelatedWorkId" IS NOT NULL DESC, relation."Id"
        ) AS "Place"
    FROM
        "WorkRelations" AS relation
        LEFT JOIN "RelatedCatalogWorks" AS related ON related."ExternalWorkId" = relation."ExternalWorkId"
)
DELETE FROM
    "WorkRelations" AS relation
USING
    ranked
WHERE
    relation."Id" = ranked."Id"
    AND ranked."Place" > 1;

UPDATE
    "WorkRelations" AS relation
SET
    "RelatedWorkId" = related."WorkId",
    "ExternalWorkId" = NULL
FROM
    "RelatedCatalogWorks" AS related
WHERE
    relation."ExternalWorkId" = related."ExternalWorkId";

-- Relations name Works only; the view of both sides follows.
DROP VIEW "WorkRelationLinks";
ALTER TABLE "WorkRelations" DROP COLUMN "ExternalWorkId";
ALTER TABLE "WorkRelations" ALTER COLUMN "RelatedWorkId" SET NOT NULL;

-- Both views of every relation: the Work that states it sees the type as stored, the related Work the inverse.
CREATE VIEW "WorkRelationLinks" AS
SELECT
    relation."Id" AS "RelationId",
    relation."WorkId",
    relation."RelatedWorkId" AS "OtherWorkId",
    relation."WorkRelationTypeId",
    relation."SourceKey",
    relation."IsSuppressed"
FROM
    "WorkRelations" AS relation
UNION ALL
SELECT
    relation."Id",
    relation."RelatedWorkId",
    relation."WorkId",
    CASE relation."WorkRelationTypeId" WHEN 1 THEN 2 WHEN 2 THEN 1 WHEN 3 THEN 5 WHEN 4 THEN 5 WHEN 5 THEN 3 WHEN 6 THEN 7 WHEN 7 THEN 6 ELSE relation."WorkRelationTypeId" END::smallint,
    relation."SourceKey",
    relation."IsSuppressed"
FROM
    "WorkRelations" AS relation;

-- A feed lists Works in its order; an AniList anime entry that is a season of a series lists the series with that season. Every feed loads again at once.
DELETE FROM "ExternalWorkFeedItems";
ALTER TABLE "ExternalWorkFeedItems" DROP COLUMN "ExternalWorkId";
ALTER TABLE "ExternalWorkFeedItems"
    ADD COLUMN "WorkId" bigint NOT NULL REFERENCES "Works" ("Id") ON DELETE CASCADE,
    ADD COLUMN "SeasonNumber" smallint NULL CHECK ("SeasonNumber" >= 0),
    ADD CONSTRAINT "UX_ExternalWorkFeedItems_Work" UNIQUE NULLS NOT DISTINCT ("FeedId", "WorkId", "SeasonNumber");
CREATE INDEX "IX_ExternalWorkFeedItems_Work" ON "ExternalWorkFeedItems" ("WorkId");
UPDATE "ExternalWorkFeeds" SET "RefreshedAt" = NULL, "RetryAfter" = statement_timestamp();

-- The full sync starts again, now into Works.
UPDATE "ExternalWorkSyncs" SET "ListedOn" = NULL, "Cursor" = NULL, "ChangesReadAt" = NULL, "CompletedAt" = NULL, "UpdatedAt" = now();

DROP TABLE "ExternalWorks";

-- The full sync reads the catalog entries it has not read yet (no FetchedAt), most popular first.
CREATE INDEX "IX_Works_CatalogUnread" ON "Works" ("CatalogNamespace", "Popularity" DESC NULLS LAST, "Id") WHERE "CatalogNamespace" IS NOT NULL AND "FetchedAt" IS NULL;

-- What ties a Work to the library: a file record (also while its storage is offline or the file is missing), a request, a wanted entry, a favorite,
-- a collection, progress, an entry on a connected AniList list, the Plex library, or a person's edit (a manual title, fact, identity, subtype, series,
-- relation, hidden relation, episode group or season mapping). A Work without any is a catalog title. One row per tie; ask it with EXISTS for one Work.
CREATE VIEW "WorkLibraryRelations" AS
SELECT
    asset."WorkId"
FROM
    "MediaAssets" AS asset
UNION ALL
SELECT
    request."WorkId"
FROM
    "AcquisitionRequests" AS request
UNION ALL
SELECT
    wanted."WorkId"
FROM
    "WantedItems" AS wanted
UNION ALL
SELECT
    favorite."WorkId"
FROM
    "WatchlistEntries" AS favorite
UNION ALL
SELECT
    member."WorkId"
FROM
    "CollectionWorks" AS member
UNION ALL
SELECT
    progress."WorkId"
FROM
    "MediaProgress" AS progress
UNION ALL
SELECT
    identity."WorkId"
FROM
    "AniListEntries" AS entry
    JOIN "WorkExternalIdentities" AS identity ON identity."Namespace" = 'anilist' AND identity."Value" = entry."MediaId"::text
UNION ALL
SELECT
    unit."WorkId"
FROM
    "AniListEntries" AS entry
    JOIN "WorkUnitExternalIdentities" AS mapped ON mapped."Namespace" = 'anilist' AND mapped."Value" = entry."MediaId"::text
    JOIN "WorkUnits" AS unit ON unit."Id" = mapped."WorkUnitId"
UNION ALL
SELECT
    identity."WorkId"
FROM
    "WorkExternalIdentities" AS identity
WHERE
    identity."Namespace" = 'plex'
    OR identity."IsManual"
UNION ALL
SELECT
    title."WorkId"
FROM
    "WorkTitles" AS title
WHERE
    title."IsManual"
UNION ALL
SELECT
    fact."WorkId"
FROM
    "WorkMetadataFacts" AS fact
WHERE
    fact."IsManual"
UNION ALL
SELECT
    work."Id"
FROM
    "Works" AS work
WHERE
    work."IsMediaSubtypeManual"
    OR work."IsFranchiseManual"
UNION ALL
SELECT
    relation."WorkId"
FROM
    "WorkRelations" AS relation
WHERE
    relation."SourceKey" = 'manual'
    OR relation."IsSuppressed"
UNION ALL
SELECT
    relation."RelatedWorkId"
FROM
    "WorkRelations" AS relation
WHERE
    relation."SourceKey" = 'manual'
    OR relation."IsSuppressed"
UNION ALL
SELECT
    unitGroup."WorkId"
FROM
    "WorkUnitGroups" AS unitGroup
UNION ALL
SELECT
    unit."WorkId"
FROM
    "WorkUnitExternalIdentities" AS mapped
    JOIN "WorkUnits" AS unit ON unit."Id" = mapped."WorkUnitId"
WHERE
    mapped."IsManual";
