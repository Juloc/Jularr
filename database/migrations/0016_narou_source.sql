-- Shōsetsuka ni Narō: its API describes web novels (provider `syosetu`), its novel pages give the chapters (direct source `Narou`).
-- A web novel's ncode is a Work identity of its own, never the identity of a published edition.
INSERT INTO "AcquisitionConnectionTypes" ("Id", "Key", "AcquisitionConnectionRoleId") VALUES
    (11, 'Narou', 1)
ON CONFLICT ("Id") DO NOTHING;

-- Off until the Owner sets Narō up: nothing is asked of it before.
INSERT INTO "Providers" ("Key", "IsEnabled") VALUES ('syosetu', FALSE)
ON CONFLICT ("Key") DO NOTHING;

ALTER TABLE "WorkExternalIdentities" DROP CONSTRAINT "WorkExternalIdentities_Namespace_check";
ALTER TABLE "WorkExternalIdentities" ADD CONSTRAINT "WorkExternalIdentities_Namespace_check"
    CHECK ("Namespace" IN ('tmdb_movie', 'tmdb_tv', 'tvdb', 'imdb', 'plex', 'openlibrary', 'isbn', 'gutenberg', 'anilist', 'musicbrainz', 'igdb', 'syosetu'));

ALTER TABLE "WorkExclusionIdentities" DROP CONSTRAINT "WorkExclusionIdentities_Namespace_check";
ALTER TABLE "WorkExclusionIdentities" ADD CONSTRAINT "WorkExclusionIdentities_Namespace_check"
    CHECK ("Namespace" IN ('tmdb_movie', 'tmdb_tv', 'tvdb', 'imdb', 'plex', 'openlibrary', 'isbn', 'gutenberg', 'anilist', 'musicbrainz', 'igdb', 'syosetu'));

-- Narō is keyless like the other direct sources.
ALTER TABLE "AcquisitionConnections" DROP CONSTRAINT "AcquisitionConnections_check4";
ALTER TABLE "AcquisitionConnections" ADD CONSTRAINT "AcquisitionConnections_check4"
    CHECK ("AcquisitionConnectionTypeId" IN (1, 3, 4, 5, 6, 11) OR "ProtectedApiKey" IS NOT NULL);
