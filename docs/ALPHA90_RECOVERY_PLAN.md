# Jularr Alpha-90 recovery — execution plan (12 October 2026)

> Later owner decisions in [BACKEND_NEXT](BACKEND_NEXT.md) take precedence: the first Owner is created in Setup without installation proof, Prowlarr/Sonarr/Radarr are one-time takeover sources only, Wake-on-LAN is an Owner setting, Games use the unified Arr+ and conversions use HLS with hls.js.

Status: **proposed implementation plan; no code migration, production database reset, provider credentials or feature completion is implied.** Target branch is `clean-start`, not `dev`. This is a short-path recovery plan, **not** an authorization to abandon the new architecture.

## 0. Evidence and source hierarchy

1. **Current implementation and binding architecture:** `clean-start`: `AGENTS.md`, `docs/CONVENTIONS.md`, `docs/FRONTEND_CONVENTIONS.md`, `docs/CODEMAP.md`, `docs/BACKEND_NEXT.md`, `docs/INSTANCE_SETUP.md`, `database/Account.sql`. Its current Backend has authentication/Profile/localization core, but **not** Work/Library/Scan/Playback/Arr+ runtime.
2. **Approved UX, subject to more recent decisions:** historical `dev` specs in `docs/mockups/**/SPEC.md`, especially setup, storage, admin acquisition settings/manual search/wanted, discover/home, login/profile, library, player and user settings.
3. **Inventory/triage:** `docs/documentation-audit-20261011` (`docs/audit/2026-10-11/README.md`, `MD_INVENTORY.md`, `CONFLICTS.md`) inventories 146 MDs/74 screen specs/94 mockup images. **This is incomplete content triage, not approval of an older runtime design.** `backup/docs-specs-pre-audit-20261011` preserves pre-audit source.
4. **Old behavior references only:** `dev/src/Jularr.Web` (1,806 old Web files). Inspect the narrow exact feature only; translate observable behavior, algorithms and tests to new contracts. **Do not copy old Pages, Stores, SQLite/JSON persistence, dependency injection or incompatible DTOs into the new host.**
5. Functional contract issues: [#813](https://github.com/Juloc/Jularr/issues/813) core request-to-play parity, [#396](https://github.com/Juloc/Jularr/issues/396) unified acquisition, [#403](https://github.com/Juloc/Jularr/issues/403) playback, [#888](https://github.com/Juloc/Jularr/issues/888) demand-paced transcoding, [#857](https://github.com/Juloc/Jularr/issues/857) resource budgets, [#880](https://github.com/Juloc/Jularr/issues/880) *historical full database cutover tracker*, [#930](https://github.com/Juloc/Jularr/issues/930) UI, [#864](https://github.com/Juloc/Jularr/issues/864) Search Planner. The newer clean-start baseline overrides draft Phase B table inventories and stale issue comments.

**Current hard gap:** `clean-start` has 22 Backend Service source files, six Logic source files, 19 Razor PageModels, one Account SQL baseline, and no real Work/Library/Player/Arr+ Backend verticals. Do not call prepared empty pages complete. The older `dev` implementation was much larger but not directly compatible.

## 1. Delivery policy and realistic target

**P0 = usable vertical, not Alpha-90 complete parity.** A delivery on **12 October** is possible only if narrow integrations, a reachable isolated PostgreSQL DB, mounted media, real sample files, provider credentials and active agent capacity are immediately available. No honest guarantee for full User + complete unified Arr+ + NAS + transcoding + nine-step setup in one day.

**Critical path:** fixed Work/Asset/File/Track + LibraryRoot contract -> isolated SQL baseline -> import scan -> authorized Library -> PlaybackPlan -> User playback. Acquisition reuses that same Work/Asset/File chain; it must not define its own. Bootstrap/permissions and minimal Admin configuration unblock all workers.

**P0 acceptance:** first Owner can sign in with an actual session; a mounted video file scans to one canonical Work/Asset/File; active Profile can browse permitted Library/Detail and Direct Play/resume; first Newznab release can be searched, manually selected, sent to SABnzbd, imported safely, and appear in the same Library; all state survives restart. If this fails, **do not ship a cosmetic "full area"**.

**P1 acceptance:** same core runs approved/auto requests, Wanted scheduling, bounded retry/history, Anime/Movie/TV scopes and player Remux/FFmpeg transcode; cover/metadata and profile module gates work end-to-end. **P2:** complete 9-step setup UX, advanced provider health, upgrade scoring, book/manga/novel parity, media optimization, storage repair, retention, NAS vendor/WOL, streaming adaptation/idle renditions, full Sonarr/Radarr migrations.

## 2. Shared architecture — no exceptions

- One ASP.NET Core host in `Jularr.Frontend`; one `Jularr.Backend` assembly, external I/O in `Jularr.Integrations`. **Data -> Service -> Logic** boundaries: typed versioned Data, static SELECT/authorization/orchestration in Service; **all DML in canonical unversioned Logic**; shared SqlContext/transaction; no Store layer, no second service host or DB.
- Ordinary relational identities and FKs: PostgreSQL `bigint` / C# `long`; one canonical `Works.Id`. Anime classification of Series/Movie; Book/LightNovel/Manga/Audio reuse agreed structure; `Work -> Structure Unit -> Edition -> Version -> MediaAsset -> StoredFile -> Track`; only real approved enum/type tables. Never resurrect discarded AccountGroups by copying older plans.
- Account != Profile, `AccountProfiles` access, Owner/MediaManager/User grants, profile-private progress/watchlist/requests. One instance-module gate + effective profile visibility; no hardcoded enabled modules.
- Reads use **PostgreSQL projections**, not live NAS/API/provider calls. Workers do bounded provider/file I/O outside long transactions and commit through Logic with idempotent updates. No destructive active-db cutover; use a disposable **isolated** PostgreSQL instance and explicitly approved fresh baseline. Never destroy `dev` or an active installation.
- Frontend Pages compose reusable C# components; shell in `Pages/Shared/Shell.cs`; typed `Form<T>`, `Table<T>`, `Tabs`, `Split` optional MasterDetail; Topbar breadcrumb is the **sole** visible page heading. Text keys registered in English through PostgreSQL localization. Page/module/component TS/SCSS, Vite hashed assets, no invented clickable actions or fake sample data.
- Security: no secrets in URL/logs/DB plain text; owner bootstrap only once with installation proof; check grants on **every Service call**, do not authorize merely by hiding controls; antiforgery for writes; allowlisted storage roots; path traversal and symlink avoidance; media file response authorization; bounded FFmpeg and network workers.

## 3. Contracts that must freeze before agent fan-out (one owner)

Create a single small reviewed contract PR, **not** a weeks-long inventory. Define exact fields, FKs, indexes, route shapes, and data types for:
- `InstanceSettings`, `InstanceModules`, exactly-once setup/bootstrap state, provider/config secret references; reuse existing Accounts/Profiles/sessions/locales.
- `LibraryRoots`/mount references and permitted file access; `Works`, provider IDs and titles, media kind, structure units, editions/versions, `MediaAssets`, `StoredFiles`, `MediaTracks`, artwork and scan operations; file fingerprints and confidence/identity relations.
- Profile Watchlist, per-content exact `MediaProgress`, completed state/history, `PlaybackSessions` and minimal `PlaybackPlan` output.
- Consumer `Request`/approval and canonical `Wanted` acquisition target; AcquisitionProfile/ReleaseRule, Indexer/provider capability, normalized ReleaseCandidate (external evidence), DownloadJob / ImportOperation / history/retry/blocklist and final library link. Temporary candidate score is **not** a permanent fact about a release.
- Storage availability vs genuinely missing file, configured root read permissions; explicit handling of missing storage and safe paths.

**Gate 0:** one schema owner checks the approved clean-cut decision rules and compiles an **isolated, clean-installable** Postgres baseline; all agents read these contracts instead of inventing duplicate tables/DTOs. The larger Phase-B draft (different historical 157/168 table counts) is **not** wholesale imported or treated as schema authority.

## 4. Parallel agent lanes (independent branches/worktrees)

| Lane | Owner and gate | First deliverable | Legacy behavior reference |
| --- | --- | --- | --- |
| A — foundation/setup | Exclusive schema/permission owner; precedes all persistence lanes | SQL + versioned DTO contracts, Owner bootstrap proof, real login/session, Instance/module/settings read/update, root/provider config save/test, Setup readiness/completion | `dev/Pages/Account/Setup*.cshtml.cs`, `dev/Features/Instance/InstanceModules.cs`; `clean-start/docs/INSTANCE_SETUP.md` |
| B — NAS/storage/library | Depends A's canonical asset/root fields | Allowlisted roots; mounted SMB/NFS/local read; bounded mount/health test; rescannable inventory and metadata; ffprobe/MediaTracks; real Library/Detail read models; safe reconciliation | `dev/Features/Storage/StorageAvailability.cs`, `StoragePaths.cs`, `Features/Library/LibraryScanner.cs`, `LibraryScanCoordinator.cs` |
| C — User | Depends A auth and B typed read contracts | Profile switch, module-aware nav, authorized Home/Discover, Library, Detail, Watchlist, Requests, exact Progress; English resources & actual page controls | `dev/Pages/Discover/Index.cshtml.cs`, `Pages/Library/*`, `Features/Home/*`; mockups Home/Discover/User Settings |
| D — player | Depends B playable File/Track and A sessions | Capability probe; one authorized PlaybackPlan; direct file streaming + Range; remux fallback; paced FFmpeg HLS/transcode; seek, resume, cleanup & diagnostics; optional safe HW probe | `dev/Features/Playback/Decision/*`, `Playback/HlsPlayback.cs`, `Playback/Transcoding/*`, `Features/Media/Optimization/*` |
| E — unified Arr+/Acquisition | Depends A and B canonical Work/Wanted/Asset fields | Request->Wanted; direct Newznab + optional Prowlarr adapter; one Manual/Auto SearchPlanner, parser/candidate scoring; SABnzbd queue; completed-download import; history/recovery | `dev/Features/Acquisition/{Wanted,Indexers,Search,Quality,Selection,DownloadClients,Import,Pipeline}` and `#396` |
| F — UI/config integration | Starts after typed contracts frozen, later depends lane success | Wire real Setup, Admin storage/provider/indexer/downloader/wanted/request/settings views to actual Services. No placeholders and no second frontend implementation. | `dev/docs/mockups/{setup-wizard,admin-storage,admin-wanted,admin-acquisition-settings,admin-providers,player,library}/SPEC.md` |
| G — integration/QA (one integration owner) | Starts immediately, runs after each vertical | Isolated DB + fixtures, seeded local sample media, mock Newznab/SABnzbd and blocked-provider cases, restart recovery, auth/permission, client and browser tests, merge order. | `#813`, `#403`, `#857` acceptance |

No lane may modify another lane's DTO/SQL contract without an explicit small contract-change PR; no parallel edits to shared Schema/Program/Service gate. Use one responsible integrator and merge small validated PRs into `clean-start` in dependency order, never seven long-lived divergent branches. Never merge from `dev` as a bulk import.

## 5. Narrow implementation sequence and gates

### Gate 1 — bootstrap and minimal setup (first dependency)
Provision an **isolated PostgreSQL DB**, install approved fresh SQL, ensure `JULARR_DB`, local HTTPS and Vite production assets. Use current host/Backend auth; add exactly-once Owner installation proof, authorized Owner+Profile login, secure cookie, actual logout/profile switch. Read readiness (DB, schema, app-data permission, media mounts, disk). Minimal Setup must save real instance/module/locale/timezone state, one library root, one metadata provider, one indexer, one SABnzbd connection; run bounded connectivity tests. Surface partial errors; optional AI/migration/branding/backup sections remain legitimately skippable when unsupported, not fake completed.

Full approved Wizard remains **nine pages**: Start/System -> Setup type -> Instance/Modules -> Storage/Roots -> Downloader/Usenet -> Providers -> AI -> Existing media -> Review/Finish. Each step edits its actual Admin owner; state survives resume, no parallel Setup-only DB. Use email/password canonical account identity, **not** legacy username/optional-email fields.

### Gate 2 — real media storage and library
- Host mounts SMB/NFS shares through OS/container volumes; Integrations owns access abstraction and bounded tests; Backend owns configured roots/constraints. Never expose raw directory browsing outside allowlist. Scan only configured allowed roots, reject symlink traversal/path escapes.
- A durable/resumable scan queues bounded directories/files, determines probable media type/structure, fingerprints without whole-file hashing on every scan, probes via ffprobe when changed, saves canonical Work->Edition/Version/Asset/File/Track. Maintain ambiguous/unmatched queue for manual resolution, not mistaken auto-matches.
- One consistent Storage availability state. Temporary offline NAS **never** triggers mass deletion or missing media; DB-stored library and cached artwork remain readable offline. Playback probes just its specific owning root/file.
- **Owner decision required for WOL:** older [#155](https://github.com/Juloc/Jularr/issues/155) reserves Wake-on-LAN to Owner and prohibits ordinary Play from triggering it, while later [#411](https://github.com/Juloc/Jularr/issues/411) describes automatic wake on demand. Until resolved: safe minimal implementation = read-only availability + bounded retry, **no automatic WOL by ordinary users**. Do not silently choose one behavior.
- Import uses staged plan + safe path mapping, hardlink iff same filesystem else copy by configured policy, atomic copy/verify/commit, never rename/delete originals on failed import; restore/retry after crash; no duplicates.

**Gate:** cold start -> NAS scan -> one playable canonical file and complete track list -> authorized Library/Detail; temporarily unavailable mount keeps DB content; restart and second scan are idempotent.

### Gate 3 — functioning User vertical
Actual Account sign-in/Logout -> permitted Profile selection -> effective instance/profile modules -> Home/Discover (from permitted providers, cached separately from library) -> Work Detail -> Request/Add -> Library/Watchlist -> Play/Reader -> exact resume/history. Keep user and Admin request status separate; use one request lifecycle. Home/Library read local DB projections only; provider discovery uses bounded refresh/cache worker. A real button must have a Service operation. No duplicated title in content. **First scope: Anime, Movie, TV**, then Book/Manga/LightNovel/Audiobook once the shared chain succeeds.

### Gate 4 — playable streams and weak-server efficiency
A single `PlaybackPlan` considers actual source audio/video/subtitles, browser capabilities, desired quality/bandwidth, detected hardware and resource budget:
1. **Direct Play** + authorized Range and intact original whenever compatible.
2. **Direct Stream/Remux** container change without re-encoding when possible.
3. **FFmpeg on-demand HLS** transcoding only when needed, with safe seek/segment naming/cache leases, cancellation, stop/end cleanup and software fallback if hardware encoder fails.

Bound live transcoding to real segment demand/high-water rather than encode the full movie in advance ([#888](https://github.com/Juloc/Jularr/issues/888)); separate fast seek/start bursts from steady-state pace. Prioritize active playback over library scan/provider refresh ([#857](https://github.com/Juloc/Jularr/issues/857)). Expose Direct/Remux/Transcode reason and transcode failure, not fake quality labels. Persist exact per-Profile resume position and separate completion through canonical Service/Logic.
**P2, not gate for first playback:** permanent container remux optimizer, weak-server multi-viewer QoS expansion, idle prepared renditions (#889, default off), full ABR multi-rendition, AI/learning, mobile casting enhancements. No automatic rewrite of stored originals.

### Gate 5 — unified Arr+ recovery
`Discover/Request -> approved Request -> canonical Wanted target -> SearchIntent -> capability-aware staged QueryPlan -> normalized candidates + provenance -> identity check -> AcquisitionProfile score/quality/language -> manual or authorized auto Grab -> SABnzbd -> verified import -> canonical Library -> Player`.

- **One** Wanted/Monitoring model for Work + appropriate unit granularity (Movie, Series/Season/Episode, Book edition, Manga/LightNovel volume/chapter), including missing/searching/failed states; request moderation stays separate.
- One SearchPlanner for Auto+Manual: actual Newznab caps/IDs/categories/query budgets, staged alias fallback, partial provider health, pagination, bounded timeout, rate limit/backoff, safe dedupe and provenance. Direct Newznab is P0; Prowlarr optional compatibility adapter, not mandatory gateway.
- One release parser + identity/coverage matcher + profile-scoped scorer; keep rejected/wrong candidate visible in Manual Search with reasons, **never auto-grab wrong unit**. Reuse confirmed evaluation algorithms from `dev`; no separate Anime/Movie/TV selection engines.
- **Keep SABnzbd as initial download executor**; native NNTP/PAR2/unrar [#715](https://github.com/Juloc/Jularr/issues/715) is deliberately later planning. Store credentials write-only/secret references, avoid redacting by hand.
- Completed download -> one verified content dispatcher -> safe media-specific mapper/placer and canonical Work file. Remote path mapping, duplicate protection, completed/failed/retry state and job history survive restart. Anime/Movie/TV E2E first; books and legacy indexer migrations later.
- Admin screens: Indexers/Provider setup and tests; AcquisitionProfile rules; Wanted list; Search/Current/History acquisition dialog; Requests moderation; download jobs and import failures. These are consumers of one Backend, not second management engines.

**Gate:** mock+actual sanctioned indexer/SAB test (with permitted media test source), candidate -> submit -> completed-download fixture -> canonical import -> playback; duplicate/ambiguous identity and outage/restart tests pass. Do not claim full Sonarr/Radarr/Readarr/Seerr/Prowlarr replacement without this and appropriate media parity.

### Gate 6 — integration, deployment and handoff
The existing local preview `docs/LOCAL_PREVIEW.md` points to `D:\jularr-clean-start-server\checkout`; it is **not evidence of a working DB**. Do not reuse active database. Assemble one disposable compose/dev environment with PostgreSQL, Jularr host, bind-mounted fixture media, optional SABnzbd, synthetic indexer fixtures, ffprobe/FFmpeg availability and exact health checks. Commit lockfile and generated asset rules, secrets externally, no destructive cleanup. Test clean install, restarted jobs, invalid passwords, profile authorization, path traversal, offline NAS, failed indexer/FFmpeg, double grab, real browser mobile/desktop responsive and performance under resource limits. Merge only independently passing focused PRs. Publish a functional gap checklist, not a percentage invented from compiling skeletons.

## 6. Sequence for aggressive parallel execution

**0–2 h (planning/contract gate, can block):** One integrator verifies exact SQL/Service contracts from clean-start and chooses *single* Work/File/Wanted/Session owners. Identify available mounted media, ffprobe/FFmpeg, isolated DB, provider/SAB credentials; record Owner bootstrap proof and NAS WOL preference. Other agents read only targeted dev files/specs and prepare tests/interfaces, **no unreviewed schema changes**.

**2–8 h:** A ships working Owner+minimal config; B ships scan->Library read with fixtures; C can wire Profile/Shell to known APIs; D builds capability/Direct Play against B contracts; E builds provider query/mock SAB transport and candidate normalization against A's frozen contracts. Integrator merges tested contracts first, then verticals; no waiting until all work is done.

**8–16 h:** Direct video play and exact progress integration; user Library/Detail/Home; Admin indexer/SAB/root config; one manually selected release -> staged verified import; restart recovery checks. If gates fail, **scope down** to a functional manually acquired local-video vertical, not polished empty pages.

**16–24 h:** IF previous gates are green, add Auto Wanted scheduling, request moderation, more media types, remux and FFmpeg fallback; run #813 real E2E tests and privacy/security/resource checks. A 24-hour window is **an aggressive coordination attempt**, not a credible guarantee of Alpha-90 parity. Full nine-step setup, every media kind and end-to-end Arr replacement require additional slices and actual integration time.

## 7. Agent work request template (enforce on every subtask)

> Base: latest `clean-start` and shared approved contract revision. Read `AGENTS.md`, relevant `docs/CONVENTIONS.md` + `docs/FRONTEND_CONVENTIONS.md` and only named owner specs. Inspect the **specific old dev file(s)** as behavior/test evidence; never import legacy Stores, namespace tree, SQL/JSON/SQLite or entire Razor pages. Implement one demonstrably working vertical using canonical `Data.<Area>.<Entity>.V1`, `Service.<Area>.<Entity>.V1` with SELECT-only reads/authorization/orchestration, `Logic.<Entity>` for DML, `Integrations` for external I/O and one shared SqlContext. No guessed DTOs/schema/permissions; raise a concrete contract blocker to the integrator immediately. Write isolated PostgreSQL/transport tests and at least one user-observable acceptance test. Preserve English UI resource registration and component-owned assets; no duplicate page titles or fake buttons. Push **your own branch**, report touched paths, tests, acceptance, missing Backend behavior and conflicts. Never merge `dev` wholesale or modify someone else's contract/branch.

Integration owner accepts a lane only after an actual read/write/response boundary test, failing/error states and one demonstrable runtime scenario; green build alone does **not** count.

## 8. Blockers requiring owner/environment facts (do not guess)

1. **Owner bootstrap proof**: isolated installation token/CLI setup proof; Owner grant matrix versus MediaManager.
2. **NAS**: actual mounted share protocol and permissions (SMB/NFS/local), source root(s), volume paths, read-only/read-write policy, and the #155 vs #411 auto-WOL policy.
3. **Playback host**: OS/container FFmpeg/ffprobe, CPUs/GPU hardware acceleration/driver access, WAN uplink, max simultaneous viewers, realistic codecs/media fixtures.
4. **Acquisition**: authorized Newznab indexer(s), SABnzbd endpoint and staging/final roots, download/import path mapping, first three supported media types.
5. **PostgreSQL**: explicitly isolated database for clean install; **no permission given** to reset existing dev or production data.

Lacking these facts, agents should implement contracts + mocks but must label external-live E2E **blocked**, not "working."

## 9. Explicitly out of the critical path

A new native NZB/NNTP downloader; full 9-page advanced wizard polish; AI/OCR/translations/learning, native mobile clients, Games, complex permission groups, heavy cross-provider migrations, full automated series season-pack handling, all subtitling enhancements, permanent pre-transcode/idle renditions, cloud/distributed architecture, Redis/Kubernetes, standalone frontend rewrite, artificial metadata provider fallback, and destructive migration of an active installation. Keep specs for later; **do not discard** them.

## 10. Source links for every agent

- Audit branch: https://github.com/Juloc/Jularr/tree/docs/documentation-audit-20261011 ; immutable pre-audit snapshot: https://github.com/Juloc/Jularr/tree/backup/docs-specs-pre-audit-20261011 .
- Canonical new Backend: https://github.com/Juloc/Jularr/blob/clean-start/docs/BACKEND_NEXT.md ; Setup: https://github.com/Juloc/Jularr/blob/clean-start/docs/INSTANCE_SETUP.md ; Frontend rules: https://github.com/Juloc/Jularr/blob/clean-start/docs/FRONTEND_CONVENTIONS.md .
- Historical behavior: https://github.com/Juloc/Jularr/tree/dev/src/Jularr.Web/Features/Acquisition ; https://github.com/Juloc/Jularr/tree/dev/src/Jularr.Web/Features/Playback ; https://github.com/Juloc/Jularr/tree/dev/src/Jularr.Web/Features/Storage ; https://github.com/Juloc/Jularr/tree/dev/src/Jularr.Web/Features/Library .
- Acceptance: https://github.com/Juloc/Jularr/issues/813 ; Unified Arr: https://github.com/Juloc/Jularr/issues/396 ; Playback: https://github.com/Juloc/Jularr/issues/403 ; transcoding pace: https://github.com/Juloc/Jularr/issues/888 ; resource limits: https://github.com/Juloc/Jularr/issues/857 ; acquisition search: https://github.com/Juloc/Jularr/issues/864 ; schema tracker: https://github.com/Juloc/Jularr/issues/880 .

**Final truth criterion:** A user can sign in, find permitted content, request or load it, watch with correct seek/resume and observe real acquisition/download/import state in the same canonical SQL domain. Nothing weaker is Alpha-90 functional parity.
