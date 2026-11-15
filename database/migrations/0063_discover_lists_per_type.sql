-- Every media type has its own Discover lists now: 10 classics (books) and 11 retro (games) are new.
ALTER TABLE "ExternalWorkFeeds" DROP CONSTRAINT "ExternalWorkFeeds_DiscoverFeedTypeId_check";
ALTER TABLE "ExternalWorkFeeds" ADD CONSTRAINT "ExternalWorkFeeds_DiscoverFeedTypeId_check" CHECK ("DiscoverFeedTypeId" BETWEEN 1 AND 11);

-- A film's new list is now what plays in cinemas and a series' the series on the air; the kept old lists are read again.
DELETE FROM "ExternalWorkFeeds" WHERE "Namespace" IN ('tmdb_movie', 'tmdb_tv') AND "DiscoverFeedTypeId" = 3;
