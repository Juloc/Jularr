-- AniList's Discover lists read whole catalogs too: the anime season, the country of origin and how many people made a title a favourite.
ALTER TABLE "Works"
    ADD COLUMN "CountryOfOrigin" varchar(2) NULL CHECK ("CountryOfOrigin" ~ '^[A-Z]{2}$'),
    ADD COLUMN "Favourites" integer NULL CHECK ("Favourites" >= 0),
    -- 1 winter, 2 spring, 3 summer, 4 fall, with the year AniList counts the season to.
    ADD COLUMN "Season" smallint NULL CHECK ("Season" BETWEEN 1 AND 4),
    ADD COLUMN "SeasonYear" smallint NULL CHECK ("SeasonYear" BETWEEN 1800 AND 2200);
CREATE INDEX "IX_Works_CatalogSeason" ON "Works" ("CatalogNamespace", "SeasonYear", "Season") WHERE "SeasonYear" IS NOT NULL;

-- The new lists: 5 this season, 6 next season, 7 popular of all time, 8 manhwa, 9 top 100. AniList's anime, manga and light novels drop their old lists.
ALTER TABLE "ExternalWorkFeeds" DROP CONSTRAINT "ExternalWorkFeeds_DiscoverFeedTypeId_check";
ALTER TABLE "ExternalWorkFeeds" ADD CONSTRAINT "ExternalWorkFeeds_DiscoverFeedTypeId_check" CHECK ("DiscoverFeedTypeId" BETWEEN 1 AND 9);
DELETE FROM "ExternalWorkFeeds" WHERE "Namespace" = 'anilist' AND "DiscoverFeedTypeId" IN (2, 3);

-- A whole AniList catalog is read again with the new fields; until then Discover asks AniList.
UPDATE "ExternalWorkSyncs"
SET
    "Cursor" = NULL,
    "ListedOn" = NULL,
    "CompletedAt" = NULL,
    "Version" = "Version" + 1,
    "UpdatedAt" = now()
WHERE
    "Namespace" = 'anilist';
