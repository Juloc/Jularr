-- AniList list entries no longer make a Work an administrator's library title. Everything but them is WorkManagedRelations (the Admin Works list);
-- WorkLibraryRelations keeps them besides, so catalog cleanup and the profile's own lists treat a listed title as used, as before.
CREATE VIEW "WorkManagedRelations" AS
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

CREATE OR REPLACE VIEW "WorkLibraryRelations" AS
SELECT
    managed."WorkId"
FROM
    "WorkManagedRelations" AS managed
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
    JOIN "WorkUnits" AS unit ON unit."Id" = mapped."WorkUnitId";
