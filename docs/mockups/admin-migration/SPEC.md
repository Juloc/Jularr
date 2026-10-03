# Admin Migration Center — V1

Status: approved planning direction. The existing Clean Migration Center mockup in this folder is the visual reference; this text is binding and wins on conflicts.

Visual reference:
- `docs/mockups/admin-migration/file_00000000ca508210935cfca79395c3bb.png`

Global UX rules:
- `docs/UX.md`

Related contracts:
- Acquisition Profiles: `docs/mockups/admin-acquisition-settings/SPEC.md`
- Storage: `docs/mockups/admin-storage/SPEC.md`
- Downloader external clients: `docs/mockups/admin-downloader-external-clients/SPEC.md`
- Library Reconciliation: `docs/mockups/admin-folder-import-mapping/SPEC.md`
- Backup/Restore: `docs/mockups/admin-backup-restore/SPEC.md`

If an image and this specification conflict, this specification wins.

## Purpose

Migration Center performs preview-first migration from older Jularr data and supported external systems into the canonical Jularr model.

All imported media must end in:

`Work -> Structure -> Edition -> Version -> Asset/File -> Track`

Migration never keeps a legacy source model as a second long-term authority.

Migration is also the owning Admin surface for **external-manager coexistence and handover state** when a supported adapter remains connected after the initial import. This must not create a second canonical media/acquisition model.

## Backup / Restore boundary

Migration and Backup/Restore are separate Admin owners.

Route source material as follows:
- a recognized current/versioned Jularr backup archive -> **Backup & Restore**;
- a legacy `AcquisitionBackupBundle` -> **Backup & Restore** legacy Acquisition-only restore/import flow;
- an old Jularr database/configuration/layout that is not a supported backup archive -> **Migration Center**;
- Sonarr/Radarr/Readarr/Plex/Jellyfin/Emby/folder/JSON/CSV source migration -> **Migration Center**.

Migration adapters may be reused internally by restore/version-upgrade code, but the UI must not offer the same artifact as two competing import/restore workflows.

## Current Sonarr implementation on dev

The existing Sonarr integration already has production-relevant safety behavior that must be preserved while Migration Center is generalized:

- per-Anime `ReadOnlyCoexistence`;
- per-Anime `ParallelAcquisition`;
- per-Anime `JularrManaged`;
- Sonarr observation of series, episode files, queue and recent history;
- fail-closed behavior when Sonarr ownership cannot be verified;
- conflict guards before grab/import/rename;
- explicit handover/revert behavior;
- migration/ownership log;
- path translation before Sonarr path comparisons.

These are implementation inputs, not a requirement to keep Anime-specific models permanently.

## Approved coexistence model

Supported **manager-style integrations** use one common three-mode ownership contract:

1. **Extern verwaltet**
   - the external manager has mutation authority for that Work/domain;
   - Jularr may observe, map identities, display state and import non-conflicting metadata/progress evidence;
   - Jularr must not independently grab/import/rename/delete managed library content for that ownership scope;
   - if the external manager cannot be observed when safety depends on it, Jularr fails closed.

2. **Gemeinsam**
   - this is a **permanently supported advanced mode**, not only a temporary migration state;
   - both systems may perform supported operations, but every mutation is ownership-checked;
   - active releases/downloads, owned paths and recent external mutations can block Jularr work;
   - Jularr may mutate only work/files/jobs it can prove are safe under the adapter contract;
   - conflicts become actionable Migration/To-Do diagnostics rather than being guessed through;
   - the UI labels this mode as advanced and explains that it requires a healthy source connection for reliable conflict detection;
   - the mode remains available after migration completion as long as the integration stays configured and supports coexistence.

3. **Jularr verwaltet**
   - Jularr owns acquisition/import/naming for the selected ownership scope;
   - the external integration may remain connected in **read-only observation mode** for that Work/scope;
   - observation is used to detect conflicting external monitoring, queue activity, imports, renames or other mutations before they can cause library conflicts;
   - observed external state is evidence/diagnostics only and does not regain ownership automatically;
   - adapters should surface conflicting external activity as actionable Migration/To-Do diagnostics and block unsafe Jularr mutations where required;
   - loss of external observation must not silently transfer authority back to the external manager;
   - supported handover may explicitly disable external monitoring, but never deletes source media or source library records;
   - when switching to **Jularr verwaltet**, the handover UI offers **Externes Monitoring deaktivieren** as an explicit checkbox;
   - for manager integrations such as Sonarr/Radarr, that checkbox is enabled by default when the adapter can perform the change safely;
   - the admin may deselect it and keep external monitoring enabled, but the resulting conflict risk is shown before confirmation;
   - the adapter records whether Jularr changed the external monitoring state so a later revert can restore only the state Jularr itself changed.

These labels are the user-facing contract. Source adapters map their native concepts into these modes only when they actually support management/coexistence capabilities.

A passive source such as Plex/Jellyfin does not show these modes merely because it is a migration source.

### Sonarr compatibility

Existing Sonarr state migrates directly:
- `ReadOnlyCoexistence` → **Extern verwaltet**
- `ParallelAcquisition` → **Gemeinsam**
- `JularrManaged` → **Jularr verwaltet**

The existing Sonarr safety behavior remains the implementation baseline for the generalized contract.

Changing modes is explicit, audited and reversible where the adapter can restore the previous external monitoring state.

### Monitoring state during handover

When a Work changes from **Extern verwaltet** to **Jularr verwaltet**, Jularr imports the source monitoring state into the canonical Jularr monitoring model where a safe semantic equivalent exists.

Rules:
- Work/Season/Episode monitoring granularity is preserved where supported;
- the handover preview shows the exact monitoring changes before commit;
- unsupported source monitoring semantics are shown as warnings and are not guessed;
- ownership mode and monitoring state remain separate concepts;
- changing ownership must not silently broaden monitoring beyond the source state;
- explicit Jularr monitoring choices made after migration are not later overwritten by read-only source observation.

### Handover confirmation

For integrations that can change source monitoring, switching to **Jularr verwaltet** shows a concise handover confirmation.

Default option:
- **Externes Monitoring deaktivieren** — checked by default.

The preview must show exactly what will change on the source side, for example:
- Sonarr series monitoring: On → Off;
- affected Work/series;
- active queue/conflict blockers;
- whether the change can later be restored automatically.

If the checkbox is cleared, Jularr keeps read-only observation active and warns that the source manager may still act on the Work. The ownership mode can still become **Jularr verwaltet**, but unsafe Jularr mutations remain blocked whenever the adapter detects conflicting source activity.

A revert may automatically re-enable source monitoring only when Jularr previously disabled it and the adapter can prove that state transition. It must not overwrite unrelated manual changes made in the external system.

### Gemeinsamer Betrieb

**Gemeinsam** may be used indefinitely.

It is intended for cases such as:
- staged migration over a long period;
- Jularr handling selected acquisitions while Sonarr/Radarr still manage other activity for the same Work;
- testing Jularr acquisition/import behavior before complete handover.

Requirements:
- adapter health/observation must be visible;
- ownership checks run before every supported Jularr mutation;
- unresolved ownership/conflict evidence blocks rather than guesses;
- if the external manager cannot be observed, **Gemeinsam fails closed** for new Jularr mutations on the affected Work/scope;
- while observation is unavailable, Jularr may continue read-only display/diagnostics but must not start new grab/import/rename/replace/delete operations that depend on coexistence safety;
- queued or in-progress operations that reach a safety boundary must pause/block instead of assuming the external system is idle;
- recovery is automatic once the adapter is healthy again and ownership can be revalidated;
- the UI must show the blocked reason explicitly, e.g. `Gemeinsam · Sonarr nicht erreichbar · Änderungen pausiert`;
- the UI may recommend switching to **Extern verwaltet** or **Jularr verwaltet** when repeated conflicts occur, but must not switch automatically;
- no timer or forced expiry is attached to the mode.

### Integration lifecycle after handover

Completing migration or switching Works to **Jularr verwaltet** does **not** automatically remove/disconnect the external integration.

Default:
- keep the integration configured;
- continue read-only observation where supported;
- preserve migration provenance and diagnostics;
- let the admin explicitly disable/remove the integration later.

Removing an integration is a separate deliberate Admin action. It must not be coupled to successful migration completion.

### Read-only observation after handover

When a Work is **Jularr verwaltet**, a still-configured manager integration should continue observing that Work where the source API supports it.

Purpose:
- detect that the external manager became monitored again;
- detect active external downloads/grabs;
- detect external imports/renames after handover;
- detect path ownership conflicts before Jularr mutates the same file;
- provide a clear conflict reason in Migration/To-Do.

Rules:
- observation never changes the effective ownership mode by itself;
- observation never gives the external manager permission to mutate Jularr-owned state;
- the adapter may fail closed for a Jularr mutation when current external state is required to prove safety;
- disabling/removing the integration ends observation, but does not rewrite Work ownership automatically;
- the UI shows observation health separately from ownership mode, e.g. `Jularr verwaltet · Sonarr beobachtet`, `Jularr verwaltet · Sonarr nicht erreichbar`.

This preserves the current Sonarr conflict-detection value after handover without keeping Sonarr as a second canonical authority.

### External manager cardinality

For one Work, Jularr supports at most **one effective external manager plus Jularr**.

Rules:
- one Work cannot simultaneously be primarily managed by Sonarr and another external acquisition manager;
- `Gemeinsam` means Jularr + that one external manager, not a three-way or N-way manager mesh;
- conflicting integration defaults for the same Work are rejected during preview/configuration;
- migration may still read metadata/progress evidence from passive sources such as Plex/Jellyfin without granting them management authority.

This keeps mutation ownership, path safety and handover deterministic.

### Ownership scope

Management ownership is resolved at **Work level only**.

Examples:
- one TV series Work → Extern verwaltet / Gemeinsam / Jularr verwaltet;
- one Anime Work → its own ownership mode;
- one Movie Work → its own ownership mode.

Do not split manager ownership by Season, Episode, Volume, Chapter or individual file.

Reason:
- acquisition/import/naming authority stays deterministic;
- file/path ownership checks stay explainable;
- handover/revert remains auditable;
- external-manager conflict detection does not need to reason about mixed managers inside one Work.

This does **not** flatten monitoring granularity.

Monitoring/Wanted may still vary below Work level where the canonical model supports it, for example:
- one Season monitored, another not;
- selected Episodes wanted/unwanted;
- chapter/volume monitoring where applicable.

Ownership answers **who may manage the Work**. Monitoring answers **which units Jularr should want/process**. These are separate concepts.

A Work-level ownership change must not silently rewrite Season/Episode monitoring selections unless the source adapter explicitly maps monitoring as part of the handover preview and the admin confirms it.

### Default ownership by integration and media type

Ownership defaults are configured per **integration × media/content type**, not as one coarse switch for the whole integration.

Example:
- Sonarr + Series/TV → **Extern verwaltet**
- Sonarr + Anime → **Jularr verwaltet**
- Jularr may therefore manage Anime while Sonarr remains the default manager for normal TV series.

A manager-style integration may expose only the media/content types it can actually manage.

Resolution precedence:
1. explicit per-Work ownership override;
2. integration + media-type default;
3. Jularr-managed fallback only when no external-manager default applies.

Per-Work override examples:
- one TV series can be **Jularr verwaltet** even when Series/TV defaults to Sonarr;
- one Anime Work can be **Gemeinsam** during a staged handover even when Anime defaults to Jularr.

Safety rules:
- only one external integration may be the effective primary manager for the same Work/scope unless an explicitly supported **Gemeinsam** contract defines the overlap;
- conflicting defaults are rejected during configuration rather than resolved by arbitrary priority;
- changing a media-type default shows the number of affected Works and requires preview/confirmation when existing effective ownership would change;
- existing explicit per-Work overrides are preserved when the default changes unless the admin explicitly resets them.

The UI may present this inside each integration as a compact table such as:
`Medientyp | Standardverwaltung | Betroffene Works | Overrides`.

Do not force admins to configure thousands of Works individually when a media-type default is sufficient.

## Main flow

The migration engine uses one normalized plan, but the wizard is **adaptive by source**.

Conceptual phases:
1. Quelle
2. Verbindung / Scan
3. Medien & Identitäten
4. Policies / Profile / Monitoring
5. Dateien & Benutzer
6. Konflikte
7. Optionen
8. Dry Run
9. Migration / Report

The UI does not force every source through nine visible pages.

Examples:
- Folder migration skips Users, Progress and Acquisition Profiles.
- Jellyfin/Plex/Emby skip Acquisition Profiles and Custom Formats.
- Sonarr/Radarr expose Monitoring, Profiles and Custom Formats prominently.
- Old Jularr may expose nearly all phases.

The existing mockup stepper is a visual baseline; implementation may combine adjacent phases into one screen when that keeps the flow clearer.

## Supported migration source families

### Alte Jularr Version

Purpose:
- migrate old Jularr/per-media-type databases and configuration into the current canonical model

May import:
- media/work identity
- seasons/episodes/volumes/chapters
- files/assets/tracks
- provider IDs/provenance
- library membership
- monitoring/acquisition state
- user accounts/groups where compatible
- progress/history
- requests
- collections/lists
- acquisition profiles/rules where compatible
- provider/settings mappings
- configuration

Legacy tables are source evidence only and are not preserved as permanent runtime models.

### Sonarr

Purpose:
- migrate Series/Anime automation state into Jularr without losing acquisition behavior

The importer should use Sonarr API/data rather than infer everything from filenames.

#### Sonarr media import

Import where available:
- Series identity and provider IDs
- Series type
- Seasons
- Episodes
- Episode files
- root folder/source path
- file path
- quality
- release group
- language/media-info evidence where available
- tags
- alternate titles/provider identifiers where useful for resolution

Series/season/episode data is mapped into the canonical Jularr hierarchy.

#### Sonarr monitoring state

Monitoring must be preserved where a meaningful Jularr equivalent exists.

Import:
- Series monitored/unmonitored state
- Season monitored/unmonitored state
- Episode monitored/unmonitored state
- monitor-new-items / future-episode behavior where available
- series-level monitoring mode when Sonarr exposes one

Migration preview must show how Sonarr monitoring maps to Jularr monitoring/acquisition policy.

Do not collapse all Sonarr monitoring into a single Work boolean if Sonarr has more specific season/episode state.

#### Sonarr Quality Profiles

Import Sonarr Quality Profiles into Jularr AcquisitionProfiles where semantically compatible.

Preserve:
- profile name
- enabled/allowed qualities
- quality ordering/preference
- grouped/equivalent quality groups where representable
- upgrades allowed
- upgrade/cutoff quality
- language preference where present
- minimum/custom-format score thresholds where supported
- upgrade-until custom-format score where supported

Every imported profile must be previewed before creation/merge.

If Jularr has a richer language/profile model, migration may split one Sonarr profile into:
- a reusable quality/release profile
- explicit target language settings

The dry run must show this transformation.

#### Sonarr Custom Formats / custom rules

Import Sonarr Custom Formats and their profile-specific scores.

Every source Custom Format/condition must receive one explicit conversion result:
- **1:1 übernommen** — native Jularr rule has equivalent semantics
- **Übersetzt** — converted into one or more Jularr-native scoring rules with equivalent intent
- **Nicht unterstützt** — no safe equivalent; not imported automatically

Approximate/heuristic conversion without a visible warning is not allowed.

Preserve, where supported:
- Custom Format name
- condition groups
- release-title regex/terms
- source
- resolution
- quality modifier
- release group
- language
- size ranges
- indexer flags
- negate
- required
- other Sonarr condition attributes that have a safe Jularr equivalent
- include-in-renaming metadata only if Jularr naming policy supports an equivalent

Also import the score of each Custom Format **per Quality Profile**.

Important:
- the Custom Format definition and its score are separate concepts
- score 0 remains informational where equivalent
- negative/positive scores must preserve intent
- minimum score / upgrade-until score belongs to the imported profile
- unsupported conditions are not silently discarded; they appear in the migration report

Jularr may translate Sonarr Custom Formats into native AcquisitionProfile scoring rules, but must not retain Sonarr-specific runtime logic as a parallel scoring engine.

#### Sonarr tags and linked behavior

Import tags and source associations where useful.

Potential mappings include:
- series tags
- indexer/tag associations
- delay-profile/tag relationships
- other source policy associations

Tags are not automatically treated as Jularr user-facing media tags.

The importer resolves their operational meaning first.

#### Sonarr paths

Root folders are mapped through the canonical Storage contract.

Migration may:
- map a Sonarr root to an existing Jularr LibraryRoot;
- invoke/reuse the Storage LibraryRoot creation flow when the admin explicitly creates a missing destination;
- set/import a Work target-root override where the canonical library/monitoring contract supports it.

Migration does **not** own an independent root-folder model.

Do not blindly persist Sonarr paths as canonical identity.

If Sonarr and Jularr see the same files under different mount prefixes, that translation belongs to the **Sonarr migration/coexistence adapter**.

Path-mapping fields:
- Sonarr/source prefix;
- Jularr-local target inside permitted Storage;
- optional source/root scope where needed.

Matching semantics reuse the same shared resolver as external download clients:
- longest matching prefix wins;
- path-boundary aware;
- slash/backslash normalization where applicable;
- deterministic overlap handling;
- live test: source path → resolved Jularr path → reachable/unreachable.

The local side must resolve inside permitted Storage. Migration never creates a second global Remote Path Mapping page.

**Default behavior is link/reconcile in place.**

Sonarr migration itself does not rename/move the media library by default.

If the admin wants cleanup/reorganization after migration, hand the resulting library to the dedicated **Library Reconciliation** flow for explicit rename/move preview and execution.

#### Sonarr configuration migration — separate from data migration

Sonarr **data/policy migration** and **instance-configuration migration** are separate selections.

Normal Sonarr data/policy migration includes:
- Series/Season/Episode identity
- files/path links
- monitoring state
- Quality Profiles
- Custom Formats + per-profile scores
- operational tag relationships where meaningful

Optional configuration migration may additionally propose:
- naming policy
- quality definitions/size limits
- delay/release timing policy
- indexer configuration
- external download-client adapter configuration
- selected media-management/import settings

Configuration is imported only when Jularr has a clear semantic equivalent.

Indexer/download-client credentials are never silently copied as trusted working configuration. Imported connection settings are shown as proposals and must be reviewed/tested; secrets require explicit protected handling.

Unsupported settings are listed in the final report rather than silently ignored.

#### Sonarr ownership / coexistence

Sonarr migration and ongoing Sonarr coexistence are related but distinct:

- **Migration** translates Sonarr identity, monitoring, profiles/rules and configuration into canonical Jularr state.
- **Coexistence** controls which system may mutate acquisition/library state while Sonarr remains connected.

The approved three-mode ownership contract applies to Sonarr. Existing safety guarantees remain binding:
- no duplicate grab when Sonarr already owns the release/episode;
- no import/rename/delete of a path Jularr cannot prove it may mutate;
- no handover while Sonarr has conflicting active work;
- no revert while Jularr still has conflicting active work;
- inability to observe Sonarr fails closed;
- handover never deletes Sonarr series or media files;
- any Sonarr monitoring mutation is explicit and reversible where supported.

Do not use Migration Center to create uncontrolled dual-write authority.

### Radarr

Purpose:
- movie acquisition/library migration

May import:
- movie identity/provider IDs
- existing files
- monitored state
- root folders
- Quality Profiles
- Custom Formats + per-profile scores
- tags/policy associations
- quality definitions
- naming/import settings where compatible
- indexer/download-client configuration where explicitly selected

Movie data maps to canonical Work/Edition/Version/File structures rather than a dedicated Radarr-compatible core.

Radarr path translation, if required, is owned by the Radarr migration/coexistence adapter and uses the same shared mapping semantics as Sonarr.

### Readarr

Purpose:
- migrate books/ebooks/audiobook-adjacent library automation state where a safe Jularr equivalent exists

May import where available:
- author/work/book identity and provider IDs;
- editions/files/formats;
- monitored state;
- root folders;
- Quality Profiles;
- Custom Formats + profile-specific scores;
- tags/policy associations;
- naming/import settings when semantically compatible;
- indexer/download-client configuration only when explicitly selected and reviewed.

Readarr source hierarchy must be translated into canonical Jularr Work/Structure/Edition/Version/Asset/File/Track concepts. Do not preserve Readarr entities as a second runtime core.

Reading-specific source ambiguity is previewed rather than guessed, especially where one source record may map to Work vs Edition.

### Bazarr

Purpose:
- migrate subtitle-management evidence/configuration where a stable Jularr subtitle/track equivalent exists

May import where supported:
- subtitle language preferences;
- subtitle file associations;
- provider/source configuration proposals;
- scoring/profile-like subtitle preferences only when Jularr has an explicit equivalent.

Bazarr does not become a permanent subtitle runtime authority after migration unless a separately approved coexistence adapter explicitly defines that behavior.

Subtitle files resolve through canonical File/Track relationships. Do not create a Bazarr-specific parallel subtitle model.

### Jellyfin

Purpose:
- use a media server as evidence for existing libraries and user playback state

May import:
- library items
- file paths
- provider/external IDs
- collections/playlists where compatible
- watched/unwatched state
- resume position
- play count/history where supported
- artwork/metadata references where useful

Source users are **never silently created as Jularr accounts**.

For each source profile/user, the wizard must offer:
- map to existing Jularr Account/Profile;
- explicitly create a new Jularr Account/Profile from the migration flow;
- skip this source user.

Creating a new Account/Profile is always an explicit admin action with normal validation/permission rules.

Progress/history is imported only after that mapping is resolved.

Jellyfin is not an acquisition-policy source.

Do not invent Quality Profiles/monitoring rules from Jellyfin playback data.

### Plex

Purpose:
- migrate media-library identity and playback state

May import:
- library items
- file locations
- provider IDs/metadata evidence
- collections/playlists where compatible
- watched state
- resume position
- playback history where available

Plex libraries must be mapped to Jularr LibraryRoots/content types.

Source users/profiles use the same explicit mapping flow:
- map to existing Jularr Account/Profile;
- explicitly create a new Jularr Account/Profile;
- skip.

No source user/profile is auto-created.

Plex labels/collections are imported only when semantics are clear.

### Emby

Same general migration family as Jellyfin:
- existing library/file identity
- provider IDs
- watched/resume/history
- collections/playlists where compatible

Emby users/profiles must be explicitly mapped to an existing Jularr Account/Profile, explicitly created as a new Jularr Account/Profile, or skipped.

Do not treat Emby metadata as a second permanent authority after canonical resolution.

### Ordnerstruktur

Purpose:
- migrate an existing filesystem library without a source application

Input:
- configured safe Storage path / LibraryRoot

Scan:
- folders
- files
- sidecars
- embedded metadata
- filenames
- media probe data

Use the existing Library Reconciliation flow for unresolved mappings.

This source can:
- link files in place
- optionally organize/rename via explicit preview
- create canonical Work/Structure/File mappings

It cannot import:
- users
- playback history
- acquisition profiles
unless provided by another source.

### JSON / CSV

Purpose:
- structured import from exported data or custom migration datasets

Supported conceptual records may include:
- Works/external IDs
- lists/collections
- users
- progress/history
- requests
- acquisition-profile mapping
- path/file mapping

Use a schema-mapping step:
- source column
- target field
- transformation
- required/optional
- validation result

Unknown columns are ignored only after explicit preview.

### Andere Quelle / Expert Adapter

Purpose:
- migration extension point for sources not covered by built-in adapters

A migration adapter must declare:
- source type/version
- supported entities
- required credentials/input
- normalized scan contract
- mapping capabilities
- validation rules

It must output the same normalized migration plan as built-in sources.

No adapter may write directly into legacy/per-source tables as a permanent model.

### Source credentials and secrets

Credentials/API keys from Sonarr/Radarr/Readarr or other manager integrations may be imported **optionally** when the source exposes them and Jularr has a safe semantic equivalent.

Requirements:
- secret import is opt-in and shown separately from normal data migration;
- secret values are never displayed in plaintext after capture;
- values are written through Jularr's canonical protected-secret storage path;
- imported endpoint/credential configuration must be tested before being marked ready;
- a failed connection test leaves the configuration present only as an explicit unresolved proposal/review item;
- migration never silently trusts imported credentials.

If a source does not expose a secret safely, Jularr asks for re-entry instead of attempting recovery tricks.

## Connection & scan

Source-specific connection page may include:
- URL/endpoint
- API key/token
- local backup/file selection
- safe folder selection
- connection test
- source-version detection

Scan options depend on source.

Examples:
- media
- seasons/episodes
- files
- metadata
- users
- progress
- requests
- profiles/rules
- settings

## Analyze contents

Before mapping, show counts by entity.

Examples:
- Works/Series/Movies
- Seasons
- Episodes
- Files
- Users
- Progress entries
- Requests
- Profiles
- Custom Formats
- Conflicts
- Unknown/unmapped items

The admin can inspect samples and unresolved records.

## Mapping

Mapping operates on normalized source records.

Use tabs such as:
- Automatic
- Manual
- Conflicts

Mappings can include:
- source Work -> canonical Work
- source season/episode -> canonical Structure/unit
- source root folder -> LibraryRoot
- source user -> Jularr user
- source Quality Profile -> AcquisitionProfile
- source Custom Format -> native scoring rule
- source monitoring state -> Jularr monitoring policy

Never identify media by title alone when stronger IDs are available.

### Canonical identity matching order

Use strongest available evidence first, for example:
1. stable source/provider IDs with known provider namespace;
2. explicit existing source-to-Work mapping;
3. compatible external-ID intersection;
4. structure/file evidence;
5. normalized title/year/type as supporting evidence only.

A title-only match may be offered for manual review when no stronger identity exists, but must not silently auto-merge ambiguous Works.

## Path mapping ownership

Path translation exists because one specific external source and Jularr see the same storage differently.

Therefore:
- Sonarr mappings belong to the Sonarr migration/coexistence adapter;
- Radarr mappings belong to the Radarr adapter;
- Readarr mappings belong to the Readarr adapter;
- legacy-Jularr mappings belong to that migration source;
- external downloader mappings remain under Downloader → Externe Clients;
- the native Jularr downloader normally requires no remote mapping.

All adapters reuse one shared mapping primitive/resolver instead of implementing subtly different prefix logic.

Storage owns the permitted local Mount/LibraryRoot targets. Migration owns only the external/source-side translation.

No standalone permanent `Import & Routing` page is created.

## Acquisition-policy translation

Migration into Acquisition must target the current integrated Jularr model:

- source Quality Profile → Jularr Acquisition Profile quality/upgrade policy;
- source Custom Format definition → shared Jularr Release Rule definition;
- source Custom Format score in a profile → profile-specific Release Rule effect/score;
- Release Profile must/must-not/preferred terms → Jularr Release Rules;
- Delay Profile → Acquisition Profile wait/source policy;
- source tags → assignment/policy evidence only where their operational meaning is understood;
- monitoring state → canonical monitoring contract, not scoring;
- source root folder → Storage LibraryRoot mapping/default/Work override, not Acquisition.

If a source uses a very large negative score as an implicit hard reject, migration may **suggest** converting it to Jularr's explicit Reject effect, but must not silently infer that intent. The Dry Run shows the proposed conversion.

No source-specific scoring engine remains active after migration.

## Import options

Separate options into two conceptual groups.

### A. Daten & Policies

Selectable entity groups:
- Media
- Metadata/provenance
- Files/path links
- Monitoring state
- Acquisition Profiles
- Custom Formats/rules
- Users
- Progress/history
- Requests
- Lists/collections

### B. Instanz-Konfiguration

Optional, separately enabled:
- Provider/indexer settings
- External download-client settings
- Naming/media-management settings
- Quality definitions/size limits
- Delay/release timing policy
- other source settings with a clear Jularr equivalent

Configuration import is never implied by selecting media migration.

Default file behavior:
- link/reconcile files in place;
- resolve final physical placement through Storage/LibraryRoot state;
- never move/delete/rename source files during normal application migration.

Optional:
- import missing metadata after migration
- store detailed migration report
- hand off library cleanup to Library Reconciliation after migration

### Progress conflict resolution

When source progress/history conflicts with existing Jularr progress:

Default:
- the state with the **newer reliable timestamp** wins.

If timestamps are missing, equal, obviously unreliable or the states are otherwise ambiguous:
- do not guess;
- surface a manual conflict for review.

Rules:
- never use "highest progress wins" as the universal rule;
- preserve both source timestamps/provenance in the migration report where available;
- completed/uncompleted conflicts are treated as semantic conflicts, not simple numeric positions;
- source history entries may be imported without overwriting the effective current resume point when the domains can be preserved independently.

### Collections / watchlists / playlists

Import uses **merge semantics by default**.

Rules:
- source entries are added when not already present;
- existing Jularr entries are preserved;
- migration does not delete local collection/watchlist/playlist membership just because the source does not contain it;
- duplicate identity is resolved through canonical Work/item identity, not title text alone;
- replacement semantics are only allowed as a separately explicit future operation, never as the default migration behavior.

## Conflict handling

Conflict classes:
- duplicate Work
- multiple canonical matches
- missing provider ID
- source path unavailable
- same file already linked
- profile name collision
- Custom Format/rule collision
- incompatible rule condition
- user collision
- progress conflict
- request conflict

Resolution:
- merge
- use existing
- create new
- skip
- manual mapping

Rules are entity-specific; do not use one global overwrite switch for everything.

## Dry Run

Dry run is mandatory before mutating persistent state.

Summary should include:
- created
- merged/updated
- skipped
- conflicts
- manual mappings
- unsupported source fields/settings

Expandable sections show detailed planned changes.

## Execution

Migration runs as a resumable operational job where feasible.

Show:
- current phase
- progress
- counts
- warnings/errors
- concise log

The source remains untouched by default.

No source deletion or move unless explicitly configured for a filesystem migration/reconciliation step.

For an explicitly approved coexistence/ownership handover adapter, narrowly scoped reversible source mutations such as Sonarr monitoring changes may occur only when the adapter contract defines them and the admin explicitly confirms them. This exception never permits deleting source media or source library records.

## Result report

Report:
- created
- updated/merged
- skipped
- conflicts remaining
- unsupported fields
- failed records
- manual follow-up required
- validation result

For Sonarr/Radarr specifically, report profile/rule conversion separately:
- Quality Profiles imported
- Custom Formats imported
- Custom Formats converted 1:1
- Custom Formats translated to native Jularr rules
- unsupported conditions/rules
- score mappings imported
- monitoring states imported by Work/Season/Episode scope
- tags/policies imported/skipped
- optional configuration proposed/imported/skipped

## Light / Dark

Both first-class Admin surfaces.

## Platforms

Desktop primary.

Tablet/mobile may:
- start simple migrations
- inspect scan/results
- review status/report

Complex mapping/profile-rule conversion is desktop-oriented.

TV unsupported.

## States

Required:
- not started
- connecting
- connection failed
- unsupported source version
- scanning
- scan partial
- conflicts
- mapping incomplete
- dry run ready
- running
- paused/retryable
- failed
- completed with warnings
- validated
- source disappeared
- permission denied

## Architecture constraints

- All media resolves into canonical Media Core.
- Legacy/source-native IDs remain provenance/evidence.
- Migration adapters emit normalized migration records/plans.
- Acquisition settings migrate into native Jularr AcquisitionProfile/scoring contracts.
- No Sonarr/Radarr scoring engine survives as runtime dependency.
- Progress imports into Progress domain.
- Users import through Accounts.
- Files/paths import through Library + Storage contracts.
- Storage owns LibraryRoots, destination defaults and physical-path validity.
- Migration/coexistence adapters own only source-side path translation.
- Acquisition Profiles never store filesystem paths, import modes or remote path mappings.
- Migration may invoke Library Reconciliation for unresolved filesystem mapping or post-migration organization.
- Data migration and instance-configuration migration are separate plan sections.
- Source users require explicit account mapping before their progress/history is imported.
- Dry run and validation are required before destructive mutation.
- Manager-style coexistence uses the approved three modes: Extern verwaltet / Gemeinsam / Jularr verwaltet.
- The ownership model is capability-driven; passive media-server migration sources do not pretend to support management modes.

## Must not implement

- No destructive big-bang migration.
- No source-media deletion/move by default.
- No title-only identity matching when stronger IDs exist.
- No uncontrolled dual-write authority.
- No permanent legacy/source tables as runtime authority.
- No silent dropping of unsupported Custom Format/rule conditions.
- No flattening all Sonarr monitor state into one Work boolean.
- No copying Sonarr/Radarr settings blindly when Jularr lacks a semantic equivalent.
- No automatic trust/import of indexer/download-client secrets without explicit protected review/test.
- No silent creation of Jularr users from Jellyfin/Plex/Emby source profiles.
- No rename/move of source media as part of normal Sonarr/Radarr/media-server migration.
- No approximate Custom Format conversion without an explicit conversion result/warning.
- No plaintext secret exposure.
- No skipping dry run for full migrations.
- No standalone global Import & Routing page.
- No duplicate Remote Path Mapping editor outside the adapter that requires the mapping.
- No source root-folder model competing with Storage LibraryRoots.
- No silent conversion of extreme negative scores into hard Reject.
- No source adapter may expose Extern verwaltet / Gemeinsam / Jularr verwaltet unless it implements the required ownership/safety capabilities.
- No multi-external-manager ownership for one Work.
- No silent source-user account creation.
- No default replacement/delete semantics for collections/watchlists/playlists.
- No automatic integration removal after migration completion.
