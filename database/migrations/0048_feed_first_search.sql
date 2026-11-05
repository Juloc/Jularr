-- Searching as Sonarr does: a source's flags (its feed, automatic and interactive searches) and, for sources without a feed, how often a wanted title is searched there.
-- Every search source may now leave automatic searches; daily limits stay with Newznab.
DO $$
DECLARE
    constraint_name text;
BEGIN
    FOR constraint_name IN
        SELECT check_constraint.conname
        FROM pg_constraint AS check_constraint
        WHERE check_constraint.conrelid = '"AcquisitionConnections"'::regclass
          AND check_constraint.contype = 'c'
          AND pg_get_constraintdef(check_constraint.oid) LIKE '%"SearchesAutomatically" AND%'
    LOOP
        EXECUTE format('ALTER TABLE "AcquisitionConnections" DROP CONSTRAINT %I', constraint_name);
    END LOOP;
END $$;
ALTER TABLE "AcquisitionConnections" ADD CONSTRAINT "AcquisitionConnections_Limits_check"
    CHECK ("AcquisitionConnectionTypeId" = 1 OR "QueryLimit" IS NULL AND "GrabLimit" IS NULL);
ALTER TABLE "AcquisitionConnections" ADD COLUMN "ReadsFeed" boolean NOT NULL DEFAULT TRUE;
ALTER TABLE "AcquisitionConnections" ADD COLUMN "SearchesInteractively" boolean NOT NULL DEFAULT TRUE;
ALTER TABLE "AcquisitionConnections" ADD COLUMN "SearchIntervalHours" integer NOT NULL DEFAULT 168 CHECK ("SearchIntervalHours" BETWEEN 6 AND 8760);

-- A wanted title has two times: the next search of every source (a new title, a resumed monitor, a button, a failed download, a retry; NULL is due now,
-- 'infinity' waits for such an event) and the next search of the sources without a feed on their own rhythm.
ALTER TABLE "WantedItems" ADD COLUMN "PeriodicSearchAt" timestamptz NULL;

-- Titles searched before keep their old time, but only for the sources without a feed; a grab or an import no longer searches again.
-- A grab its indexer limited keeps its retry.
UPDATE "WantedItems"
SET
    "PeriodicSearchAt" = coalesce("NextSearchAt", now()),
    "NextSearchAt" = 'infinity'
WHERE
    "LastSearchedAt" IS NOT NULL
    AND "LastOutcomeKey" IS DISTINCT FROM 'acquisition_rate_limited'
    AND ("NextSearchAt" IS NOT NULL OR "LastOutcomeKey" IN ('grabbed', 'imported'));

-- A grab its indexer limited waits for the pause with the release's link, kept protected like a key, and is fetched again then.
ALTER TABLE "AcquisitionReleases" ADD COLUMN "ProtectedDownloadUrl" bytea NULL;
