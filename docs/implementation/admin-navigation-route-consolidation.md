# Admin Navigation & Legacy Route Consolidation — implementation pack

Status: **ready for implementation**

Branch baseline: `dev`

## Canonical sources

- Global Admin navigation/ownership: `docs/UX.md` §17
- Cross-spec result: `docs/CROSS-SPEC-CONSISTENCY-AUDIT.md` §§21–29
- Planning coverage: `docs/PLANNING-AUDIT.md`
- System consolidation: `docs/mockups/admin-system-diagnostics/SPEC.md`
- Devices & Sessions: `docs/mockups/admin-devices-sessions/SPEC.md`
- Storage: `docs/mockups/admin-storage/SPEC.md`
- Downloader: `docs/mockups/admin-downloader/SPEC.md`
- Providers: `docs/mockups/admin-providers/SPEC.md`
- Acquisition Profiles: `docs/mockups/admin-acquisition-settings/SPEC.md`
- Migration: `docs/mockups/admin-migration/SPEC.md`
- Backup: `docs/mockups/admin-backup-restore/SPEC.md`

This pack does not redesign any of those owners.

## Goal

Make Admin navigation have one canonical source, then retire legacy top-level destinations only when their target owner has feature parity.

The implementation must:
- remove the second hard-coded Admin navigation;
- keep authorization and instance-module filtering server-derived;
- keep every currently required Admin workflow reachable while consolidation is incomplete;
- make contextual/legacy routes activate the correct owning Admin destination;
- never create dead links to planned-but-unimplemented pages;
- preserve current data/stores while routes/UI are reorganized;
- migrate GET URLs safely and never rely on blind POST redirects;
- end with one permanent destination per owning Admin area.

## Current implementation audit

### 1. Two competing Admin navigation sources exist

Canonical shared-shell navigation already exists in:

- `src/Jularr.Web/Features/Localization/UiShellNavigation.cs`
- `src/Jularr.Web/Pages/Shared/_AppNavigation.cshtml`

But most Admin pages additionally render:

- `src/Jularr.Web/Pages/Admin/_AdminNavigation.cshtml`

The page-local partial has its own route list and only partial module awareness. It is already inconsistent with `UiNavigationCatalog.Admin`.

**Target:** `UiNavigationCatalog.Admin` is the only Admin navigation catalog. Admin pages do not maintain another list.

### 2. Existing catalog is also behind the approved target

Examples on current `dev`:
- Wanted is present in the page-local Admin nav but missing from `UiNavigationCatalog.Admin`;
- Storage has a real `/Admin/Storage` page but no canonical Admin catalog entry;
- Appearance is present in the page-local nav but missing from the canonical Admin catalog;
- History is a separate page-local nav entry even though the approved target makes it the third Activity / To-Do tab/context;
- Scans is still a permanent catalog entry although scan/reconcile starts from Storage and operational state belongs to Activity;
- Resources/Logs/Health are separate routes while their approved target owner is System & Diagnostics;
- Sessions and Devices are separate routes while their approved target owner is Devices & Sessions;
- Usenet, Indexers, Download Clients and Sonarr still mix responsibilities now assigned to Downloader, Providers and Migration;
- `UiNavigationCatalog` currently points the translation-management entry at `/LocalizationAdmin`, while the Razor page route is `/Admin/Languages`.

### 3. Authorization cannot be flattened during consolidation

Current policies differ:
- Dashboard / Activity / History / Wanted / Requests / Storage / Resources / Logs / Scans: `AdminMedia`;
- Users / AI / Appearance / Instance / System / Health / Devices / Sonarr: `AdminSystem`;
- Sessions: `SessionsStopOthers`;
- Usenet / Indexers / Download Clients / legacy Acquisition/Naming / Reading Sources: `AcquisitionSettings`;
- Mapping pages: `MappingEdit`.

A combined destination must preserve these differences at tab/action level. Route consolidation is not permission consolidation.

## Binding implementation rules

### Single navigation source

`UiNavigationCatalog.Admin` owns:
- order;
- grouping;
- label key;
- canonical href;
- active-match roots;
- authorization policy;
- instance-module gates.

Do not introduce `AdminNavigationCatalog`, a second JSON menu, page-local arrays or hard-coded sidebar links.

### Page-local Admin navigation is retired

Remove every:

`<partial name="_AdminNavigation" ... />`

from Admin pages after the shared shell covers the route correctly.

Then delete:

`src/Jularr.Web/Pages/Admin/_AdminNavigation.cshtml`

Add a test that fails if a new Admin page references that partial again.

### Only implemented destinations are clickable

Planned target pages such as Providers, Backup, Migration, Notifications or General Settings must not be added to the live catalog until a real route with the required authorization exists.

Legacy entries remain temporarily only when removing them would make a still-needed workflow undiscoverable.

### Contextual routes are not permanent destinations

These remain reachable but do not receive their own permanent sidebar item:
- `/Admin/MediaDetail`;
- Manual Search pages/dialogs;
- `/Admin/History`;
- `/Admin/Scans`;
- `/Admin/Reconciliation/*`;
- download/import assignment repair;
- merge/mapping editors;
- Games editors/import resolution when implemented;
- Setup after first-run.

Their paths must activate the owning permanent destination where one exists.

### Redirect only after parity

Do not replace a working legacy page with a redirect merely because the target spec exists.

A legacy route may redirect only after:
1. its target page contains all still-supported functionality;
2. its state/store has a canonical owner;
3. its authorization is preserved;
4. links/forms inside the repository point at the new owner;
5. route/redirect tests exist.

## Phase A — one Admin shell, no behavior migration

This phase can be implemented immediately.

### A1. Rebuild `UiNavigationCatalog.Admin` as the canonical current-state menu

Use approved target grouping names, but include transitional entries only where the replacement is not implemented yet.

#### Overview
- **Dashboard** -> `/Admin`
- **Activity / To-Do** -> `/Admin/Operations`

Activity active matches must include:
- `/Admin/Operations`
- `/Admin/Operation`
- `/Admin/History`
- `/Admin/Scans`

Do not keep permanent History or Scans entries.

#### Media
- **Wanted** -> `/Admin/Wanted` when Acquisition is enabled
- **Requests** -> `/Admin/Requests` when Acquisition is enabled
- current generic/Anime acquisition route `/Acquisition` remains transitional until the approved Wanted/Manual Search pipeline fully replaces its remaining unique functions

Do not add a second Admin-only Library identity/store. A future Admin Library entry is only a bridge into canonical Library/media-management context.

#### Acquisition & integrations — transitional until owner pages exist
Keep currently necessary routes reachable:
- `/Admin/Usenet`;
- legacy `/Settings/Acquisition`;
- mapping review/segments where still needed;
- subtitle operational/configuration entry while Provider parity is incomplete;
- `/Admin/Sonarr` while Migration parity is incomplete.

These entries are explicitly transitional. New code must not expand their ownership.

#### Administration
Represent current implemented destinations:
- Storage -> `/Admin/Storage`;
- Users & Permissions -> `/Admin/Users`;
- Sessions and Devices remain transitional separate entries until Phase C;
- Resources / Logs / Health remain transitional until Phase B has System parity;
- AI -> `/Admin/Ai`;
- UI translation management -> canonical route `/Admin/Languages`;
- API keys remain transitional until the API/automation owner is specified;
- Appearance -> `/Admin/Appearance`;
- Instance -> `/Admin/Instance`;
- System -> `/Admin/System`.

Do not add Notifications / Backup / Migration / General Settings until their routes exist.

### A2. Fix canonical active ownership

At minimum add tests for:
- `/Admin/History` -> active `admin-operations`;
- `/Admin/Scans` -> active `admin-operations`;
- `/Admin/Storage` -> active Storage entry;
- `/Admin/Appearance` -> active Appearance entry;
- `/Admin/Wanted` -> active Wanted entry;
- `/Admin/Languages` -> active translation-management entry;
- user/role/capability detail routes -> active Users entry;
- operation detail -> active Activity entry.

Exactly one visible Admin child should be active whenever the route belongs to a visible Admin owner.

### A3. Remove the duplicate partial

After A1/A2 tests pass:
- remove `_AdminNavigation` from every Admin page;
- delete the partial;
- remove CSS that is used only by that duplicate navigation if no other component references it.

The app-shell sidebar remains visible on Admin pages.

### A4. Do not change route handlers/stores in Phase A

Phase A is navigation-only:
- no DB schema change;
- no JSON-store migration;
- no page handler relocation;
- no redirect of POST actions;
- no Sonarr/downloader/provider behavior change.

## Phase B — System & Diagnostics consolidation

Canonical final route:

`/Admin/System`

Canonical tabs from the spec:
- Übersicht
- Ressourcen
- Abhängigkeiten
- Logs
- Diagnose
- Updates
- Runtime

### B1. Move presentation, reuse services

Reuse existing:
- `AdminDashboardService` resource telemetry;
- `SystemHealthService`;
- `GitHubReleaseCheckService`;
- `OperationStore` log access;
- existing runtime/job health services.

Do not duplicate those stores/services for the new tabs.

### B2. Remove Storage mutation from System

Current `SystemModel` still owns LibraryRoot creation, Wake-on-LAN, scan and reconciliation settings.

Move those controls to Admin Storage first.

System may display read-only Storage health and deep-link to Storage.

### B3. Preserve authorization

Until a more granular capability model exists:
- owner/system-only System configuration, update checks and sensitive diagnostics keep `AdminSystem`;
- existing `AdminMedia` access to Resources/Logs must not be silently broadened to sensitive System data or silently removed.

If Resources/Logs are embedded in the unified System shell, tab visibility and handlers must enforce their existing policy separately. Never protect the whole page only with the weaker policy and then accidentally expose owner-only tabs.

### B4. Compatibility routes after parity

After B1–B3:
- `GET /Admin/Resources?... ` -> `/Admin/System?tab=resources&...`
- `GET /Admin/Logs?... ` -> `/Admin/System?tab=logs&...`
- `GET /Admin/Health` -> the appropriate System overview/dependency/update tab(s)

Legacy GETs preserve meaningful query parameters.

Do not use blind 307/308 redirects for legacy POST handlers. Move the mutation to a shared service/handler first, then have the old POST endpoint explicitly call the same operation during the compatibility window or retire the old form only after all callers are gone.

Remove Resources/Logs/Health permanent catalog entries only after this parity gate.

## Phase C — Devices & Sessions consolidation

Canonical route:

`/Admin/Devices`

User-facing destination label:

**Devices & Sessions**

Tabs:
- Live Sessions
- Geräte
- Anmeldungen & Sicherheit

### C1. Reuse current owners

Reuse:
- `AdminSessionsService`;
- `PlaybackStreamSessionStore`;
- `KnownDeviceRegistry`;
- `SecurityEventLog`.

No new session/device store.

### C2. Preserve different permissions

Base route may be reachable to Admin-media roles, but each tab/action must enforce its actual capability:
- Live Sessions -> current `SessionsStopOthers` behavior;
- Devices/security administration -> current `AdminSystem` behavior.

A Media Manager must not gain owner-only device/security data merely because the pages share one shell.

### C3. Legacy route

After parity:

`GET /Admin/Sessions -> /Admin/Devices?tab=sessions`

`/Admin/Devices` remains the canonical route.

Then remove the separate Sessions sidebar entry.

## Phase D — Downloader / Providers / Acquisition owner split

Do this only when the approved target pages exist.

### Downloader

Canonical route family:

`/Admin/Downloader`

Secondary tabs are defined by `admin-downloader/*`.

Move/own:
- native downloader overview/queue/servers/processing/schedule/settings;
- external download-client adapters;
- external-client remote path mappings.

Compatibility targets after parity:
- `/Admin/Usenet` -> Downloader;
- `/Settings/DownloadClients` -> Downloader -> Externe Clients.

Do not move Indexer/Search provider configuration into Downloader.

### Providers

Canonical route:

`/Admin/Providers`

Move provider configuration for:
- Indexer/Search;
- Metadata;
- Subtitles;
- Translation;
- Reading Sources;
- Identity/Login where supported.

Compatibility routes after parity:
- `/Settings/Indexers` -> Providers, Indexer/Search family;
- `/Admin/ReadingSources` -> Providers, Reading Sources family;
- provider-configuration parts of subtitle settings -> Providers, Subtitles family.

Operational subtitle completeness/manual subtitle work may remain contextual and must not be conflated with provider configuration.

### Acquisition Profiles

Canonical route:

`/Admin/Acquisition`

Own only:
- quality/upgrade;
- Release Rules/scoring;
- language acquisition policy;
- wait/delay;
- source/provider preference;
- profile assignment/test.

Legacy `/Settings/Acquisition` cannot be redirected as one block until all of its mixed settings are migrated.

Its old state must be split by owner:
- scoring/profile/delay -> Acquisition;
- LibraryRoot/default destination/import mode -> Storage;
- external download-client path mapping -> Downloader adapter;
- source-manager coexistence mapping -> Migration adapter;
- old Acquisition backup actions -> Backup & Restore.

### Naming / mapping

Do not invent a new permanent sidebar destination merely to relocate:
- `/Settings/Naming`;
- `/Settings/ReadingNaming`;
- `/Settings/MappingReview`;
- `/Settings/MappingSegments`.

Keep them as contextual/deep-link workflows until their canonical importer/library/media-management owner exposes equivalent entry points.

## Phase E — Migration, Backup, Notifications, General Settings

Add these permanent navigation destinations only when their implementations exist:
- `/Admin/Migration`;
- `/Admin/Backup`;
- `/Admin/Notifications`;
- `/Admin/General`.

### Sonarr compatibility after Migration parity

When Migration implements the approved Sonarr adapter:
- `/Admin/Sonarr` -> `/Admin/Migration?source=sonarr`;
- `/Settings/Sonarr` -> same adapter configuration;
- `/Settings/SonarrMigration` -> same adapter ownership/handover view.

Preserve the generalized Work-level coexistence semantics from `docs/SONARR_MIGRATION.md`.

### Backup compatibility

Recognized current/versioned Jularr backup artifacts go to Backup & Restore.

Legacy `AcquisitionBackupBundle` also enters through Backup & Restore as the explicitly restricted legacy Acquisition-only flow.

Do not route those artifacts through Migration.

## Route compatibility rules

### GET

Compatibility GET redirects must:
- preserve meaningful filters/search/IDs;
- resolve to exactly one canonical owner;
- avoid redirect loops;
- be test-covered.

Use normal redirect semantics for page navigation after parity.

### POST

Never assume a POST can be safely redirected to a differently shaped handler.

Before retiring a legacy POST:
1. extract/reuse the mutation service;
2. make canonical and compatibility handlers call the same service;
3. update all repository forms/actions;
4. test authorization and idempotency;
5. remove the compatibility POST only after no supported caller needs it.

### Authorization

A redirect must never become an authorization bypass.

Both compatibility route and canonical destination enforce the required policy. Handler-level capabilities remain checked server-side.

## Navigation catalog implementation details

### Keep `UiNavigationEntry`

Use the existing fields:
- `Href`;
- `Matches`;
- `Policy`;
- `Module` / `Modules`.

Do not add a new menu persistence model.

`Matches` may include contextual/legacy route roots so those routes activate the canonical owner without receiving their own sidebar item.

### Group keys

Replace the old conceptual groups:
- `adminPeople`
- `adminMedia`
- `adminSystem`

with target-oriented localized groups such as:
- Overview
- Media
- Acquisition & integrations
- Administration

Add them through `UiTranslationResources`; do not hard-code English/German in Razor.

### Modules

Acquisition-gated entries remain hidden when `InstanceModule.Acquisition` is disabled.

Do not create module gates for Downloader, AI, Games, Requests or Generic Downloads until those real `InstanceModule` contracts exist.

The native downloader's own enabled/disabled state remains a Downloader service setting, not a shell module.

## Required tests

Update/add tests under `tests/Jularr.Tests`.

### Catalog invariants
- every permanent Admin entry has one unique ID;
- every entry uses a known authorization policy;
- every live Href resolves to an implemented page;
- no permanent destination appears twice;
- group labels and entry labels exist in `UiTranslationResources`;
- no `_AdminNavigation` reference remains.

### Active owner tests
Cover at least:
- Dashboard;
- Activity;
- Operation detail;
- History;
- Scans;
- Wanted;
- Requests;
- Storage;
- Users/User/Roles/Capabilities;
- Sessions;
- Devices;
- AI;
- Appearance;
- Instance;
- System;
- Resources;
- Logs;
- Health;
- Usenet;
- Sonarr;
- `/Admin/Languages`;
- legacy Admin-scoped `/Settings/*` routes.

### Role tests
For Owner, MediaManager and User:
- catalog visibility matches policy;
- hidden navigation does not replace server authorization;
- a role never sees a link whose destination rejects that role solely because the catalog policy is too broad.

### Module tests
With Acquisition disabled:
- Wanted/Requests/acquisition/Usenet/Sonarr/legacy Acquisition settings that are actually Acquisition-gated disappear from the shell;
- unrelated Storage/System/Users/AI entries remain unaffected.

### Redirect tests
For each compatibility redirect introduced in Phases B–E:
- old GET reaches canonical owner;
- query state is preserved;
- no loop;
- forbidden role remains forbidden;
- old POST is not blindly redirected.

## Files expected in Phase A

Primary:
- `src/Jularr.Web/Features/Localization/UiShellNavigation.cs`
- `src/Jularr.Web/Features/Localization/UiTranslationResources.cs`
- `tests/Jularr.Tests/AppShellNavigationTests.cs`
- `tests/Jularr.Tests/RoleNavigationTests.cs`
- `tests/Jularr.Tests/InstanceModuleTests.cs`

Admin Razor pages currently containing `_AdminNavigation` references must be updated.

Delete after migration:
- `src/Jularr.Web/Pages/Admin/_AdminNavigation.cshtml`

Do not touch domain persistence in Phase A.

## Acceptance criteria

Phase A is complete when:
1. only `UiNavigationCatalog.Admin` defines permanent Admin navigation;
2. every Admin page uses the shared app shell without the duplicate Admin partial;
3. Wanted, Storage and Appearance are correctly represented by the canonical catalog;
4. History and Scans remain reachable but are not independent permanent destinations;
5. `/Admin/History` and `/Admin/Scans` activate Activity / To-Do;
6. `/Admin/Languages` is the real translation-management route used by navigation;
7. policy/module filtering still passes tests;
8. no feature/store/route behavior was removed merely for menu cleanup.

The full consolidation is complete when:
1. Resources/Health/Logs are System & Diagnostics tabs/deep links;
2. Sessions is consolidated into Devices & Sessions;
3. Downloader, Providers and Acquisition Profiles own their approved responsibilities;
4. Sonarr migration/coexistence is under Migration;
5. Backup/Notifications/General appear only once their routes exist;
6. legacy GET routes either redirect to or deep-link into one canonical owner;
7. no old page remains a competing configuration owner;
8. current supported POST operations have explicit compatibility or have been fully migrated;
9. authorization and instance-module behavior is unchanged or intentionally tightened by its owning spec;
10. all navigation/route ownership tests pass.

## Implementation order

Implement in this order:

1. **Phase A first** — single-source navigation, no behavior migration.
2. **System & Diagnostics parity** — then B redirects.
3. **Devices & Sessions parity** — then C redirect.
4. **Downloader / Providers / Acquisition** — migrate ownership before D redirects.
5. **Migration / Backup / Notifications / General** — add only as each real implementation lands.

Do not combine all phases into one large route rewrite.
