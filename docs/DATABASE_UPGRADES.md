# Release database upgrades

Upgrade support starts with the first official clean-start release on `main`.
Current pre-release baselines may still change. Importing an old
unversioned database and modifying the Owner's running test database remain outside
this package. An existing unversioned database is left unchanged at startup.

## Startup contract

Stop the previous Jularr process before starting the replacement image against the
same database. Serialized concurrent starts do not permit mixed application/schema
versions to serve requests against one database at the same time.

- `DatabaseBaseline` is the single installation and upgrade owner. Before other
  hosted workers start, a PostgreSQL session advisory lock serializes concurrent
  installers and upgraders in the same database. Backup I/O runs outside a database
  transaction; the migration transaction revalidates the schema afterward.
- Fresh installation applies the embedded baselines, schema history and any
  registered ordered migrations in one transaction. A second start is idempotent.
- `DatabaseSchemaVersions` records every applied version and its cumulative SQL
  fingerprint. Gaps, modified history, an empty history or a newer schema stop a
  versioned startup before schema changes. Downgrading the application image does
  not downgrade the database.
- Before upgrading existing versioned data, Jularr publishes a complete PostgreSQL
  custom-format dump, key ring and manifest through the existing backup owner.
  The manifest identifies the **source** schema version and fingerprint. The
  `Backup:Path`, `Backup:Passphrase` and persistent key directory remain canonical.
  Existing accounts require an available, nonempty persistent key ring. Missing
  tools, a failed dump, inaccessible backup storage or failed key copying
  abort the upgrade. No schema migration or new version entry has run at that point.
- Migrations and version entries commit together. Failure rolls back their DDL and
  data changes; the completed pre-upgrade backup remains available. Startup can retry.
  Upgrade backups are not pruned during startup.

## Adding a supported release upgrade

At the first official release, freeze the baseline SQL and its version-1 fingerprint.
Later changes add reviewed, embedded SQL to `DatabaseBaseline.Migrations` in
contiguous order from version 2. The first version (2) creates the provider catalog
`ExternalWorks` using the same backup-first upgrade mechanism. Scripts contain no transaction-control commands;
startup owns the transaction. Do not edit previously released scripts or run a
second migration mechanism. Each version's fingerprint includes all previous SQL,
with normalized line endings. Fresh installations replay the same history.

Restore and upgrade are separate operations. Stop the application before restoring
an older release into a **separate empty database**, recover its matching key ring,
then start a supported newer image against that restored database and persistent
data directory. The ordinary startup path creates its own backup and upgrades it.
The existing setup restore UI continues to require the current schema fingerprint;
it does not restore an older schema underneath a running newer application. Keep
the old database, image and backups until recovery has been verified. For sealed
keys, use the matching release's restore flow and backup passphrase.

There is no general reverse-DDL promise. Recover a failed deployment using its
pre-upgrade backup, matching image and key ring. Never remove the original volumes
as an update or recovery step.

## Verification and remaining release gate

`DatabaseBaselineTests` exercises concurrent fresh starts, preserved unversioned
startup, incompatible version history, backup failure and atomic installation
rollback. Real PostgreSQL-18 dump/restore tests additionally preserve accounts,
password/token bytes, profiles, Works, requests, paused monitoring, credentials,
download jobs and progress across a failed migration, retry, concurrent starts,
restart, restore and upgrade; the sealed key ring is recovered as well.

The migration fixture is not a published N/N+1 image pair. Before release,
`#1201` still requires frozen release artifacts, an actual image/volume upgrade,
the combined storage/download/artwork schema, supported-platform recovery and
Owner VM/NAS/SAB acceptance. No active database was changed by these tests.
