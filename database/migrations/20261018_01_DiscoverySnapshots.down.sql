BEGIN;

SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';

DO $migration$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM "SchemaMigrations"
        WHERE "Key" = '20261018_01_DiscoverySnapshots'
    ) THEN
        RAISE EXCEPTION 'Migration 20261018_01_DiscoverySnapshots is not installed';
    END IF;
END
$migration$;

-- Deliberate rollback: the only data lost is regenerable provider snapshots.
-- Never drop the shared migration ledger or any canonical media/profile table.
DROP TABLE "DiscoverySnapshots";
DELETE FROM "SchemaMigrations"
WHERE "Key" = '20261018_01_DiscoverySnapshots';

COMMIT;
