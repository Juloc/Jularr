# Admin Backup / Restore — V1

Status: approved planning direction. The existing Clean mockup in this folder is the visual reference; this text is binding and wins on conflicts.

Visual reference:
- `docs/mockups/admin-backup-restore/file_000000009c3c8243b9ebfe9bd5da491a.png`

Global UX rules:
- `docs/UX.md`

Related contracts:
- Storage: `docs/mockups/admin-storage/SPEC.md`
- Migration: `docs/mockups/admin-migration/SPEC.md`
- System/Diagnostics: `docs/mockups/admin-system-diagnostics/SPEC.md`
- Acquisition: `docs/mockups/admin-acquisition-settings/SPEC.md`

## Purpose

Backup / Restore protects **Jularr application state**.

It must be able to recover an instance after:
- application/configuration corruption;
- failed migration/update;
- database loss;
- accidental administrative changes;
- moving Jularr to another host when a portable backup was created.

It is **not** a media-file backup product.

Canonical library payloads such as movies, episodes, books, manga archives, audiobooks, ROMs and other managed media remain on Storage and are not copied into a normal Jularr application-state backup.

## Current implementation on dev

The current implementation is only a partial predecessor:

- `AcquisitionBackupService`
- `AcquisitionBackupBundle`
- export/restore controls currently exposed through `/Settings/Acquisition`

It currently:
- exports selected JSON files under `/data/acquisition`;
- validates JSON and known filenames before restore;
- previews whether each file changes;
- preserves already-encrypted acquisition secrets as stored;
- can only decrypt those secrets on another installation when the relevant ASP.NET Data Protection keys are also available.

It does **not** currently provide:
- a full PostgreSQL application-state backup;
- a general Jularr backup archive;
- cross-domain restore;
- backup history/catalog;
- scheduled backups;
- whole-restore rollback;
- portable encrypted secret recovery;
- dependency-aware selective restore.

The target implementation must absorb this functionality into one general Backup/Restore system. Do not keep a second user-facing "Acquisition backup" system permanently.

Legacy acquisition bundles may remain importable as a **legacy Acquisition-only restore/import source**, but they must never be presented as full Jularr backups.

## Ownership

Backup / Restore owns:
- backup archive creation;
- backup manifest/versioning;
- backup validation;
- scheduled backup policy;
- retention of Jularr backup archives;
- restore preflight/preview;
- restore execution;
- restore validation/report;
- automatic pre-restore safety backup.

Storage owns:
- physical Mounts;
- Backup Target paths/roles;
- capacity/availability of the target.

System/Diagnostics owns:
- runtime health;
- logs;
- dependency diagnostics.

Migration owns:
- transforming external/legacy systems into Jularr;
- Sonarr/Radarr/etc. migration semantics.

Backup/Restore restores a **known Jularr backup format**. It is not a generic migration importer.

## Backup content model

A full backup is a versioned archive with a manifest and integrity metadata.

Conceptual contents:

```text
Jularr Backup
├─ manifest
├─ PostgreSQL application-state snapshot
├─ durable non-database configuration/state
├─ optional portable secret material
└─ checksums / validation metadata
```

The archive format must be versioned independently from the Jularr application version.

### Manifest

The manifest records at minimum:
- backup format version;
- Jularr application version;
- database/schema version;
- created UTC timestamp;
- source instance identifier;
- source instance display name;
- backup reason/type;
- included scopes/domains;
- secret mode;
- archive checksum/integrity data;
- required restore capabilities/version range;
- whether the backup completed cleanly.

Do not rely on the filename as authoritative metadata.

## Included state

A **full application-state backup** includes durable state required to reconstruct Jularr behavior, including where currently persisted:

- canonical PostgreSQL application data;
- Accounts and Profiles;
- roles/groups/capabilities;
- profile settings/preferences;
- canonical Media Core identity and provider mappings;
- library structure records and file metadata;
- progress/history/bookmarks/highlights;
- Learning state;
- requests/wanted/monitoring state;
- Acquisition Profiles and Release Rules;
- durable acquisition configuration;
- Provider configuration;
- Downloader configuration;
- Storage configuration/LibraryRoot definitions;
- instance/module/general settings;
- appearance settings;
- notification configuration/subscriptions;
- AI configuration/policies;
- other durable module settings explicitly registered with the backup system.

"Full" means full **application state**, not physical media payloads.

## Explicitly excluded

Normal backups do not contain:
- movie/episode/video payload files;
- EPUB/PDF/CBZ/media payloads;
- audiobook/audio payloads;
- ROM/game payloads;
- Native Download Workspace incomplete data;
- repair/extract scratch data;
- transcode/temp data;
- regenerable caches;
- artwork derivative caches when they can be rebuilt;
- runtime health snapshots;
- active in-memory sessions;
- transient telemetry;
- application log files;
- previous backup archives;
- arbitrary files found under `/data` that have not been explicitly registered as durable state.

Do not implement "tar all of /data" as the backup contract.

## Database consistency

The PostgreSQL portion must represent one consistent database snapshot.

The backup engine must not produce a "successful" backup made from mutually inconsistent tables while normal writes continue across unrelated points in time.

Implementation may use the appropriate PostgreSQL snapshot/dump mechanism, but the user-facing contract is:
- one consistent database backup point;
- clearly failed if a consistent snapshot cannot be created;
- never silently omit database domains.

The backup manifest stores enough schema/version information for compatibility checks before restore.

## Non-database durable files

Non-database state is included through an explicit allowlisted/registered backup contribution contract.

Each contributing module must define:
- which durable artifacts belong in backup;
- how they are validated;
- whether they contain secrets;
- restore dependencies;
- whether they can be restored selectively.

Do not recursively copy whole module directories when they also contain cache/runtime files.

The existing Acquisition JSON stores should migrate into this mechanism.

## Secrets and portability

Secrets must never be exported in plaintext.

The UI exposes a clear secret mode when creating/exporting a portable backup:

### Portable with secrets

- contains the protected material required to restore configured credentials;
- the archive must be encrypted with a recovery password/key;
- recovery material is never stored inside the archive in a form that can decrypt itself;
- the password/key is never written to logs/history.

Use this when the backup should restore integrations on another Jularr installation.

### Without portable secrets

- secret values/key material are omitted or remain installation-bound;
- non-secret configuration is retained;
- restore explicitly lists integrations that require credentials to be entered again.

This mode is valid for users who do not want portable credentials in an export.

### Existing Data Protection state

Current acquisition secrets are installation-bound through ASP.NET Data Protection.

The new backup system must not pretend that copying the encrypted JSON values alone makes those credentials portable.

If protected key material is included for portability, it must only exist inside an appropriately encrypted backup payload.

## Backup destinations

### Stored backups

Scheduled/server-side backups require a configured Storage role suitable for backups.

Storage owns the path and Mount. Backup/Restore only references the configured Backup Target.

The UI shows:
- target name;
- Mount;
- free space/status;
- whether the target is currently writable.

A backup target on the same physical failure domain as the primary Jularr data may be allowed but should show a non-blocking resilience warning.

No arbitrary filesystem path entry belongs on Backup/Restore.

### Direct export

An authorized admin may create a backup and download/export the resulting archive directly without permanently retaining it on a Backup Target, where implementation permits.

Temporary export files must be cleaned up.

## Page structure

One Admin destination: **Backup & Restore**.

Desktop page order:

1. status / latest backup summary;
2. automatic backup policy;
3. primary actions;
4. backup table/history;
5. selected backup detail when opened.

Do not create several shallow subpages when a focused page + dialogs/wizards is sufficient.

## Header / status

Show only useful status:
- latest successful backup;
- latest failed backup when applicable;
- automatic backup enabled/disabled;
- configured Backup Target;
- next scheduled backup when scheduling is enabled.

Primary actions:
- **Backup erstellen**
- **Backup importieren / hochladen**
- optional **Jetzt sichern** when scheduled policy exists

Do not use dashboard-style vanity statistics.

## Automatic backup policy

Configurable:
- enabled;
- frequency/schedule;
- Backup Target;
- retention count;
- optional maximum age;
- secret mode/encryption policy supported for automatic backups;
- optional pre-update backup policy when update orchestration supports it.

Retention applies only to backup archives owned by Jularr.

Retention deletion never touches media/library data.

The UI shows the effective next run and warns when the target is unavailable or nearly full.

## Backup list

Desktop columns:
- Created
- Type / reason
- Jularr version
- Scope
- Size
- Validation
- Location
- Actions

Types/reasons may include:
- Manual
- Scheduled
- Pre-Restore
- Pre-Update

Validation states:
- Valid
- Not yet validated
- Warning
- Invalid
- Incompatible

Actions:
- Details
- Validate
- Download/export
- Restore
- Delete archive

Unavailable/incompatible backups remain inspectable; Restore is disabled with the exact reason.

## Backup detail

Show:
- manifest metadata;
- source instance;
- created timestamp;
- app/schema versions;
- included domains;
- excluded classes;
- secret mode;
- integrity result;
- compatibility result;
- archive size/location;
- creation reason;
- validation errors/warnings.

Do not expose secret values.

## Create Backup dialog

Keep creation short.

Required:
- scope;
- destination/export behavior;
- secret portability mode when relevant.

### Scope

Primary choices:
- **Vollständiger Jularr-Zustand** — normal/recommended backup.
- **Konfiguration** — optional configuration-only export when supported by stable domain contracts.

Do not offer arbitrary database-table checkboxes.

Advanced/selective domain exports may exist only for domains with an explicit dependency-safe backup contract.

### Creation flow

`Configure → Preflight → Create → Verify → Ready`

Preflight checks:
- PostgreSQL available;
- Backup Target available/writable when used;
- sufficient free space estimate where available;
- encryption/recovery requirements satisfied;
- no incompatible maintenance operation already running.

A created archive is not marked Ready until its own integrity verification passes.

## Restore entry points

A restore may start from:
- a stored backup row;
- an uploaded/imported Jularr backup archive;
- a recognized legacy acquisition bundle, which opens a restricted Acquisition-only import path.

Uploaded files are validated before they are persisted or used.

## Restore wizard

Restore is a dedicated wizard because it is destructive.

Steps:

1. **Backup wählen / entsperren**
2. **Validieren**
3. **Umfang**
4. **Vorschau & Konflikte**
5. **Bestätigen**
6. **Wiederherstellen**
7. **Validierung & Bericht**

### 1. Select / unlock

Show:
- source;
- backup timestamp;
- source instance;
- archive version;
- encryption/secret mode.

Encrypted portable backup requires the recovery credential.

Do not disclose whether a guessed password was "almost" correct.

### 2. Validate

Checks:
- archive integrity/checksum;
- manifest validity;
- supported backup format version;
- Jularr application compatibility;
- database/schema compatibility;
- required modules/capabilities;
- archive completeness;
- secret payload decryptability where required;
- sufficient temporary/target storage;
- PostgreSQL availability;
- current Backup Target availability for the mandatory pre-restore safety backup.

A backup from a newer unsupported format/runtime is blocked rather than guessed.

### 3. Restore scope

Primary option:
- **Vollständig wiederherstellen**

Selective restore is allowed only at explicit domain boundaries whose dependencies are modeled.

Examples that may become selectable when supported:
- instance/configuration;
- users/profiles/permissions;
- Acquisition Profiles/Rules;
- Provider/Downloader settings;
- appearance/general settings.

Do not expose raw tables/files.

If selecting one domain requires another, the wizard:
- automatically includes the required dependency with explanation; or
- blocks the unsafe combination.

Media identity/progress/library state must not be partially restored unless its complete dependency graph is supported.

### 4. Preview & conflicts

Before mutation, show a semantic summary.

Examples:
- Accounts/Profiles changed
- Works/media records changed
- Acquisition Profiles replaced
- Provider configurations changed
- credentials requiring re-entry
- configured Storage roots missing on this host
- modules unavailable on current instance
- settings that will remain unchanged in a selective restore

For full restore, make clear that current application state will be replaced by the backup state.

Do not show only "N files will change".

### Storage reconciliation

A backup restores Storage **configuration**, not the media files themselves.

When restored LibraryRoots/Mount expectations do not exist on the current host:
- do not invent paths;
- do not delete media records;
- mark affected Storage configuration unresolved;
- require mapping/reconciliation through Storage/Migration tooling;
- show affected roots before confirmation.

### External integrations

After restore, integrations are classified:
- ready;
- credentials required;
- endpoint unreachable;
- disabled by current module/policy.

A temporarily unreachable integration does not invalidate the whole backup if its configuration is structurally valid.

### 5. Confirmation

Confirmation summarizes:
- backup timestamp/source;
- restore scope;
- number/type of changed domains;
- critical warnings;
- whether active work will be paused;
- mandatory pre-restore safety backup.

A destructive full restore requires an explicit confirmation action.

Do not require typing artificial phrases unless there is a concrete safety reason.

## Pre-restore safety backup

Before the first destructive mutation, Jularr creates a **Pre-Restore** backup of the current state.

Rules:
- it uses the same canonical backup engine;
- it must complete and verify successfully before restore proceeds;
- failure blocks normal UI restore;
- it is retained separately from the backup being restored;
- the restore report links to it for recovery.

Do not claim rollback is available unless this safety backup was successfully created and verified.

## Restore execution

Once destructive mutation starts:
- normal cancellation is disabled;
- new mutating admin operations/jobs are quiesced or blocked as required;
- affected services use a maintenance/restoring state;
- progress is stage-based, not fake percentage animation.

Example stages:
- Quiesce
- Database restore
- Durable configuration restore
- Schema compatibility/migration
- Rebuild/reload runtime caches
- Validate
- Resume

Active user playback/download behavior during restore must be deterministic. The implementation should stop/block operations that could write inconsistent state rather than attempting live full-state replacement.

## Post-restore validation

Restore is not complete when files/rows were merely written.

Validate at minimum:
- database opens and expected schema is valid;
- required canonical settings can be read;
- Accounts/Profiles resolve;
- instance/module configuration resolves;
- Storage configuration loads;
- Provider/Downloader configuration loads;
- backup-managed durable files parse successfully;
- no required restore migration remains incomplete.

Then report:
- Success
- Success with warnings
- Failed / recovery required

Warnings may include:
- unavailable Storage mount;
- credentials must be re-entered;
- external service unreachable;
- optional module unavailable.

## Failure and recovery

If restore fails after mutation begins:
- preserve the failure report;
- do not silently continue with a partially restored state;
- keep the instance in a safe maintenance/degraded state as needed;
- offer recovery from the verified Pre-Restore backup.

Automatic rollback may be implemented only if it is itself transactional/reliable for all affected state. Otherwise present an explicit recovery operation.

Never label a per-file atomic write as a whole-application transactional restore.

## Selective restore semantics

Selective restore is **domain replacement/import**, not raw overwrite of arbitrary files.

Every selectable domain must define:
- identity keys;
- dependencies;
- conflict behavior;
- secret behavior;
- validation;
- whether references outside the selected domain are preserved or remapped.

If a domain does not define this, it is not selectable.

This prevents destructive combinations such as restoring progress referring to Works that were not restored.

## Legacy Acquisition backup migration

Existing `AcquisitionBackupBundle` archives remain recognizable during the transition.

Behavior:
- identify them as **Legacy Acquisition Backup**;
- preview the contained acquisition settings;
- restore/import only into current Acquisition-related contracts;
- migrate old quality/profile/policy structures forward where required;
- never imply that users/media/progress/database were included;
- retire `/Settings/Acquisition` backup controls after feature parity.

No permanent second backup catalog.

## Permissions

Backup/Restore is a high-risk system capability.

Default:
- Owner/system-administration capability required to create full backups;
- Owner/system-administration capability required to restore;
- download/export of backups containing portable secrets requires the same high-level capability;
- permission checks are enforced server-side, not only by hidden buttons.

Media-manager acquisition permissions alone do not imply full-instance backup/restore access.

## Audit / History

Create, validate, delete and restore actions produce durable Admin History/operation records with:
- actor;
- timestamp;
- backup identifier;
- scope;
- result;
- warning/error summary.

Never log:
- recovery passwords;
- API keys;
- decrypted secrets;
- encryption key material.

The backup table is a backup catalog, not a replacement for global Admin History.

## Detailed / Compact Admin density

The global Admin `Detailliert | Kompakt` preference applies.

Detailed:
- richer manifest/compatibility descriptions;
- expanded warning context;
- restore-preview explanations.

Compact:
- denser backup table;
- less repeated explanatory copy;
- same warnings, validation and actions.

Compact must never hide restore risk or compatibility errors.

## Visual contract

Clean baseline:
- light neutral operational surface;
- purple Jularr accent;
- compact Admin sidebar;
- table/list oriented;
- restrained status colors;
- no decorative anime/Japanese artwork.

Dark mode is first-class.

Use warnings for real risk only; do not cover every backup row in colored badges.

## Platforms

### Desktop

Primary management/restore surface.

Full:
- backup policy;
- backup table/detail;
- create dialog;
- restore wizard;
- validation report.

### Tablet

Supported with stacked detail panels and full-screen restore wizard where needed.

### Mobile

Supports:
- backup status/list;
- create manual backup;
- inspect validation;
- emergency restore flow with full-screen steps.

Dense configuration and large conflict review may recommend Desktop but cannot become unusable solely because viewport is mobile.

### TV

Unsupported.

## Required states

- no Backup Target configured
- no backups
- loading catalog
- creating
- estimating/preflight
- verifying
- ready
- scheduled backup enabled
- target offline
- target read-only
- target nearly full
- backup invalid
- checksum mismatch
- encrypted/locked
- wrong/unusable recovery credential
- legacy Acquisition backup detected
- backup from unsupported newer version
- backup from supported older version
- restore preview ready
- restore blocked by dependency
- storage-root conflict
- credentials required after restore
- creating Pre-Restore backup
- restoring / maintenance state
- restore success
- restore success with warnings
- restore failed before mutation
- restore failed after mutation
- recovery available
- permission denied

## Acceptance criteria

V1 is complete only when:

1. one canonical backup engine represents full Jularr application state;
2. PostgreSQL state is captured consistently;
3. non-database durable state is explicitly registered/allowlisted;
4. canonical media payload files and transient workspaces are excluded;
5. created archives have a versioned manifest and integrity verification;
6. secrets are never exported plaintext;
7. portable-secret behavior is explicit and actually recoverable;
8. stored backups use Storage-owned Backup Targets;
9. restore always performs compatibility/integrity preflight;
10. restore shows a semantic preview before mutation;
11. normal UI restore creates and verifies a Pre-Restore safety backup first;
12. failure cannot be reported as success with silently partial state;
13. post-restore validation runs before returning the instance to normal operation;
14. legacy Acquisition backups can be recognized without being mistaken for full backups;
15. the old Acquisition-specific backup UI is retired after general Backup/Restore reaches parity;
16. all destructive actions are permission-gated server-side;
17. Light/Dark and global Admin density behavior are implemented.

## Must not implement

- No media-file backup masquerading as application backup.
- No recursive raw backup of all `/data`.
- No plaintext secrets.
- No self-decrypting portable archive.
- No backup archive recursively containing previous backup archives.
- No arbitrary filesystem destination typed on this page.
- No restore without validation and explicit confirmation.
- No normal full restore without a verified Pre-Restore safety backup.
- No arbitrary table/file checkbox restore UI.
- No unsupported cross-version restore by guesswork.
- No silent partial restore.
- No fake transactional/rollback claim.
- No restore that silently invents missing Storage paths.
- No permanent separate Acquisition backup system.
- No dependence on legacy per-media tables as the long-term backup format.


## Storage Lifecycle backup boundary (#414)

Backup/Restore must preserve durable Storage Lifecycle **intent/state**, not media payloads.

Include where implemented:
- lifecycle policies and policy revisions;
- Off/Review/Automatic mode;
- explicit Keep / Never-delete / temporary-Keep protections;
- quality floors, exclusions and maintenance/resource rules;
- root/library policy assignments;
- reacquisition suppressions/guards required to prevent intentionally cleaned media from being immediately reacquired;
- lifecycle audit/history according to the normal retention contract.

Do not include:
- canonical media payloads;
- trash/grace-period media bytes;
- optimized/transcode working outputs;
- temporary migration copies;
- rebuildable storage analytics/cache.

Restore safety:
- restored policies remain bound to canonical IDs where valid;
- missing/remapped LibraryRoots are unresolved until Storage mapping/validation completes;
- destructive Automatic policies stay paused for unresolved/offline-invalid root assignments;
- restore preview must call out lifecycle policies/protections that cannot be mapped;
- Backup/Restore never executes a lifecycle cleanup merely because restored state references a missing file/root.
