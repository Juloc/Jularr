# Offline Target Completion — Implementation Pack

Status: **binding planning pack for the approved Offline target UX; implementation code is not authorized by this document alone**.

Branch used to assemble this pack: `planning/offline-ux-20261004`.

Primary backlog:
- #839 — true offline cold-start + unified local Reader repository;
- #840 — device-local Offline actions on consumer detail surfaces;
- #415 — Smart Offline, only after the explicit Offline foundation is stable.

Existing foundations that remain authoritative:
- #221 — Offline Library & Reader Sync;
- #225 — bounded offline playback;
- #289 — Unified Reader;
- #662 — canonical exact progress/completion;
- #819 — PDF Smart Book;
- #835–#838 — canonical Notifications target implementation.

Binding UX:
- `docs/mockups/downloads-offline/SPEC.md`;
- `docs/mockups/offline-settings/SPEC.md`;
- `docs/mockups/download-selection/SPEC.md`;
- `docs/mockups/download-quick-view/SPEC.md`;
- `docs/mockups/download-notifications/SPEC.md`;
- `docs/mockups/offline-storage-location/SPEC.md`;
- `docs/mockups/home/SPEC.md` — disconnected cold-start state;
- `docs/mockups/player/SPEC.md` — Offline playback;
- `docs/mockups/reader/SPEC.md` — Offline reading;
- `docs/OFFLINE_UX_CROSS_SPEC_AUDIT.md`.

---

## 1. Goal

Finish one coherent **device-local Offline product** across Web/PWA, Android and future native Desktop without creating a second media model, second progress model, second notification system or separate per-media download managers.

The finished flow is:

```text
Detail / Player / Reader
    -> Offline download selection
    -> existing device-local package/download owner
    -> global Download Indicator / Quick View
    -> Downloads & Offline
    -> verified local package
    -> normal Player / Reader
    -> local-first progress / annotations
    -> canonical sync when connectivity returns
```

Cold-start requirement:

```text
app/browser starts with server unreachable
    -> safe local owner bootstrap
    -> normal Jularr shell in disconnected mode
    -> local catalog projection
    -> verified local Player / Reader content remains reachable
```

---

## 2. Current implementation map

The current code already contains substantial Offline foundations. The implementation work must converge them rather than replace them.

### 2.1 PWA structured Reading

Current browser Reading Offline path:

- `wwwroot/js/offline-library.js` — pure package/diff/state rules;
- `offline-library-storage.js` — profile-scoped IndexedDB + OPFS/degraded storage;
- `offline-library-manager.js` — manifest download/update/queue;
- `offline-library-repository.js` — local-first Reader content + progress/bookmark sync seam;
- `offline-library-ui.js` — current consumer controls;
- `ClientApiOfflineLibraryEndpoints` — manifest/chapter/assets/sync server boundary.

Current strength:
- verified local chapters;
- differential manifest logic;
- progress/bookmark sync queue;
- local-first in-page chapter content.

Current gap:
- navigation cold-start still falls through to the generic service-worker Offline document;
- the normal Reader page still starts life as server-rendered HTML;
- the local chapter repository is entered only after an online Reader page already exists;
- chapter Contents and all Reader state are not yet fully local-first from a cold launch.

### 2.2 PWA binary media

Current browser binary package path:

- `offline-media.js`;
- `offline-media-storage.js`;
- `offline-media-manager.js`;
- `offline-media-ui.js`;
- service-worker `/_offline-media/**` response path;
- `ClientApiOfflineMediaPackageService` + endpoints.

The server package service already exposes media-package sources for:
- Anime Episode;
- Audiobook;
- Movie;
- TV/Series;
- Manga chapter;
- Book document.

Current strength:
- profile-scoped packages/chunks;
- Range/chunk downloads;
- browser quota admission;
- resumable package state;
- local media response through the service worker.

Current gaps relative to approved UX:
- package presentation metadata is too thin for full cold-start Home/manager experiences;
- detail actions are inconsistent and media-specific;
- current manifest request does not model the approved quality/audio/subtitle/Learning/image-package selection;
- portable rendition choice is mostly implementation-driven rather than resolved from a user package request;
- current package manager and structured Reading manager are parallel adapters rather than one consumer projection.

### 2.3 Android

Current native foundations:

- `OfflineDownloads` / `OfflineStore`;
- `LibraryDownloads` / `LibraryStore`;
- `OfflineAccountPolicy`;
- `DownloadStateMachine`;
- `OfflineStoragePolicy`;
- WorkManager workers;
- media verification;
- progress queues;
- Library manifest diff + local request interception.

Important:
- keep the existing owner boundary;
- do not replace WorkManager/local files with a browser abstraction;
- native storage remains native;
- only shared product contracts/read models/actions should converge.

### 2.4 PWA service worker

Current navigation fallback:

```text
navigate
 -> network
 -> on failure: /offline.html
```

Private media bytes are correctly excluded from CacheStorage.

This is the main cold-start blocker.

### 2.5 Current detail actions

Current implementation is fragmented:

- `_OfflineLibraryAction.cshtml` provides a whole-work Reading action;
- Episode/Manga surfaces contain direct `data-offline-*` controls;
- the generic binary package API supports additional media kinds even when their detail UI does not expose the finished action;
- labels such as Requested/Downloading/Preparing are also used for **server/library acquisition**, making an unqualified "Downloading" state ambiguous.

#840 must solve this through one semantic component/contract, not more page-local buttons.

---

## 3. Non-negotiable ownership model

### 3.1 Server owns canonical media identity

Offline packages reference canonical:

`Work -> Structure -> Edition -> Version -> Asset/File -> Track`

A client-local package never creates an `OfflineWork`, `OfflineEpisode`, `OfflineEdition` or other parallel canonical identity.

### 3.2 Client owns local bytes and transfer state

Each client implementation owns:
- local package bytes;
- local queue/transfer state;
- verification state;
- local storage admission;
- current-device storage location.

The server does **not** own device-local byte placement.

### 3.3 Canonical progress remains canonical

Keep separate:

```text
CurrentItem
ResumePosition
CompletedThrough
ProviderProgress
```

Offline writes locally first and later reconcile through the same canonical progress owner.

No Offline-specific watched/read history.

### 3.4 One consumer projection, not necessarily one physical store

PWA Reading and PWA binary media currently use different storage shapes for valid technical reasons.

Do **not** force all bytes into one physical database.

Instead introduce one read-only/application-level **Offline Local Catalog projection** over adapters.

It may aggregate:
- structured Reading package store;
- binary media package store;
- native Android media store;
- native Android Reading store.

The projection is not a new durable truth.

### 3.5 Notifications remain external to Offline ownership

Offline emits domain facts.

Canonical Notifications owns:
- inbox persistence;
- Bell;
- Toast attention;
- Push/E-Mail;
- Quiet Hours;
- Digest;
- read/unread;
- shared action resolution.

No `OfflineNotificationStore`.

---

# 4. Target local contracts

## 4.1 OfflineLocalCatalog — read-only projection

Introduce a shared application contract conceptually equivalent to:

```text
OfflineLocalCatalog
  listActive()
  listReady()
  listRecentReady()
  listContinueReady()
  find(target)
  getUsage()
  getSyncStatus()
```

Implementations:
- PWA adapter over `offline-library-*` + `offline-media-*`;
- Android adapter over `LibraryDownloads` + `OfflineDownloads`;
- future native Desktop adapter over its managed store.

This contract must **not** own persistence.

### OfflineLocalItem projection

Consumer projection should be able to express:

- package ID;
- canonical media target type/id;
- Work ID where applicable;
- structural unit identity;
- Work title;
- unit title/label;
- artwork/local artwork descriptor;
- media category: Video / Reading / Audio;
- package origin: explicit / Smart Offline;
- state: queued/downloading/paused/waiting/verifying/failed/ready;
- downloaded/total bytes when meaningful;
- local verified state;
- created/updated/completed timestamp;
- user-facing failure class;
- local open target;
- current local progress summary;
- update/stale state;
- pinned/Keep Offline state.

This is a **view model**, not a new domain table.

## 4.2 Package presentation metadata

Existing local manifests/packages must retain enough metadata to render cold-start Home/manager without the server.

At minimum persist inside the existing package record/manifest:

- Work title;
- structural unit label;
- media kind;
- canonical IDs;
- local artwork reference when packaged;
- duration/page/chapter count where useful;
- selected Edition/language;
- selected audio/subtitle package summary for video;
- local open route/target descriptor;
- explicit vs prefetched origin;
- progress snapshot as a fallback only; current local progress queue remains newer when present.

Never persist provider secrets or server filesystem paths.

## 4.3 OfflineOwnerBootstrap

Cold-start cannot depend on `document.body.dataset.profileId`, because no authenticated Razor page exists yet.

Add one minimal local owner bootstrap record.

Required fields conceptually:

```text
schemaVersion
serverOrigin
profileId / stable owner key
state = accessible | locked
lastVerifiedAt
optional non-sensitive display label
```

Rules:
- no bearer/session secret;
- no password/token;
- set/update only from an authenticated current-profile context;
- explicit logout locks/removes according to Offline Settings policy before navigation completes;
- profile switch makes the previous owner's content inaccessible before the new profile is shown;
- a 401/revocation while reachable updates the local boundary;
- a pure network failure does not silently purge the last valid owner;
- owner uncertainty fails closed.

PWA may store this in a tiny dedicated IndexedDB record/local bootstrap DB. Do not rely on a DOM data attribute for cold-start.

Android continues to use `OfflineAccountPolicy`; adapt it to the same semantics rather than replacing it.

---

# 5. Phase 0 — contract consolidation and tests

Goal: define shared shapes before changing UI.

## 5.1 Add target contracts

Create/extend canonical client contracts for:

- local package identity/presentation;
- offline package request options;
- effective package selection;
- package-size estimate;
- available track/language package choices;
- verified local open target;
- local catalog projection.

Prefer evolving existing `ClientApiOffline*` contracts.

Do not create `/api/v2/offline` in parallel unless a breaking boundary absolutely requires it.

## 5.2 Package request contract

The current manifest endpoint mostly says "give me the package".

The approved Download Selection UX requires the server to resolve:

- scope;
- video/audio/image quality target;
- audio-track package;
- subtitle package;
- Learning data;
- selected chapter/episode IDs;
- current defaults/capabilities;
- expected size.

Introduce one request shape conceptually:

```text
OfflinePackageRequest
  target
  unitIds?
  quality?
  audioPolicy?
  audioTrackIds?
  subtitlePolicy?
  subtitleTrackIds?
  includeLearning?
  imageQuality?
```

The server resolves the request against canonical Version/File/Track capabilities.

The client never chooses codecs/container/transcode rules directly.

## 5.3 Estimate/options endpoint

The selection dialog needs truthful data before starting.

Either:
- extend an existing descriptor endpoint; or
- add a focused options/preview operation under the same Client API family.

It should return:
- allowed scopes;
- current local state if useful;
- quality choices that actually exist/can be produced;
- available audio/subtitle tracks;
- resolved defaults;
- estimated bytes when known;
- reasons a choice is unavailable.

Avoid duplicate "options" ownership in each media feature.

## 5.4 Tests

Add contract-level tests before UI:
- media-kind mapping;
- package request validation;
- no canonical-ID duplication;
- size-estimate unknown vs known;
- unavailable track handling;
- explicit-vs-prefetched origin preservation;
- owner/profile isolation.

---

# 6. Phase 1 — safe PWA cold-start shell (#839)

Goal: fresh launch with server unreachable still renders the normal disconnected Jularr Home contract.

## 6.1 Do not cache authenticated Razor HTML as the source of truth

Avoid solving cold-start by blindly storing complete private Home/Reader HTML in CacheStorage.

Reasons:
- cross-profile leakage risk;
- stale CSRF/session markup;
- stale server state;
- hard-to-version private HTML.

Keep private media/package data in the existing profile-scoped stores.

## 6.2 Upgrade the service-worker navigation fallback

Current:

```text
network navigation failure -> generic /offline.html
```

Target:

```text
network navigation failure
  -> cached static Offline shell bootstrap document
  -> OfflineOwnerBootstrap
  -> local route resolver / OfflineLocalCatalog
  -> disconnected Home or local Reader/Player target
```

`offline.html` may remain the physical bootstrap document, but it must stop being a separate generic product page.

It should mount the **same product hierarchy/visual language** as the normal shell.

## 6.3 Static assets

Precache only the minimal versioned shell assets needed to:
- render app chrome;
- render Offline Home;
- open Downloads & Offline;
- open local Player/Reader bootstrap;
- read local package stores.

Keep private content outside CacheStorage.

## 6.4 Original requested route

The fallback must preserve/inspect the intended route.

Examples:
- `/` -> disconnected Home;
- local Reader deep link -> Reader bootstrap if target exists locally;
- local playable target -> Player bootstrap;
- unsupported/server-only route -> contextual Offline unavailable state.

Do not redirect every failed navigation to a Home-only dead end.

## 6.5 Home local projection

Implement a client-side disconnected Home projection using `OfflineLocalCatalog`.

It needs:
- Continue Offline;
- Available on this device;
- optional Recently downloaded;
- waiting/failed Download Indicator state.

Do not reproduce server recommendation logic.

No server-only row should render as endless skeleton.

## 6.6 Search

If local metadata index is sufficient:
- search the local catalog only;
- label it as Offline search.

Otherwise:
- keep Search entry;
- explain that full Search requires the server.

No filesystem scan.

## 6.7 Reconnect

On `online` / successful server probe:
1. revalidate account/profile;
2. drain progress/bookmark/Learning queues;
3. refresh package/update state;
4. transition Home back to online data;
5. preserve current route/focus/scroll where practical.

Do not reload merely to erase the Offline banner if incremental recovery works.

---

# 7. Phase 2 — one Reader repository path (#839, #289 dependency)

Goal: normal Reader consumes one document/repository contract online or offline.

## 7.1 Eliminate server-render dependency for content resolution

The Reader page may retain server-rendered shell/bootstrap online during migration, but **chapter/document content must move behind one client-readable repository boundary**.

Target:

```text
Reader
  -> ReaderDocumentRepository
       -> canonical Client API URL
            -> PWA local interceptor when verified local exists
            -> Android WebView local interceptor when verified local exists
            -> server when required/available
```

Do not keep:
- one server-rendered content implementation;
- one PWA string renderer;
- one Android special renderer.

## 7.2 Reuse Unified Reader work

This phase must land on top of current #289/#819 document/layout contracts.

It must support:
- reflowable Book/LN/Web Novel;
- Manga/image sequence;
- fixed-layout EPUB;
- PDF Original;
- PDF Smart Book;
- scans/magazines/artbooks according to document capabilities.

Offline is a source adapter, not a renderer selection rule.

## 7.3 PWA local API interception

Current Android already has local request interception for Offline Library routes.

Add an equivalent PWA local responder for canonical Reader repository requests.

Refactor browser storage access so code used by a service worker does not depend on `window` / DOM-only state.

Preferred:
- shared storage-core module with explicit `profileId/owner` argument;
- small window adapter;
- small service-worker adapter.

Avoid duplicating the IndexedDB/OPFS schema implementation.

## 7.4 Manifest-backed Contents

While offline:
- Contents comes from local manifest;
- previous/next comes from local canonical structure;
- not-downloaded chapters remain visible when structure is known;
- only verified local units are openable.

This closes the current chapter-drawer network dependency.

## 7.5 Progress/bookmarks/annotations

The Reader uses one local-first write API.

Required:
- exact locator progress;
- completion;
- bookmarks;
- supported annotations;
- Learning events where defined.

Android's existing `LibraryDownloads.recordProgress` / bookmark queue must be wired to the Reader path rather than left unused.

Do not widen #221's older coarse locator shape if #662/#289 now provide a stronger canonical locator; migrate toward the stronger model.

## 7.6 Manga

Manga page packages reuse the same manifest/repository/sync model.

Do not keep Manga solely in the generic binary package path if that prevents:
- canonical chapter structure;
- page-order manifest;
- exact Reader progress;
- Reader offline Contents.

Binary page assets can still live in the binary/object storage adapter.

One Reader package contract may reference image resources without forcing text payload semantics.

---

# 8. Phase 3 — cold-start Player/local media open (#839)

Goal: verified local video/audio opens without server bootstrap.

## 8.1 Local playback bootstrap descriptor

Persist enough verified descriptor data with the package to recreate:
- title/unit;
- duration;
- selected/local tracks;
- local media resource URL;
- resume state;
- Learning package availability;
- canonical next-target metadata when known.

Do not persist a full online `PlaybackPlan` as authoritative offline state.

The local package itself is the offline delivery capability.

## 8.2 PWA

Use existing service-worker `/_offline-media/**` byte serving.

Add a local Player bootstrap path that:
- resolves package from the active local owner;
- opens the normal Player chrome;
- only exposes locally packaged tracks;
- writes local progress queue;
- handles missing canonical-next as defined in Player SPEC.

## 8.3 Android

Keep native Media3/local-file playback.

Make cold-start navigation able to reach `OfflineDownloads.readyEpisode` / corresponding audio target without requiring a server bootstrap first.

Do not regress native MediaSession behavior.

---

# 9. Phase 4 — detail-surface Offline actions (#840)

Goal: every supported consumer detail surface uses one explicit **device-local** Offline action contract.

## 9.1 Separate two meanings

Server/library acquisition:
- Request;
- Requested;
- Looking for media;
- Preparing library media;
- server acquisition/download/import.

Device-local copy:
- Offline herunterladen;
- Wird offline gespeichert;
- Offline verfügbar;
- Offline aktualisieren;
- Offline-Download erneut versuchen;
- Offline-Kopie entfernen.

Never use bare **Downloading** when the surface can represent both.

## 9.2 Shared action component

Replace page-local Offline buttons with one semantic component/controller.

Conceptual inputs:

```text
OfflineActionModel
  target type/id
  capability
  local state
  selection required?
  update available?
  Smart Offline origin?
```

Output:
- one action/status;
- opens Download Selection when required;
- directly starts only when there is genuinely nothing to choose;
- opens manager for state/recovery where appropriate.

The component is presentation over the existing local owners.

## 9.3 Anime / TV

Anime/TV detail:
- current episode;
- multiple episodes;
- season scope.

Episode rows show device-local state separately from server availability/request state.

Do not create `SeasonDownload`.

## 9.4 Movie

Movie is normally one unit.

Open selection only for meaningful options:
- quality;
- audio;
- subtitles;
- Learning if applicable.

No fake scope chooser.

## 9.5 Book / Light Novel

Replace current whole-book-only `_OfflineLibraryAction` flow with:
- whole Work;
- selected chapters;
- current chapter + selected following units as supported.

Reuse existing `enqueueBook(... chapterIds)`.

## 9.6 Manga

Expose:
- chapter selection;
- volume selection when canonical structure supports it.

Use the unified Reader/offline package engine.

## 9.7 Audiobook

Expose:
- whole audiobook;
- chapter/track selection when canonical structure supports it;
- audio quality.

Reuse the binary audio package adapter.

## 9.8 Smart Offline overlap

If selected bytes already exist as `prefetched`:
- reuse them;
- promote to explicit/Keep Offline;
- do not enqueue duplicate bytes.

---

# 10. Phase 5 — shared Download Selection implementation

Goal: implement the approved cross-media modal/sheet once.

## 10.1 Component

Desktop:
- modal.

Mobile:
- full-height/near-full-height sheet.

The same component renders media capability sections.

## 10.2 Default resolution

Defaults come from Offline Settings:
- quality;
- audio package;
- subtitle package;
- image quality;
- Learning inclusion.

Per-download override remains ephemeral input to the package request.

Do not persist one-off overrides as new profile defaults.

## 10.3 Size/admission preview

Before confirmation:
- server/package layer estimates bytes when possible;
- local client applies Jularr limit/browser quota/native reserve;
- already-valid local bytes are subtracted/reused;
- unknown size stays unknown.

## 10.4 Confirmation idempotency

Repeated confirmation must resolve to the same logical local package/job.

No duplicate queue record.

---

# 11. Phase 6 — Downloads & Offline + Quick View projection

Goal: replace fragmented settings/list status with the approved manager while reusing the existing local stores.

## 11.1 One UI query layer

UI reads `OfflineLocalCatalog`.

It does not directly enumerate separate Reading/binary stores in each page.

## 11.2 Manager

Implement:
- Downloads;
- Offline available;
- Video / Reading / Audio filters;
- grouping;
- Pause/Resume/Retry/Remove/Update;
- Smart Offline label/Keep Offline;
- storage summary.

## 11.3 Quick View

Global indicator:
- current-device only;
- active/failed/waiting;
- local-first;
- no separate queue state.

Current `_AppAccountFooter` indicator linking directly to Settings should migrate to the approved global indicator/Quick View entry.

## 11.4 Offline page route

Create a proper consumer `Downloads & Offline` route/surface.

Do not overload `Settings/Offline` as both:
- queue/inventory manager;
- settings/preferences.

Settings and manager cross-link.

---

# 12. Phase 7 — Offline Settings target

Implement target settings incrementally over actual capability.

## Baseline real controls

- network policy;
- background/power where enforceable;
- video/audio/image quality defaults;
- audio/subtitle package defaults;
- Learning data default;
- local Jularr storage limit;
- native safety reserve;
- storage location only where real;
- Smart Offline controls only once #415 implementation exists;
- cleanup/update/sign-out policy;
- sync status;
- notification deep link.

Capability rule:

```text
supported -> interactive
forced -> read-only explanation
unsupported -> hidden
```

Never ship fake native controls on PWA.

---

# 13. Phase 8 — native storage migration

Goal: implement `offline-storage-location` only on capable native clients.

Required transaction:

```text
old verified copy
 -> copy to candidate destination
 -> verify candidate
 -> atomically switch local reference
 -> delete old copy
```

Must survive restart.

Do not count migration as a media download.

If Activity Center #413 later surfaces migration progress, it projects the same operation.

---

# 14. Phase 9 — Notifications integration

Blocked/shared dependency:
- #835–#838.

Offline emits events only.

Initial event mapping:
- explicit package accepted -> action feedback only;
- verified Ready -> completion event according to user preference;
- Failed -> durable event;
- storage attention -> durable event;
- Smart Offline batch -> optional quiet event;
- temporary connection wait -> no durable notification.

Canonical Notification attention policy decides Toast/Push behavior.

The Offline visual reference showing a mobile bottom snackbar must not override canonical Notification placement.

---

# 15. Phase 10 — Smart Offline (#415)

Only after:
- explicit download package contract is stable;
- manager/Quick View exist;
- storage budget/admission is stable;
- local progress sync is stable.

Smart Offline:
- creates the same package/job type with `origin=prefetched`;
- never owns separate cache bytes;
- explicit packages have priority;
- Keep Offline promotes origin;
- eviction removes speculative local copies only;
- next-up selection stays bounded;
- no whole-library mirroring;
- avoid waking sleeping server storage solely for speculation.

---

# 16. Migration from current browser stores

Do not discard existing user downloads unnecessarily.

## 16.1 Schema versioning

Both browser stores need explicit upgrade paths.

For new presentation fields:
- derive from existing manifests/package metadata where possible;
- mark missing optional metadata as unknown;
- refresh descriptors opportunistically when online;
- do not invalidate verified bytes solely because artwork/title metadata is missing.

## 16.2 Reading store

Keep current IndexedDB/OPFS content.

Upgrade manifest metadata in place.

## 16.3 Binary media store

Keep chunks/packages when:
- canonical target;
- ETag/size/hash;
- verification identity

still match.

Do not re-download just to migrate UI state.

## 16.4 Owner bootstrap migration

On first online run after update:
- derive bootstrap from the authenticated `data-profile-id`;
- mark it accessible;
- associate existing same-profile stores.

Signed-out state must not auto-adopt arbitrary existing profile databases.

---

# 17. Security and privacy gates

Required tests:

- cold-start with valid accessible owner shows only that owner;
- explicit logout locks/removes before offline shell can reopen bytes;
- profile switch never flashes previous profile titles/art;
- server-origin switch does not reuse old packages;
- no profile id/owner bootstrap -> no private local catalog;
- stale service worker cannot read a different owner's DB;
- local search cannot enumerate locked profiles;
- OS notification/deep link revalidates owner context before Retry/Open.

No DRM bypass.

No portable file-sharing UI.

---

# 18. Failure/recovery matrix

| Condition | Owner | User surface |
| --- | --- | --- |
| Network lost mid-transfer | local downloader | Waiting for connection |
| Wi-Fi policy blocks transfer | local downloader | Waiting for Wi-Fi |
| Browser quota insufficient | admission policy | selection/manager/settings |
| Native free space insufficient | admission policy | selection/manager/settings |
| Local bytes corrupt | verifier/package owner | manager + Player/Reader recovery |
| Storage location removed | native storage owner | manager/settings/Player/Reader |
| Server unavailable but package Ready | local package | normal Player/Reader |
| Next episode/chapter absent | Player/Reader | focused unavailable-next state |
| Sync offline | progress/annotation queue | subtle pending state |
| Owner locked | account policy | content hidden/locked |
| Package stale | package update owner | Update available |
| App/process restart | local package/queue | reconstruct from durable state |

No condition above gets a second page-local state machine.

---

# 19. Test plan

## 19.1 Web unit/engine tests

Extend:
- `OfflineLibraryEngineTests`;
- `OfflineLibraryRepositoryEngineTests`;
- Offline media engine/storage decision tests;
- package request/options tests;
- local catalog projection tests;
- owner bootstrap decision tests.

## 19.2 Web integration tests

Extend/add:
- `OfflineLibraryReaderIntegrationTests`;
- service-worker cold navigation behavior;
- local manifest Contents;
- local deep-link Reader bootstrap;
- local media Player bootstrap;
- profile isolation;
- logout lock/remove semantics;
- reconnection sync;
- detail-action terminology and capability rendering;
- Download Selection idempotency.

## 19.3 Browser E2E

At minimum:

1. sign in;
2. download Episode + Book chapters;
3. verify Ready;
4. kill tab/browser context;
5. make server/network unavailable;
6. relaunch PWA from cold state;
7. Home shows only verified local items;
8. open local episode;
9. progress locally;
10. open Reader;
11. navigate downloaded chapters;
12. attempt missing next chapter;
13. reconnect;
14. state synchronizes without moving progress backward.

Repeat with a second Profile to prove isolation.

## 19.4 Android unit tests

Extend existing:
- `OfflineAccountPolicyTest`;
- `DownloadStateMachineTest`;
- `OfflineStoragePolicyTest`;
- `OfflineMediaVerifierTest`;
- `OfflineProgressQueueTest`;
- `LibraryManifestDiffTest`;
- `LibraryRequestInterceptionTest`;
- `LibrarySyncQueueTest`.

Add projection/owner bootstrap adapter tests if needed.

## 19.5 Android instrumentation/E2E

- download content;
- process death;
- airplane mode;
- app cold launch;
- local Home/Downloads;
- local playback;
- local Reader;
- profile switch;
- storage removal if external storage path supported;
- reconnect/sync.

---

# 20. Rollout / compatibility order

Recommended implementation PR sequence:

### PR A — contracts + projection
- package request/options contract;
- local catalog read model;
- owner bootstrap;
- tests only + no major UX switch.

### PR B — PWA cold-start Home
- service-worker fallback bootstrap;
- local Home projection;
- local Downloads entry;
- owner isolation.

### PR C — Unified Reader local repository
- canonical ReaderDocumentRepository;
- PWA local API responder;
- local Contents;
- exact progress/bookmark wiring;
- Android Reader bridge;
- Manga adapter.

### PR D — local Player cold-start
- PWA local player bootstrap;
- native entry alignment;
- unavailable-next Offline behavior.

### PR E — shared detail Offline action + selection
- #840;
- Anime/TV/Movie/Reading/Manga/Audiobook;
- per-download package request.

### PR F — Downloads & Offline manager + Quick View
- local catalog UI;
- grouping/actions;
- global indicator.

### PR G — Offline Settings target + cleanup/update policy
- capability-driven settings;
- sign-out policy;
- sync status.

### PR H — storage location migration
- native only where supported.

### PR I — canonical Notification routing
- integrate only against #835–#838 final runtime contracts.

### PR J — Smart Offline
- #415;
- only after explicit Offline foundation proves stable.

Do not combine PR A–J into one large implementation PR.

---

# 21. Dependency gates

## Reader gate

Do not land a new Offline Reader renderer that would compete with #289/#819.

Cold-start Reader must use the current Unified Reader document/layout contracts.

## Progress gate

Do not use legacy per-media progress as the permanent Offline sync owner.

Use #662/canonical progress.

## Notification gate

Do not build an Offline-specific Toast/Bell runtime while #835–#838 are establishing the canonical one.

## Smart Offline gate

Do not implement speculative prefetch before explicit package selection, local catalog and storage admission are stable.

---

# 22. Acceptance for #839

#839 is complete only when:

- PWA cold-start with server unreachable renders the normal disconnected Jularr shell;
- local owner bootstrap fails closed;
- verified local content is discoverable from a fresh launch;
- local Reader deep links work without a prior online Reader page;
- Reader Contents comes from the local manifest;
- Reader progress/bookmarks use the local-first canonical sync path;
- Manga reuses the same Reader package/repository model;
- Android Reader uses its local queue/interception path;
- local Player/Reader can open verified packages without a server bootstrap;
- reconnect revalidates ownership and syncs without regressing progress;
- no second Reader/Player/progress identity exists.

---

# 23. Acceptance for #840

#840 is complete only when:

- Anime/TV, Movie, Book/LN, Manga and Audiobook detail surfaces expose one consistent device-local Offline action when capable;
- server acquisition and device-local copy are visibly different concepts;
- all selection-capable media use the shared Download Selection component;
- Book/LN supports selected chapters, not only whole Work;
- quality/audio/subtitle/image/Learning options come from the canonical package request/options contract;
- existing Ready bytes are reused;
- Smart Offline overlap promotes/reuses rather than duplicates;
- action state is derived from the same local owner used by manager/Quick View;
- no page creates a separate Offline queue.

---

# 24. Definition of target completion

The Offline target is complete when a user can:

1. choose exactly what to take offline;
2. understand size/quality/tracks before starting;
3. see live current-device progress anywhere;
4. manage all Offline media in one consumer surface;
5. restart the app/browser without connectivity;
6. immediately see and open verified local media;
7. watch/read/listen using the normal Player/Reader;
8. continue progress/bookmarks locally;
9. reconnect and synchronize safely;
10. change native storage safely where supported;
11. opt into bounded Smart Offline later without creating a second subsystem.

Architecture completion means the same behavior is achieved without:
- duplicate canonical media identity;
- duplicate progress stores;
- duplicate download queues;
- duplicate notification systems;
- per-media Offline applications;
- server acquisition concepts leaking into device-download UX.
