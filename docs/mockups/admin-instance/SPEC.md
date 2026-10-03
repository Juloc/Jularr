# Admin Instance Settings & Global Admin Compact Mode — V1

Status: approved planning direction. The current `/Admin/Instance` implementation is the starting point. The approved detailed/compact mockup becomes the visual baseline once uploaded to this folder.

Global UX rules: `docs/UX.md`.

If an image and this specification conflict, this specification wins.

## Purpose

Admin → Instance owns server-wide module availability and only the small amount of configuration that is truly instance-wide.

It must not become a second Storage, Downloader, Provider, AI or Diagnostics settings page.

The existing runtime module system remains canonical. This specification primarily changes the presentation and adds one shared Admin UI density preference.

## Current implementation

Already implemented and must be reused:

- route: `/Admin/Instance`
- authorization: `JularrPolicies.AdminSystem`
- canonical module enum: `Features/Instance/InstanceModules.cs`
- canonical durable module service/store: `IInstanceModuleService` / `InstanceModuleStore`
- disabled module data is retained
- instance module gates have precedence over profile/user capability settings
- module state is already consumed by navigation, routes, services/background work and client feature exposure
- current media modules:
  - Anime
  - Movie
  - Tv
  - Manga
  - Novel
  - Book
  - Audiobook
- current feature modules:
  - Learning
  - Acquisition
  - Tracking

Do not replace these contracts with a second settings model.

## Target screen

Primary route remains:

`/Admin/Instance`

Primary tabs may become:

1. **Module**
2. **Allgemein** — only after a real general InstanceSettings contract exists
3. **Region & Sprache** — only after corresponding instance-wide values exist
4. other tabs only when backed by real instance-owned settings

Do not render empty/fake tabs merely because a mockup contains them.

### Module tab

The Module tab contains two logical groups:

- Medienmodule
- Feature-Module

The current module identities are used exactly as implemented.

Each module exposes:
- icon
- localized name
- enabled/disabled state
- concise scope description
- dependency/effect information when relevant
- link to the module's owning settings page where one exists

Changing a module:
- never deletes module data;
- updates the canonical `IInstanceModuleService`;
- immediately affects all implemented runtime gates;
- warns before disabling a module if active jobs/sessions/configuration are affected;
- does not invent a restart requirement unless the implementation actually requires one.

## Two presentation modes

The Admin UI supports two global presentation modes:

- **Detailliert**
- **Kompakt**

This is a **global Admin UI preference**, not an Instance-module setting and not a per-page independent toggle.

Changing it on any Admin page changes the presentation throughout the Admin area for that admin user.

The selected mode persists across:
- Admin navigation
- Admin Dashboard
- Instance
- Storage
- Downloader
- Providers
- AI
- Users & Permissions
- Activity / To-Do / History
- Wanted / Requests / Manual Search
- Backup / Restore
- Migration
- Diagnostics
- future Admin screens using the shared Admin shell/components

It does **not** change normal consumer Home/Library/Player/Reader UI.

## Persistence of Admin density

Admin presentation mode is profile/user scoped.

It must not be stored in:
- `InstanceModuleStore`
- server-wide module configuration
- a page-local browser-only flag

Target contract should be a shared profile preference such as:

`AdminUiDensity = Detailed | Compact`

A shared Admin-shell service/component resolves it once and exposes it to all Admin pages.

Browser-local caching may be used for immediate rendering, but the durable preference belongs to the authenticated profile so it follows the admin across devices where appropriate.

## Shared switch

Use one shared segmented control:

`Ansicht   Detailliert | Kompakt`

The control may live in the shared Admin shell/header and can also be surfaced from Admin Appearance settings.

It must represent the same persisted global preference everywhere.

Do not create a different `CompactMode` setting on individual pages.

## Detailed mode

Detailed mode is useful when configuration context matters.

For Instance modules it uses compact cards:
- module icon + name
- state toggle
- short explanation
- optional dependency/effect note
- settings action

Detailed mode may use:
- cards
- explanatory secondary text
- expanded health/context rows
- visible summaries around complex settings

It must still remain operationally dense; no decorative hero cards or filler text.

## Compact mode

Compact mode is the preferred high-density Admin working layout.

For Instance, use exactly two primary tables:

### Medienmodule

Columns:
- Modul
- Status
- optional Abhängigkeiten / Hinweis
- Aktionen

Rows:
- Anime
- Series / TV
- Movies
- Manga
- Light Novels
- Books
- Audiobooks

Only modules currently exposed by the canonical implementation appear.

### Feature-Module

Columns:
- Modul
- Status
- optional Abhängigkeiten / Hinweis
- Aktionen

Rows currently:
- Learning
- Acquisition
- Tracking

Compact mode rules:
- one module per row;
- direct switch in the Status column;
- settings/details via one compact action;
- no repeated descriptions if the module name is sufficient;
- warnings/dependencies appear only when relevant;
- no large cards for simple boolean/state lists.

## Global compact-mode behavior

The same principle applies across the entire Admin UI.

### Tables and lists

Prefer:
- shorter row height
- reduced vertical padding
- fewer repeated labels
- inline state/action columns
- expandable row details for secondary information

Do not remove meaningful data merely to become compact.

### Forms

Compact mode may:
- reduce spacing between fields;
- place compatible fields in multi-column rows on Desktop;
- collapse help text behind info/help affordances;
- keep destructive/security warnings visible.

### Dashboard / status pages

Compact mode may:
- reduce card padding;
- combine related summary values;
- favor small status rows/tables over large isolated tiles.

It must not hide active failures, warnings or operational state.

### Dialogs / wizards

Compact mode reduces density only where useful.

Step-based workflows, confirmations and dangerous actions retain sufficient spacing and clarity.

Do not turn a wizard into a dense spreadsheet merely because Compact mode is enabled.

### Mobile

Compact preference still applies semantically, but:
- touch targets remain at least the platform minimum;
- tables transform into compact stacked rows/cards if horizontal width is insufficient;
- no tiny desktop controls are forced onto mobile.

## Same information and actions

Detailed and Compact are two presentations of the same feature contract.

They must use:
- the same query/model;
- the same permissions;
- the same commands/actions;
- the same validation;
- the same module/settings stores.

No business logic may depend on UI density.

A page must not return different semantic results because Compact mode is enabled.

## Module dependencies

Dependencies must come from real application contracts, not hard-coded UI assumptions.

Examples:
- if a feature requires Acquisition, disabling Acquisition should explain the affected surface;
- if Tracking only has effect for particular enabled media modules, the UI may indicate that context;
- disabled dependencies may disable or warn on a dependent toggle when required by backend policy.

The UI does not silently enable unrelated modules unless the domain contract explicitly defines that behavior and shows the change before saving.

## Existing implementation alignment

### Keep

- `InstanceModule` enum identities
- `IInstanceModuleService`
- runtime gates
- data-retention behavior
- existing authorization
- media vs feature grouping

### Change / add

- replace the current plain checkbox list with shared toggle/table/card components;
- add Detailed/Compact presentation;
- add shared persisted Admin UI density preference;
- use the shared density in all Admin screens as they are brought onto the approved Admin component system;
- expose dependency/effect warnings from real runtime metadata/contracts;
- optionally add general instance tabs later only when backed by actual data contracts.

### Explicitly not part of this page

Do not move these settings into Instance:
- Storage paths/mounts
- Downloader/Usenet server settings
- Provider credentials/capabilities
- AI provider/model/task routing
- Backup destination/retention
- logs/diagnostics
- user permissions
- acquisition scoring profiles

Those keep their dedicated Admin areas.

## Future module additions

A module may appear in Admin → Instance only after its full runtime gate is implemented.

Do not show a toggle that only hides navigation while jobs/services/API behavior remain active.

Examples such as AI, Requests, Native Downloader, Games or Generic Downloads require their own full module contract before becoming independent Instance switches.

Until then, the UI follows the canonical module enum/runtime implementation rather than the conceptual Setup Wizard list.

## Light / Dark

Detailed and Compact modes work in both Light and Dark themes.

Density is independent of theme.

## Required states

- loading settings
- ready
- save/update running
- module enabled
- module disabled
- dependency warning
- active work affected by disable
- update failed
- stale concurrent change
- permission denied
- compact preference loading/fallback

## Must not implement

- No second module store.
- No page-local Compact setting.
- No business logic keyed off UI density.
- No fake module toggles without complete runtime gates.
- No module disable that deletes data.
- No forced restart banner unless technically required.
- No duplicate Storage/Downloader/Provider/AI settings on Instance.
- No Compact mode that hides failures, security warnings or required actions.
- No tiny inaccessible controls on mobile.
