# Offline Settings — End-State Product Contract

Status: **binding target-product planning specification; approved Clean Purple Desktop/Mobile visual reference is present in this folder**.

This specification defines the finished Jularr offline/download preference surface across Web/PWA, Android phone/tablet and future native desktop clients. Implementation may land in phases, but the UX contract is the end-state target and must not be reduced to today's implementation.

The approved mockup uses the Jularr Clean Purple light visual language with one continuous consumer settings page on Desktop and a sectioned/accordion presentation on Mobile. The approved image reference lives in this folder. If the image and this specification conflict, this specification wins.

Related:
- `docs/mockups/downloads-offline/SPEC.md` — current-device queue and offline inventory management
- `docs/OFFLINE_LIBRARY.md` — structured reading packages and offline sync
- `docs/ANDROID_CLIENTS.md` — native download/player contracts
- `docs/mockups/player/SPEC.md` — playback/audio/subtitle/quality semantics
- `docs/mockups/user-settings/SPEC.md` — shared settings layout rules
- issue #221 — Offline Library & Reader Sync
- issue #225 — bounded offline playback
- issue #415 — Smart Offline / bounded prefetch

## 1. Product role

Offline Settings configures **how this profile/device prepares, stores, updates and cleans up offline content**.

It does not manage individual queue items. Individual downloads, failures and stored items belong to `downloads-offline/SPEC.md`.

It must never expose:
- SABnzbd/Admin downloader settings;
- indexers/release selection;
- NAS/library-root paths;
- transcoding internals;
- HTTP/hash/debug values;
- server import or Wanted rules.

Offline Settings is consumer-facing.

## 2. Entry points and page shape

Primary Settings path:

`Profile -> Settings -> Downloads & Offline`

The screen follows the standard setting-page contract:
- contextual Back where the shared shell uses one;
- localized page heading **Offline-Einstellungen** / **Offline Settings**;
- the broader navigation/family label may remain **Downloads & Offline**;
- concise description: downloads, storage and Smart Offline for this device;
- grouped settings rows;
- immediate persistence for simple reversible preferences;
- confirmation only for destructive actions.

The current-device download manager is a separate consumer surface. Provide a clear **Manage downloads** action near the top.

Desktop:
- keep the normal Jularr app sidebar only; do not add a second settings sidebar;
- use one wide, readable settings column;
- place a compact current-device summary strip directly under/alongside the page heading;
- stack the major settings groups vertically as full-width sections;
- inside a section, a short title/description column may sit beside the actual controls;
- storage and Smart Offline may use denser internal layouts, but each remains one coherent section rather than a dashboard of independent cards;
- no dashboard grid, KPI wall or right-side analytics rail.

Mobile:
- one column;
- show the current-device summary first, then **Manage downloads**;
- present major groups as compact expandable sections/accordions;
- the approved reference shows **Smart Offline** expanded to demonstrate the richer controls while other groups stay collapsed/summary-first;
- segmented controls may wrap or use sheets;
- destructive actions remain separated at the bottom;
- no squeezed desktop table and no attempt to show all Desktop controls simultaneously.

A modal can be used for sub-editors such as custom storage limit, storage location picker or per-media Smart Offline amounts. The full settings surface itself is not reduced to a tiny dialog.

## 3. Ownership model

Settings are intentionally split by responsibility.

### Device-local preferences

These vary by hardware/storage/network and do not need to match another device:
- download network policy;
- roaming/metered permission;
- offline quality profile;
- download-track packaging policy;
- local storage limit;
- free-space safety reserve;
- storage location;
- this-device Smart Offline participation;
- this-device automatic cleanup policy;
- background/power constraints.

### Profile-scoped preferences

Reuse existing profile owners where the preference already exists:
- preferred audio languages;
- preferred subtitle languages;
- subtitle behavior;
- Learning availability/preferences;
- notification categories.

Do not duplicate those values inside Offline Settings.

Smart Offline content intent may be profile-scoped where the canonical Smart Offline policy owns it, while this device still decides whether it participates and how much local storage it can provide.

### Mandatory reliability behavior

The following are product guarantees, not user settings:
- downloads survive supported app/process restarts;
- safe resume/retry;
- content verification before Ready;
- stale/corrupt detection;
- atomic finalization;
- offline progress/bookmark queueing and reconciliation;
- account/profile isolation;
- bounded retry/backoff;
- preserving the last verified copy until an update is safely finalized where possible.

Do not create toggles for correctness.

## 4. Current-device summary

At the top, show a compact summary when meaningful:
- Jularr offline storage currently used;
- configured Jularr offline limit;
- available device/browser storage only when the platform can report it reliably;
- current storage location on native clients;
- active download count if non-zero.

Never confuse:
- physical device free space;
- browser quota;
- Jularr's configured offline limit.

If the platform cannot know physical free space, omit that number.

Primary action:
- **Manage downloads**

Secondary contextual action:
- **Review storage** / **Free up space** only when usage is near a limit.

## 5. Network

### Download connection policy

Choices:

- **Wi-Fi / unmetered only** — recommended default;
- **Any connection**.

On platforms that expose metered state without the Wi-Fi concept, use the equivalent **Unmetered only** wording.

Behavior:
- existing ready offline items remain usable regardless of policy;
- queued work waits when the active connection is ineligible;
- waiting is not shown as an error;
- eligibility changes resume work automatically.

### Roaming

Where the platform can detect roaming reliably:

**Allow downloads while roaming** — default Off.

Hide this setting when the client cannot detect roaming.

### Explicit vs automatic downloads

A user-initiated explicit Download may optionally ask for one-time override when blocked by the current network policy.

Smart Offline never silently overrides network policy.

### Remote-server downloads

Jularr may download from the user's server over a remote connection if that connection is otherwise permitted. Do not expose server topology or NAS details here.

## 6. Background and power

Show only controls the current client can enforce.

### Background downloads

**Allow downloads in background** — default On on native clients.

On platforms where background transfer cannot be guaranteed, show a read-only capability note instead of a fake toggle.

Turning this Off means:
- active transfers pause when the app is no longer allowed to run them;
- ready content is unaffected;
- queue state is preserved.

### Smart Offline while charging

**Prefer Smart Offline while charging** — default On where charging state is available.

This constrains speculative/background preparation only. Explicit downloads still follow the normal user-initiated path.

### Battery saver

When the OS reports battery-saver/low-power mode, Smart Offline pauses automatically. This is a fixed safety behavior, not a separate toggle.

Explicit downloads may continue only if the OS permits and the user has not paused them.

## 7. Offline video quality

Applies to Anime, TV and Movie downloads when the server/client can provide more than one valid offline rendition.

Label: **Video download quality**

Choices:
- **Automatic** — balances display capability, source quality and configured storage;
- **Original** — highest/source-equivalent quality when an offline rendition exists;
- **High**;
- **Medium**;
- **Data saver**.

The UI never exposes codec/container/transcode implementation.

Rules:
- selection is a device-local target, not a permanent media identity;
- Jularr resolves the concrete offline rendition through the canonical media/version/file model;
- changing the default does not silently replace existing explicit downloads;
- existing items may expose **Redownload at new quality** from the manager/detail surface;
- Smart Offline uses the current device default unless its own lower quality cap is configured.

If only one offline rendition exists, hide or read-only-resolve the quality setting rather than pretending alternatives exist.

## 8. Offline audio quality

For Audiobooks or audio-only offline assets where multiple renditions are supported:

Label: **Audio download quality**

Choices:
- Automatic;
- Original;
- High;
- Standard;
- Data saver.

Hide when the client/server exposes only one audio rendition.

This setting does not choose language tracks.

## 9. Reading image quality

For image-heavy Reading content such as Manga, scanned PDF pages or illustration-heavy packages:

Label: **Offline image quality**

Choices:
- **Optimized** — default; visually high quality with bounded local size;
- **Original** — source-resolution assets where allowed/available.

Text-first EPUB/Novel content is unaffected.

Changing this preference does not duplicate Work identity and does not create a separate Manga download engine.

## 10. Audio track packaging for video

Use profile audio-language preferences as the source of language priority.

Offline-specific packaging policy:

Label: **Audio tracks to include**

Choices:
- **Preferred only**;
- **Preferred + original** — recommended default;
- **All available**.

Rules:
- forced inclusion required by the chosen offline rendition is automatic;
- unavailable preferred tracks do not fail the whole download;
- track IDs remain canonical media tracks;
- no per-download persistent language setting is created outside the package selection.

A Download-selection sheet may override this policy for one download.

## 11. Subtitle packaging for video

Use the profile's subtitle language preferences and subtitle behavior.

Label: **Subtitles to include**

Choices:
- **Preferred languages** — recommended default;
- **Preferred + forced/SDH**;
- **All text subtitles**;
- **None**, only when the resulting offline playback remains valid.

Learning subtitle/cue data required for enabled Jularr Learning features is treated as part of the offline Learning package, not as an ordinary subtitle track.

Image subtitles may be included only when the client can render them offline or the chosen offline rendition safely incorporates them.

A Download-selection sheet may override this policy per item.

## 12. Offline Learning data

Visible only when Learning is enabled and the profile is allowed to use it.

Label: **Include Learning data offline**

Default: On.

Includes only the bounded data required for supported offline interactions, for example:
- normalized subtitle cues/tokens;
- known reading/meaning data already available for the downloaded content;
- current relevant learning state snapshot;
- queued local learning interactions that can safely sync later.

It must not bulk-copy the user's entire Learning database merely because one media item is downloaded.

Turning this Off affects future/updated packages. Existing packages are not destructively rewritten without confirmation.

## 13. Offline storage limit

Label: **Offline storage limit**

This is the maximum storage Jularr is allowed to intentionally occupy for managed offline content on this device/profile.

Offer:
- sensible presets appropriate to the platform;
- **Custom**.

The UI must label it as a **Jularr offline limit**, not total device capacity.

Rules:
- lowering the limit never silently deletes explicit user downloads;
- if explicit downloads already exceed the new limit, keep them and block new admission until usage is reduced or the limit is raised;
- Smart Offline is always subordinate to explicit downloads;
- speculative content may be evicted to satisfy the effective Smart Offline/device budget;
- internal temporary update space must still honor the safety reserve.

PWA:
- effective storage is additionally bounded by browser quota;
- a configured Jularr limit cannot promise space the browser will not grant.

Native:
- admission also considers actual free storage.

## 14. Free-space safety reserve

Native clients with reliable filesystem capacity expose:

Label: **Keep device space free**

Choices:
- Automatic — recommended;
- 1 GB;
- 2 GB;
- 5 GB;
- 10 GB;
- Custom.

Jularr refuses new downloads that would cross this reserve.

The reserve protects the device, not the Jularr quota.

PWA/browser clients hide this when physical free-space semantics are not reliable.

## 14.1 Multi-profile device capacity

The physical device/browser capacity check must account for all Jularr-managed Offline bytes that still exist on this device, including retained-but-locked packages belonging to another profile.

Privacy rule:
- the active profile sees itemized details only for its own accessible content;
- when other-profile retained bytes affect admission, show only an aggregate reserved/other-profile amount where needed;
- never reveal another profile's titles, artwork, progress, filenames or package identity.

This prevents two profiles from independently overcommitting one browser quota/device disk while preserving profile isolation.

The shared device limit applies to Jularr-managed local bytes. Per-profile item visibility remains isolated.

## 15. Storage location

Visible only on clients that support safe user-selectable managed storage.

Examples:
- Android internal app storage;
- supported app-specific external/removable storage;
- native Desktop managed folder.

Show:
- current location;
- available space when reliable;
- **Change location**.

Changing location opens a focused migration dialog:
1. choose destination;
2. validate writability/capacity;
3. choose **Move existing downloads** or **Use for new downloads only** where both are safe;
4. move/copy with verification;
5. only remove the previous verified copy after the new copy is verified.

If removable storage disappears:
- ready items on it become **Storage unavailable**, not deleted;
- queued work pauses;
- Jularr never silently falls back and duplicates everything into internal storage.

PWA hides Storage location entirely.

This location is for client-managed offline copies, never a server LibraryRoot/NAS path.

## 16. Per-media storage view

Expose a concise read-only breakdown when measurable:
- Video;
- Reading;
- Audio;
- Games, when Jularr-managed Game packages are supported on this client;
- Smart Offline;
- Other managed offline assets.

Only Jularr-managed Game package/install bytes count toward the Games breakdown. External launcher installations must not be guessed or counted unless that integration authoritatively exposes and manages those bytes.

Do not require the user to configure hard per-category quotas in V1 of the target design unless a concrete need appears. One device limit plus a Smart Offline budget is easier to reason about.

## 17. Smart Offline — master behavior

Smart Offline is a first-class end-product feature, not a placeholder.

Purpose:
Prepare likely-next sequential content automatically without turning Jularr into whole-library mirroring.

This device setting:

**Use Smart Offline on this device** — default Off until the user opts in.

When enabled:
- uses canonical next-up/progress/library facts;
- downloads only bounded likely-next content;
- respects device storage/network/power policy;
- marks every speculative item as `prefetched`;
- never creates a second cache format or progress model;
- never deletes canonical server media;
- never outranks explicit downloads.

If the profile-level Smart Offline policy is disabled by instance/profile policy, hide or explain the device toggle as unavailable.

## 18. Smart Offline — media types

Show only categories meaningful for the profile and supported by the client.

Independent toggles:
- **Anime / TV episodes**
- **Manga chapters / volumes**
- **Books / Light Novels next chapters**
- **Audiobook next chapters** only if the audiobook package model supports bounded chapter-level prefetch.

Movies are not automatically prefetched merely because Smart Offline is On.

Games are also **not** part of predictive Smart Offline by default (#851). Managed Games may participate in the shared device Offline limit/storage-location accounting, but speculative Game installs require a future explicit Games-specific opt-in policy rather than silently extending #415.

For each enabled sequential category, allow a bounded amount:

### Video
**Keep next episodes ready**
- 1;
- 2;
- 3;
- 5;
- Custom within a safe product maximum.

### Manga
**Keep ahead**
- next chapter;
- next 3 chapters;
- next 5 chapters;
- rest of current volume where size/budget permits.

### Books / Light Novels
**Keep ahead**
- next chapter;
- next 3 chapters;
- next 5 chapters.

### Audiobooks
If supported:
- next chapter;
- next 3 chapters;
- remaining current section/book only when the size budget permits and the content model makes that useful.

Smart Offline amounts are targets, not guarantees. Storage/network/source availability may reduce them.

## 19. Smart Offline storage budget

Label: **Smart Offline budget**

Offer:
- percentage of the Jularr offline limit;
- or an absolute size editor where clearer.

Recommended default after enabling: a conservative bounded share.

Rules:
- explicit offline downloads are outside/above the speculative budget;
- Smart Offline may use only space left after explicit content and safety reserve;
- lowering the budget evicts only `prefetched` items;
- active playback/currently open content is never evicted;
- pinned/kept items are converted/protected as explicit;
- eviction is least-recently-useful/used according to canonical planner rules.

## 20. Smart Offline quality

Optional separate setting:

**Smart Offline video quality**
- Same as normal downloads;
- High;
- Medium;
- Data saver.

This allows speculative content to consume less space without lowering the user's explicit-download preference.

Reading/audio equivalents reuse the normal offline quality unless a concrete category-specific setting becomes necessary.

## 21. Smart Offline network behavior

Default: inherits normal download connection policy.

Additional option when supported:

**Allow Smart Offline on metered connections** — default Off.

This can only broaden Smart Offline within platform/network capability; roaming remains separately protected.

Speculative preparation should avoid waking sleeping server storage solely for prefetch. It can wait until the owning source storage is already available unless the user explicitly initiated a normal download.

## 22. Smart Offline lifecycle

Default behavior:

**Replace completed Smart Offline items with the next item** — On.

Process:
1. completion/progress is recorded locally;
2. pending progress is safely queued/synced;
3. completed prefetched item becomes eligible for eviction;
4. next candidate is prepared if budget/network/source allow.

Never delete an item currently being played/read or with local state that has not been durably queued.

**Keep** / **Pin offline** on a prefetched item promotes it to explicit content so Smart Offline eviction can no longer remove it.

## 23. Automatic cleanup of explicit downloads

Separate from Smart Offline.

Label: **Remove completed manual downloads automatically**

Default: Off.

When enabled, choose:
- After completion + successful sync;
- After 1 day;
- After 7 days;
- After 30 days.

Optional category scope:
- Video;
- Reading;
- Audio.

Safety:
- only items explicitly covered by the user's cleanup policy are eligible;
- pinned/kept items are never auto-removed;
- pending offline progress/bookmarks/learning events are safely queued before deletion;
- source/server media and Library membership are never touched.

This feature must remain obviously opt-in.

## 24. Keep downloaded content updated

Group: **Updates**

### Structured reading content

**Automatically update downloaded Reading content** — default On.

Uses manifest/version diffing:
- fetch only changed/new chapters/assets;
- keep unaffected verified content;
- do not make the whole Work unavailable while a small update is pending.

### Video/audio replacements

Large binary replacements require a separate policy:

Label: **When a downloaded video/audio file is replaced on the server**

Choices:
- **Ask before redownloading** — recommended default;
- **Update automatically on allowed network**;
- **Keep current offline copy until I update it**.

Jularr never splices versions.

### Metadata/artwork

Small metadata/artwork refreshes required to keep a downloaded item understandable may update automatically when online. No separate toggle is needed.

## 25. Download notifications

Do not duplicate the central Notifications preference model.

Show a row:
**Download notifications** -> opens/jumps to the relevant Notifications settings.

Supported categories should include:
- download completed;
- download failed/needs attention;
- Smart Offline batch prepared only when useful;
- storage limit/attention warning.

No notification per chunk or routine retry.

## 26. Sign-out and local data

Group: **Account & privacy**

Label: **When signing out on this device**

Choices:
- **Keep downloads locked for this account** — recommended on personal devices;
- **Remove this account's offline content from this device**.

Rules:
- another profile/account can never see/play the retained content;
- changing server/origin or removing the account follows the canonical security invalidation policy;
- pending local progress/bookmark/learning events attempt safe synchronization when possible before destructive removal, but sign-out must not be blocked indefinitely by an unreachable server;
- the confirmation explains unsynced-local-state consequences if destructive removal is chosen while offline.

A managed/shared-device policy may force removal on sign-out; when forced, show the effective policy read-only.

## 27. Bulk storage actions

Group: **Storage management**

Actions:
- **Manage downloads**;
- **Remove Smart Offline content**;
- **Remove all offline content from this device**.

Optional category-specific clear:
- Remove downloaded Video;
- Remove downloaded Reading;
- Remove downloaded Audio.

Destructive actions show an explicit consequence summary:
- local copies only;
- server Library remains;
- canonical media remains;
- synced progress/bookmarks remain;
- unsynced queued state is handled according to the sync safety rules.

Do not call this "Clear cache" because offline downloads are intentional managed content, not disposable browser cache.

## 28. Offline sync behavior

There is no general "Sync offline progress" toggle.

When connectivity returns:
- progress;
- completion;
- bookmarks;
- supported annotations/learning events;
- other defined offline-first state

sync through their canonical owners.

Show a read-only status row only when useful:
- **All offline changes synced**
- **3 changes waiting for connection**
- **Sync needs attention**

Allow **Retry sync** for a real failure.

Do not expose queue IDs or technical payload counts beyond useful user context.

## 29. Download scheduling and concurrency

Jularr automatically manages safe concurrency, retry and backoff.

Do not expose:
- thread count;
- chunk size;
- worker count;
- HTTP connections.

A user-visible time-of-day download scheduler is not part of the baseline target unless a concrete product requirement is added later. Network/power/storage policy already provides the meaningful controls.

## 30. Per-download overrides

The normal Download-selection sheet can override for one download:
- scope (episode/season/chapter/volume/etc.);
- quality;
- audio-track package;
- subtitle package;
- image quality where relevant.

Overrides apply only to that logical download selection.

The Settings page controls defaults and policy. It must not grow into a media picker.

## 31. Capability-driven rendering

Every setting has one of three states:
1. supported -> interactive;
2. meaningful but externally forced -> read-only with effective value;
3. unsupported/not meaningful -> hidden.

Do not show disabled clutter for features the current client can never use.

Examples:
- PWA hides storage location/free-space reserve when unavailable;
- iOS/PWA may show limitations for background transfer instead of a fake switch;
- Android/Desktop can expose filesystem-backed controls;
- quality controls hide when only one rendition exists.

The same semantic setting names/behavior are used across clients where supported.

## 32. Failure and degraded states

### Storage location unavailable
Show:
- current location unavailable;
- affected downloads paused/unavailable;
- Reconnect/Retry;
- Change location where supported.

Do not silently delete inventory.

### Browser persistent storage not granted
Explain:
- offline data may be more vulnerable to browser eviction;
- request persistent storage again only when the browser supports it;
- do not claim a guarantee the browser does not provide.

### Limit exceeded
Existing explicit downloads remain.
New admission is blocked with actions:
- Manage downloads;
- Increase limit.

### Smart Offline cannot fit
Reduce/evict speculative content only.
Do not treat it as an error requiring the user to fix unless no useful plan can run for a prolonged reason.

## 33. Accessibility

- all settings have programmatic labels/descriptions;
- segmented options are keyboard and screen-reader operable;
- storage charts are never the only representation of values;
- destructive actions are not encoded by red alone;
- focus returns correctly after modal editors;
- status changes use restrained live-region announcements;
- touch targets follow shared mobile sizing;
- no auto-updating value causes layout/focus jumps.

## 34. Localization

All strings use the canonical Jularr localization catalog.

Use consumer language:
- Download;
- Offline available;
- Smart Offline;
- Storage limit;
- Wi-Fi/unmetered;
- Keep / Remove.

Do not expose implementation terms such as OPFS, WorkManager, ETag, manifest hash, rendition encoder or Storage Access Framework.

## 35. Approved visual composition

The approved direction is one **single consumer settings page**, not multiple mini-pages and not an Admin dashboard.

### Desktop composition

Use the normal Jularr shell with the existing left application navigation. The page body is a calm vertical stack:

1. **Page header + current-device summary**
   - heading **Offline-Einstellungen**;
   - short one-line description;
   - compact device identity;
   - storage used vs Jularr limit;
   - active download count when non-zero;
   - primary **Downloads verwalten / Manage downloads** button.

2. **Netzwerk & Hintergrund / Network & Background**
   - connection policy;
   - roaming;
   - background downloads;
   - charging preference for Smart Offline;
   - one restrained informational note for automatic battery-saver behavior.

3. **Qualität & Offline-Inhalte / Quality & Offline Content**
   - video quality;
   - audio quality;
   - Reading/image quality;
   - audio-track package;
   - subtitle package;
   - Learning data toggle.

4. **Speicher / Storage**
   - one compact storage visualization plus exact text values;
   - category breakdown;
   - Jularr offline limit;
   - free-space reserve;
   - storage location where supported;
   - storage-management action.

5. **Smart Offline**
   - master device toggle;
   - media-category toggles and ahead amounts;
   - Smart Offline budget;
   - Smart Offline video quality;
   - metered/mobile-data allowance;
   - replace-completed behavior;
   - explanatory line that explicit downloads have priority.

6. **Automatisierung & Updates / Automation & Updates**
   - explicit auto-cleanup;
   - Reading auto-update;
   - changed video/audio replacement policy.

7. **Konto, Sync & Benachrichtigungen / Account, Sync & Notifications**
   - sign-out retention policy;
   - sync status with details/retry only when relevant;
   - deep-link to normal download notification preferences.

8. **Gefahrenbereich / Danger zone**
   - remove Smart Offline content;
   - remove all offline content from this device.

Do not add an inner section sidebar. Do not scatter these sections into separate dashboard cards across three columns. Horizontal space is used inside each section only to keep labels and controls aligned and readable.

### Desktop density

The page may show several sections within one 19:9 mockup, but each section must read as one coherent settings block. Use generous whitespace, restrained borders and shared Clean Purple controls. Avoid oversized cards, decorative metrics and duplicated explanatory copy.

### Mobile composition

Order:

1. Back + **Offline-Einstellungen**;
2. current-device summary;
3. **Downloads verwalten**;
4. expandable **Netzwerk & Hintergrund**;
5. expandable **Qualität & Offline-Inhalte**;
6. expandable **Speicher**;
7. expandable **Smart Offline**;
8. expandable **Automatisierung & Updates**;
9. expandable **Konto, Sync & Benachrichtigungen**;
10. separated **Gefahrenbereich**.

The approved reference keeps Smart Offline expanded because it is the richest new product capability. This is a reference state, not a requirement that Smart Offline must always be the initially open accordion.

### Visual language

- Clean Purple light reference;
- neutral white/light-gray surfaces;
- purple used for selected state, toggles and primary actions;
- red reserved for destructive actions;
- standard Jularr outline icons;
- no gradients/glow;
- no Admin-table visual language;
- no cards nested inside cards unless a focused sub-control genuinely needs containment;
- charts always have textual values and never carry meaning alone.

Dark mode must use the same hierarchy and semantics even when the approved image is Light.

## 36. What is intentionally not configurable

Never add user controls for:
- checksum/hash verification;
- atomic finalization;
- retry backoff internals;
- progress conflict algorithm;
- account isolation;
- chunk sizes;
- concurrency worker count;
- cache/database implementation;
- whether corrupted content may be treated as Ready;
- whether canonical server media is deleted.

These are correctness/safety invariants.

## 37. Acceptance criteria

- [ ] The page describes the finished offline product, not only current implementation.
- [ ] Network policy supports unmetered/Wi-Fi-only and any connection, with roaming control where measurable.
- [ ] Native clients can expose background/power controls without fake browser equivalents.
- [ ] Video, audio and image-heavy Reading have truthful quality defaults when multiple renditions exist.
- [ ] Offline video packaging can define audio/subtitle inclusion without duplicating profile language preferences.
- [ ] Learning data can be included as a bounded offline package when Learning is enabled.
- [ ] Jularr offline limit is clearly distinct from device free space/browser quota.
- [ ] Native clients support a free-space safety reserve.
- [ ] Native clients can support a safe managed storage-location migration where the platform permits it.
- [ ] Smart Offline is fully specified: device opt-in, supported media categories, ahead amount, budget, quality, network/power constraints and eviction.
- [ ] Explicit downloads always outrank speculative Smart Offline content.
- [ ] Prefetched content can be kept/pinned and thereby protected from Smart Offline eviction.
- [ ] Explicit auto-cleanup is separate, opt-in and never removes pinned content.
- [ ] Reading package updates are differential.
- [ ] Large video/audio source replacements have an explicit update policy.
- [ ] Sign-out policy is explicit and never exposes one account's local content to another.
- [ ] Bulk removal clearly affects local copies only.
- [ ] Offline progress/bookmark/learning synchronization remains automatic and canonical.
- [ ] Unsupported settings are hidden rather than shown as dead controls.
- [ ] No Admin acquisition/downloader/NAS implementation details leak into the page.
- [ ] Desktop, mobile, PWA and native capability differences are intentional and understandable.
- [ ] Desktop renders as one continuous settings page with no inner section sidebar or dashboard grid.
- [ ] Mobile uses compact expandable sections with the same settings semantics rather than a squeezed Desktop layout.
- [ ] The current-device summary and Manage downloads action are visually distinct from the preference groups.
- [ ] Smart Offline is a normal settings section, not a separate mini-dashboard.
