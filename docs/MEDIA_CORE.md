# Universal media core

Canonical design and decision record for Jularr's provider-independent media core
(issue #592, the foundational child of epic #556 §1). It also absorbs the deferred
identity issue #432 (canonical identity) and metadata-provenance issue #435.

## Status

Implemented on `feat/592-media-core`: the EF Core–modelled core entities, their
PostgreSQL migration (`Data/Migrations/*_MediaCoreFoundation`), the read/write
service surface (`Features/MediaCore`), the non-invasive bridge to the existing
per-type records, and a focused test suite. The build is warning-free,
`dotnet ef migrations has-pending-model-changes` is clean, and the tests run
against an ephemeral `postgres:18`.

This is the identity/structure foundation the other #556 children (Movie/TV
libraries, discovery, instant-play, requests, shell) build on. It **unifies** the
existing per-type models without replacing them: the per-type tables (Anime,
NovelWork/BookEdition, MangaSeries) are untouched in this epoch.

## Decision

- A single provider-independent unit — `Work` — with a stable Jularr id represents
  every real work of the six supported media types: Movie, Series, Anime, Book,
  Manga, Light Novel (`WorkMediaType`). The model is not shaped by any one provider.
- External provider identities are **normalized rows** (`WorkExternalIdentity`), not
  columns on `Work`. A `(provider, media type, external id)` resolves to at most one
  work (unique index), so a provider mapping is **correctable** by moving `WorkId`.
- Everything relational: no JSON blob substitutes for a model (epic #556 §18).
- The core is additive. It never matches on a filename alone (#556): identities come
  from providers or explicit owner corrections, always with recorded evidence.

## Model

All entities live in `Features/MediaCore` and are EF-modelled in
`Data/AppDbContext.ConfigureMediaCore`, so they are covered by the model snapshot and
`has-pending-model-changes`.

| Entity | Purpose |
| --- | --- |
| `Work` | Stable id + media type; caches the resolved primary title and year. |
| `WorkTitle` | Original / English / romaji / native / alternative / localized / synonym / short titles, one primary per work, de-duplicated on a folded normalized value. |
| `WorkExternalIdentity` | Normalized provider identity (AniList, MAL, TMDB, TVDB, IMDb, ISBN, Plex, Jellyfin, …) with confidence, evidence, manual-override and review state. Unique per `(provider, media type, external id)`. |
| `WorkRelation` | Typed directed edges between works (prequel/sequel/side-story/adaptation/…), unique per `(from, to, type)`, curatable. |
| `WorkSeason` / `WorkEpisode` | Series/anime structure. Episodes carry **both** season numbering and anime **absolute** numbering and mark **specials** (season 0). |
| `WorkVolume` / `WorkChapter` | Book/manga/light-novel structure; decimal chapter numbers allow "10.5" specials. |
| `WorkEdition` | An editorial variant of the whole work (book ISBN edition, translation, cut line). |
| `WorkVersion` | A concrete acquirable/physical release variant of a unit (`UnitKey` = `S01E04` / `V03`, or null = whole work). Modelled **separately** from editions. |
| `WorkFieldProvenance` | Per-field source attribution (#435): source, provider id, fetched-at, confidence, manual-override, fallback priority. One row per `(work, field)`. |
| `WorkSourceLink` | Non-invasive bridge to a legacy per-type record (`Anime`, `NovelWork`, `BookEdition`, `MangaSeries`, `Episode`) by primary key. Unique per `(kind, source id)`. |

## Service surface

- `WorkService` — write side: create works; `EnsureWorkByExternalIdentityAsync`
  (idempotent resolve-or-create); link/upsert identities and titles; typed relations
  with optional inverse; `SetFieldProvenanceAsync` (applies the #435 ladder);
  `ReassignExternalIdentityAsync` (correctable mapping, marks a manual override and
  records the change in the #525 anime mapping audit where the work bridges to anime).
- `WorkStructureService` — idempotent upserts for seasons/episodes, volumes/chapters,
  editions and versions.
- `WorkQueryService` — read side, `AsNoTracking` projections: identity resolution
  (`FindWorkIdByExternalIdentityAsync`), titles/identities/relations/structure,
  provenance, source links, `ResolveWorkForSourceAsync`, and an aggregate
  `WorkSummary` the #556 children consume.
- `LegacyWorkBridge` — adapters that ensure a work for an existing Anime / NovelWork /
  BookEdition / MangaSeries and bridge it via `WorkSourceLink`. Bridging is idempotent
  and never modifies the legacy tables.

Registered by `MediaCoreRegistration.AddMediaCore` (called from `Program.cs`).

## #432 / #435 reconciliation

- **#432 (canonical identity):** satisfied at the foundational level — one canonical
  Jularr id per work, many normalized provider identities, strong/weak evidence
  (`Confidence`/`Evidence`), aliases (`WorkTitle`), correctable/manual mappings that a
  provider refresh cannot silently overwrite, and review states for ambiguous cases.
  The heavier merge/split *resolution workflow* (safe merge suggestions, manual
  merge/split preserving progress, duplicate detection, identity-change history UI)
  remains open under #432 as a follow-up on top of this model.
- **#435 (metadata provenance):** the field-level model is implemented
  (`WorkFieldProvenance` + `MetadataFieldSources`) with the deterministic ladder
  `manual > preferred provider > secondary provider > local/NFO > filename`; a manual
  override is never overwritten by a provider refresh. Wiring every existing
  per-type metadata write through it is a follow-up owned by the per-type adapters.

## Localized metadata and artwork policy (#820)

The canonical Work identity is language-independent. A locale never creates another Work.

The current core already supports language-tagged `WorkTitle` rows and field-level provenance, but the durable multi-locale metadata model is a follow-up owned by #820. Language-dependent fields must become locale-aware without duplicating language-neutral facts.

Conceptually:

```text
Work
  -> locale-aware textual metadata (de-DE, en, ja-JP, ...)
  -> locale-neutral facts (year, runtime, identities, structure, ...)
  -> field provenance per resolved localized value
  -> artwork variants (locale-specific or neutral)
```

For localized fields such as title/synopsis/description, provenance semantics apply to the resolved `(Work, locale, field)` value. Manual localized corrections remain protected from provider refresh exactly like non-localized manual corrections.

Provider-localized metadata and machine-translated/generated metadata are distinct provenance classes. A value translated by Jularr from a provider's English source must record the original source/locale plus the translation/generation provider/model/version where applicable; it must not masquerade as a provider-supplied localization. Diagnostics/edit surfaces must be able to explain this distinction even when normal consumer UI stays visually quiet.

Instance policy has two modes:
- Fixed instance language: one admin-selected UI/metadata language is required;
- Free/per-user languages: profiles may select their language, and Admin may require durable Library metadata coverage for every active profile language.

In Free mode with multi-language coverage enabled, a new profile locale creates deduplicated background `(Work, locale)` metadata work for durable Library/monitoring Works. Provider pacing/health/backoff remain shared infrastructure concerns.

Opening a Work never waits for a bulk backfill. Missing preferred-locale metadata renders from the best local fallback immediately and promotes/enqueues that specific `(Work, locale)` job at interactive priority.

Locale resolution is deterministic and family-aware: exact locale -> parent/base language -> configured fallback (exact then parent) -> English -> original/source language -> best locally available value. Duplicate steps are skipped.

The spool is operationally bounded:
- deduplicate equivalent `(Work, locale)` jobs;
- enforce global and per-provider concurrency/rate budgets;
- integrate provider health/backoff;
- remember provider language/field/artwork capabilities so known-unsupported work is not repeatedly queued;
- cache valid negative results separately from transient failures, with bounded expiry/revalidation;
- refresh only missing/stale data rather than refetching fresh data on normal reads;
- preserve last-known-good data when refresh fails.

When both text and artwork are missing, prioritize visible/identity-relevant localized text first, then essential poster/cover, then larger/nonessential artwork such as backdrops/banners/logos.

Locales no longer required by active profile policy stop receiving bulk work but are not immediately deleted. Persisted variants enter a retention/cleanup lifecycle and are reused if the locale becomes required again before cleanup.

Discovery stays outside bulk persistence:
- remote provider candidates are locale-aware bounded cache/evidence;
- they are not materialized into every configured locale;
- durable localized Work metadata begins only after canonical resolution plus a durable Jularr relationship.

Artwork follows the same identity rule. The durable target is a generic Work artwork model with slots such as Poster/Cover, Backdrop/Banner and Logo, optional locale on language-specific variants, neutral fallback, and local Jularr-owned derivatives for normal Library reads. Anime-specific artwork storage is a migration source, not the long-term cross-media owner.

Existing Anime metadata/artwork must therefore move through a bounded one-time migration into canonical Work ownership when #820 is implemented. Preserve provider identities, localized values, provenance, manual/user artwork precedence and offline-capable local derivatives. Ambiguous mappings require reconciliation instead of silent duplicate Work creation. After the supported upgrade cutoff, normal runtime reads/writes must use only the canonical Work metadata/artwork path; do not retain permanent dual-read or dual-write fallback to legacy Anime stores.

## Follow-ups

- Per-type adapters: populate the core from providers during Anime/Novel/Book/Manga
  imports and scans (rich metadata, full identity sets, full structure), and read
  through the core in library/discovery surfaces.
- #432 merge/split resolution workflow and identity-change audit.
- Route existing per-type metadata writes through `WorkFieldProvenance` (#435).
- #820: add durable locale-aware Work metadata/artwork variants plus the fixed/free language policy and prioritized metadata spool.
- Owner UI for reviewing/correcting mappings (`mediaCore.*` translation keys) once a
  surface exists.
- Per-user requirement/track/MediaVersion modelling on top of `WorkVersion` (#556 §22).
