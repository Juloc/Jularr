# Admin General Instance Settings — V1

Status: approved planning direction. This page does not yet have a complete canonical backend store on `dev`; implementation must add one before wiring the UI. Approved mockups uploaded to this folder are visual references; this text remains binding.

Global UX rules: `docs/UX.md`.
Instance module settings: `docs/mockups/admin-instance/SPEC.md`.
Appearance settings: `docs/mockups/admin-appearance/SPEC.md`.
Setup Wizard: `docs/mockups/setup-wizard/SPEC.md`.

If an image and this specification conflict, this specification wins.

## Purpose

Admin → Allgemeine Einstellungen owns the small set of defaults that are truly instance-wide:

- instance identity/name;
- default interface language;
- region/locale;
- timezone;
- time/date/number formatting defaults;
- default metadata/title-language behavior;
- optional default release region;
- read-only instance/setup information and a link back to Setup when appropriate.

It must not become a generic configuration dumping ground.

## Current implementation status

At the time of this specification, `dev` does not expose one canonical `InstanceSettingsStore` covering these values.

Therefore implementation must first introduce a central durable settings contract, for example conceptually:

`InstanceGeneralSettings`

with one owning store/service.

Do not:
- save these settings directly in individual Razor page models;
- duplicate values between Setup Wizard and Admin settings;
- store them in `InstanceModuleStore`;
- store them in `InstanceAppearanceSettingsStore`;
- use environment variables as the normal editable persistence model.

The Setup Wizard and this page must read/write the same canonical settings.

## Navigation

Target Admin hierarchy:

`Admin → Instanz → Allgemein`

Sibling instance-level surfaces may include:

- Allgemein
- Module
- Darstellung

Storage, Downloader, Provider, AI, Users, System/Diagnostics, Backup and Migration remain separate Admin destinations.

## Visual structure

This is a **normal settings page**, not a wizard.

Do not number sections as setup steps.

Desktop uses:

- normal page header;
- primary content column with settings groups;
- optional narrow read-only `Instanz-Informationen` panel on the right when useful;
- one `Änderungen speichern` action for atomic settings changes.

Tablet/mobile stack the same groups.

No `1 / 2 / 3 / 4` step indicators.

## 1. Allgemein

Section heading is simply `Allgemein`; the number here is documentation only and must not appear as a setup-step number in the UI.

### Instanzname

Human-readable Jularr instance name.

Examples:
- Jularr
- Zuhause
- Media Server

Used where a server/instance identity is needed.

Rules:
- trimmed;
- non-empty;
- reasonable length limit;
- not used as database identity.

### Öffentliche/angezeigte Bezeichnung

Optional separate display/server label only if a real product use exists, for example:
- invitations;
- notification sender label;
- external client pairing.

If no distinct semantic use exists during implementation, **do not add this second field**; use `Instanzname` only.

Do not create two synonymous fields just because the mockup contains both.

### Standard-UI-Sprache

Instance default UI language for:
- newly created profiles/accounts where applicable;
- system-generated text when no user locale applies;
- first-run/default display.

Personal user language override remains in User Settings when allowed.

### Region / Locale

Default locale such as:
- de-DE
- en-US
- ja-JP

Owns regional formatting defaults where user-specific override is absent.

Do not infer timezone solely from locale.

### Zeitzone

Canonical instance timezone used for:
- server-side presentation defaults;
- Calendar default context;
- schedules that explicitly use instance-local time;
- notification/default scheduling where no user timezone applies.

Store a stable timezone identifier, not a fixed UTC offset.

Stored domain timestamps remain UTC/offset-aware as defined by architecture.

### Uhrzeit-Format

Choices:
- Locale-Standard
- 24 Stunden
- 12 Stunden

If `Locale-Standard` is selected, the locale determines the display format.

## 2. Medien & Metadaten

This group owns **instance defaults**, not provider-specific configuration.

### Bevorzugte Metadaten-Sprache

Default language preference for normalized media metadata where supported:
- title
- description
- synopsis
- general textual metadata

Providers still decide what languages/capabilities they support.

### Fallback-Sprache

Used when the preferred metadata language is unavailable.

Rules:
- optional;
- must not silently replace canonical original-language metadata;
- provenance remains attached to provider data.

### Standard-Titeldarstellung

Use one shared semantic preference, for example:
- Bevorzugt
- Original
- Romanisiert

This is a presentation default.

It must not:
- rewrite canonical titles;
- create duplicate Works;
- alter provider identity.

### Namen / Personendarstellung

Do **not** add an Anime-specific field such as `Namensdarstellung (Anime)` unless there is a real shared product contract for person/name ordering.

If needed later, make it generic, for example:
- Locale-Standard
- Originale Reihenfolge
- Westliche Reihenfolge

Do not hard-code Japanese/anime-specific behavior into global instance settings.

## 3. Regionale Defaults

Normal settings section, not a wizard step.

### Datumsformat

Choices should include:
- Locale-Standard
- supported explicit display presets

This changes display only.

Do not change stored timestamps/dates.

### Zahlenformat

Prefer:
- Locale-Standard

Only expose explicit override presets if the formatter actually supports them centrally.

### Standard-Release-Region

Optional.

Purpose:
- preferred region when release/provider data is region-specific;
- default acquisition/discovery context only where the owning feature supports it.

Examples:
- Weltweit / keine Präferenz
- Deutschland
- USA
- Japan

This is a default hint, not a hard provider filter unless explicitly configured elsewhere.

Do not duplicate AcquisitionProfile region rules here.

## 4. Instanz-Informationen

This is a read-only information block, not a setup step.

Desktop may show it as a narrow side panel.
Mobile may show it as a collapsed information group after the editable settings.

Useful read-only values:

- Jularr version;
- build/commit when available;
- PostgreSQL version;
- instance created date if reliably stored;
- last settings change if reliably stored;
- setup completed/incomplete state.

Do not fabricate values that are not persistently known.

### Setup status

If the instance has a Setup completion contract, show a concise status:

- Setup abgeschlossen
- Setup unvollständig

Optional action:

`Setup erneut öffnen`

This action navigates to the Setup Wizard.

It must **not** make this settings page look like a continuation of the Setup Wizard.

There are no numbered Setup phases here.

## Save behavior

General instance settings are saved atomically with explicit `Änderungen speichern`.

Reasons:
- multiple related fields may change together;
- locale/timezone combinations need validation;
- partial save could leave confusing defaults.

Behavior:
- dirty state enables Save;
- validation errors stay beside fields;
- successful save shows concise confirmation;
- navigation with unsaved changes uses normal unsaved-change protection where supported.

Do not auto-save each dropdown independently unless the shared settings architecture later standardizes safe transactional field persistence.

## Validation

### Language

Only supported application locale IDs may be selected.

### Region / locale

Must be recognized by the shared localization layer.

### Timezone

Must be recognized by the server/runtime timezone catalog.

No raw arbitrary timezone string field in normal UI.

### Metadata language

Only normalized supported language codes.

### Release region

Only values from a shared region catalog if this setting is implemented.

## Defaults and inheritance

Resolution should follow:

`instance default → profile/user override where allowed → feature-specific override where explicitly supported`

Examples:

- instance UI language → user UI language;
- instance timezone → user timezone;
- instance metadata language → profile display preference where supported.

A user preference can override presentation defaults but cannot alter server-wide scheduling semantics unless that feature explicitly uses user-local time.

## Binding language policy (#820)

Language behavior is one canonical instance policy shared by UI localization and media-metadata localization.

### Language mode

Admin chooses exactly one mode:

- **Fixed instance language** — one admin-selected language applies to UI and normalized media metadata for the whole instance. Personal UI/metadata-language overrides are disabled.
- **Free / per-user languages** — the instance language is the default for new profiles/system context, while profiles may select their own UI/metadata language.

Do not implement separate unrelated UI-language and metadata-language policy modes.

### Fixed instance language

In Fixed mode:
- the selected instance language is the only required metadata locale;
- Jularr does not derive a required locale set from profile preferences;
- user language controls are hidden/disabled rather than appearing to save ignored values;
- a provider may still fall back when a localized field does not exist, but that fallback does not enroll another profile locale or create another Work.

### Free / per-user languages

In Free mode:
- profiles may select their effective UI/metadata language;
- English is the baseline metadata fallback after any explicitly configured instance fallback, then original/source language/best locally available value;
- Admin may enable **Keep Library metadata for all active profile languages**.

When that option is enabled, the required metadata locale set is derived from the effective metadata languages currently used by profiles on the instance.

A new profile language must enqueue missing durable Library metadata for that locale in the shared background metadata spool. The spool is provider-rate-aware and processes lower-priority bulk work slowly.

If a user opens a Work whose preferred locale is still queued or missing, that specific `(Work, locale)` fetch is promoted to interactive priority. The page immediately renders the best locally persisted fallback and does not wait for the provider.

Suggested priority:
1. currently opened Work;
2. Watchlist / Reading List / monitored / requested Works;
3. imported/local and in-progress Works;
4. recently used/recently added Works;
5. remaining durable Library.

Discover-only provider candidates are excluded from this bulk multilingual backfill. They keep bounded locale-aware candidate/artwork caches until a durable Jularr relationship exists.

Changing modes/languages never duplicates a Work. Missing locale variants are queued; existing variants may remain cached until normal retention/cleanup.

Detailed data-model/queue semantics are owned by #820 and `docs/MEDIA_CORE.md`.

### Locale fallback

Metadata fallback is locale-family aware:

```text
exact profile/instance locale
 -> parent/base language
 -> configured fallback exact locale
 -> configured fallback parent/base
 -> English
 -> original/source language
 -> best locally available value
```

Duplicate steps are skipped.

### Coverage status

When multi-language Library coverage is enabled, this page may show only a compact status summary such as required locales and overall pending/failed state.

Detailed per-locale coverage, queue depth, Pause/Resume/Retry, provider capability diagnostics and explicit refresh belong to the owning Admin metadata/provider/operations surface. Do not turn General Settings into a queue dashboard.

Removing the last profile using a locale does not immediately delete its metadata. That locale stops receiving normal bulk backfill and enters the shared retention/cleanup lifecycle defined by #820.

## Setup Wizard relationship

The Setup Wizard does not own duplicate settings.

During first setup it may collect:
- instance name;
- default language;
- region;
- timezone;
- appearance baseline.

Those values are written into their owning canonical stores.

After setup:
- this page edits General/Region/Metadata defaults;
- Admin Appearance edits appearance defaults;
- Admin Instance edits modules.

Reopening Setup reads the same current values.

## Appearance boundary

Do not put here:
- Clean / Original Jularr;
- Light / Dark / System;
- accent;
- profile theme override permissions;
- Admin Compact/Detailed.

Those belong to Admin Appearance / profile Appearance.

## Module boundary

Do not put module enable/disable switches here.

Those belong to Admin Instance.

## Storage boundary

Do not put:
- Mounts;
- LibraryRoots;
- download paths;
- backup paths;
- safe filesystem browser;
- lifecycle cleanup/optimization/tiering policies;
- free-space reserve/forecast;
- storage migration/evacuation;
- storage maintenance/resource windows.

Those belong to Storage/Backup according to the owning spec; canonical storage lifecycle belongs to Admin Storage (#414).

## Provider / Acquisition boundary

Do not put:
- metadata provider priority;
- indexers;
- provider credentials;
- language capability matrices;
- quality profiles;
- Custom Formats/scoring.

The metadata-language setting here is only an instance-level normalized default.

## Clean visual baseline

Until an Original-J-specific version is explicitly requested, planning/mockups for this page use the **Clean** Admin design:

- white/light neutral surfaces;
- purple Jularr accent;
- compact left Admin navigation;
- thin borders;
- restrained cards/groups;
- no decorative anime artwork;
- no sakura/torii/ink background;
- no giant hero area;
- no fake marketing copy.

Dark mode remains required for implementation even if the planning image is Light.

## Detailed / Compact Admin mode

Global Admin density applies.

### Detailed

- descriptions under less obvious settings;
- slightly larger spacing;
- read-only instance-info panel visible.

### Compact

- shorter rows;
- reduced descriptions;
- same settings and validation;
- no settings hidden.

Density is a profile preference and does not alter stored instance values.

## Platforms

### Desktop

Primary.
Two-column layout is acceptable:
- editable settings left;
- narrow instance information right.

### Tablet

One/two columns depending on width.

### Mobile

One column.
Settings groups can be collapsible for navigation convenience, but they are **not numbered setup steps**.

Save action remains clearly reachable.

### TV

Unsupported.

## Required states

- loading;
- ready;
- dirty/unsaved;
- saving;
- saved;
- validation error;
- unsupported timezone/locale from migrated old settings;
- partial legacy configuration;
- permission denied;
- save conflict/stale version where optimistic concurrency is used.

## Migration / legacy values

If old configuration or migrated settings contain unsupported values:

- preserve the original evidence until the admin resolves it where possible;
- show the invalid field clearly;
- require selection of a valid replacement before save;
- do not silently reset to a default.

## Implementation target

Introduce one canonical general instance settings contract before page implementation.

Conceptually it should own only fields belonging to this page, for example:

- InstanceName
- LanguageMode
- DefaultUiLanguage
- DefaultLocale
- TimeZoneId
- TimeFormatPreference
- DefaultMetadataLanguage
- KeepMetadataForActiveProfileLanguages
- FallbackMetadataLanguage
- DefaultTitlePresentation
- DateFormatPreference
- NumberFormatPreference
- DefaultReleaseRegion (optional)

Exact field names are an implementation detail; ownership and semantics above are binding.

Use database-backed persistent configuration consistent with Jularr architecture.

## Must not implement

- No setup-step numbers or wizard semantics on this page.
- No duplicate Setup-specific settings store.
- No duplicate Appearance fields.
- No module toggles.
- No Storage paths.
- No provider credentials/priorities.
- No Acquisition scoring.
- No arbitrary timezone/locale free-text values.
- No Anime-specific global naming option without a generic shared contract.
- No destructive action.
- No raw JSON/YAML editor.
- No environment-variable editor.
- No silent reset of unsupported migrated settings.
- No second general settings store per page/module.
- No separate competing UI-language and metadata-language policy modes.
- No synchronous whole-library metadata backfill inside a settings save request.
