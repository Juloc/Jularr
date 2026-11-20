-- A release taken from the search across the indexers without a work: it downloads like any other, is never imported by itself and waits in the
-- queue for a person. Its row has no wanted item.
ALTER TABLE "AcquisitionReleases" ALTER COLUMN "WantedItemId" DROP NOT NULL;
