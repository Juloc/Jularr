# Downloads & Offline — Consumer Download Manager

Status: **binding planning specification; visual reference pending upload/approval**.

This screen is the canonical consumer surface for device-local offline media and active offline-download work. It unifies the existing Offline Library and Offline Media capabilities without creating another download engine, progress store, media identity model or server-side acquisition queue.

Relevant existing contracts:
- `docs/OFFLINE_LIBRARY.md`
- `docs/ANDROID_CLIENTS.md`
- `docs/mockups/user-settings/SPEC.md`
- `docs/mockups/library/SPEC.md`
- `docs/mockups/player/SPEC.md`
- issue #221 — Offline Library & Reader Sync
- issue #225 — bounded offline playback
- issue #415 — later Smart Offline prefetch

## 1. Purpose and boundary

The page answers four user questions:

1. What is downloading to this device now?
2. What is already usable offline on this device?
3. Is anything blocked or failed?
4. How much local offline storage is being used?

It is a **personal device-local management surface**.

It must not expose or merge:
- Admin Downloader / SABnzbd queue;
- Wanted / monitoring;
- Request acquisition progress;
- import/processing jobs;
- NAS/library-root storage management;
- transcoder/codec diagnostics;
- server filesystem paths;
- another device's local download inventory.

Server acquisition and local offline downloading are separate concepts. A Work can already exist in Jularr and then be copied to the current device for offline use.

## 2. Canonical ownership

This screen is presentation over existing owners.

Media identity remains:

`Work -> Structure -> Edition -> Version -> Asset/File -> Track`

Progress remains owned by the canonical progress model and existing offline reconciliation paths.

Offline content remains owned by:
- Offline Library contracts for structured reading packages;
- Offline Media contracts for portable playable media;
- platform-local storage/download adapters for the actual bytes and local queue state.

The page may group local items visually by Work, season, volume or media type. Those groups are presentation only and must not create durable `SeriesDownload`, `BookDownload`, `MovieDownload` or similar parallel domain roots.

## 3. Information architecture

Primary path:

`Profile -> Settings -> Downloads & Offline`

The Settings landing page lists **Downloads & Offline** in the Media group.

Secondary entry points may deep-link here:
- the compact global download indicator;
- an offline/download action on a media detail surface;
- a download-completed or download-failed notification;
- storage-limit/error recovery actions.

Do not add Downloads as a permanent primary bottom-navigation item.

The page is one level below Settings and uses the normal contextual Back behavior from the shared account/settings shell.

## 4. Platform contract

The product semantics are shared across clients. Capability differences are explicit.

### PWA / Web
- show only offline operations that the current browser implementation can safely support;
- reading packages use the existing Offline Library storage path;
- playable media uses the existing Offline Media package path where available;
- do not use the generic service-worker static cache as the durable media store;
- browser storage/quota limitations must degrade clearly.

### Android phone/tablet
- use the native managed-download implementation and app-private local storage;
- queue/retry/restart behavior may be stronger than browser behavior;
- the same user-facing states and actions apply.

### Future desktop client
A future native Windows/macOS client may provide a native local-file adapter, but it must reuse the same server contracts, media identity and user-facing semantics instead of creating a desktop-only business model.

### Unsupported capabilities
If a media type cannot be downloaded reliably on the current client, do not show a dead Download action. Existing already-local content remains visible when supported.

## 5. Page hierarchy

### Header
Show:
- contextual Back;
- title: **Downloads & Offline**;
- optional concise description;
- compact device-local storage summary;
- entry to Offline Settings.

Do not duplicate the title elsewhere on the page.

### Primary tabs
Exactly two primary tabs:

1. **Downloads**
2. **Offline available**

Counts may appear when useful:
- Downloads count = items still requiring transfer/user attention;
- Offline available count = fully usable local items.

Do not include completed items indefinitely in the active Downloads count.

### Filters
Shared media filter:
- All;
- Video;
- Reading;
- Audio.

Only show a category when it is supported or currently has relevant content.

Optional search filters the local inventory by visible title/unit metadata.

Sorting should stay simple:
- active downloads: operational priority/state, then creation order;
- offline available: most recently used/downloaded by default, with optional title/size sorting only if needed.

No advanced filter builder.

## 6. Downloads tab

This tab contains local work that is not simply a stable ready item.

Canonical user-facing state families:

| State | Meaning | Primary action |
| --- | --- | --- |
| Queued | Accepted but transfer has not started | Cancel |
| Downloading | Bytes/content are actively transferring | Pause |
| Paused | User intentionally stopped the transfer | Resume |
| Waiting for connection | Transfer cannot currently use the network | Cancel |
| Waiting for Wi-Fi | Wi-Fi-only policy blocks transfer | Cancel |
| Verifying / Preparing | Transfer finished but local copy is not yet safe to expose | none |
| Failed | Action is required or automatic retry is exhausted | Retry |
| Update available | Existing verified copy remains usable but a newer package/version exists | Update |
| Needs attention | Local copy is missing/corrupt/stale and cannot be treated as ready | Repair/Retry or Remove |

Client implementations may have a smaller internal state machine. Waiting, verifying or attention states can be derived UI states where the underlying owner exposes enough information; the UI must not invent false certainty.

### Download row/card

Show only information useful to the consumer:
- artwork;
- Work title;
- concrete unit, e.g. episode/chapter/book;
- media type when ambiguity exists;
- state label;
- progress bar when meaningful;
- downloaded / total size when known;
- optional speed and estimated remaining time only when based on real measurements;
- primary action;
- overflow menu for destructive/secondary actions.

Do not show:
- release names;
- indexer/source;
- downloader client;
- NAS path;
- hashes;
- HTTP status;
- codec/transcode implementation details.

### Progress truthfulness

A percentage is shown only when the client has a meaningful denominator.

Examples:
- byte-backed video/audio: downloaded bytes / expected bytes;
- complete structured reading selection: verified selected units / selected units, optionally with known byte totals.

Never show a decorative/fake percentage for indefinite work.

Speed and ETA are optional and disappear when unstable or unknown.

## 7. Offline available tab

Contains only content that the current client considers verified and usable locally.

A ready item shows:
- artwork;
- Work title;
- downloaded unit/scope;
- local size when known;
- offline-ready state;
- Open / Play / Read;
- overflow action to Remove from device.

This list is **not another Library**:
- no recommendation logic;
- no ownership/request controls;
- no server acquisition diagnostics;
- no full metadata detail replacement.

Opening an item routes to the normal Reader/Player/detail flow using local-first playback/content resolution.

## 8. Grouping and multi-unit media

Grouping is visual and collapsible where it materially reduces clutter.

### TV / Anime series
Group by Work/season when several episodes exist.

Example summary:
- Work title;
- "12 episodes offline";
- total local size.

Expanded children show per-episode state.

Work-level actions may include:
- resume all paused/failed selected downloads;
- remove all local episodes for that Work.

Do not create a separate durable series-download entity.

### Movie
One playable Work is normally one offline item.

### Book / Light Novel
Support:
- whole selected work;
- selected chapters.

Show scope truthfully:
- **Complete offline** when every selected/current required unit is verified;
- **8 of 21 chapters offline** for partial content.

### Manga
Reuse the structured offline package model:
- chapter and/or volume grouping;
- page/image assets remain part of the package;
- no Manga-only download subsystem.

### Audiobook
Use the same surface when offline-audio capability exists.
Do not force an audiobook implementation merely because the screen can represent it.

### Games
Games are not part of this first offline-download contract unless a concrete local/offline game package capability is separately defined. Do not expose an empty Games filter.

## 9. User actions

### Start
The normal media surface owns the initial Download/Offline action. Starting a download:
1. resolves the canonical media/package descriptor;
2. checks current-client capability;
3. checks local admission/storage policy;
4. creates/updates the existing client-local queue;
5. immediately reflects the state in this page and the global indicator.

### Pause
Pause must preserve valid partial progress where the underlying downloader supports continuation.

### Resume
Resume continues the same logical download. It must not silently create a duplicate queue item.

### Cancel
Cancel stops unfinished work. Confirmation is needed only when cancellation also discards substantial partial local data and that consequence is not obvious.

### Retry
Retry operates on the existing failed logical item and must preserve reusable verified/partial data where safe.

### Remove
Remove deletes the device-local offline copy only.

It must not:
- delete canonical server media;
- remove the Work from Library;
- clear canonical watch/read progress;
- cancel monitoring/request state.

Use a concise confirmation when removal is destructive and non-trivial.

### Update
Update keeps the last verified copy usable until the replacement is safely finalized where technically possible. Structured reading packages should reuse differential update behavior.

## 10. Detail-page offline action contract

All supported detail/player/reader entry surfaces use one semantic vocabulary.

Recommended user-facing action/state:
- **Download** — action;
- **Downloading…** — active state;
- **Offline available** — verified state;
- **Update available** — verified but stale state;
- **Retry download** — failed state;
- **Remove offline download** — destructive action.

Do not mix equivalent terms such as Save offline / Cache / Keep local / Download across media types unless localization requires a context-specific phrase.

The shared page remains the place for queue/storage management; detail pages show only the local action/status needed for that media item.

## 11. Storage summary

The page may show a compact storage panel.

Required:
- total bytes occupied by Jularr-managed offline content when measurable;
- category breakdown when reliable: Video, Reading, Audio;
- link to Offline Settings.

Optional:
- configured offline quota/limit;
- reliable platform-provided free/available storage.

Important:
- a browser storage quota estimate is not the same as physical device free space;
- do not label a configured Jularr limit as device capacity;
- do not fabricate total/free space when the platform cannot provide it.

The storage panel is operational context, not a storage-admin dashboard.

## 12. Offline Settings relationship

This screen manages items. Offline Settings owns reusable preferences such as:
- Wi-Fi-only downloads;
- effective per-device/profile offline limit where supported;
- later Smart Offline controls;
- bulk clear/manage actions when appropriate.

Do not duplicate all settings inside the management page.

A small Settings link/button is enough.

## 13. Network behavior

### Connection lost during transfer
Do not treat ordinary connectivity loss as a permanent failure.

Show **Waiting for connection** when the job can resume automatically.

### Wi-Fi-only policy
When enabled and the current network is ineligible:
- show **Waiting for Wi-Fi**;
- preserve the queue;
- resume automatically when eligible.

### Server/storage unavailable
Existing verified offline content remains usable.

New transfers may wait/fail with a consumer-safe message. Do not expose NAS wake/storage-root internals here.

## 14. Restart and recovery

After browser/app/process/device restart:
- local ready items must still be discoverable;
- unfinished jobs must recover according to the platform's persistent queue capabilities;
- UI state must be reconstructed from durable local download/package data, not stale visual state;
- a local item is never promoted to Ready solely because the previous UI said it was complete.

## 15. Verification and integrity

Ready/Offline available requires the existing owning contract's verification/finalization rule.

Incomplete, stale or corrupt local content must not appear as successfully offline.

If verification fails:
- retain recoverable data where safe;
- show Retry/Repair or Remove;
- provide technical diagnostics only in appropriate logs/Admin diagnostics, not the consumer page.

## 16. Updates and version changes

When the canonical source changes:
- keep a still-valid verified local copy usable until replacement where possible;
- expose **Update available**;
- structured packages update only changed/new units/assets where the existing manifest contract supports it;
- stale chunks/resources from a replaced media identity must not be resumed as if they belonged to the new file.

No background update may silently delete a user-explicit offline copy without an explicit policy.

## 17. Storage limit / insufficient space

Admission failure must be understandable.

Message family:
- **Not enough offline storage**
- explain whether the Jularr offline limit or platform/browser storage is the blocker when known.

Actions:
- **Manage downloads**
- **Offline settings**

Never resolve a full device by deleting explicit user downloads automatically.

Later Smart Offline eviction may remove only disposable speculative copies according to #415. Explicit downloads have priority.

## 18. Offline page behavior

The Downloads & Offline surface should remain useful without server connectivity.

At minimum show from local state:
- verified offline inventory;
- active/paused/waiting local queue state;
- local sizes/state where stored;
- Open/Play/Read for supported ready content;
- Remove local content.

Disable/hide actions that require the server, such as starting a new download or fetching a new manifest.

Do not replace the whole page with a generic network error when useful local state exists.

## 19. Account/profile isolation and logout

Offline data is scoped to the authenticated account/profile and server origin according to the existing client implementation.

Requirements:
- one profile must never see another profile's offline inventory;
- profile switch/logout must follow the platform's existing secure retention/clear policy;
- inaccessible retained bytes must never become visible to another profile;
- account removal/revocation must invalidate access appropriately.

The UI must not invent a second local identity model.

If logout currently clears managed offline data on a platform, the user-facing logout flow should make that consequence understandable where it is material.

## 20. Global download indicator

The authenticated app shell may show a compact indicator while local downloads need attention.

Examples:
- download icon + active count;
- subtle failed-state marker when one or more items need action.

Selecting it opens this page with the Downloads tab active.

Behavior:
- hidden when there is no active/attention state;
- no permanent progress dashboard in primary navigation;
- completion may briefly surface via normal notification/toast, then the indicator can disappear.

## 21. Notifications

Use notifications sparingly.

Useful events:
- selected download completed;
- download failed and requires attention;
- multi-item selection/season batch completed, preferably summarized instead of one notification per child.

Do not notify for:
- every chunk;
- every verification step;
- routine auto-resume.

Notification delivery preferences remain under the existing Notifications contract.

## 22. Error presentation

Consumer-safe error families:
- connection unavailable;
- waiting for Wi-Fi;
- insufficient offline storage;
- source currently unavailable;
- download failed;
- local copy could not be verified;
- update failed, previous copy still available;
- permission/account access no longer valid.

Primary recovery should be Retry, Resume, Manage, Settings or Remove as appropriate.

Internal exception text must not leak into the UI.

## 23. Empty states

### No active downloads
Keep the Downloads tab simple:
- "No active downloads."
- optional link back to Library/Discover only if it is useful and does not imply every item is downloadable.

### Nothing offline
Explain briefly that supported media can be downloaded from its normal detail surface.

Do not use a giant marketing hero.

### Offline with no local content
Show a concise offline-specific empty state. Do not imply new content can be fetched without a connection.

## 24. Responsive behavior

### Desktop
Preferred structure:
- main content column for tabs, filters and download/offline rows;
- optional narrow right rail for storage summary and one small settings/action group;
- rows remain information-dense rather than becoming oversized cards.

Target mockup can use a wide 19:9 composition.

### Mobile
- normal smartphone aspect ratio;
- one column;
- storage summary becomes compact and moves above/below tabs or into Settings;
- cards/rows show only essential metadata;
- primary pause/resume/retry action remains directly reachable;
- overflow menu holds destructive/secondary actions;
- no horizontally compressed desktop table.

The normal Jularr mobile primary navigation remains unchanged.

## 25. Accessibility

- every icon-only action has an accessible label;
- progress bars expose semantic value/min/max when known;
- state is communicated by text/icon, never color alone;
- pause/resume/retry/remove are keyboard accessible on Web;
- focus order follows visual order;
- touch targets follow the shared mobile target sizing;
- live download progress announcements are throttled and must not spam screen readers;
- Reduced Motion applies to progress/transition animation.

## 26. Theme and visual language

Use the shared Jularr Clean/Original design system.

For the Clean Purple reference:
- light neutral surfaces;
- restrained purple accent;
- shared typography, radii, borders, buttons, tabs and status chips;
- no decorative gradients/glow;
- no generic admin-dashboard styling;
- media artwork carries most visual imagery.

Light and Dark remain first-class even if the first approved mockup is Light.

The visual mockup is a reference. This text spec wins on behavior/semantics when an image contains placeholder or technically misleading data.

## 27. Localization

All labels/messages use the normal Jularr localization catalog.

Do not hardcode German/English strings in page-local JavaScript or native UI.

State terms should map consistently across Web/PWA and Android even when the platform implementation differs internally.

## 28. Explicitly out of scope for this screen/spec

Do not add here:
- Smart Offline / automatic next-episode prefetch;
- automatic watched-item eviction;
- download scheduler/time windows;
- bandwidth throttling;
- server-side acquisition/download queue;
- release/indexer selection;
- transcode profiles/codecs;
- cross-device file transfer;
- offline-file sharing/export;
- DRM/license-server behavior;
- automatic mirroring of whole libraries;
- destructive canonical-media cleanup;
- a new progress database;
- a new media identity hierarchy.

Smart Offline remains later work under #415 and must reuse these local-download owners when implemented.

## 29. Acceptance criteria

- [ ] One consumer screen manages current-device offline work across supported media types.
- [ ] The screen is reachable from Profile -> Settings and the global download indicator.
- [ ] Active and ready items are clearly separated.
- [ ] Queued, downloading, paused, waiting, failed and ready behavior is understandable.
- [ ] Progress percentages are shown only when meaningful.
- [ ] Pause/resume/retry/cancel/remove act on the existing logical local download instead of creating duplicates.
- [ ] Remove affects only the current device's offline copy.
- [ ] Ready items open through the normal Reader/Player flow.
- [ ] Series/books can be visually grouped without creating parallel domain identities.
- [ ] Storage usage is truthful and does not confuse browser quota, Jularr limit and physical device capacity.
- [ ] The page remains useful offline from local state.
- [ ] Local content is isolated per profile/account.
- [ ] Restart/recovery does not promote incomplete data to Ready.
- [ ] Corrupt/stale content is not presented as available offline.
- [ ] Desktop and mobile layouts are intentionally designed.
- [ ] Unsupported client/media capabilities do not produce dead controls.
- [ ] Admin acquisition/downloader details never leak into the consumer surface.
- [ ] The implementation reuses existing Offline Library / Offline Media contracts and canonical progress/media identity.
