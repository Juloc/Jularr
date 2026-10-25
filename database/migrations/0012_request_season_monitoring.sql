-- A request may begin at one verified TVDB season; moderation must retain that intent.
-- Shared Wanted uses the earliest requested season and does not narrow an existing target.
ALTER TABLE "AcquisitionRequests"
    ADD COLUMN "StartSeasonNumber" smallint NULL CHECK ("StartSeasonNumber" BETWEEN 1 AND 999);
ALTER TABLE "WantedItems"
    ADD COLUMN "MinimumSeasonNumber" smallint NOT NULL DEFAULT 1 CHECK ("MinimumSeasonNumber" BETWEEN 1 AND 999);
