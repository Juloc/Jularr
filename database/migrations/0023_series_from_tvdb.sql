-- Series come from TheTVDB like Sonarr; TMDB gives only their rating, so its series catalog is no longer kept whole.
DELETE FROM "ExternalWorkSyncs" WHERE "Namespace" = 'tmdb_tv';

-- Catalog titles of that catalog go unless something ties them to the library or a file record exists, as in the catalog cleanup;
-- their places in Discover's series lists go with them, and those lists are renewed soon.
DELETE FROM
    "Works" AS work
WHERE
    work."CatalogNamespace" = 'tmdb_tv'
    AND NOT EXISTS (SELECT 1 FROM "WorkLibraryRelations" AS library WHERE library."WorkId" = work."Id")
    AND NOT EXISTS (SELECT 1 FROM "MediaAssets" AS asset WHERE asset."WorkId" = work."Id");

UPDATE "ExternalWorkFeeds" SET "RetryAfter" = statement_timestamp() WHERE "Namespace" = 'tmdb_tv';

-- Series of the library take their details from TheTVDB again: the metadata fill does it a few at a time, opening one does it at once;
-- until then and while TheTVDB cannot answer they keep what they have.
UPDATE
    "Works" AS work
SET
    "DetailedAt" = NULL
WHERE
    work."MediaTypeId" = 2
    AND work."DetailedAt" IS NOT NULL
    AND EXISTS (SELECT 1 FROM "WorkLibraryRelations" AS library WHERE library."WorkId" = work."Id");

-- The people TheTVDB credits are known by its ids.
ALTER TABLE "PersonExternalIdentities" DROP CONSTRAINT "PersonExternalIdentities_Namespace_check";
ALTER TABLE "PersonExternalIdentities" ADD CONSTRAINT "PersonExternalIdentities_Namespace_check"
    CHECK ("Namespace" IN ('tmdb', 'tvdb', 'openlibrary', 'musicbrainz', 'anilist', 'igdb'));
