# Database migrations

The schema is frozen: `database/*.sql` is schema version 1 and `database/baseline.sha256` holds its hash.
The Owner only updates a running installation, so every schema change is
a new numbered migration. `DatabaseBaselineTests`/`DatabaseMigrationTests` fail when a baseline file changes
(`Baseline_MatchesTheFrozenHash`). `node scripts/freeze-baseline.mjs` rewrites the hash and is not part of ordinary work.

## Adding a migration

1. Create `database/migrations/NNNN_short_name.sql`: `NNNN` is the next schema version (the first is `0002`, no gaps), the name is
   lower-case words joined by `_`. The file is embedded automatically as `Jularr.Database.Migrations.<file name>`.
2. Plain SQL without `BEGIN`/`COMMIT`; startup owns the transaction. Use guards where they help (`IF NOT EXISTS`, `ADD COLUMN IF NOT EXISTS`).
   Put new tables, columns and constraints in the migration only, never in a baseline file.
3. Never edit or delete a migration that was released: its checksum is recorded, a changed one stops the start. Fix a mistake with the next migration.
4. Test it: create a database from the baseline, insert data, apply the migration, check that the data is kept and the version is recorded
   (see `DatabaseMigrationTests`; test migrations live in the test assembly).

## Startup

`DatabaseUpgrade.RunAsync` (called by `Program.cs` before the host serves) runs `DatabaseBaseline`, the one owner, under a PostgreSQL
advisory lock so two instances never migrate at once. Migrations run automatically, without confirmation.

- Empty database: baselines, then `DatabaseSchemaVersions` records version 1 (`Version`, `Name`, `Fingerprint`, `AppliedAt`).
- Baseline without a version table (an earlier build): every table, view and column of the baseline must exist (checked against a scratch
  installation that is rolled back); then the table is created and version 1 recorded. Anything else is refused and left unchanged.
- Versioned database: the recorded history must match this release (`Fingerprint` is the cumulative SHA-256 of baseline and all migrations
  up to that version, line endings normalized). A changed or missing version or a newer database stops the start.
- Pending migrations: first a complete backup (database dump, key ring, manifest) through the backup owner into `Backup:Path` (`/data/backups`),
  using `Backup:PgDumpPath`. If the backup cannot be made, nothing is migrated and Jularr does not start. With nothing pending no backup is made.
- All pending migrations and their version entries run in one transaction in numeric order. A failure rolls everything back.

## A failed update

The failure is logged (critical) with the migration name and reason. Jularr then does not start the application: `DatabaseMaintenance`
(Frontend) serves one page on the configured addresses that answers every request with 503 and the German text
"Datenbank-Update fehlgeschlagen ... siehe Log; die vorherige Version kann wieder gestartet werden" (`maintenance.databaseUpdate.*`). The process
stays up instead of exiting, so a reverse proxy shows the page rather than a bad gateway and a restart policy does not repeat the backup and the
failing update. Recover by starting the previous version (the database is unchanged) or a corrected one. Do not remove volumes.

Restore and update are separate: restore an older backup into a separate empty database with the matching key ring and image, then start the
newer image on it; the ordinary startup backs it up and migrates it. There is no reverse DDL. Downgrading the image does not downgrade the database.

A real image/volume update N to N+1 and Owner VM/NAS/SAB acceptance remain tracked in `#1201`.
