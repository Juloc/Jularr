# Offline Phase A — Canonical Contracts, Local Catalog and Owner Bootstrap

Status: **binding implementation plan for Phase A of the approved Offline target**.

Branch: `planning/offline-ux-20261004`.

Parent implementation pack:
- `docs/implementation/offline-target-completion.md`

Primary backlog:
- #839 — true Offline cold-start + unified local Reader repository;
- #840 — device-local Offline actions on consumer detail surfaces;
- #851 — Games first-class Offline/install projection through a Games-owned local package owner;
- #861 — package lifecycle hardening: atomic replacement, capacity accounting and resource reuse;
- #862 — future Games offline save reconciliation for divergent multi-device changes.

This document is intentionally code-structure-specific. It applies the current Jularr architecture and maintainability rules before implementation starts.

---

## 1. Startup gate result

### Canonical owners already present

Do not invent replacements for these:

| Responsibility | Existing canonical owner |
| --- | --- |
| media identity | `MediaCore` — Work / WorkEpisode / WorkVolume / WorkChapter / WorkEdition / WorkVersion |
| server library bytes | `Library` — MediaAsset / StoredFile / tracks |
| video playback target | `PlaybackVideoTarget` / canonical Playback services |
| Reader rendering capability | `ReaderCore` — ReaderDocument / ReaderLayoutKind / ReaderCapabilities |
| canonical progress | `Progress` — MediaProgress / VideoProgress / Reader progress migration target |
| structured reading Offline package | `Features/OfflineLibrary` + `ClientApiOfflineLibrary*` |
| binary media Offline package | `ClientApiOfflineMediaPackageService` + PWA/native local stores |
| Smart Offline planning | `OfflinePrefetchService` / `OfflinePrefetchPlanner` |
| device-local bytes/queue | each client; never the server |
| notifications | canonical Notifications subsystem, not Offline |

### Existing code patterns to preserve

Server:
- thin `ClientApi*Endpoints`;
- typed record DTOs at the Client API boundary;
- application services own queries/policy;
- cancellation token propagated through database/file/probe work;
- Razor Pages are presentation boundaries, not domain owners.

PWA:
- small IIFE modules under `wwwroot/js`;
- pure decision functions separated from IndexedDB/OPFS/network adapters;
- private package bytes stay out of CacheStorage;
- storage is profile scoped.

Android:
- wire models in `core-model`;
- HTTP/parsing in `core-api`;
- device-local state under `app-mobile/.../offline`;
- WorkManager for durable background work;
- pure policies/state machines are tested independently.

### Directly related problems found

Phase A must fix or explicitly migrate these rather than layering more state on top:

1. PWA Reading and binary Offline stores have separate active-account/settings state.
2. Android `OfflineStore` and `LibraryStore` each persist their own `OfflineAccount`, so the same security boundary has two durable copies.
3. PWA binary storage remembers only an active profile id; the structured Reading store relies on `body[data-profile-id]`. Neither is sufficient for a disconnected cold start.
4. PWA logout currently clears both binary and text Offline stores from `offline-media-manager.js`, while the target product supports either **keep locked** or **remove**.
5. `ClientApiOfflineMediaPackageService.GetManifestAsync` can create a portable rendition. A manifest/options read can therefore hide expensive mutation/transcoding.
6. current TV package resolution is folder-based and can effectively package a whole series instead of a canonical episode selection.
7. current Reading Offline manifests are keyed by legacy `NovelWork`/`NovelChapter` identity while the target architecture requires canonical Work/WorkChapter.
8. current Manga Offline media is a page-image package outside the structured Reader manifest path; target Reader architecture must converge these without creating a second Manga Reader.
9. current `Settings/Offline` server model enumerates server media. The target Downloads & Offline manager must instead be a projection of **this device's local state**.
10. server acquisition states and device-local Offline states currently overlap in vocabulary such as `Downloading`.

These are root-cause constraints for the implementation, not reasons to add compatibility state forever.

---

# 2. Phase A boundaries

Phase A establishes four things:

1. **canonical Offline package target/request/options contract**;
2. **one client-local owner bootstrap/security boundary**;
3. **one read-only OfflineLocalCatalog projection per client**;
4. **migration/guards/tests** proving the above.

Phase A does **not** yet implement:
- the final Downloads & Offline screen;
- final Download Selection UI;
- Home cold-start shell;
- full Reader cold-start interception;
- storage-location migration;
- Notification runtime changes;
- Smart Offline expansion.

Those later phases consume the contracts established here.

---

# 3. Architectural decision: no new server Offline database/domain root

Do **not** add:
- `OfflineWork`;
- `OfflineEpisode`;
- `OfflineChapter`;
- `OfflinePackage` EF entity;
- a server table describing device downloads;
- a server-side queue mirroring client state.

The server knows:
- canonical media;
- available server library resources;
- what package **could** be prepared.

The client knows:
- what this device actually requested;
- local transfer state;
- local verified bytes;
- local storage location;
- explicit vs prefetched origin.

This separation is mandatory.

---

# 4. Canonical package target

New Offline selection APIs must target canonical identities rather than adding more legacy route semantics.

Use one wire target shape conceptually equivalent to:

```text
ClientOfflinePackageTarget
  WorkId              required
  WorkEpisodeId       optional
  WorkChapterId       optional
  EditionId           optional
  Intent              watch | read | listen
```

Rules:

- `WorkId` is always canonical MediaCore Work.
- at most one structural-unit field is present;
- Movie watch: Work only;
- Anime/TV watch: Work + WorkEpisode;
- Reading current chapter: Work + WorkChapter;
- whole Reading work: Work only;
- Audiobook listen: Work + audiobook Edition when canonical edition resolution exists;
- no legacy Anime/Episode/NovelWork/MangaSeries id enters the new target contract.

Do not use one untyped `UnitId` without a unit kind. Explicit fields are safer and clearer than guessing which table a Guid belongs to.

### Compatibility adapters

Existing first-party routes may remain temporarily while callers migrate:

- `/episodes/{legacyEpisodeId}/offline-download`;
- `/offline-media/{kind}/{id}/...`;
- `/offline-library/works/{legacyNovelWorkId}/...`.

They are compatibility adapters only.

Deletion gate:
- all first-party PWA/Android callers use canonical target requests;
- Offline local records persist canonical Work/structure ids;
- tests prove no new target is created from legacy ids in UI code.

Do not build new features on the legacy routes.

---

# 5. Package intent is not media identity

The target needs a small stable consumption-intent vocabulary:

- `watch`
- `read`
- `listen`

This exists because one canonical Book Work can have:
- a readable Edition;
- an audiobook Edition.

It must not be represented by a second Work type.

The package resolver derives the actual delivery strategy from canonical content capabilities.

---

# 6. Reader package delivery strategies

Do not branch simply by file extension or old media page.

Use current `ReaderDocumentDescriptor`, `ReaderLayoutKind` and Reader capabilities.

The target strategies are:

| Reader shape | Local package strategy |
| --- | --- |
| reflowable text / LN / web novel | structured manifest + selected chapters + assets |
| image sequence Manga | structure manifest + selected chapter/page resources |
| fixed pages / PDF / scans | managed document/page package |
| EPUB opened as reflowable document | managed document or normalized structured package according to canonical Reader adapter |
| comic archive | managed document/image package according to Reader layout adapter |
| magazine/artbook | fixed/image package |
| PDF Smart Book | source document plus only the canonical derived translation/text artifacts selected for Offline use |

Important:
- strategy is a transport/storage choice, not a new Reader;
- Reader rendering still uses canonical ReaderDocument;
- Manga must not keep an independent Offline Reader state model;
- chapter-level selection is exposed only when canonical structure can represent it safely.

---

# 7. Media capability matrix

Phase A contracts must be able to represent all target media without promising unsupported choices.

| Media | Scope capability | Quality | Track/package capability |
| --- | --- | --- | --- |
| Anime episode | episode / selected episodes / season | video | audio + subtitle + Learning |
| TV episode | episode / selected episodes / season | video | audio + subtitle |
| Movie | single Work | video | audio + subtitle |
| Book/LN/Web novel | work / volume / selected chapters | text/image | Edition/language + Learning when available |
| Manga | chapter / volume / selected chapters | image | image quality |
| PDF/scan/comic/magazine | document; page/chapter only if modeled | document/image | Edition/derived translation if available |
| Audiobook | whole book; chapter/track only when canonical structure exists | audio | selected audio package |
| Games | separate Games-owned install/package contract (#851) | game/release capability | projects into the shared OfflineLocalCatalog; never forced into MediaCore Work/MediaProgress |

A capability not supported by the canonical source is omitted. The UI must not render a disabled fake feature just for parity.

---

# 8. Shared Client API selection contract

Create:

`src/Jularr.Web/Features/ClientApi/ClientApiOfflinePackageContracts.cs`

This file owns **wire shapes and stable wire keys only**.

It must not query EF, inspect files or decide package policy.

Recommended records:

```text
ClientOfflinePackageTarget
ClientOfflinePackageOptions
ClientOfflineScopeOption
ClientOfflineUnitOption
ClientOfflineQualityOption
ClientOfflineTrackOption
ClientOfflineEditionOption
ClientOfflinePackageRequest
ClientOfflinePackagePreview
ClientOfflinePackageEstimate
```

Use stable strings for cross-platform wire values, centralized through one contract helper following the existing `ClientApiOfflinePrefetchContract` pattern.

Do not serialize C# enum ordinals as public wire values.

## 8.1 Options response

Options are **capabilities**, not user settings.

Return enough to render Download Selection:

- canonical target;
- media/intent;
- available units and groups;
- allowed scope presets;
- available Editions/languages;
- quality choices actually supportable;
- audio tracks;
- subtitle tracks;
- Learning-data availability;
- image-quality choices;
- current server-side source availability;
- whether estimate is possible.

Do not return:
- server filesystem paths;
- FFmpeg arguments;
- NAS path;
- downloader/indexer details;
- provider secrets.

## 8.2 Package request

The request represents one explicit selected result.

Recommended fields:

```text
Target
SelectedUnitIds
EditionId
QualityKey
ImageQualityKey
AudioTrackIds
SubtitleTrackIds
IncludeLearning
```

Rules:
- ids must have been validated against the canonical target;
- no arbitrary path;
- no arbitrary codec/container;
- no arbitrary bitrate supplied by the client;
- quality is a stable product key resolved by the server;
- missing fields mean canonical/default choice, not "guess a random source".

## 8.3 Exact track selection

The server options response may mark:
- default;
- preferred;
- forced/SDH;
- original-language status.

The client applies Offline Settings defaults and submits **exact selected track ids**.

This avoids duplicating language preference policy inside every package service.

The server still validates that the submitted ids belong to the selected canonical source.

## 8.4 Estimate

Use:

```text
ClientOfflinePackageEstimate
  Status = known | approximate | unknown
  Bytes?
```

Never fabricate precision.

For direct original resources, exact size can be known.

For a portable rendition that does not yet exist:
- return approximate only if the existing probe/quality model can justify it;
- otherwise return unknown.

Do not transcode just to calculate an estimate.

---

# 9. Read vs command separation for portable renditions

This is required by Jularr maintainability rules.

Current `OfflinePortableRenditionService.GetAsync` mixes:
- capability inspection;
- cache lookup;
- expensive rendition creation.

Target split:

```text
Inspect/Describe
  -> read-only
  -> determines direct-original vs rendition-needed
  -> may probe
  -> never creates/deletes a rendition

Prepare
  -> command
  -> called only after a confirmed package request
  -> creates/reuses deterministic portable rendition
```

Do not add a forwarding wrapper around the old method.

Refactor the service so each public method has real distinct behavior.

After all callers migrate:
- remove/rename the old ambiguous `GetAsync`;
- manifest/options GET paths must not trigger a transcode.

The content endpoint must serve a resource already resolved/prepared by the confirmed package flow. It must not unexpectedly start FFmpeg on a media GET.

---

# 10. Server code placement

Phase A server changes should stay inside established owners.

## 10.1 Client API

Files:

- **new** `ClientApiOfflinePackageContracts.cs`
- **new** `ClientApiOfflinePackageEndpoints.cs` only if a canonical cross-media route is introduced
- evolve `ClientApiOfflineMediaPackageService.cs`
- evolve `ClientApiOfflineLibraryService.cs`
- evolve `OfflinePortableRenditionService.cs`
- `Program.cs` only for required endpoint/service registration

Do not add:
- provider/factory abstraction;
- generic repository;
- new persistence service;
- `OfflinePackageManager` that duplicates the existing package services.

## 10.2 Canonical target resolution

Video:
- reuse the final canonical video target resolver from the Playback/MediaCore work;
- do not add a second episode mapping service in Offline.

Reading:
- resolve through canonical Work/WorkChapter/Reader identity as #289 migration permits;
- while legacy `NovelWork` remains the storage source, the adapter resolves from canonical Work to the legacy source behind the existing OfflineLibrary service;
- do not leak that legacy id back into the new wire target.

Audiobook:
- use canonical Book Work + audiobook Edition/Version;
- until canonical audiobook chapter structure exists, expose whole-audiobook scope only.

## 10.3 Endpoint behavior

Preferred target API shape:

```text
POST /api/client/v1/offline/packages/options
POST /api/client/v1/offline/packages/preview
POST /api/client/v1/offline/packages/prepare
```

Why POST:
- target/options requests can contain structured target/capability input;
- preview is read-only but has a structured body;
- prepare is explicitly a command and may materialize a rendition.

If implementation finds that `options` needs only a small route/query target, GET is acceptable, but it must remain read-only.

`prepare` returns a delivery descriptor identifying which existing content endpoints/resources the client must download.

Do **not** make a new byte-stream subsystem.

Existing:
- structured chapter/assets endpoints;
- binary media resource endpoints

remain the byte delivery paths until the unified Reader repository phase changes them.

---

# 11. Package prepare descriptor

A prepare response must identify a deterministic local package.

It should include:

- package contract/schema version;
- canonical target;
- selected unit ids;
- selected Edition;
- selected track ids;
- resolved quality;
- resource ids;
- resource kind;
- size;
- content URL;
- ETag/version/hash;
- local presentation metadata;
- optional artwork resource;
- Reader delivery strategy when relevant.

The package id must be deterministic for the **logical selected package identity**, not random per click.

Repeated identical confirmation:
- reuses/resumes the same local package;
- does not create a duplicate queue entry.

A materially different selection can create/update a different package revision, but the client must still reconcile overlap rather than duplicate identical resources.

The contract must leave room for safe local generations/resources used by #861:
- a logical package identity is stable across updates;
- a candidate generation/revision is not Ready until fully verified;
- resource identity includes enough version/hash information for deterministic reuse;
- the old Ready generation stays addressable until the client atomically commits the candidate.

A1 does not need to implement full local reference counting/garbage collection, but it must not define a wire shape that makes atomic replacement or known-resource reuse impossible later.

---

# 12. Presentation metadata required for cold start

A device package must contain enough safe metadata to render without server access.

At minimum:

- canonical Work id;
- structural unit id(s);
- title;
- unit label/title;
- consumer category: Video / Reading / Audio / Games; Games uses a typed Games-owned target reference rather than a MediaCore Work target;
- media type/content type;
- selected Edition/language;
- duration or chapter/page counts where useful;
- local open-target descriptor;
- optional local artwork resource id;
- package origin stored by the client;
- created/updated/ready timestamps.

Artwork:
- if the package includes artwork, store it as a normal verified local resource;
- do not call a remote cover URL "offline artwork";
- if artwork was not packaged, cold-start UI uses a clean placeholder.

Do not persist:
- host paths;
- auth cookies/tokens;
- provider credentials;
- admin acquisition data.

---

# 13. One client-local owner authority

## 13.1 Product rule

All device-local Offline stores on one client must consult **one active owner authority**.

Owner identity:

```text
server origin + profile id
```

This matches current Android `OfflineOwner.key`.

No offline timeout/license expiry is invented. Jularr is not adding DRM.

States:

- `accessible` — last valid authenticated owner may use its local packages;
- `locked` — bytes may remain, but catalog/content is inaccessible;
- no owner — fail closed.

## 13.2 Explicit logout

Logout behavior is controlled by the device Offline retention setting:

- **Keep downloads locked**:
  - owner becomes locked before logout completes;
  - transfers stop;
  - no local title/art/progress is exposed while signed out.

- **Remove downloads**:
  - cancel work;
  - clear that owner's local packages/sync queues;
  - then clear/lock owner;
  - logout continues only after local cleanup reaches a safe terminal result or a user-visible recovery path exists.

Do not hard-code unconditional deletion in the generic media manager.

## 13.3 Network failure

A network/server failure is not logout.

Never lock/purge merely because:
- request timed out;
- server is sleeping;
- device is offline.

Only a trustworthy authentication result or explicit user action changes access state.

## 13.4 Profile switch

Before showing the new Profile:
- old owner's catalog becomes inaccessible;
- new owner becomes accessible only after authoritative profile activation.

Do not flash the old profile's titles/art during transition.

Retained old-profile bytes may remain physically present according to retention policy, but they are not enumerable from the new owner catalog.

---

# 14. PWA owner bootstrap

Create one environment-neutral browser module, recommended:

`wwwroot/js/offline-owner-storage.js`

It owns a tiny IndexedDB database specifically for the active Offline owner boundary.

Do not put this state in:
- Reading DB;
- binary package DB;
- server HTML;
- a second copy in both stores.

Suggested record:

```text
schemaVersion
origin
profileId
state
lastVerifiedAtUtc
```

The current active owner key is derived from origin + profile id.

Security:
- no secret/token/password;
- no display metadata required;
- no list of other owners;
- database is same-origin browser data;
- a locked/no-owner record never grants access.

### Why IndexedDB instead of only localStorage

The owner record is part of Offline correctness and must be:
- versioned;
- atomic;
- available to future worker-side local routing if required;
- independent of DOM/server-rendered markup.

Do not use localStorage as the new canonical owner source.

The existing media `ACTIVE_PROFILE_KEY` becomes migration input only and is removed after migration.

---

# 15. PWA owner migration

First authenticated run after upgrade:

1. current server-rendered authenticated Profile is authoritative;
2. write `accessible` owner bootstrap;
3. existing Reading/media stores for that profile remain in place;
4. no byte migration is required merely to add owner bootstrap.

Cold start:
- bootstrap record supplies profile id;
- open only that profile's existing databases;
- never scan database names looking for candidates.

Logout:
- shared owner/retention flow handles both stores;
- remove current media-manager-specific logout cleanup hook after migration.

If old `ACTIVE_PROFILE_KEY` disagrees with the authenticated page:
- authenticated page wins;
- do not merge old profile data into the new profile.

If cold-start has no new owner record:
- do not trust the legacy active-profile key by itself;
- show safe disconnected/auth state;
- require one successful authenticated run to adopt legacy packages.

This is deliberately fail-closed.

---

# 16. Android owner consolidation

Current problem:
- `OfflineSnapshot.account`;
- `LibrarySnapshot.account`;

are two durable copies of the same security fact.

Phase A introduces one app-private owner store, recommended:

`clients/android/app-mobile/src/main/kotlin/de/juloc/jularr/mobile/offline/OfflineOwnerStore.kt`

Keep:
- `OfflineAccount`;
- `OfflineOwner`;
- `OfflineAccountPolicy`;

as the shared model/pure policy.

Both:
- `OfflineDownloads`;
- `LibraryDownloads`;

read/apply account decisions through the same owner store.

Do not add a second policy.

### Android migration

On first upgraded load:

- if both old snapshots have the same account -> adopt it;
- if exactly one has an account -> adopt it;
- if both are empty -> no owner;
- if they disagree -> fail closed and require online revalidation; do not guess.

After migration:
- old snapshot account fields are decode-only compatibility fields for the supported upgrade window;
- new writes do not persist two accounts;
- remove the legacy fields after the upgrade window.

Do not delete media bytes solely because the two old metadata files disagree.

### Android settings note

`OfflineSettings.wifiOnly` and `LibrarySettings.wifiOnly` are also duplicate device configuration.

Do not create a third copy in Phase A.

The later Offline Settings implementation must move shared network/storage defaults to one client-local settings owner.

Until then, Phase A code must not add more shared settings into either media-specific snapshot.

---

# 17. OfflineLocalCatalog

The catalog is a **read-only projection**, not a store.

### Games adapter boundary (#851)

Games is first-class in the consumer Offline projection but remains a separate canonical domain.

- media items use canonical Work/structure targets;
- Games items use canonical Game/GameRelease targets;
- the Games module owns local install/package bytes, runtime capability and save state;
- the shared catalog only projects Games state into Home, Downloads & Offline and Quick View where appropriate;
- Games never uses MediaProgress merely to fit the shared catalog;
- predictive Smart Offline does not include Games by default.


Required consumers:
- disconnected Home;
- Downloads & Offline;
- Quick View/global indicator;
- local detail-action state;
- Smart Offline inventory report;
- local Search.

It aggregates current physical stores.

## 17.1 Stable projection model

Conceptually:

```text
OfflineLocalItem
  PackageId
  Target
  Category
  Title
  UnitLabel
  Artwork
  State
  Origin
  BytesDownloaded
  BytesTotal
  Ready
  UpdateAvailable
  CreatedAt
  UpdatedAt
  ReadyAt
  OpenTarget
  ProgressSummary
  Failure
```

Category:
- `video`
- `reading`
- `audio`
- `games`

Origin:
- `explicit`
- `prefetched`

No separate "pinned" truth is required if **Keep Offline** promotes a prefetched item to explicit.

## 17.2 Normalized local state

Target projection vocabulary:

- queued
- preparing
- downloading
- paused
- waiting-for-connection
- waiting-for-wifi
- verifying
- ready
- failed
- update-available
- storage-unavailable

`needs-attention` is a presentation/severity derived from concrete states, not another durable transfer state.

Adapters map existing states to this vocabulary.

Do not rewrite every existing downloader state machine in Phase A solely for naming parity.

## 17.3 PWA implementation

Recommended new file:

`wwwroot/js/offline-local-catalog.js`

Responsibilities:
- obtain current accessible owner from `offline-owner-storage.js`;
- read structured Reading records through `JularrOfflineLibraryStorage.openStoreForProfile(profileId)`;
- read binary packages through `JularrOfflineMediaStorage`;
- merge queued progress where needed for resume display;
- normalize state/presentation;
- return immutable snapshots/arrays to UI callers.

It must not:
- enqueue;
- pause;
- remove;
- mutate progress;
- decide Smart Offline eviction;
- persist a third copy of package state.

Use explicit owner/profile arguments internally. Do not read `document.body.dataset.profileId` in catalog logic.

## 17.4 Android implementation

Recommended local-only files:

- `offline/OfflineCatalogModels.kt`
- `offline/OfflineLocalCatalog.kt`

Do **not** put local catalog models into `core-model`; `core-model` is for server/client wire contracts.

The catalog reads:
- `OfflineStore` media snapshot;
- `LibraryStore` reading snapshot;
- shared `OfflineOwnerStore`.

It returns one projection.

It owns no files and no WorkManager jobs.

---

# 18. Local package origin migration

Current Smart Offline contract already has:
- explicit;
- prefetched.

Persist that origin in the actual local package record, not only in plan inventory sent to the server.

Migration:
- every existing user-created package becomes `explicit`;
- no existing package is assumed prefetched merely because #415 exists;
- future Smart Offline creates `prefetched`;
- Keep Offline changes origin to `explicit` atomically in the local owner.

This allows one catalog and one eviction policy.

---

# 19. Canonical-id migration in local records

Do not immediately delete working legacy identifiers needed to open old packages.

Add canonical ids alongside existing migration ids.

Example transitional local record:

```text
canonicalWorkId
canonicalWorkEpisodeId?
canonicalWorkChapterId?
legacyEpisodeId?       // migration/read adapter only
legacyNovelWorkId?     // migration/read adapter only
legacyChapterId?       // migration/read adapter only
```

Rules:
- new packages require canonical ids;
- old package migration resolves canonical ids on the first online refresh;
- once canonical ids are stored, UI/catalog uses them;
- legacy ids remain only as transport/source adapter input;
- remove legacy fields after supported migration window.

Never create a canonical Work solely from Offline client input. Resolution happens server-side through existing MediaCore migration/bridge owners.

---

# 20. Reader identity edge cases

Phase A must account for:

### Reflowable legacy Novel/Book
Current package can continue storing legacy chapter payload ids during migration, but catalog/selection target is canonical Work/WorkChapter once mapping exists.

### Fixed PDF / Smart Book
Do not model every PDF page as a WorkChapter if canonical Reader treats pages as fixed regions. The package target remains Work/Edition/document; local Reader locator owns page position.

### Manga
Canonical WorkChapter is the structural unit. Individual image pages are Reader content/resources, not MediaCore chapters.

### Comic archive without chapter structure
Whole document package until explicit structure exists.

### Translation
Generated translation is a variant/derived Edition/Version according to current Reader/Translation migration direction. Offline does not invent `OfflineTranslation`.

### TTS
TTS capability is not part of package identity unless actual generated/local audio becomes a managed package resource. Device-native TTS requires no package bytes.

---

# 21. Video identity edge cases

### Movie
`WorkId`, no WorkEpisode.

### Anime / TV
`WorkId + WorkEpisodeId`.

The new package resolver must use the same canonical video identity as Player/Progress.

Do not use:
- legacy Episode id as final target;
- TV folder as package identity;
- file path as episode identity.

Multiple physical versions/files:
- server selects a WorkVersion/Asset according to package request/quality;
- package descriptor records the resolved canonical Version/Asset identity;
- local progress continues to target Work/WorkEpisode, so source replacement does not reset progress.

---

# 22. Audio identity edge cases

Audiobook is:
- canonical Book Work;
- audiobook Edition/Version/Asset.

Until a canonical audiobook chapter/track structure exists:
- whole audiobook only;
- local audio resources can still contain multiple files/tracks;
- UI must not pretend track files are canonical chapters.

Once structure exists, selected units can be added without changing package ownership.

---

# 23. Browser quota / native capacity

Package options and preview report **content estimate only**.

Client admission remains client-local.

PWA:
- Jularr configured Offline limit;
- `navigator.storage.estimate()` browser quota;
- persistent-storage status.

Native:
- Jularr limit;
- physical free space;
- safety reserve;
- selected managed storage location.

Do not send physical free-space data to the server merely for package planning.

Do not label PWA quota as device disk free space.

### Device-wide accounting with owner-scoped visibility

Admission must reason about **all Jularr-managed bytes physically retained on the current device/browser**, not only the active profile's visible packages.

This includes retained-but-locked packages of another profile because those bytes still consume the same disk/quota.

Privacy remains owner-scoped:
- active profile sees itemized details only for its own accessible packages;
- when hidden-owner bytes affect capacity, expose only an aggregate reserved/other-profile amount where necessary;
- never reveal another profile's title, artwork, progress, filenames or package identity.

The local capacity owner therefore needs a device-total usage view plus owner-scoped item views. It must not create a server-side device inventory.

Jularr-managed Games packages participate in the same physical capacity accounting through their Games adapter. External launcher installations do not unless the integration authoritatively manages those bytes.

---

# 24. Module gates and authorization

Every server options/preview/prepare request must resolve:

```text
instance module
-> authorization/media capability
-> canonical target existence
-> server library/resource availability
-> requested package choices
```

Do not rely on the button being hidden.

Disabled instance modules return a safe unavailable/not-found contract consistent with current Client API behavior.

No package endpoint bypasses the normal server authorization boundary.

---

# 25. Error model

Use stable client error codes with concise messages.

Required classes include:

- invalid_offline_target
- offline_target_not_found
- offline_not_supported
- offline_source_unavailable
- invalid_offline_scope
- invalid_offline_quality
- invalid_offline_track
- offline_preparation_failed

Do not expose:
- exceptions;
- source path;
- FFmpeg command;
- NAS details.

PWA/native map codes to localized product copy.

---

# 26. No N+1 package option query

Options for a Work containing many episodes/chapters must be built with bounded/batched queries.

Do not:
- query one WorkEpisode at a time;
- query one subtitle track per episode in a loop;
- call provider APIs to populate Download Selection.

Use local canonical/library data.

Preview/prepare may resolve only selected units after options.

---

# 27. Cancellation and timeouts

Server:
- propagate request cancellation through DB/probe/file work;
- any FFmpeg preparation keeps an explicit real timeout;
- do not replace cancellation with `CancellationToken.None` unless work intentionally outlives the request and has an owning background operation.

Current `OfflinePortableRenditionService.CreateAsync` uses `CancellationToken.None` for the process after an in-flight task is created.

When Phase A refactors preparation, make lifetime ownership explicit:
- either request-owned and cancellable;
- or background-operation-owned with a durable/reusable operation contract.

Do not leave an orphaned "fire until six hours" task with no owner merely because the HTTP caller disconnected.

---

# 28. JavaScript structure/conventions

Do not add a framework.

Keep lightweight modules.

New modules need real ownership:

- `offline-owner-storage.js` — one owner security/bootstrap store;
- `offline-local-catalog.js` — read-only aggregation projection.

Do not create:
- `offline-utils.js`;
- `offline-helper.js`;
- multiple tiny wrappers;
- a second event bus.

Existing `CustomEvent` notifications from managers may continue as invalidation signals.

Catalog consumers re-read canonical local stores rather than receiving duplicated full state in events.

Errors:
- catch expected unavailable/capability failures where UI has a fallback;
- do not blanket-swallow storage corruption or programming errors.

---

# 29. Kotlin structure/conventions

Keep:
- wire DTOs in `core-model`;
- HTTP parse/route code in `core-api`;
- local-only models/stores in `app-mobile`.

New server wire DTOs belong in:
- `core-model/.../OfflinePackageModels.kt` or a cohesive extension of current Offline models.

New HTTP methods belong in:
- `core-api/.../OfflineClientApi.kt` / shared implementation, not in Activity/UI.

New local projection/owner store belongs in:
- `app-mobile/.../offline`.

Do not create:
- one repository interface per data class;
- factories around singleton stores;
- forwarding use-case classes whose only behavior is calling the store.

---

# 30. C# conventions for implementation

All new/touched C# follows current central and Jularr rules:

- PascalCase/camelCase;
- four spaces;
- Allman braces;
- calls/signatures/conditions stay on one physical line when readable and <= 230 chars;
- no line over 280 chars;
- cancellation propagated;
- XML docs only where reusable/public contract meaning is non-obvious;
- comments explain invariants/trade-offs, not obvious syntax;
- no tiny forwarding helpers;
- no broad catch fallback;
- no hidden write in validation/options methods;
- no direct Razor-page EF queries for package policy;
- no new dependency without a current need.

Any deterministic architecture rule introduced here should receive a source/architecture test where practical.

---

# 31. Exact Phase A file plan

## Server — new

### `src/Jularr.Web/Features/ClientApi/ClientApiOfflinePackageContracts.cs`

Own:
- canonical target DTO;
- options/request/preview/prepare DTOs;
- stable wire-key parsing/naming;
- validation constants only.

### `src/Jularr.Web/Features/ClientApi/ClientApiOfflinePackageEndpoints.cs`

Only if the implementation uses the canonical cross-media endpoint family.

Own:
- HTTP boundary;
- auth;
- model validation;
- mapping application outcomes to HTTP/error codes.

No EF queries.

## Server — evolve

### `ClientApiOfflineMediaPackageService.cs`

- resolve canonical video/audio/document targets;
- options/preview/prepare for binary resources;
- no folder-as-TV-target final semantics;
- no mutating options read.

### `ClientApiOfflineLibraryService.cs`

- options/preview/prepare for structured Reader packages;
- canonical target adapter;
- selected-unit validation;
- retains existing chapter/assets/sync responsibilities during migration.

### `OfflinePortableRenditionService.cs`

- separate read-only inspection from command preparation;
- explicit cancellation/lifetime ownership.

### `ClientApiOfflineMediaPackageEndpoints.cs`

- existing routes become adapters or are migrated to the new canonical endpoint;
- no new product feature may depend on legacy kind/id routes.

### `ClientApiOfflineLibraryEndpoints.cs`

- same compatibility rule.

### `Program.cs`

Only add registrations/maps actually required by the chosen canonical endpoint implementation.

Do not create a registration abstraction for one service.

## PWA — new

### `wwwroot/js/offline-owner-storage.js`

Canonical browser owner bootstrap.

### `wwwroot/js/offline-local-catalog.js`

Read-only local projection.

## PWA — evolve

### `offline-media-storage.js`

- expose explicit-profile reads needed by catalog;
- migrate/remove legacy active-profile key after owner bootstrap adoption;
- add origin/canonical target/presentation fields to package schema.

### `offline-library-storage.js`

- continue explicit-profile opening;
- package metadata migration;
- no separate active owner.

### `offline-media-manager.js`

- stop owning logout/account cleanup;
- package origin/canonical ids;
- later consumes canonical prepare response.

### `offline-library-manager.js`

- package origin/canonical ids;
- later consumes selected canonical units.

### `service-worker.js`

Phase A only precaches the new safe static modules if required.
Cold-start route logic is Phase B.

### `_Layout.cshtml`

Authenticated page bootstrap writes/revalidates the shared owner state.
Avoid inline business logic; call the owner module.

## Android — new

### `app-mobile/.../offline/OfflineOwnerStore.kt`

One durable owner authority.

### `app-mobile/.../offline/OfflineCatalogModels.kt`

Local-only projection types.

### `app-mobile/.../offline/OfflineLocalCatalog.kt`

Read-only aggregation.

### `core-model/.../OfflinePackageModels.kt`

Only shared wire DTOs introduced by the canonical server package contract.

## Android — evolve

### `OfflineStore.kt`
### `library/LibraryStore.kt`

- account fields become migration/decode compatibility only;
- local media/reading records gain canonical ids, origin and safe presentation fields.

### `OfflineDownloads.kt`
### `library/LibraryDownloads.kt`

- use one owner store/policy;
- no independent account writes.

### `core-api/.../OfflineClientApi.kt`
### `core-api/.../HttpJularrClientApi.kt`

- canonical options/preview/prepare calls;
- typed parse;
- old endpoints remain only for supported compatibility window.

---

# 32. Schema evolution rules

Every local schema change is versioned.

PWA:
- IndexedDB `onupgradeneeded` migration;
- no delete-and-recreate DB as normal upgrade.

Android:
- state codec version;
- backward decode;
- deterministic migration;
- atomic file replace.

Corrupt metadata:
- do not silently adopt another owner;
- do not mark unverified bytes Ready;
- preserve recoverable bytes where possible;
- surface repair/re-download later.

---

# 33. Phase A tests

## 33.1 Server

Add/extend tests for:

- canonical target validation;
- Movie vs WorkEpisode target shape;
- invalid WorkChapter/Work combinations;
- audiobook Work + Edition validation;
- instance-module gate;
- quality key validation;
- audio/subtitle track ownership validation;
- selected units all belong to Work;
- options/preview do not create rendition files;
- prepare is idempotent/reuses deterministic rendition;
- cancellation of preparation;
- no N+1 regression for multi-unit options where practical;
- stable package id for equivalent request;
- legacy adapter resolves to the same canonical package target.

## 33.2 PWA pure/engine tests

Add tests for:

- owner accessible / locked / absent;
- authenticated adoption;
- network failure does not lock owner;
- logout keep vs remove decision input;
- catalog merges Reading + Video + Audio;
- another profile is never enumerated;
- existing package without origin migrates to explicit;
- state normalization;
- local progress overrides stale packaged resume;
- no owner -> empty catalog;
- legacy active-profile key alone cannot unlock data.

## 33.3 Android

Add/extend:

- `OfflineAccountPolicyTest`;
- owner-store migration same/single/mismatch cases;
- both download subsystems use one owner;
- catalog projection over both stores;
- origin default migration to explicit;
- profile/server switch isolation;
- old state codec remains readable;
- mismatched old accounts fail closed without deleting bytes.

## 33.4 Architecture/source guards

Where deterministic, add guards proving:

- new Client API Offline endpoints do not query `AppDbContext` directly if project test patterns support this;
- no new page-local Offline queue/store class is introduced in detail pages;
- no second persistent account field is introduced in new Offline local stores;
- new canonical package target records contain canonical Work identity.

Do not create brittle tests that merely assert private method names.

---

# 34. Phase A implementation slices

Do not implement all of Phase A in one huge commit.

### A1 — wire contract + read-only server options

- canonical package target;
- options/request/preview DTOs;
- canonical validation;
- no preparation yet;
- tests.

### A2 — preparation semantics cleanup

- split portable rendition inspect vs prepare;
- canonical prepare response;
- migrate first-party PWA caller;
- compatibility gate/tests.

### A3 — PWA owner bootstrap

- one owner store;
- authenticated adoption;
- logout policy seam;
- legacy active-profile migration;
- tests.

### A4 — Android owner consolidation

- one owner store;
- migrate two snapshot account copies;
- both download owners consume it;
- tests.

### A5 — OfflineLocalCatalog projections

- PWA catalog;
- Android catalog;
- canonical id/origin/presentation projection;
- tests.

### A6 — local record canonical-id migration

- add canonical Work/structure ids to new packages;
- resolve existing package ids on online refresh;
- no destructive byte redownload;
- tests.

Only after A1–A6 are stable should Phase B cold-start Home consume them.

---

# 35. Decisions deliberately deferred

These must not be guessed in Phase A:

- exact Smart Offline category expansion beyond #415 current candidate support;
- audiobook chapter model before canonical audiobook structure exists;
- whether a specific PDF/EPUB is stored as raw document vs normalized Reader resources — ReaderDocument capability decides;
- exact native Desktop filesystem implementation;
- Activity Center integration;
- final Notification attention policy;
- whether browser persistent storage was granted.

The contract exposes capability/unknown states so these can be added without a second subsystem.

---

# 36. Phase A completion gate

Phase A is complete only when:

- one canonical package target contract exists;
- options/preview are non-mutating;
- preparation is an explicit command;
- current media/reading package owners are reused rather than replaced;
- PWA has one durable Offline owner bootstrap;
- Android has one durable Offline owner authority;
- one read-only local catalog projection exists per client;
- catalog contains Reading, media and Games adapter projections when the client supports them;
- all new packages carry canonical identity + origin;
- old packages migrate without unnecessary re-download;
- package/resource identity supports atomic replacement and known-resource reuse without forcing a destructive update;
- device admission can account for retained hidden-owner bytes without exposing their metadata;
- profile/server isolation fails closed;
- no duplicate queue/progress/notification state is introduced;
- tests cover owner migration, target validation and projection;
- touched code passes the repository maintainability completion gate;
- `dotnet restore Jularr.sln`;
- `dotnet build Jularr.sln --no-restore`;
- `dotnet test Jularr.sln --no-build`;
- Android affected-module tests pass;
- frontend/PWA tests covering the touched Offline engines pass.

Phase B may then implement disconnected Home/cold-start using these owners instead of inventing new state.
