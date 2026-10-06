# Offline Library & Reader Sync

Tracking issue: **#221** (parent context: #69, Android/TV architecture in
[ANDROID_CLIENTS.md](ANDROID_CLIENTS.md); reader model in
[UNIFIED_READER.md](UNIFIED_READER.md)).

This document is the contract for Jularr's offline Book/Novel library: a
canonical versioned package a client downloads, verifies and reads without
network access, plus offline-first reading-state sync. It intentionally
reuses one server model instead of separate PWA/Android business logic
(matching the bounded offline playback design of #225/PR #341).

Status: **part 1 delivered** (server contract, sync endpoint, PWA download
manager foundation, Settings → Offline page). **Part 2 delivered
incrementally, per client:**

- **2A — PWA** (this section): the reader ↔ offline library bridge,
  offline-first Novel and Book progress/bookmark sync, in-page offline
  chapter navigation, the "Save offline" action on the Novel work page, the
  Library "Offline" filter, a compact global download indicator and
  JS-catalog localization of `offline-library-ui.js`.
- **Android**: native download engine (`LibraryStore`/`LibraryDownloads`/
  WorkManager jobs), WebView request interception and native entry points;
  see [Android implementation](#android-implementation).

Both clients' reader-side wiring is real but still narrower than the
issue's full `Reader -> BookRepository` shape for a genuinely cold,
from-scratch offline page load — see the note at the end of the Android
section and [Part 2 TODO](#part-2-todo) below for what remains on each.

## Part 2A: reader repository, offline-first sync, discoverability (PWA)

### Reader ↔ offline library bridge (`offline-library-repository.js`)

`Reader -> BookRepository -> {local source first, server source when
required}` (the issue's target shape) is implemented once, in
`wwwroot/js/offline-library-repository.js`, shared by the Novel reader
(`novel-position.js`/`novel-annotations.js`/`novel-reader.js`) and the Books
reader (`books-reader.js`) — there is no per-reader offline logic.

**Chapter content is served local-first whenever a verified local copy
exists — online or offline, not "only when offline".** A chapter is only
ever marked verified when its hash matches the manifest's *current* server
hash (the same finalization rule `offline-library.js`'s `isBookComplete`
already enforces), so the local copy and what the server would render are
guaranteed identical; the initial server-rendered page therefore never needs
to re-render already-correct content from the local copy. What was actually
missing, and what this slice adds: **in-page offline chapter navigation**.
While the browser is offline, following the previous/next chapter footer
link to a chapter that *has* been downloaded renders it locally (through
`renderNovelBlocksHtml`/`renderBookParagraphsHtml`, the same block/paragraph
→ HTML mapping the server itself uses, so there is exactly one place that
turns chapter content into reader markup) instead of a failing full-page
navigation. Following a link to a chapter that has *not* been downloaded
shows a clear inline notice instead of the browser's own broken/offline
error page. Online, none of this engages: the existing full-page navigation
is unchanged and remains the only online code path.

This is deliberately narrow: only the always-present footer previous/next
links are covered (the chapter drawer's list itself still needs network to
load; see [Part 2 TODO](#part-2-todo)), and a fresh, cold-start navigation to
a Read URL while fully offline (nothing already loaded this session) still
falls back to the generic `offline.html` shell — see below.

### Progress and bookmarks: one canonical, offline-first write path (Novels and Books)

Novel and Book reading progress and bookmark add/remove (rename, Novels only)
now always go through `manager.queueSyncEvent`/`drainSyncQueue`
(`offline-library-repository.js`'s
`forWork(workId).queueProgress`/`queueBookmarkUpsert`/`queueBookmarkRemove`)
— **online and offline alike, not two different code paths.** The local
queue write always succeeds immediately (bookmarks are optimistic: the
bookmark id is client-generated, per the sync contract's explicit support
for client-supplied ids, so the UI updates without waiting on the network),
then an opportunistic `drainSyncQueue()` is attempted whenever
`navigator.onLine` is true. `Pages/Novels/Read.cshtml.cs`'s and
`Pages/Books/Read.cshtml.cs`'s own
`Progress`/`Bookmark`/`RemoveBookmark`/`BookmarkLabel` POST handlers are no
longer called by either reader; they are left in place (harmless, unused by
this client) rather than removed, to avoid widening this slice's
server-side surface for a client-only change. Renaming a Novel bookmark that
belongs to a *different* chapter (from the "other chapters"/search list,
which itself requires network to have loaded) still uses the existing
targeted endpoint, since only the current chapter's bookmarks carry every
field needed to safely rebuild a full offline upsert event.

Highlights are **not** part of the offline sync contract (PR #359 only
covers progress and bookmarks) and intentionally keep using their existing
online-only endpoints unchanged.

Books have an arbitrary `BookLanguageCatalog` target language (default
`"id"`), not the Novel-specific bilingual ja/de model, so
`Features/OfflineLibrary/OfflineLibrarySync.cs`'s reconcilers detect
Book-typed works (`NovelWork.SourceProvider ==
BookCatalogService.ImportedBookProvider`) and, for those, normalize the
anchor language through `BookLanguageCatalog.Normalize` (preserving the
literal `"original"` marker, like `BookReaderAnnotationStore.NormalizeLanguage`
does for highlights) instead of `NovelReadingLanguage.Normalize`'s ja/de rule,
and skip `NovelChapterText`'s ja/de-translation-driven anchor-text
re-resolution entirely — the client-supplied paragraph/offset/anchor-text is
stored as-is. `NovelProgress.AnchorLanguage`/`NovelBookmark.Language` are
already free-form strings, so this needed no schema change (#374).

### Discoverability: Save-offline action, Library filter, download indicator

- The reusable `_OfflineLibraryAction` partial ("Save offline") is now also
  included on the Novel work page (`Pages/Novels/Work.cshtml`), alongside its
  existing placement on the Books library detail page.
- A client-side-only **Library "Offline" filter** (`data-offline-library-filter`
  / `data-library-filter-option`) on `Pages/Books/Index.cshtml` and
  `Pages/Novels/Index.cshtml` shows/hides already-rendered cards using
  `manager.listBooks()`; it never asks the server what is downloaded, since
  that is profile-and-device-local browser state the server never sees.
- A compact **global download indicator** (`data-offline-download-indicator`,
  `Pages/Shared/_AppUserMenu.cshtml` in the header account menu and the phone
  Profile footer, rendered on every authenticated page) shows a small "N downloading" label via `manager.onChange`, hidden
  entirely when nothing is in progress.

### Localization

`offline-library-ui.js`'s previously plain-English strings (state labels,
button text, Settings → Offline copy, the new indicator/filter strings) now
read from the UI catalog (`offlineLibrary.*` keys, `UiTranslationResources.cs`)
through a JSON script element (`#offline-library-text`, rendered once by
`_Layout.cshtml`), the same pattern `pwa.js`'s `shellText`/`#app-shell-text`
already uses for `pwa.*` keys. The four `offline-library-*.js` scripts
themselves also moved from being duplicated on `_OfflineLibraryAction.cshtml`
and `Settings/Offline.cshtml` to one global, authenticated-only inclusion in
`_Layout.cshtml` (needed anyway for the indicator to work on every page).

## Why a separate contract from bounded offline playback (#225)

#225's `/api/client/v1/offline/*` endpoints exist to let the phone play the
*same* self-hosted media file it would stream, verified by a content
fingerprint of one file. Offline library content is structurally different:
one work is a tree of volumes and chapters, each with its own version, that
must be downloaded and updated *differentially* — re-fetching one changed
chapter must never require re-fetching the whole book. Reusing the playback
contract would either bolt a tree/version model onto a single-file descriptor
or duplicate progress reconciliation with different semantics for the same
underlying idea. Instead, `Features/OfflineLibrary/**` and
`ClientApiOfflineLibrary*.cs` mirror #225's *shape* (one additive capability
flag, a read-only descriptor endpoint, a monotonic progress reconciler) while
having their own chapter/volume/hash contract.

## Canonical model

Books and Novels already share one table set (`NovelWork` → `NovelVolume` →
`NovelChapter`, plus `NovelProgress`/`NovelBookmark`/`NovelHighlight`), owned
by `Features/Novels/**`. The offline library adds **no parallel content
tables** — it only adds two sync-related columns on `NovelBookmark`
(`SyncUpdatedAt`, `ClientEventId`) and one tombstone table
(`NovelBookmarkTombstone`). Manifest and chapter payload generation
(`Features/OfflineLibrary/OfflineLibraryQueries.cs`) is a read-only
projection over the existing tables — exactly like `NovelCatalogQueries` and
`NovelChapterText`, which it reuses directly (both are `internal`, and
`internal` is assembly-scoped, not namespace-scoped, so this is intentional
reuse rather than a workaround).

## Server contract (`/api/client/v1/offline-library`, capability `offlineLibrary`)

All endpoints are additive v1, authenticated, and advertised by the new
`offlineLibrary` capability flag (`ClientApiContracts.cs`). Manifest/chapter/
asset reads are shared library content (same authorization as
`/api/client/v1/anime/{id}`); the sync endpoint is profile-scoped.

### `GET /works/{workId}/manifest` → `ClientOfflineLibraryManifest`

```
workId, schemaVersion, contentVersion, title, author, description,
coverAssetUrl, issuedAtUtc,
volumes:  [{ volumeId, number, title, kind, coverAssetUrl }]
chapters: [{ chapterId, volumeId, number, title, hash, hasContent, hasTranslation }]
```

- `schemaVersion` is `OfflineLibraryContract.SchemaVersion` (currently `1`):
  bump it for a breaking wire-shape change; older clients can refuse to
  parse a manifest whose `schemaVersion` they do not understand.
- `contentVersion` (`OfflineLibraryContract.ComputeWorkContentVersion`) is a
  SHA-256 over title/author/cover and every chapter hash **in reading order**.
  It changes when a chapter is added, removed, reordered or edited, or when
  cached title/author/cover metadata changes. A client can skip a manifest
  fetch entirely when it already has this exact version cached (subject to
  its own revalidation policy), and always knows *something* changed without
  inspecting every chapter.
- Each chapter's `hash` (`OfflineLibraryContract.ComputeChapterHash`) is a
  SHA-256 of the chapter's `SourceHash` plus the identity
  (`language:provider:promptVersion:sourceHash`) of every *current* cached
  translation (order-independent). "Current" means the translation's
  `SourceHash` still matches the chapter's — the same rule the reader itself
  uses to pick which cached translation to render
  (`NovelChapterText.LoadAsync`), so the offline package can never disagree
  with what the online reader would show. **Differential download**: a
  client compares this hash per chapter against what it already stored; only
  a changed or new hash needs `GET /chapters/{id}`, and a chapter no longer
  listed is removed locally.

### `GET /chapters/{chapterId}` → `ClientOfflineLibraryChapterPayload`

```
chapterId, workId, volumeId, number, title, hash,
originalText,
blocks:       [{ kind, level, runs: [{ text, ruby, emphasis, strong }], imageAssetUrl, imageAlt }]
translations: [{ targetLanguage, text }]
```

- `blocks` mirrors `NovelContentBlock`/`NovelReaderBlock` (paragraph/heading/
  image with inline runs); an image block's `imageAssetUrl` is one of the
  asset URLs below, never raw bytes inline.
- `translations` includes only the current translation per language (see
  above) — never every historical translation attempt.

### Progressive translation compatibility (#834)

Live provider output is never an offline artifact. The offline library may package
only translated text that has reached #834's validated `BlockCompleted` state
and still matches the current canonical source/execution identity.

The existing `translations: [{ targetLanguage, text }]` payload represents a
complete current chapter translation. Until the offline contract gains explicit
block-level coverage/identity fields, an incomplete progressive run is excluded
from that array rather than being serialized as if the chapter were complete.

If block-level partial translation coverage is added later, the manifest/payload
must make completeness explicit per stable source block, hash that coverage into
the chapter ref and let the Reader fall back to source text for missing blocks.
A client must never infer full translated-edition availability merely because
some translated blocks are cached locally.

- `hash` is repeated here (equal to the manifest's ref for this chapter) so a
  client can immediately detect a version race: if the manifest was fetched,
  then the chapter changed again before the payload request landed, the
  returned `hash` will differ from what the client expected and the client
  should treat the chapter as still pending (fetch again later) rather than
  mark it verified with a mismatched hash.

### `GET /assets/{volumeId}/{asset}` — cover/illustration bytes

Reuses `NovelVolumeAssetStore` (`Features/Novels`) exactly as `/Novels/Asset/`
does. **Path safety**: the store only resolves a content-addressed file name
matching `^[a-f0-9]{32}\.(jpg|png|gif|webp)$`
(`NovelVolumeAssetStore.IsAssetName`/`.Resolve`); anything else — traversal
sequences, encoded traversal, arbitrary extensions, or a name that was never
saved for that volume — resolves to `null` and the endpoint answers `404`.
No host filesystem path is ever part of the URL or the response.

### `POST /sync` — reading-state batch (profile-scoped)

```json
{
  "progress":  [{ "clientEventId", "workId", "chapterId", "positionPermille",
                  "anchorLanguage", "anchorParagraphIndex", "anchorOffset",
                  "clientTimestampUtc" }],
  "bookmarks": [{ "clientEventId", "bookmarkId", "type": "upsert" | "remove",
                  "workId", "chapterId", "language", "positionPermille",
                  "paragraphIndex", "characterOffset", "anchorText",
                  "label", "style", "color", "clientTimestampUtc" }]
}
```

At most `OfflineLibraryContract.MaxSyncBatchItems` (200) items per list per
request. Response mirrors each event back with an `outcome` and, for
progress, the resulting canonical position.

#### Progress: forward-only (mirrors #341's `OfflineProgressReconciler`)

Novel/Book reading position is a `(chapter number, position‰)` tuple instead
of episode milliseconds, but the rule is the same shape as offline playback
progress: **`OfflineLibrarySyncRules.DecideProgress`** only ever moves the
tuple forward (later chapter, or same chapter/later position). An identical
replay is `unchanged`; anything behind the stored position is
`ignored_behind`; an unknown chapter (or one that does not belong to the
given work) is `chapter_not_found`. Applying a checkpoint calls the exact
same `NovelProgressService.SaveProgressAsync` the online reader uses —
there is no second progress writer.

#### Bookmarks: last-writer-wins with tombstones

Bookmarks can be added, edited or removed while offline, on potentially more
than one device, so a simple "newest wins" per field is not enough — a
device must also be able to tell a genuine deletion apart from "I haven't
synced this add yet". `OfflineLibrarySyncRules.DecideBookmark` treats a live
bookmark's `SyncUpdatedAt` and a tombstone's `DeletedAtUtc` as one shared
clock and takes whichever is later:

- An event older than that clock is `ignored_stale` — it never overwrites a
  newer edit and never resurrects a bookmark removed later.
- `upsert` (add and edit share one event shape) creates or updates the
  bookmark and clears any tombstone (a later add after a remove
  *resurrects* the bookmark — deterministic, not a race, because it is
  strictly newer).
- `remove` deletes the bookmark and records/updates a tombstone
  (`NovelBookmarkTombstone`, keyed by the same bookmark id).
- An event that exactly repeats the currently-known timestamp is
  `unchanged` (idempotent replay, no duplicate writes, no extra history).

The **bookmark id is client-supplied** (`NovelBookmark.Id`'s setter, already
public) so a device can create a bookmark offline and reference it in a
later edit/removal before the server has ever seen it, and so two devices
converge on the same row instead of creating duplicates. `ClientEventId` is
stamped on the bookmark for auditing which event last touched it; it is not
used for idempotency by itself — the last-writer-wins timestamp is.

Both reconcilers are profile-scoped throughout (every query/write filters by
`ProfileId`); one profile's sync batch can never see or affect another's
progress, bookmarks or tombstones.

## PWA download manager

Three new `wwwroot/js/` modules, split the same way `tts.js`/`reader-tts.js`
already are — pure engine vs. browser I/O vs. DOM wiring — so the pure parts
run under the existing Jint test harness
(`tests/Jularr.Tests/OfflineLibraryEngineTests.cs`, mirroring
`DeviceSpeechEngineTests.cs`):

- **`offline-library.js`** (`window.JularrOfflineLibrary`) — pure, no
  IndexedDB/OPFS/network: `diffManifest` (differential detection),
  `isBookComplete` (finalization rule — a book is "available offline" only
  once every selected chapter's *verified local hash* matches the manifest),
  `transitionQueueItem`/`nextEligibleItem` (the queue state machine:
  `queued → downloading → verified`, with `paused`/`failed`/`cancelled`
  branches and bounded exponential backoff via `computeBackoffMs`),
  `isNetworkEligible` (Wi-Fi-only, explicitly best-effort — see below),
  `namespaceKey`, `formatBytes`/`totalStorageBytes`.
- **`offline-library-storage.js`** (`window.JularrOfflineLibraryStorage`)
  — IndexedDB (manifests, download queue/state, verified chapter hashes,
  local settings, the reading-state sync queue) and OPFS (chapter JSON,
  covers/illustrations), namespaced per profile (see below). Requests
  `navigator.storage.persist()` once per store and exposes both that result
  and whether OPFS itself is available as `isDegraded`/`persisted`; when
  OPFS is unavailable, chapter payload JSON is kept in IndexedDB instead
  (**degraded mode** — the UI surfaces this explicitly, see the Settings
  page below) and images are simply not cached rather than exhausting
  IndexedDB with binary blobs.
- **`offline-library-manager.js`** (`window.JularrOfflineLibraryManager`)
  — wires the two together: `enqueueBook` (fetch manifest, diff, queue the
  difference), `processQueue`/`pause`/`resume`/`retryFailed`,
  `removeChapter`/`removeBook`, `storageUsage`, the Wi-Fi-only setting, and
  `queueSyncEvent`/`drainSyncQueue` for the reading-state sync endpoint
  above (queued here so the transport and conflict handling already exist
  once the reader, part 2, starts calling into it).

### Atomic finalization

A chapter is written to OPFS/IndexedDB, then **read back and compared**
before it is recorded as verified — a truncated or corrupted write is
caught immediately rather than surfacing as a broken chapter later. A book's
manifest status only flips to `"available"` when `isBookComplete` is true
for every chapter the user selected. There is no intermediate state where a
partially-downloaded book looks complete.

### Storage ownership (PWA)

| Data | Store |
| --- | --- |
| Manifests, download queue/state, verified chapter hashes, Wi-Fi-only setting, reading-state sync queue | IndexedDB |
| Chapter payload JSON, cover/illustration bytes | OPFS (falls back to IndexedDB for chapter JSON only, in degraded mode) |
| App shell / static assets | Cache Storage via `service-worker.js` (unchanged) |

`service-worker.js`'s `isStaticAsset` allowlist is unchanged and explicitly
documented to never include `/api/client/v1/offline-library/**`: books never
enter the generic cache, and the service worker's own version bump/cleanup
(`CACHE_VERSION`) therefore cannot purge downloaded books — they live in a
completely separate storage area.

### Per-user isolation

Every IndexedDB database name and OPFS root directory is namespaced by the
signed-in profile id, read from `document.body.dataset.profileId`
(rendered by `Pages/Shared/_Layout.cshtml`, already the existing pattern —
see `pwa.js`'s own `data-offline-logout` handling). **No profile id means no
database is opened at all** — signing out hides every account's offline
state immediately (nothing to enumerate, nothing to accidentally show)
without deleting it, so a user who signs back in on the same device gets
their downloads back. Deleting a book's local files permanently is only ever
an explicit action in Settings → Offline or the "Save offline" control,
never an implicit side effect of signing out.

### Wi-Fi-only

`isNetworkEligible` uses the (Chromium-only) Network Information API
(`navigator.connection`) and is explicitly best-effort per #221: when the
API is unavailable, download eligibility is *not* blocked — silently never
starting a download because a feature detection failed would be worse than
occasionally downloading on cellular. The Settings → Offline page's toggle
label says so.

### Settings → Offline (`Pages/Settings/Offline.cshtml`)

Storage usage (with the degraded-mode notice when OPFS is unavailable), the
Wi-Fi-only toggle, and per-book remove. The reusable **"Save offline"**
action (`Pages/Shared/_OfflineLibraryAction.cshtml`,
`OfflineLibraryActionViewModel`) is included once, on the Books library
detail page (`Pages/Books/Library.cshtml`) — see [Part 2](#part-2-todo) for
where it still needs to be placed. Whole-book download only for part 1;
per-chapter selection reuses the same manifest/diff/queue machinery and is
part 2 scope.

## Smart prefetch (#415)

Server-side policy and selection that keeps the next episodes / chapters
ready on a device within a hard storage cap. It reuses this contract and the
bounded playback downloads (#225); there is no second cache, queue or
progress store. **Off by default.**

- **Policy** (`OfflinePrefetchPolicy`, per profile, JSON store
  `/data/offline/prefetch/{profileId}.json`, no migration): on/off, cap,
  scope (episodes / chapters), how many items ahead per series/work, and
  whether metered connections are allowed (off by default). Edited at
  `Settings → Downloads → Smart prefetch` (`Pages/Settings/Prefetch`). A
  missing or damaged file means Off.
- **Selection** (`OfflinePrefetchCandidateSource`): from canonical
  progress only — Continue Watching plus `EpisodeSequence` for following
  unwatched episodes, `NovelProgress` for the current/next unread chapters.
  Ranked by depth (the item to continue with for every series/work first),
  then by recent activity. Chapter sizes are estimated from text length;
  episodes use the media file size.
- **Decision** (`OfflinePrefetchPlanner`, pure): given the policy, the
  candidates and the device inventory, it returns downloads and evictions.
  Prefetched bytes never exceed the cap (or what the device limit leaves
  after explicit downloads). **Only `prefetched` items are ever evicted,
  least recently used first**, never running downloads and never items that
  are wanted next; `explicit` items are neither evicted nor counted against
  the cap. Lowering the cap shrinks prefetched content the same way.
- **Client contract** (`ClientApiOfflinePrefetch.cs`): the device reports its
  inventory (`kind`, `itemId`, `sizeBytes`, `origin`, `lastUsedUtc`,
  `active`) and its `connection`; the server returns the plan. The server
  keeps no copy of the inventory. Clients must record plan downloads with
  origin `prefetched` and flip an item to `explicit` when the user saves or
  keeps it. Executing the plan on Android (WorkManager trigger, origin
  tracking in `OfflineStore`) is not implemented yet.

## Migration

One migration, `20260926144053_AddOfflineLibrarySync`: two columns on
`NovelBookmarks` (`SyncUpdatedAt`, `ClientEventId`) and the new
`NovelBookmarkTombstones` table (cascade-deleted with its `NovelWork`).
`SyncUpdatedAt` defaults to a constant far-past timestamp for existing rows
(SQLite `ADD COLUMN` cannot default to another column's value) — existing
bookmarks predate offline sync entirely, so any real client event outranks
that default under last-writer-wins.

## Tests

- `tests/Jularr.Tests/OfflineLibraryTests.cs`: chapter/work hash
  determinism and order-(in)dependence, differential manifest detection
  against a real SQLite database, chapter payload translation filtering and
  image asset URL resolution, asset path-safety (traversal, wrong
  extension, unknown/unsaved names, cross-volume isolation), the pure
  `DecideProgress`/`DecideBookmark` rules, and full reconciler runs proving
  idempotent replay, forward-only progress, tombstoned removal +
  resurrection, and profile isolation.
- `tests/Jularr.Tests/OfflineLibraryEngineTests.cs`: the JS engine
  (`offline-library.js`) under Jint — manifest diffing, the finalization
  rule, queue state transitions (including invalid/no-op transitions),
  eligible-item selection with backoff, Wi-Fi-only eligibility, namespacing
  and storage formatting.

## Android implementation

`clients/android/app-mobile`, package `de.juloc.jularr.mobile.offline.library`
(kept separate from `mobile.offline`, the #341 bounded-offline-playback
package, so the two features stay independently reviewable). Consumes the
contract above through new `JularrLibraryApi`/`OfflineLibraryJson`
(`core-api`) and `ClientOfflineLibrary*`/`OfflineLibrary*Event`/`Result`
models (`core-model`), gated by the `offlineLibrary` capability flag.

- **`LibraryStore`** — app-private persistence (books, chapters, settings,
  progress/bookmark sync queues), a sibling of `OfflineStore` (#341): the
  same `AtomicFile` + hand-written `org.json` codec, **not Room** — there is
  no Room/reflection-serialization dependency anywhere in this app, and
  introducing one for this feature alone was rejected as the higher-risk,
  higher-friction option (see the file's KDoc for the trade-off). Chapters
  and assets are stored keyed by their own globally-unique id/name
  (`<owner>/chapters/<chapterId>.json`, `<owner>/assets/<asset>`), not
  nested under a book folder, so a request path (`/chapters/{id}`,
  `/assets/{volumeId}/{asset}`) resolves to a local file directly.
- **`LibraryManifestDiff`** — pure differential diff (unit tested): a
  chapter is downloaded only if new or its hash changed; unchanged chapters,
  whatever their local download state, are left alone; deselected or
  manifest-dropped chapters are queued for local removal.
- **`LibraryContentIo.writeVerified`** — atomic finalization: writes to
  `<file>.part`, reads the bytes back and byte-compares before an atomic
  `renameTo`, exactly the "write, then read back and compare" rule the PWA
  engine uses. A chapter is additionally rejected (retried later, not marked
  verified) if the fetched payload's `hash` no longer matches the manifest
  hash it was requested for (the version-race case `docs` calls out above).
- **`LibraryDownloads`** — the manager: `enqueueBook` (manifest fetch + diff
  + per-chapter WorkManager scheduling, whole-book or `chapterIds`
  selection), pause/resume/retry/remove per chapter, remove per book,
  Wi-Fi-only setting, storage usage. Reuses `OfflineAccountPolicy`,
  `OfflineAccount`, `OfflineOwner` and `DownloadStateMachine` from `#341`
  directly (unmodified) since none of them are anime-specific — the account
  boundary (logout locks, account/server switch purges) and the
  queued/downloading/paused/ready/failed state machine are exactly the same
  shape for a chapter as for an episode. Cover/volume-cover assets are
  downloaded best-effort inline (not a tracked WorkManager job): they never
  gate "available offline" and a missed one is simply retried on the next
  book refresh.
- **`LibraryChapterDownloadWorker`** / **`LibrarySyncWorker`** — WorkManager
  jobs mirroring `OfflineDownloadWorker`/`OfflineProgressSyncWorker`. A
  chapter is one atomic download unit (not sub-file HTTP Range/resume like
  episode media): chapters are bounded-size text, so the *job* itself is the
  resumable unit via WorkManager's own retry/backoff — the same granularity
  the PWA queue already uses. `LibrarySyncWorker` re-checks `/me` before
  draining the queue, exactly like the offline-playback progress worker.
- **WebView local interception** (`JularrWebShell.shouldInterceptRequest`,
  `LibraryRequestInterception`): a same-origin `GET` matching the
  offline-library manifest/chapter/asset path shape is answered from local
  storage when available ("local source first"), otherwise falls through to
  the network. This is the option (a) the issue asks for — the same web
  reader would render offline content without a second rendering path — but
  its effect today is limited: `/Novels/Read`/`/Books/Read` do not yet fetch
  chapter content through `/api/client/v1/offline-library/**` client-side
  (that is exactly the still-open "Reader repository abstraction" item
  below), so this interception is currently exercised only if/when that
  reader-side wiring lands. No `addJavascriptInterface`/JS bridge is added;
  same-origin enforcement and the fixed, regex-validated path allowlist are
  the only things that make this safe to expose.
- **Entry points**: a floating native "Save offline" action
  (`LibrarySaveOfflineOverlay`) appears over the WebView while browsing a
  book/novel detail page (`/Novels/Work/{id}` or `/Books/Library/{id}`,
  detected by URL path — the same technique `WebNavigationPolicy` already
  uses for the episode Play route, not a JS bridge), and an "Offline books"
  management screen (`LibraryDownloadsScreen`) is reachable next to the
  existing Downloads screen (notification, launcher shortcut, *Server
  unavailable* screen).
- **Sync queue**: `LibraryProgressQueue`/`LibraryBookmarkQueue` are local
  queue-bookkeeping only (record/acknowledge/batch, one pending entry per
  work/bookmark id) — the server's forward-only and last-writer-wins rules
  are the actual conflict resolution; the client always adopts whatever the
  server answers with. `recordProgress`/`recordBookmark` and the queue are
  fully implemented and tested, but (like the PWA side) have no reader call
  site yet in this PR — wiring them up is the same reader-integration slice
  as item 2 below, now shared across both clients.

**Relationship to the PWA's 2A reader bridge above:** the two clients solve
"local source first" differently today. The PWA's
`offline-library-repository.js` reads its own IndexedDB/OPFS store directly,
in-process, from JS running on the already-loaded reader page — it never
issues a `fetch()` to `/api/client/v1/offline-library/**` when serving local
content, so Android's WebView-level interception of that same path (above)
has nothing to intercept from a PWA reader page and is orthogonal to it.
Android's interception, in turn, only takes effect if/when the WebView-hosted
`/Novels/Read`/`/Books/Read` page itself starts *fetching* chapter content
through that path client-side, which it does not yet do (both readers still
render chapter content server-side on load). A fully unified "cold, from
scratch, offline page load" story on both platforms most likely means
teaching the reader pages to fetch chapter content through
`/api/client/v1/offline-library/**` explicitly (letting Android's existing
WebView interception and a PWA service-worker equivalent both answer it
locally), rather than each client only patching narrower gaps in-process as
this PR and 2A currently do. Left for a follow-up rather than block either
slice on the other.

## Part 2 TODO

Left after part 2A (above):

1. **Cold-start offline page load / a fully unified reader repository.**
   Opening a `/Novels/Read/{id}` or `/Books/Read/{id}` URL directly while
   fully offline (nothing already loaded this session, e.g. from the
   Library's Offline filter) still falls back to the generic
   `service-worker.js` `/offline.html` shell on the PWA rather than
   rendering the downloaded chapter; Part 2A's in-page offline chapter
   navigation (footer previous/next, while a reader page is already open)
   covers the more common "lost connectivity mid-session" case without this.
   As noted at the end of the Android section above, a full fix on both
   platforms most likely means teaching `/Novels/Read`/`/Books/Read` to
   fetch chapter content through `/api/client/v1/offline-library/**`
   client-side (so Android's existing WebView interception actually takes
   effect, and a PWA service-worker fetch handler could answer the same
   requests from IndexedDB/OPFS) rather than each client only patching
   narrower gaps in-process as today.
2. The chapter drawer's list (`novel-chapter-drawer.js`, PWA) still requires
   network to load (`OnGetChaptersAsync`); it could fall back to the local
   manifest's chapter list while offline instead of only showing a retry
   button.
3. Per-chapter selection UI on both clients, reusing `enqueueBook`'s
   `chapterIds` option / `LibraryDownloads`'s equivalent (whole-book
   download only so far).
4. **Android reader-side wiring.** `LibraryDownloads.recordProgress`/
   `recordBookmark` and the local sync queues are implemented and tested but
   have no reader call site yet (mirrors item 1's PWA-side gap before
   part 2A) — wiring the WebView-hosted reader's progress/bookmark writes to
   them is the Android equivalent of part 2A's Novel/Book wiring above.
5. **Manga reuse**: the manifest/chapter/asset/sync contract here is
   already generic over "work → volume → chapter" content; a Manga chapter
   payload would swap `originalText`/`blocks` for an ordered page-image
   list while keeping the same manifest hash/diff/sync machinery. No second
   download engine should be built for it.
