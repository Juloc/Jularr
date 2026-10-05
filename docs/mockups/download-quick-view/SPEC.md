# Global Download Indicator + Quick View

This specification defines the global consumer download status control and its compact Quick View.

Approved visual references for this surface live in this folder. If image and text conflict, this specification wins.

Related:
- `docs/mockups/downloads-offline/SPEC.md` — full current-device download/offline manager
- `docs/mockups/offline-settings/SPEC.md` — device/download policy
- `docs/mockups/download-selection/SPEC.md` — per-download creation flow
- `docs/mockups/notifications-center/SPEC.md` where present — notification/inbox history
- issue #221 — Offline Library & Reader Sync
- issue #225 — bounded offline playback
- issue #415 — Smart Offline

## 1. Product role

The global Download Indicator answers one question from anywhere in the authenticated app:

**Do my current-device downloads need attention, and what are they doing right now?**

Selecting it opens a compact Quick View for immediate operational actions.

It is not:
- the full offline inventory;
- a second queue owner;
- a settings page;
- download history;
- Admin acquisition/downloader state;
- a replacement for notifications.

The full manager remains `Downloads & Offline`.

## 2. Canonical ownership

The indicator and Quick View are presentation over the same current-device local download state used by:
- explicit offline downloads;
- structured Reading offline packages;
- playable media offline packages;
- Smart Offline prefetched items.

They must not maintain a separate durable queue, badge database or progress model.

On reload/restart, visible state is reconstructed from canonical local download/package state.

## 3. App-shell placement

### Desktop / wide Web

Place the Download Indicator in the global top bar near the right-side utility controls.

Preferred order:

`Search ... | Download Indicator | Notifications | Profile`

Keep Downloads separate from the Notification Bell:
- Downloads = live operational state;
- Notifications = event/inbox history.

### Mobile

Place the indicator in the normal top app bar when space permits.

Preferred conceptual order:

`Menu / Logo | utilities | Download Indicator | Notifications | Profile`

Do not add a permanent Downloads item to the bottom navigation solely for this feature.

### Fullscreen media

The global indicator does not force the normal shell into fullscreen Player/Reader modes.

Downloads continue in the background; the shell shows the current state again when the user returns.

## 4. Visibility rules

Show the global Download Indicator while at least one relevant item is in any of these states:

- Queued;
- Downloading;
- Paused;
- Waiting for connection;
- Waiting for Wi-Fi / unmetered network;
- Verifying / Preparing;
- Failed / Needs attention;
- recently completed and still relevant to surface.

Do not keep it visible merely because the device has ready offline content.

When there are:
- no active jobs;
- no failed/attention jobs;
- no recent completion state to surface;

the indicator may disappear entirely.

## 5. Badge count

The badge shows the number of **unfinished logical local Offline/install units requiring current queue attention**.

Examples:
- 3 episodes in progress/queue -> `3`;
- one Movie -> `1`;
- 5 selected chapters -> `5`.

A whole-season batch counts its child episode units, not a synthetic `1 season`, because the actual work/recovery state is per downloadable unit.

Do not count:
- Ready items;
- historical completions;
- already-ready Smart Offline items.

Failed items remain counted until resolved/removed because they still require attention.

## 6. Indicator visual states

Use one shared Download icon with restrained state treatment.

### Active

- normal download icon;
- purple numeric badge;
- optional subtle progress ring only when a truthful aggregate is available.

### Waiting

- download icon;
- small waiting/Wi-Fi/clock semantic marker where useful;
- no error-red treatment.

### Paused

- subtle pause marker.

### Failed / needs attention

- red attention dot/badge treatment in addition to the count;
- do not replace the icon with a large warning symbol.

### Recently completed

- brief success/check treatment is allowed;
- do not permanently retain a success badge.

No decorative looping animation.

## 7. Aggregate progress

An optional ring/aggregate value is allowed only when it is mathematically meaningful.

For byte-backed items with known totals:

`sum downloaded known bytes / sum expected known bytes`

Do not calculate aggregate progress by averaging arbitrary item percentages.

Items without a meaningful denominator must not distort the aggregate.

If no trustworthy aggregate is possible, show icon + badge only.

## 8. Open behavior

### Desktop

Selecting the indicator opens an anchored popover below/near the icon.

Target width:
- approximately 380–440 px;
- enough for title, status, progress and primary action without becoming a second page.

### Mobile

Selecting the indicator opens a bottom sheet.

Target:
- approximately 65–80% of viewport height depending on content;
- draggable/expandable when supported by the shared sheet system.

The Quick View should feel immediate and lightweight.

## 9. Quick View header

Header:

**Downloads**

Summary line examples:
- `3 aktiv · 1 wartet`
- `2 aktiv · 1 Fehler`

Optional secondary header action:
- **Alle pausieren** when multiple runnable transfers exist;
- **Alle fortsetzen** when all relevant transfers are paused.

Do not put storage analytics in the header.

A settings icon may deep-link to Offline Settings only if it remains clearly secondary.

## 10. Priority order

Sort visible Quick View entries by user attention:

1. Failed / Needs attention;
2. actively Downloading;
3. Waiting for Wi-Fi / connection;
4. Paused;
5. Queued;
6. Verifying / Preparing;
7. recently Completed.

Within the same state, use the owning queue's stable order.

Do not let Smart Offline speculative work push explicit user downloads below it.

## 11. Maximum visible content

Desktop:
- approximately 5 visible rows/groups.

Mobile:
- approximately 5–7 visible rows/groups depending on screen height.

Do not turn the Quick View into an indefinitely scrolling mini-manager.

Footer always provides:

**Alle Downloads anzeigen / View all downloads**

which opens the full `Downloads & Offline` manager.

## 12. Standard item row

A normal row may contain:

- artwork/cover when useful;
- Work title;
- concrete unit label;
- state;
- progress bar when meaningful;
- downloaded/total size when known;
- speed/ETA only when measured reliably;
- one directly reachable primary action;
- overflow menu for secondary/destructive actions.

Example:

**Atomic Habits**  
`Kapitel 5 · 126 / 146 MB · 86%`

Primary action:
- Pause.

Do not expose release names, file paths, indexers, downloader clients, hashes or HTTP details.

## 13. Primary inline actions by state

| State | Direct action |
| --- | --- |
| Downloading | Pause |
| Paused | Resume |
| Failed | Retry |
| Waiting | normally none |
| Queued | normally none |
| Verifying | none |
| Recently completed | Open / Play / Read when useful |

Destructive actions such as Cancel/Remove should normally stay in overflow.

## 14. Overflow actions

### Downloading
- Pause;
- Cancel.

### Paused
- Resume;
- Cancel.

### Failed
- Retry;
- Remove.

### Waiting
- Cancel;
- optional one-time network override only when the policy supports it.

### Smart Offline
- Keep offline / Pin;
- Pause/Resume where applicable;
- Remove.

### Completed
- Open / Play / Read;
- Show in Downloads;
- Remove offline copy.

Only render actions valid for the item's real state/capabilities.

## 15. Batch / Work grouping

When multiple units from the same Work are active, the Quick View may group them.

Reference group:

**Frieren: Beyond Journey’s End**  
`3 Episoden · 2.8 / 5.3 GB · 68%`

State summary:
`E07 lädt · E08 wartet · E09 in Warteschlange`

Group primary action:
- Pause all / Resume all when meaningful.

Chevron expands to child units.

Grouping is presentation only. It must not create a new durable SeasonDownload/SeriesDownload owner.

## 16. Expanded group children

Expanded children remain compact:

- `E07 · Like a Fairy Tale · 78%`
- `E08 · Together Again · Wartet auf WLAN`
- `E09 · Aura the Guillotine · Warteschlange`

Per-unit actions remain available.

The group summary recalculates from real child state.

## 17. Smart Offline

Smart Offline uses the exact same Quick View.

When useful, mark an item/group with a restrained **Smart Offline** badge.

Rules:
- no separate Smart Offline queue;
- explicit downloads have higher operational priority;
- a Smart Offline item can be promoted with **Offline behalten / Keep offline**;
- promotion reuses valid local bytes and does not create a duplicate download.

Ready Smart Offline items do not keep the global indicator visible by themselves.

## 17.1 Games

Games may participate in the same Quick View through the Games-owned install/package adapter.

A row may show:
- Downloading;
- Installing;
- Verifying;
- Waiting/Paused;
- Failed;
- Update available;
- recently completed/Ready transition.

Actions delegate to the Games owner:
- Pause / Resume;
- Retry;
- Show in Downloads & Offline;
- open Game Detail;
- Play/Continue after Ready where useful.

The Quick View must not:
- own Game install state;
- expose ROM paths, BIOS paths or runtime internals;
- count an external launcher installation as a Jularr download;
- reuse MediaProgress for Games.

Games operations follow normal explicit-work priority. Predictive Smart Offline does not schedule Games by default.

## 18. Waiting for Wi-Fi / unmetered network

State text:

**Wartet auf WLAN**  
or the platform-equivalent **Wartet auf ungemessene Verbindung**.

Secondary copy:
`Startet automatisch bei geeigneter Verbindung.`

This is not a failure.

Do not show Retry.

If product policy supports a one-time explicit override, expose it in overflow only.

## 19. No connection

State:

**Wartet auf Verbindung**

The local job remains present.

When connectivity becomes eligible again:
- the canonical downloader resumes according to its policy;
- the row updates automatically.

Do not convert temporary offline state into a permanent error.

## 20. Failed state

Reference:

**Kaiju No. 8 · S1 E01**  
**Download fehlgeschlagen**

Consumer-safe reason:
`Verbindung zum Server fehlgeschlagen.`

Direct action:
- **Erneut versuchen**

Overflow:
- Remove.

Never surface:
- stack traces;
- status codes;
- storage paths;
- fingerprints/hashes;
- downloader/indexer internals.

## 21. Storage admission failure

State:

**Nicht genügend Offline-Speicher**

Direct action:
- **Speicher verwalten**

Secondary:
- **Offline-Einstellungen**

The Quick View never automatically deletes explicit downloads.

Smart Offline speculative copies may only be evicted through their canonical policy.

## 22. Storage location unavailable

Native clients may expose:

**Speicher nicht verfügbar**

Actions:
- Retry/Recheck;
- Manage storage.

Affected jobs remain paused/unavailable according to canonical local state.

Do not silently duplicate the download into a different storage location.

## 23. Verifying / preparing

After transfer completion but before final validation:

**Wird überprüft …**

Do not present the item as:
- 100% ready;
- playable offline;
- completed;

until canonical verification/finalization succeeds.

## 24. Recently completed

A successful item may remain briefly in the Quick View:

**Offline verfügbar ✓**

Action:
- Play / Read / Open.

It disappears from Quick View after it has been surfaced/seen or after the short relevance window expires.

The complete ready item remains in `Downloads & Offline -> Offline verfügbar`.

The Quick View is not a completion history.

## 25. Completion retention semantics

The UX contract does not require a hard visible timer.

Required behavior:
- newly completed items are surfaced briefly;
- once no longer operationally relevant, they leave Quick View;
- the full manager remains the durable inventory view.

Do not keep weeks of completed entries in the popover.

## 26. Pause all / resume all

When multiple transfers are active, the header may offer:

**Alle pausieren**

When the relevant jobs are all paused:

**Alle fortsetzen**

Semantics:
- applies to local transfers on this device;
- includes Smart Offline transfer work when it is part of the same queue;
- does not alter Ready/Completed content;
- does not override connection/power eligibility after Resume.

No separate Smart Offline global pause button is needed inside Quick View.

## 27. Queue priority

Display the real operational ordering.

Baseline:
1. explicit user download;
2. explicit update;
3. Smart Offline speculative transfer.

Do not expose manual drag-and-drop queue prioritization in Quick View.

Promoting Smart Offline to explicit may increase its queue priority according to the canonical owner.

## 28. Item navigation

Selecting a row/group outside its action controls:

### Active/failed/waiting item
Open/focus the corresponding item in `Downloads & Offline`.

### Recently completed item
Open the normal consumer destination when practical:
- Player;
- Reader;
- media detail.

Action buttons must stop propagation and must not also navigate.

## 29. Footer

Required primary footer action:

**Alle Downloads anzeigen**

-> opens `Downloads & Offline`.

Optional secondary:
- **Offline-Einstellungen**

Settings must remain secondary to the manager link.

## 30. Empty transition state

If the Quick View is already open and the final active/attention item resolves:

**Keine aktiven Downloads**

Optional helper:
`Deine Offline-Inhalte findest du unter Downloads & Offline.`

Action:
- Open Downloads & Offline.

After closing, the global indicator may disappear.

## 31. Offline behavior

Quick View is local-state-first.

Without server connectivity it can still show:
- queue state;
- Paused;
- Waiting;
- Failed local state;
- Ready/recent completion;
- Smart Offline origin;
- local progress already persisted.

Do not replace the entire Quick View with a generic network error.

Server-dependent actions degrade individually.

## 32. Restart recovery

After app/browser/process restart:

- indicator count comes from persisted canonical local state;
- paused/waiting/failed items restore correctly;
- a previous visual 100% is not treated as Ready unless finalization persisted;
- no badge state is reconstructed from stale DOM/UI memory.

## 33. Profile/account isolation

The indicator and Quick View show only the active account/profile's accessible local downloads.

On profile/account switch:
- close the old Quick View;
- rebuild badge/count from the new active owner;
- never briefly expose prior-profile titles/artwork/state.

## 34. Relationship to Notifications

Downloads and Notifications remain separate surfaces.

A failed/completed download may generate a notification, but:

**Download Quick View**
- live operational state;
- pause/resume/retry;
- current queue.

**Notification Center**
- event/inbox history;
- read/unread state;
- informational follow-up.

Do not merge them into a single durable UI model.

## 35. Platform behavior

### PWA / Web
- anchored popover on Desktop;
- bottom sheet on Mobile;
- truthful browser background limitations.

### Android
- same in-app semantics;
- Android foreground/system download notification may exist in parallel;
- system notification does not replace the in-app Quick View.

### Future native desktop
- same UX contract over the native local download adapter.

## 36. Accessibility

- icon accessible label includes current status/count, e.g. **4 aktive Downloads**;
- badge count is not color-only;
- status icons have text equivalents;
- progress bars expose semantic values when known;
- frequent progress updates are throttled for assistive technology;
- new attention/failure state may use a polite live region;
- Desktop popover is keyboard reachable;
- Escape closes it;
- focus returns to the Download Indicator;
- mobile sheet follows shared focus/touch rules.

## 37. Visual direction

Approved reference direction:
- Jularr Clean Purple light theme;
- Desktop app/home context in background with anchored Downloads popover;
- Mobile app context with Downloads bottom sheet;
- indicator visibly carries an active count;
- approximately four representative items in the open Quick View:
  - grouped active Anime/TV batch;
  - Waiting for Wi-Fi;
  - active Reading download;
  - Failed item;
- footer **Alle Downloads anzeigen**;
- Offline Settings may appear as secondary mobile action.

The Quick View must remain visually lighter and smaller than the full Downloads & Offline manager.

Dark mode follows the same hierarchy.

## 38. Explicitly out of scope

Do not place in Quick View:
- storage charts;
- storage-location editor;
- quality selection;
- audio/subtitle selection;
- Learning package configuration;
- Smart Offline policy editor;
- notification preferences;
- full download history;
- Admin acquisition/download state;
- releases/indexers;
- NAS/storage-root diagnostics;
- codec/container settings;
- queue drag-and-drop;
- bandwidth/concurrency tuning.

## 39. Acceptance criteria

- [ ] A global current-device Download Indicator appears only while operationally relevant.
- [ ] Badge count represents unfinished logical download units and includes failed attention items.
- [ ] Desktop opens a compact anchored popover.
- [ ] Mobile opens a compact bottom sheet.
- [ ] Quick View shows at most a small current subset and links to the full manager.
- [ ] Failed items are prioritized above normal active work.
- [ ] Active items support direct Pause; Paused supports Resume; Failed supports Retry.
- [ ] Destructive Cancel/Remove stays secondary/overflow.
- [ ] Multiple units from one Work may group without creating a new domain owner.
- [ ] Smart Offline uses the same Quick View and can be promoted to explicit/Keep offline.
- [ ] Games-owned managed install/update operations can project into the same Quick View without creating a Games queue inside the component.
- [ ] Waiting for Wi-Fi/connection is not presented as an error.
- [ ] Verifying is distinct from Ready.
- [ ] Recent completions are temporary in Quick View and remain in the full offline inventory.
- [ ] Quick View remains useful without server connectivity.
- [ ] Restart/profile-switch behavior is reconstructed from canonical local state and preserves isolation.
- [ ] Notifications remain a separate event/history concern.
- [ ] No Admin downloader/acquisition internals appear.
