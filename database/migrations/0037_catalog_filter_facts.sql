-- What else Discover filters a catalog's titles by (country and season came with 0036), as the catalog sync reads it (AniList and TMDB films); values of the CatalogFacts enums.
ALTER TABLE "Works"
    ADD COLUMN "CatalogFormatId" smallint NULL CHECK ("CatalogFormatId" BETWEEN 1 AND 10),
    ADD COLUMN "ReleaseStatusId" smallint NULL CHECK ("ReleaseStatusId" BETWEEN 1 AND 5),
    ADD COLUMN "CatalogSourceId" smallint NULL CHECK ("CatalogSourceId" BETWEEN 1 AND 15),
    ADD COLUMN "Episodes" integer NULL CHECK ("Episodes" > 0),
    ADD COLUMN "Chapters" integer NULL CHECK ("Chapters" > 0),
    ADD COLUMN "Volumes" integer NULL CHECK ("Volumes" > 0),
    -- Minutes of the film or of one episode.
    ADD COLUMN "DurationMinutes" integer NULL CHECK ("DurationMinutes" > 0),
    ADD COLUMN "GenreIds" smallint[] NOT NULL DEFAULT '{}',
    -- The catalog's own ids: AniList's tags, and its streaming and reading sites or TMDB's streaming services in the instance's region.
    ADD COLUMN "TagIds" integer[] NOT NULL DEFAULT '{}',
    ADD COLUMN "SiteIds" integer[] NOT NULL DEFAULT '{}';

CREATE INDEX "IX_Works_CatalogGenres" ON "Works" USING gin ("GenreIds") WHERE "CatalogNamespace" IS NOT NULL;
CREATE INDEX "IX_Works_CatalogTags" ON "Works" USING gin ("TagIds") WHERE "CatalogNamespace" IS NOT NULL;
CREATE INDEX "IX_Works_CatalogSites" ON "Works" USING gin ("SiteIds") WHERE "CatalogNamespace" IS NOT NULL;

-- The kept titles are read again for these facts: AniList walks again from the start, TMDB's films are read again most popular first.
UPDATE "ExternalWorkSyncs" SET "ListedOn" = NULL, "Cursor" = NULL WHERE "Namespace" = 'anilist';
UPDATE "Works" SET "FetchedAt" = NULL WHERE "CatalogNamespace" = 'tmdb_movie' AND "FetchedAt" IS NOT NULL;
