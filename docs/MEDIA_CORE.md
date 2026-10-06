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

The durable multi-locale metadata model exists for Movie/Series (see "Persisted Work metadata and artwork" below); the rest of #820 builds on it. Language-dependent fields are locale-aware without duplicating language-neutral facts.

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

## Persisted Work metadata and artwork (#820 slice 1: Movie/Series)

Implemented for Movie and Series Works from TMDB; the locale-aware model is ready for the per-profile spool of slice 2.

**Owners.** `WorkMetadataStore` (Features/MediaCore) is the persistence owner: explicit parameterized PostgreSQL for every table
below. `WorkMetadataLocales` owns locale semantics (normalization, the instance metadata locale, the fallback order, artwork
languages); `WorkArtworkSelection` owns the artwork choice; `WorkMetadataPresentation` resolves rows for one viewer locale, purely.
`WorkMetadataRefresher` (Features/Metadata) is the one writer: it fetches through the TMDB adapter
(`TmdbDiscoveryProvider.GetWorkMetadataAsync`, through the shared `ProviderExecutor` rate gate/circuit) and decides every overwrite
with `MetadataFieldSources`. `WorkArtworkCache` (Features/Artwork) owns the local derivative files.

**Tables** (own `BIGINT GENERATED BY DEFAULT AS IDENTITY` keys; `WorkId uuid` references the legacy Guid `Works` key with
`RESTRICT`; no cascades: a merge moves or deletes the rows explicitly, see below):

| Table | Grain / unique key | Content |
| --- | --- | --- |
| `WorkMetadataFacts` | one per Work (`WorkId`) | language-neutral facts: original title/language, release date, runtime, rating (0-10) + count, certification + its ISO country, studios `text[]`, production countries `text[]`. Per-field provenance stays in `WorkFieldProvenance` (field keys `WorkMetadataFactFields`), so a manual correction is protected by the existing ladder. |
| `WorkLocalizedValues` | `(WorkId, Locale, Field, Position)` | localized text (`WorkLocalizedField`: Title, Overview, Tagline, Genre, Trailer); list fields use one row per position and are replaced as a group. Provenance beside the value: `Origin` (`ProviderLocalized`, `OwnerEntered`, `Derived`, `MachineTranslated`), `Source`, `SourceLocale`, `ProviderExternalId`, `Confidence`, `FallbackPriority`, `IsManualOverride`, `FetchedAt`, `UpdatedAt`. |
| `WorkCredits` | `(WorkId, Kind, Position)` | cast (top 20 in billing order) and key crew (director, writers, creators; top 10); replaced as a whole. |
| `WorkArtwork` | `(WorkId, Slot, Language)` | at most one variant per slot (`Poster`, `Backdrop`, `Logo`) and ISO 639-1 language (`''` = neutral/textless): provider file identity, size, votes, `CacheKey` of the local WebP derivative (`NULL` until downloaded), `IsManualOverride`, fetched/cached times. |
| `WorkMetadataRefreshes` | `(WorkId, Locale)`, index `NextAttemptAt` | the durable spool and freshness record: priority, status (`Queued`, `Fresh`, `Failed`), consecutive attempts, next attempt, last success, root-cause `LastError`. |

**Rules.**
- Text before artwork; poster before backdrop and logo. An empty provider value never erases a stored one; a manual value or pin
  (`IsManualOverride`, `owner` source) is never replaced; a list field with one protected row keeps the whole list.
- Locale fallback (one implementation, `WorkMetadataLocales`): exact → parent cultures → configured fallback and its parents →
  English → original language → best locally stored value (ordinal); a neutral step also matches a regional sibling (`de` → `de-DE`);
  each field falls back on its own.
- Artwork: posters/logos prefer the viewer's language family, then neutral, then the highest-voted other variant; backdrops prefer
  neutral (highest voted), then the viewer's language. The spool keeps, per slot and locale, the best image in the locale's language
  and the best neutral one (else the best of any language). Ties break by votes, vote count, width, file identity.
- Artwork is untrusted input: the adapter accepts only single-segment `/[A-Za-z0-9_-]+.(jpg|jpeg|png|webp)` TMDB paths and builds
  `https://image.tmdb.org/t/p/{size}{path}`; the cache fetches only HTTPS on allow-listed hosts with a client that follows no redirects,
  requires an image content type, at most 20 MB and decodable JPEG/PNG/WebP, and writes `/data/cache/artwork/work/{key[..2]}/{key}.webp`
  where the key is a SHA-256 of the variant identity; no provider or user text becomes part of a path.
- Retention: metadata lives as long as the Work; artwork is bounded by the unique key (a replaced variant's file is deleted when the
  new one is stored); derivatives are compressed WebP (posters ≤512 px, wide art ≤1600 px); orphaned files are swept on reconcile.

**Spool.** `WorkMetadataRefreshService` is a hosted worker following the durable due-list pattern of the franchise refresh
(`BackgroundWakeSignal` + due rows read from the database; a wake only shortens the wait). One entry runs at a time, bulk entries
are spaced, claims are leased (`FOR UPDATE SKIP LOCKED`, 10 min lease) so a crash only delays an entry. Outcomes: success → `Fresh`,
due again after 30 days (`Stale` priority); network/5xx/timeouts and busy-CDN (408/429/5xx) artwork downloads → `Queued` with exponential backoff (1 min doubling
to 6 h; the worker wakes when a backoff ends); 400/404/422/unusable answers → `Failed`, re-probed after 7 days (negative result); 429 / open circuit / refused credentials pause the
whole pass without penalizing the entry. Nothing runs on startup; the first pass waits a minute. Works of a disabled Movie/TV module
are not claimed.

**Entry points.** `TmdbDiscoveryProvider.EnsureCanonicalWorkAsync` (the Request materialization) queues the Work at `Requested`
priority; it never fetches durable metadata inline. `WorkMetadataRefreshQueue.RequestMetadataRefreshAsync(workId, interactive)` is the
narrow call for a detail page when a Work is opened (wiring it into the pages belongs to the UI slice): it promotes a missing or queued entry to `Interactive` without waiting and
never refetches fresh data or bypasses a failure's backoff. The reconcile (start + every 6 h) is the idempotent backfill:
TMDB-identified Movie/Series Works without an entry are queued as `Imported` (file or progress), `Requested` (no legacy library
record) or `Library`.

**Reads.** `VideoDetail.Metadata` (`WorkMetadataView`) carries title, original title, synopsis, tagline, genres, release date,
runtime, rating, certification, studios, production countries, trailers (YouTube keys with watch/embed URLs), cast, crew and the
poster/backdrop/logo as `/works/{workId}/artwork/{artworkId}?v=…` URLs; `VideoDetail.Title` is the localized title when one exists.
Library Movie/Series cards get poster, backdrop and rating from one query for the whole page. Reads never call a provider and never
write. The artwork endpoint (`Pages/Artwork/Work`) serves only cached files, 404s for a media type the profile cannot browse, and
answers `private, immutable`.

**Instance locale.** Until the Admin General language policy exists, the spool fetches `WorkMetadataLocales.InstanceDefault` (the UI
source locale) and readers resolve from the profile's UI locale.

**Slice 2 and later.** Fixed/Free instance language mode and its setting; per-profile locale enrollment into the spool (rows for
additional locales, no schema change); provider capability memory and a separate negative cache per `(provider, locale, field)`;
machine-translation provenance columns (provider/model/version) when translation lands; Admin coverage/queue view with
pause/resume/retry and explicit refresh; open-time promotion wired into the detail pages (UI slice); person artwork and "More like
this"; Anime metadata/artwork migration into these tables and retirement of the Anime-specific stores; Discover preferring persisted
metadata for already durable Works; per-field merge of facts (a merge keeps the survivor's facts row whole, so a manual fact value of the
absorbed Work is not carried over yet).

## Follow-ups

- Per-type adapters: populate the core from providers during Anime/Novel/Book/Manga
  imports and scans (rich metadata, full identity sets, full structure), and read
  through the core in library/discovery surfaces.
- #432 merge/split resolution workflow and identity-change audit.
- Route existing per-type metadata writes through `WorkFieldProvenance` (#435).
- #820: the remaining slices listed under "Persisted Work metadata and artwork".
- Owner UI for reviewing/correcting mappings (`mediaCore.*` translation keys) once a
  surface exists.
- Per-user requirement/track/MediaVersion modelling on top of `WorkVersion` (#556 §22).
