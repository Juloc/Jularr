# Setup Wizard — V1

Status: approved planning direction; current Setup Wizard mockup is the visual baseline, with the first three screens and final two screens consolidated as described below.

Global UX rules: `docs/UX.md`.

If an image and this specification conflict, this specification wins.

## Purpose

Establish a safe minimal Jularr instance without exposing every advanced setting.

The wizard is shown:
- on a fresh/unconfigured instance;
- when setup was not completed;
- when an authorized admin explicitly reopens it.

The wizard writes the same underlying settings used by the normal Admin pages. It does not own a duplicate configuration model.

## Final page structure

1. **Start & System**
2. **Setup-Art**
3. **Instanz & Module**
4. **Storage & Library Roots**
5. **Downloader / Usenet**
6. **Provider**
7. **AI**
8. **Bestehende Medien / Migration**
9. **Zusammenfassung, Test & Fertig**

Steps are adaptive:
- disabled modules remove irrelevant steps;
- migration can prefill later configuration;
- optional integrations can be skipped;
- completed setup can be resumed after interruption.

## 1. Start & System

This combines the previous:
- Willkommen
- Systemprüfung
- Admin Konto

into one compact first screen.

### Welcome

Show only a short setup introduction and what will be configured.

No separate decorative welcome page.

### System readiness

Run automatically and show compact status rows:
- PostgreSQL reachable
- schema/migration state
- writable application/data directory
- configured container mounts visible
- available storage
- app version
- required background services/readiness

States:
- OK
- Warning
- Blocking error

Blocking failures prevent continuation.
Warnings remain visible but may allow continuation.

### First Owner/Admin

On the same screen, when no owner exists:
- username
- display name
- optional e-mail
- password
- password confirmation
- language
- timezone

This account receives the initial owner/admin capabilities.

If an owner already exists:
- do not create another;
- show the existing owner state;
- skip the account form.

No broad permissions are granted to later users by default.

## 2. Setup-Art

Options:
- Neue Instanz
- Bestehende Installation migrieren
- optional: Einstellungen/Backup wiederherstellen when supported

### Backup vs migration routing

The Setup-Art choices are entry points into existing owners, not duplicate restore logic.

- a recognized current/versioned Jularr backup archive opens/reuses **Backup & Restore**;
- a legacy `AcquisitionBackupBundle` uses the Backup & Restore legacy Acquisition-only flow;
- old Jularr data/layout that is not a supported backup archive uses **Migration Center**;
- Sonarr/Radarr/Readarr/Plex/Jellyfin/Emby/folder/JSON/CSV sources use **Migration Center**.

The same artifact must not be offered simultaneously as both "restore" and "migration".

### Neue Instanz

Continue with clean configuration.

### Migration

Choose a supported source, for example:
- old Jularr
- Sonarr
- Radarr
- Jellyfin
- Plex
- Emby
- folder structure
- JSON/CSV

Migration uses the dedicated Migration Center contracts.

The setup wizard may launch/host the initial migration flow, but it does not duplicate migration semantics.

Imported configuration remains reviewable in later setup steps.

## 3. Instanz & Module

Fields:
- instance name
- default language
- region
- timezone
- optional appearance baseline:
  - visual style: Clean / Original Jularr
  - brightness: System / Light / Dark
  - accent/default theme color
  - instance logo/name override where supported

### Module activation

Render the same canonical instance modules exposed by Admin -> Instance. Do not maintain a second Setup-only module list.

Current `dev` module contract includes:
- Anime;
- Movie;
- TV;
- Manga;
- Novel;
- Book;
- Audiobook;
- Learning;
- Acquisition;
- Tracking.

Additional concepts such as Games, Requests, Native Downloader, AI or Generic Downloads appear as independent module switches **only after** their complete canonical instance runtime gate exists. Setup Wizard must not invent switches that Admin -> Instance cannot actually enforce.

Rules:
- Software is not a separate module in the current architecture; software/installers remain admin-only Generic Downloads unless a later approved requirement changes that.
- Games is its own specialized module/library and does not use Generic Downloads as its final library, but it appears as a Setup module switch only when the canonical Games instance-module contract exists.
- canonical module state controls feature availability and navigation;
- disabled modules hide their later setup steps;
- module switches shown here remain editable later in Instance Settings;
- disabling a module must not silently delete its data.

The wizard must not invent feature-specific storage/settings outside their owning modules.

## 4. Storage & Library Roots

Show detected/allowed mounts.

For each mount:
- path/identity
- free space
- writable/readable state
- status

Admin selects which mounts Jularr may use.

### Library Roots

Create logical LibraryRoots:
- name
- path
- supported content types
- status

Examples:
- Anime
- Series
- Movies
- Manga
- Books
- Audiobooks
- Games

One physical mount may contain multiple LibraryRoots.

### Games storage

When a canonical Games instance module exists and is enabled, or when the setup flow is explicitly configuring an available Games installation without pretending there is a module switch:
- allow creation/selection of a Games-capable LibraryRoot;
- the Games importer owns final Game/platform/release folder organization inside that root;
- do not route identified Games to Generic Downloads;
- BIOS/Firmware is restricted Games runtime data, not a normal LibraryRoot. The restricted storage role/path is owned by Admin Storage; Games Admin later owns BIOS/Firmware requirements, validation and artifact/runtime binding.

### Optional storage roles

Depending on the capabilities/configuration selected for this setup:
- Native Download Workspace when the downloader is being configured;
- Generic Downloads Root when that managed role is actually supported/needed;
- optional future backup/cache/transcode roles only when their owning feature exists.

Do not infer independent module switches for these roles. Storage roles follow their owning feature contracts and the canonical Instance module system.

Path selection uses the safe Storage path browser.

Validate:
- path exists
- readable
- writable
- available free space where measurable
- path does not conflict with another role unexpectedly

## 5. Downloader / Usenet

Show this step when downloader configuration is part of the selected setup and the installation exposes the relevant capability.

Do **not** invent a separate Native Downloader instance-module switch. Until such a complete runtime gate exists, Setup follows the real canonical module/capability state and simply configures the Downloader owner when applicable.

### Native Usenet

Minimal first-run fields:
- server
- port
- TLS
- username
- password
- connections
- selected Download Workspace

Actions:
- connection test
- optional simple transfer/read test

Do not expose all advanced processing/scheduler/cache settings here.

### External client

Optional alternative/compatibility path:
- client type
- endpoint
- authentication
- category mapping
- remote path mapping where necessary
- test

Native downloader remains the normal first-class Jularr path.

## 6. Provider

Configure only providers needed to make the enabled instance usable.

Families may include:
- Identity/Login
- Metadata
- Indexer/Search
- Subtitles
- Translation
- Reading Sources

Show:
- provider
- enabled
- health
- required/optional state
- test action

The wizard should clearly distinguish:
- required for the enabled feature set
- recommended
- optional

Identity/Login providers selected here define which configured external sign-in methods may be offered after setup. Login auto-provisioning, when enabled, must use conservative explicit default roles/capabilities.

Advanced priorities, capability matrices, rate limits and provider-specific tuning remain in Admin Provider settings.

### Games provider requirements

When Games is actually available in the installation/setup context, the Provider step may offer/recommend configured Metadata providers that declare Games capabilities.

Use the normal Metadata provider family. Do not create a separate Games-provider setup model.

At minimum the setup summary should make it clear whether Games metadata is:
- Ready;
- Optional/degraded;
- Not configured.

Game runtime/emulator and BIOS/Firmware configuration remain owned by Admin Games and are not duplicated into generic Provider configuration.

Secrets are masked/write-only.

## 7. AI

Show this step when server/shared AI capability is available and the admin chooses to configure it during setup.

Do **not** depend on a fake AI InstanceModule. Until a complete canonical AI runtime gate exists, this is optional setup of the dedicated Admin AI contract and may be skipped for later configuration.

Allow:
- provider selection
- endpoint/credentials
- model discovery
- simple default model assignment

Suggested simple categories:
- Text
- Vision
- Image

Optional:
- prefer local provider

Complex task routing, fallback rules, budgets and generated-content policy remain in Admin AI.

AI may be skipped if instance policy permits configuration later.

## 8. Bestehende Medien / Migration

Options:
- Keine bestehenden Medien
- Vorhandene LibraryRoots scannen
- Migration aus anderem System fortsetzen

### Existing Library scan

Options may include:
- analyze files
- use metadata/provider IDs
- inspect sidecars
- probe media
- mark uncertain mappings for review

Unresolved folders/files are sent into the dedicated Library Reconciliation flow.

Do not silently rename/move files during setup.

### Migration

If setup was started with migration:
- show current migration state
- continue unresolved mapping
- show imported settings/profiles/monitoring where relevant
- allow return to the dedicated Migration Center for complex conflicts

## 9. Zusammenfassung, Test & Fertig

This combines the previous:
- Zusammenfassung & Test
- Fertig

into one final screen.

### Configuration summary

Show compact rows:
- System
- Owner/Admin
- Instance & Modules
- Storage
- Downloader
- Providers
- AI
- Existing media/migration

Each row:
- status
- concise detail
- edit action

### Final tests

Run:
- database readiness
- Storage read/write
- LibraryRoot validation
- downloader health if configured
- required provider health
- AI provider/model health if configured
- unresolved blocking migration/setup conflicts

Result states:
- Passed
- Warning
- Failed

Blocking failures prevent finishing.

### Complete setup

When all required checks pass:
- `Setup abschließen`

The same screen transitions to the finished state:
- setup completed
- configuration saved
- tests passed / warnings count
- modules enabled
- libraries prepared

Primary action:
- `Zu Jularr`

Secondary:
- `Setup erneut öffnen`

No separate final page is necessary.

## Resume behavior

The wizard is resumable.

Persist:
- completed step
- current step
- validated settings
- pending warnings
- migration reference if one exists

On reopening:
- continue from the last incomplete meaningful step;
- rerun readiness checks that may have become stale.

Do not rely only on browser-local state.

## Validation behavior

Validation happens both:
- inside the relevant step;
- once again in the final system test.

A successful earlier test does not permanently suppress a later failure.

## Ownership boundaries

Setup Wizard orchestrates configuration but does not own the underlying data.

Ownership remains:
- Accounts -> owner/admin
- Instance Settings -> instance/module switches
- Storage -> mounts/roles/LibraryRoots
- Downloader -> native/external downloader configuration
- Providers -> provider configuration
- AI -> AI provider/model/task settings
- Migration -> external/legacy migration
- Library Reconciliation -> ambiguous filesystem mapping

## Appearance

Clean / Original Jularr and Light / Dark / System are first-class.

Clean uses no decorative background artwork and defaults to the purple Jularr accent.
Original Jularr uses the Japanese decorative skin and defaults to the established red/pink accent.
Accent variants use shared semantic tokens/hue shifting.

Appearance may be selected during setup, but setup must remain usable with defaults.

## Platforms

### Desktop
Primary setup experience.

### Tablet
Fully supported with stacked sections.

### Mobile
Supported for basic setup, but complex Storage/Migration mapping may use full-screen pages and should not squeeze desktop tables.

### TV
Initial server pairing/client selection may exist elsewhere; the full Admin setup wizard is not required on TV.

## Required states

- fresh instance
- existing owner
- system check running
- blocking system failure
- non-blocking warning
- module disabled
- storage unavailable
- insufficient permissions
- provider unavailable
- downloader connection failed
- AI skipped/disabled
- migration in progress
- migration conflict
- media scan running
- resumable incomplete setup
- final validation failed
- completed

## Must not implement

- No separate decorative Welcome page when Start/System can contain it.
- No separate final success page when final validation/results can transition in place.
- No SQLite production setup path.
- No duplicate setup-only settings model.
- No arbitrary unrestricted filesystem access.
- No requirement to configure every optional provider.
- No default broad permissions for non-owner users.
- No media-type-specific database setup.
- No silent file rename/move during setup.
- No permanent dependency on setup wizard after completion.
