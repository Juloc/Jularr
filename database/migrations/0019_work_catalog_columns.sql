-- Step 1 of one title table (#1651): Works gains the catalog columns of ExternalWorks so a later migration can merge them. Schema only, no behaviour change.
ALTER TABLE "Works"
    ADD COLUMN IF NOT EXISTS "OriginalTitle" varchar(500) NULL,
    ADD COLUMN IF NOT EXISTS "Overview" varchar(4000) NULL,
    -- Author, artist or developer as the catalog names them.
    ADD COLUMN IF NOT EXISTS "Creator" varchar(4000) NULL,
    ADD COLUMN IF NOT EXISTS "ImageUrl" varchar(500) NULL CHECK ("ImageUrl" ~ '^https://'),
    ADD COLUMN IF NOT EXISTS "BackdropUrl" varchar(500) NULL CHECK ("BackdropUrl" ~ '^https://'),
    ADD COLUMN IF NOT EXISTS "Popularity" real NULL CHECK ("Popularity" >= 0),
    -- On a scale of ten, whatever scale the catalog uses.
    ADD COLUMN IF NOT EXISTS "Rating" numeric(4, 2) NULL CHECK ("Rating" BETWEEN 0 AND 10),
    ADD COLUMN IF NOT EXISTS "Votes" integer NULL CHECK ("Votes" >= 0),
    -- The place in the catalog's current trending list of its type; NULL when it is not trending.
    ADD COLUMN IF NOT EXISTS "TrendingRank" integer NULL CHECK ("TrendingRank" > 0),
    ADD COLUMN IF NOT EXISTS "IsAdult" boolean NOT NULL DEFAULT FALSE,
    -- The catalog the minimal row came from; NULL for a Work that never was a catalog row.
    ADD COLUMN IF NOT EXISTS "CatalogNamespace" varchar(16) NULL CHECK ("CatalogNamespace" IN ('tmdb_movie', 'tmdb_tv', 'tvdb', 'openlibrary', 'anilist', 'musicbrainz', 'igdb')),
    ADD COLUMN IF NOT EXISTS "FetchedAt" timestamptz NULL,
    -- Since when the details (child tables) exist; NULL means a minimal row only.
    ADD COLUMN IF NOT EXISTS "DetailedAt" timestamptz NULL,
    -- The last time the Work page was opened; details nobody opens for a while are dropped.
    ADD COLUMN IF NOT EXISTS "ViewedAt" timestamptz NULL,
    ADD COLUMN IF NOT EXISTS "SearchText" tsvector GENERATED ALWAYS AS (to_tsvector('simple', "CanonicalTitle" || ' ' || coalesce("OriginalTitle", ''))) STORED;

-- Discover reads each list of a catalog's type in its own order; no subtype counts as 0.
CREATE INDEX IF NOT EXISTS "IX_Works_CatalogTrending" ON "Works" ("CatalogNamespace", "MediaTypeId", coalesce("MediaSubtypeId", 0), "TrendingRank") WHERE "TrendingRank" IS NOT NULL;
CREATE INDEX IF NOT EXISTS "IX_Works_CatalogPopularity" ON "Works" ("CatalogNamespace", "MediaTypeId", coalesce("MediaSubtypeId", 0), "Popularity" DESC NULLS LAST) WHERE "CatalogNamespace" IS NOT NULL;
CREATE INDEX IF NOT EXISTS "IX_Works_CatalogRating" ON "Works" ("CatalogNamespace", "MediaTypeId", coalesce("MediaSubtypeId", 0), "Rating" DESC NULLS LAST) WHERE "CatalogNamespace" IS NOT NULL;
CREATE INDEX IF NOT EXISTS "IX_Works_CatalogReleased" ON "Works" ("CatalogNamespace", "MediaTypeId", coalesce("MediaSubtypeId", 0), "ReleasedOn" DESC NULLS LAST) WHERE "CatalogNamespace" IS NOT NULL;
CREATE INDEX IF NOT EXISTS "IX_Works_CatalogSearch" ON "Works" USING gin ("SearchText");
-- A full sync reads titles without details in the order users are most likely to look for them.
CREATE INDEX IF NOT EXISTS "IX_Works_CatalogUndetailed" ON "Works" ("CatalogNamespace", "Popularity" DESC NULLS LAST) WHERE "CatalogNamespace" IS NOT NULL AND "DetailedAt" IS NULL;

-- Existing Works already have their details and count as just opened, so the cleanup never drops them at once.
UPDATE "Works" SET "DetailedAt" = "UpdatedAt", "ViewedAt" = now() WHERE "DetailedAt" IS NULL;

-- Cache tier per catalog: 1 lean, 2 standard, 3 full catalog; trending titles get their details in advance.
ALTER TABLE "ExternalWorkSyncs"
    ADD COLUMN IF NOT EXISTS "CacheTier" smallint NOT NULL DEFAULT 2 CHECK ("CacheTier" BETWEEN 1 AND 3),
    ADD COLUMN IF NOT EXISTS "DetailsTrending" boolean NOT NULL DEFAULT FALSE;

-- Details of titles without a library relation that nobody opened for this many days are dropped; separate from the cover cache's KeepDays.
ALTER TABLE "InstanceSettings"
    ADD COLUMN IF NOT EXISTS "CatalogDetailKeepDays" smallint NOT NULL DEFAULT 14 CHECK ("CatalogDetailKeepDays" BETWEEN 1 AND 365);
