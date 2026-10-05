# Jularr target architecture

Status: planning baseline. This document defines architecture boundaries before major feature implementation resumes. `DOMAIN.md` is authoritative for domain meaning; `DOMAIN-AUDIT.md` defines the transition from current models.

## 1. Goals

- One modular application, not a collection of parallel mini-products.
- One canonical media/domain model.
- PostgreSQL is the canonical relational store.
- Web, PWA, Android, Android TV and future native clients consume the same application contracts.
- Media-specific behavior is implemented as specialization, not duplicated infrastructure.
- External providers are adapters and never become the domain model.
- Background work is explicit, observable, retryable and idempotent.
- UI does not access EF entities directly as its contract.
- No new legacy bridge may become permanent architecture.

## 2. Architectural style

Jularr remains a modular monolith unless a concrete operational reason requires a process boundary.

```text
Clients
  -> HTTP/Application endpoints
     -> Application use cases
        -> Domain modules
           -> Ports/interfaces
              -> Infrastructure adapters
                 -> PostgreSQL / NAS / indexers / downloaders / metadata / AI
```

Do not split into microservices merely because domains are separated. Domain boundaries are code and ownership boundaries first.

## 3. Target modules

### MediaCore
Owns canonical shared media identity:
- Work
- titles and external identities
- relations
- Season/Episode and Volume/Chapter structure
- Edition/Version
- metadata provenance
- artwork identity

No feature may create a second canonical Anime/Movie/Book/etc. identity model.

### Library
Owns locally available content:
- Asset
- File
- Track
- LibraryRoot association
- media inspection/fingerprints
- availability
- import-to-library mapping

Library references MediaCore identities.

### Metadata
Owns provider integration and metadata resolution:
- provider interfaces/adapters
- search candidates
- raw/cache provider data
- identity resolution
- merge/conflict policy
- field provenance
- refresh jobs

AniList/TMDB/OpenLibrary/etc. are adapters behind this module.

### Acquisition
Owns the universal Wanted-to-Import pipeline:
- WantedItem
- search orchestration
- ReleaseCandidate
- scoring/profiles
- DownloadJob
- ImportJob
- AcquisitionEvent/history

Anime, TV, Movies, Manga, Books and Light Novels use this module rather than independent pipelines.

### Storage
Owns physical storage infrastructure:
- LibraryRoot configuration
- path policy
- NAS availability
- wake/retry behavior
- free-space/health information
- safe file operations

It does not own media identity.

### Playback
Owns playback planning and session state:
- client capabilities
- PlaybackPlan
- Direct Play / Remux / Transcode decision
- selected tracks
- ActiveSession
- playback diagnostics
- stream/transcode lifecycle

Platform-specific player UI is client presentation over this shared contract.

### Reader
Owns reading presentation/session behavior:
- document rendering contract
- locators
- reader preferences
- annotations/highlights/bookmarks integration

It reads canonical written Assets and does not own separate Novel identity.

### Progress
Owns personal consumption state:
- MediaProgress
- history
- completion
- resume
- offline reconciliation

Progress targets canonical Work/structural identities and survives release/file upgrades.

### Subtitles
Owns subtitle acquisition/normalization and interactive cue representation:
- embedded/external tracks
- subtitle provider adapters
- cue parsing
- language selection
- provenance

Learning consumes subtitle cues; it does not duplicate them.

### Translation
Owns reusable translation tasks and persisted derivatives:
- provider abstraction
- local/self-hosted and optional remote adapters
- document/chapter translation jobs
- provider-neutral progressive translation run/block contract (#834)
- validated partial block cache and resume/idempotency semantics
- Translation provenance
- derived Edition/Version creation/finalization
- cache/versioning

Translation normalizes provider-native token/delta or final-response behavior
behind its application contract. Reader never owns provider protocol details,
partial JSON or a second translation cache. Only validated completed blocks are
durable/reusable; a complete derived Edition/Version is published after final
validation. Translation generation is shared content capability rather than a
Learning/profile-owned capability.

Reader and Learning can consume this module through application contracts.

### Learning
Owns curriculum and learner state:
- curriculum/course hierarchy
- learner progress
- vocabulary/cards/reviews
- scheduling
- media-context references

Learning may reference Work/Episode/Chapter but never duplicate their identities.

### AI
Owns generic AI execution/configuration:
- server AI configuration/policy
- personal AI configuration
- task contracts
- provider/model adapters
- usage/limits where needed
- provenance for persistent generated artifacts

Feature modules define task intent; AI executes it. AI must not absorb feature/domain logic.

### Discovery
Owns cross-provider discovery/recommendation presentation data:
- discover queries;
- filters;
- recommendation candidates;
- resolution to the canonical target owned by the relevant domain.

For watch/read/listen media, that target is canonical `Work` plus optional presentation/structure context.

Games remains deliberately outside MediaCore. Games discovery resolves through an explicit Games application port to canonical `Game` identity; Discovery must not force Game/GameRelease into Work/Edition merely to share Search UI.

A discovery result is not automatically a library item.

### Collections
Owns profile-scoped Collection metadata and canonical Work membership.

Persisted modes:
- Manual — explicit membership/order;
- Smart — versioned rule expression + include/exclude overrides evaluated against local/cached Media Facts;
- Linked — locally persisted Work membership synchronized from an external provider list.

Boundary rules:
- cross-media is normal Collection behavior, not a separate Collection type;
- franchise/adaptation grouping uses canonical Work relations through Smart/derived views;
- CollectionEntry stores WorkId, never provider identity as canonical membership;
- Games is outside the V1 Work-based Collection boundary; adding it later requires an explicit typed cross-domain collection contract;
- Smart render/preview performs no provider network calls;
- Linked render performs no provider network calls;
- Linked sync persists provider list/item evidence first, resolves to canonical Work IDs, then updates local membership;
- ambiguous identity becomes mapping/review state instead of title-only silent merge;
- removing/deleting Collection membership never deletes media/progress/request state.

### Accounts
Owns authentication, Accounts, Profiles, linked login identities, profile selection, authorization and user/group policy.

Boundary rules:
- Jularr Account ID is always internal/provider-independent.
- External login adapters authenticate/link `AccountLoginIdentity`; they do not become Account IDs.
- Login establishes an Account session; the selected Profile is a separate server-authoritative context.
- Profile-scoped progress/history/ratings/preferences/connections resolve through the active Profile.
- Account roles/capabilities and security remain Account-level unless a policy explicitly restricts a Profile.
- Switching Profile does not create a new Account session and must never merge personal state.
- Login-provider capability and personal sync/Connection capability are separate adapter capabilities.
- Auto-provisioned external-login Accounts receive only configured conservative defaults; external auth can never imply Owner/Admin authority.
- Passkeys authenticate Accounts; Profile PINs only guard Profile activation.

### Devices
Owns registered clients, capabilities and cross-device targeting/handoff metadata.

### Admin/Operations
Owns operational views and commands, not duplicate business logic:
- jobs/activity
- provider health
- storage health
- acquisition diagnostics
- migration/maintenance

Admin UI calls the same application services as automated flows wherever possible.

### Instance module gates
Server-wide module availability is resolved before profile settings or permissions. The canonical
runtime contract is `IInstanceModuleService` / `InstanceModuleStore`, persisted below
`/data/system`. Existing installations and newly introduced modules default to enabled.

Resolution order is:

```text
instance module
-> authorization / media capability
-> profile module preference
-> feature/media-scope setting
```

The instance module is the hard upper bound. Authorization/capability can further restrict access.
The **profile module preference** is a generic personal opt-in/opt-out layer: a user may turn an
instance-enabled, permitted module Off or back On for their own profile, but can never use that
preference to grant themselves a module disabled by the instance or forbidden by authorization.

Profile module preferences must be durable and generic rather than reimplemented as a different
boolean store for every module. Existing module-specific settings (for example Learning modes and
media-scope overrides) remain the source of detailed behavior and are resolved only after the generic
profile module preference is On.

A personal module Off:
- hides that module's user-facing navigation/surfaces for the profile;
- stops only profile-specific work/sync/notifications owned by that module where applicable;
- preserves all existing user/domain data and detailed settings;
- does **not** stop shared instance work needed by other profiles.

Re-enabling restores preserved state subject to current instance policy and authorization.

A module switch is exposed in **Admin → Instance** only after that module's complete vertical slice
uses the same gate for navigation, routes/API, application services and background work. Disabling a
module at instance level preserves its stored data; re-enabling restores access. Queued/retryable jobs
must re-check the instance module before doing work rather than relying only on the state at enqueue
time.

Implemented verticals currently include:
- **Learning** — hides Learning navigation/routes and stops learning assistance, vocabulary/text preparation and Learning client capabilities.
- **Anime** — removes Anime from effective media capabilities, client/library playback surfaces, scans and Anime-specific acquisition work.
- **Movie / TV** — remove the respective video type from effective capabilities/search/calendar/offline surfaces and block media-specific library/import writes.
- **Manga / Novel / Book** — remove the respective reading type from capabilities, discovery/search, Home, Watchlist/franchises, release calendar, offline/acquisition surfaces and related background work.
- **Audiobook** — independently gates audiobook search, offline packages, progress, library/import writes and acquisition even though audiobook works share the Book capability family.
- **Acquisition** — hides request/downloader/admin routes and stops request execution, Wanted passes, imports, health checks and SABnzbd acquisition work.
- **Tracking** — hides AniList settings/progress UI, blocks AniList network sync and pauses the automatic sync loop.

All switches retain their stored domain data and resume from that state when re-enabled.

## 4. Dependency rules

Allowed conceptual direction:

```text
MediaCore <- Library
MediaCore <- Metadata
MediaCore <- Acquisition
MediaCore <- Progress
MediaCore <- Learning
MediaCore <- Discovery/Collections
Library   <- Playback/Reader/Subtitles
Storage   <- Library/Acquisition infrastructure usage
AI        <- feature task adapters (not domain identity)
```

Rules:
- MediaCore cannot depend on Acquisition, Playback, Learning or UI.
- Storage cannot depend on Anime/Novel/etc. feature models.
- Provider adapters cannot be referenced directly from Razor pages/controllers.
- UI cannot query AppDbContext as a shortcut for application behavior.
- Cross-module writes go through explicit application/module contracts.
- Avoid circular module dependencies; shared primitives must be genuinely small and stable.

## 5. Application layer

Every user-visible or background action is an application use case.

Examples:
- AddWork
- RefreshMetadata
- RequestMedia
- SearchWantedItem
- GrabRelease
- ImportDownload
- StartPlayback
- UpdateProgress
- TranslateEdition
- GenerateChapterArtwork

A use case coordinates modules and transaction boundaries. Domain services should not know HTTP, Razor, Android or job-runner details.

## 6. API contracts

Do not expose EF entities as public/client contracts.

Use explicit request/response DTOs grouped by capability. Contracts should be version-tolerant and platform-neutral.

Examples:

```text
GET  /api/library/works/{id}
GET  /api/library/works/{id}/structure
POST /api/acquisition/requests
POST /api/playback/plans
POST /api/playback/sessions/{id}/progress
GET  /api/discovery
POST /api/translation/jobs
```

Exact routes are implementation details to finalize per vertical slice; the rule is stable capability contracts, not page-specific database-shaped APIs.

## 7. Persistence

PostgreSQL is the only target production relational database.

### DbContext
A single physical DbContext is acceptable for the modular monolith, but configuration and ownership must be modularized. The current giant `AppDbContext` must not remain the place where all domain knowledge accumulates.

Target:
- module-owned entity configurations (`IEntityTypeConfiguration<T>`)
- module-owned migrations/schema decisions coordinated centrally
- explicit indexes/constraints
- normalized language/provider identifiers
- no SQLite compatibility shaping new schema

SQLite import remains migration tooling only and is removable after the supported migration window.

### Transactions
- use database transactions for local atomic changes;
- external side effects must not be assumed transactional;
- persist job/state transitions before/after external calls;
- use idempotency keys or unique constraints for retryable operations where appropriate.

## 8. Background jobs

Background operations must use one common job model/runtime abstraction.

Required behavior:
- typed job kind + payload/reference
- queued/running/succeeded/failed/cancelled state
- attempts and bounded retry policy
- next attempt time
- progress/status text suitable for UI
- timestamps
- correlation to Work/WantedItem/Download/Import/etc.
- structured failure reason
- idempotent handler expectation
- cancellation where safe

Examples:
- metadata refresh
- indexer search
- download polling/import
- media inspection
- subtitle search
- translation
- AI generation
- artwork generation
- maintenance/migration

Do not create a different ad-hoc hosted service + status table for every feature.

## 9. Provider architecture

Use capability-specific provider ports rather than one enormous universal provider interface.

Examples:
- `IIdentityLoginProvider`
- `IProfileConnectionProvider`
- `IMetadataProvider`
- `IReleaseSearchProvider`
- `IDownloadClient`
- `ISubtitleProvider`
- `ITranslationProvider`
- `IAiProvider`

Provider registration includes:
- stable provider key
- capabilities (including Login/Identity and Profile Connection/sync where applicable)
- configuration schema
- health/test operation
- optional rate-limit information

Secrets remain server-side and are never returned through normal API DTOs.

## 10. Acquisition data flow

```text
User/automation
 -> WantedItem
 -> search providers
 -> normalized ReleaseCandidates
 -> profile/scoring
 -> selected candidate
 -> DownloadJob
 -> download client
 -> completed download
 -> ImportJob
 -> identify canonical Work/unit/Edition
 -> Version/Asset/File/Track
 -> Progress/Library immediately sees canonical content
```

Every step is inspectable in Admin UI. Manual Search uses the same candidates/scoring data rather than a separate path.

## 11. Metadata flow

```text
query/external id
 -> provider candidates
 -> identity resolver
 -> Work match/create
 -> provider snapshots/evidence
 -> resolved canonical fields
 -> WorkFieldProvenance
```

Refreshing metadata must not overwrite manual/admin corrections without policy. Provider removal must not invalidate canonical internal IDs.

Provider responses used for durable product features are persisted as local evidence/snapshots (`ProviderEntitySnapshot` concept) with provider/entity/external identity, normalized fields, refresh timestamps/stale state and optional resolved WorkId. Linked Collections additionally persist external list/membership evidence. Normal UI rendering consumes local state rather than making provider availability a page-render dependency.

## 12. Playback flow

```text
Client capabilities + requested canonical unit
 -> available Versions/Assets/Files/Tracks
 -> user preferences
 -> PlaybackPlanner
 -> PlaybackPlan
 -> Direct Play | Remux | Transcode
 -> ActiveSession
 -> periodic progress
 -> canonical MediaProgress
```

Web, Android and TV use the same planning semantics. They may render completely different controls.

Completion contract of video progress: a checkpoint carries the exact resume position and a client-declared `completed` flag. The server never infers completion from a position, because a seek, scrub or resume can land past the threshold without the content having been watched. Clients declare completion only when playback itself reached `VideoProgressService.CompletionThreshold` or ended, or when the user marks the item watched. A completed item keeps its completed state while a later rewatch stores a new resume position, and Continue Watching offers that resume. `CompletedThrough` is the contiguous completed prefix of a work's regular canonical episodes, owned by `VideoProgressService.GetCompletedThroughAsync`; AniList write-back reads only that projection. The legacy `EpisodeProgress` / `EpisodePlaybackHistory` tables are a one-time backfill source with no runtime reader or writer.

## 13. Reader/translation flow

```text
requested Work/Edition/Chapter
 -> preferred available language edition
 -> if unavailable and policy permits: shared TranslationJob/run
 -> stable semantic source blocks
 -> provider adapter (delta-capable or bounded final-response)
 -> validated completed translated blocks
 -> Reader can consume partial readable coverage
 -> final validation
 -> translated derived Edition/Version
 -> persisted/cached Asset
 -> Reader document contract
 -> progress/annotations
```

Progressive translation is ordered/idempotent and reconnectable by stable
run/block identity. Provider deltas are transient infrastructure events; the
Translation module validates and normalizes them before Reader consumption.
Completed blocks survive retry/restart, while incomplete output cannot be
published as a complete derivative. Multiple viewers share canonical work rather
than starting duplicate provider runs. Search/TTS/offline/durable annotation
features consume completed validated text, not ephemeral deltas.

Never destructively overwrite source text with machine translation.

## 14. Client architecture

### Web/PWA
Razor Pages may remain the server page technology, but complex interactive surfaces should consume explicit application/API contracts. Shared Fluent-style components and design tokens belong in a coherent UI system rather than page-local CSS copies.

### Android phone/tablet
Native shell/player may exist where platform APIs materially improve playback, gestures, Media3, background playback etc. Business rules and canonical identity remain server contracts.

### Android TV
Remote/focus-first presentation. Same playback/library contracts; no reuse of phone interaction assumptions.

### iOS/iPadOS Web/PWA
Explicit WebKit compatibility path. Use progressive enhancement and platform capability detection. Do not fork server/domain behavior for WebKit limitations.

### Future native iOS
May use AVPlayer/native capabilities but consumes the same Jularr contracts.

## 15. UI architecture rules

- One design system/tokens/components.
- No page-specific reinvention of standard buttons, cards, dialogs, fields, navigation or state views.
- Responsive behavior is specified, not guessed during implementation.
- Every major screen defines loading, empty, error, partial and ready states.
- Admin and user navigation are distinct information architectures but reuse components.
- Permission-hidden features are enforced server-side too; hiding UI is not authorization.
- Do not expose implementation jargon unless it is intentionally an Admin/diagnostic surface.

Detailed page/platform behavior belongs in `UX.md` and approved mockups.

## 16. Security

- Server-side authorization for every privileged operation.
- Least-privilege role/capability checks rather than trusting navigation visibility.
- Validate/normalize all provider/user inputs.
- Parameterized EF/database access; no user-controlled SQL.
- Safe process invocation for ffmpeg/ffprobe/tools; never concatenate untrusted shell commands.
- Prevent path traversal: external filenames/paths never directly choose arbitrary server paths.
- SSRF protections for configurable remote endpoints and artwork/media fetches.
- Upload size/type/content validation.
- Secrets stored using the project's secret/config mechanism and redacted from logs/UI.
- CSRF protection for cookie-authenticated state changes.
- Output encoding/sanitization for imported HTML/EPUB/provider content.
- Explicit permission checks around AI/provider credentials and shared generated artifacts.

## 17. Observability

Use structured logs and stable correlation IDs for long flows.

Important IDs to correlate:
- request
- job
- Work
- WantedItem
- DownloadJob
- ImportJob
- PlaybackSession

Admin diagnostics should explain state/reason, not require reading raw server logs for normal failures.

## 18. Error model

Application failures use stable categories:
- validation
- not found
- conflict
- forbidden
- provider unavailable
- storage unavailable
- retryable external failure
- permanent external failure
- unsupported media/capability

Clients receive safe error codes/messages; detailed exceptions remain server-side.

## 19. File/module layout target

Do not create hundreds of tiny projects. Start with coherent feature/module folders and split assemblies only when boundaries need enforcement.

Suggested direction:

```text
src/Jularr.Web/
  Modules/
    MediaCore/
      Domain/
      Application/
      Persistence/
      Api/
    Library/
    Metadata/
    Acquisition/
    Storage/
    Playback/
    Reader/
    Progress/
    Subtitles/
    Translation/
    Learning/
    AI/
    Discovery/
    Collections/
    Accounts/
    Devices/
  Infrastructure/
    Database/
    Jobs/
    Filesystem/
    Http/
    MediaTools/
  Ui/
    Components/
    Layout/
    DesignSystem/
  Pages/
```

This is a boundary guide, not permission to perform a giant folder-only refactor. Files move when the owning module is migrated.

## 20. Coding rules for agents

Before implementing a vertical slice, an agent must:
1. read `DOMAIN.md`, `DOMAIN-AUDIT.md`, `ARCHITECTURE.md` and the relevant UX spec;
2. identify owning modules and existing implementation;
3. state what is reused, evolved and removed;
4. avoid introducing a parallel model/service;
5. implement the complete slice including UI states, authorization, persistence and tests;
6. verify against acceptance criteria;
7. report remaining legacy dependencies explicitly.

Agents must not:
- create a second data model because the old one is inconvenient;
- add compatibility fields without a documented migration purpose;
- query legacy tables from new UI merely to finish faster;
- duplicate CSS/components/services;
- leave a second half-finished path behind after replacing one;
- silently change architecture or product behavior.

## 21. Testing strategy

### Domain/application
- unit tests for deterministic policies/scoring/planning;
- integration tests against PostgreSQL for persistence/query behavior.

### Provider adapters
- contract tests with recorded/synthetic fixtures where practical;
- timeout/rate-limit/error mapping tests.

### Vertical slices
- integration tests for API/use case + database;
- authorization tests;
- migration/backfill tests where schema changes occur.

### UI
- component/state tests where valuable;
- browser E2E for critical flows;
- responsive regression coverage for specified breakpoints/platform profiles;
- explicit iOS/WebKit and TV/manual/device QA matrices for behavior browser automation cannot reliably prove.

Do not consider a feature complete solely because it builds.

## 22. Migration strategy

No big-bang rewrite.

Order:
1. complete canonical schemas/contracts missing from MediaCore/Library/Progress;
2. add safe migration/backfill from one legacy domain at a time;
3. move reads to canonical contracts;
4. move writes to canonical contracts;
5. verify parity and data preservation;
6. remove legacy write path;
7. remove bridge/read path;
8. drop obsolete schema only after verification and supported migration window.

During transition, one side must be declared authoritative. Avoid uncontrolled dual-write systems.

## 23. Architecture gates before feature coding resumes

Required:
- `DOMAIN.md` accepted;
- `DOMAIN-AUDIT.md` accepted;
- this architecture accepted;
- `UX.md` complete enough to define navigation/screens/platform behavior;
- approved mockups for major screens;
- dependency-ordered implementation roadmap.

Critical bug/security fixes remain allowed. New feature work must not expand legacy architecture while these gates are incomplete.

### Canonical search/discovery identity

Provider records are discovery/metadata inputs, never parallel domain identities.

For watch/read/listen media, Search and recommendation adapters resolve provider hits through canonical Work identity before normal presentation. Series/Anime remain `Work -> Season -> Episode`; one Work can have multiple provider mappings, including AniList entries for seasons/parts/specials.

For Games, Search/Discover resolves through the isolated Games module to canonical `Game` identity and optional GameRelease/platform presentation context. A Game is not converted into a Work.

The shared presentation contract is therefore conceptually a **typed canonical target + optional provider/presentation target**:
- Media target -> Work;
- Games target -> Game.

Default surfaces deduplicate by the owning domain's canonical identity. Provider-native views may expose individual provider entries, but durable actions first resolve back to the canonical target. This rule is shared by Search, Discover, Home and Requests; Library/Calendar use only the target kinds they explicitly support.

