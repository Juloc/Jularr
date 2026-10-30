-- The cover of an AniList list entry, so an entry that is no title of the library yet still shows on the profile's lists.
ALTER TABLE "AniListEntries" ADD COLUMN "CoverUrl" varchar(500) NULL CHECK (length(btrim("CoverUrl")) > 0);
