# Download Selection — Shared Offline Download Dialog / Sheet

This specification defines the shared consumer flow used when a user starts an explicit offline download from a media detail/player/reader surface.

Approved visual references for this flow live in this folder. If image and text conflict, this specification wins.

Related:
- `docs/mockups/downloads-offline/SPEC.md` — current-device queue and offline inventory
- `docs/mockups/offline-settings/SPEC.md` — defaults and device policy
- `docs/mockups/player/SPEC.md`
- `docs/mockups/reading-detail/SPEC.md`
- issue #221 — Offline Library & Reader Sync
- issue #225 — bounded offline playback
- issue #415 — Smart Offline

## 1. Role

This surface configures **one explicit download action**.

It does not replace Offline Settings and it does not manage the queue after the download has started.

Typical flow:

`Detail / Player / Reader -> Download -> Download Selection -> Downloads & Offline`

The dialog/sheet may override defaults for this one logical download only.

## 2. Shared shell

Desktop:
- centered modal over the current media detail/player/reader context;
- approximately 720–800 px wide;
- one vertical information hierarchy;
- episode/chapter selection may scroll internally when needed;
- footer actions stay reachable.

Mobile:
- full-height or near-full-height bottom sheet;
- normal smartphone scale;
- same semantic order as Desktop;
- sticky summary/action footer.

Header:
- title **Offline herunterladen / Download for offline**;
- concise Work/context label;
- close action.

Do not turn the surface into a settings dashboard.

## 3. Scope selection

The first section answers **what is being downloaded**.

### Anime / TV
Depending on the source context:
- current episode;
- selected episodes;
- whole season.

Reference multi-select state:
- checkbox per episode;
- episode number + title;
- local/offline state when relevant;
- estimated size when known;
- selected count;
- Select all.

### Movie
No artificial scope picker. The Movie is the download unit.

### Book / Light Novel
- whole work;
- selected chapters;
- current chapter + optional following chapters where useful.

### Manga
- selected chapters;
- whole current volume when the structure supports it;
- whole work only when the resulting size/policy remains reasonable and the product explicitly supports it.

### Audiobook
- whole audiobook;
- selected chapters/tracks when canonical structure supports it.

Do not create separate dialog implementations per media type. The shell and semantics stay shared; only the scope selector changes.

## 4. Existing-local state

Each selectable unit may expose a restrained state:

- already offline;
- Smart Offline;
- update available;
- currently downloading;
- unavailable on this client.

Already-offline content is not silently downloaded again.

If selected:
- same package/quality -> reuse it;
- changed package/quality -> explain that it will be updated/redownloaded;
- Smart Offline -> allow **Offline behalten / Keep offline**, promoting it from speculative to explicit without re-downloading unchanged bytes.

## 5. Quality

Show quality only when the selected media/client exposes meaningful alternatives.

### Video
Default comes from Offline Settings.

Choices:
- Automatic / recommended;
- Original;
- High;
- Medium;
- Data saver.

Show a consumer-readable resolved summary when known, e.g.:
- `1080p · ca. 5.3 GB for 3 episodes`.

Do not expose codec/container/transcoder internals.

### Audio
For audiobook/audio-only content:
- Automatic;
- Original;
- High;
- Standard;
- Data saver.

### Manga / image-heavy Reading
- Optimized;
- Original.

For text-first Books/Light Novels, hide quality when it has no useful meaning.

## 6. Audio tracks

For video downloads, use Offline Settings as the default package policy.

Compact row:
- **Audio**
- resolved value, e.g. `Deutsch + Original`
- chevron opens a focused track selector only when the user wants to override it.

Per-download selector may choose available canonical tracks/languages.

Unavailable preferred tracks must not cause the entire download to fail when a valid package can still be produced.

## 7. Subtitles

Compact row:
- **Untertitel / Subtitles**
- resolved languages/policy;
- chevron opens a focused selector.

Use canonical subtitle tracks.

Do not expose provenance/codec implementation in the normal flow.

If image subtitles require special handling, Jularr resolves that through the package/playback capability contract.

## 8. Learning data

When Learning is enabled and supported for this content:

**Learning-Daten offline verfügbar** — uses the Offline Settings default.

The per-download toggle may override that default.

Bounded package may include:
- normalized cues;
- token reading/meaning data required by supported offline learning interactions;
- relevant current learning-state snapshot.

Do not copy unrelated global Learning data.

Hide the row when Learning is unavailable for this content/profile.

## 9. Smart Offline interaction

If one or more selected units already exist because of Smart Offline, show one concise informational block.

Example:
- **1 selected episode is already available through Smart Offline.**
- action: **Offline behalten / Keep offline**.

Keeping:
- changes ownership/origin from speculative to explicit;
- protects it from Smart Offline eviction;
- reuses unchanged local data;
- does not enqueue a duplicate download.

The dialog must never force a re-download solely to convert Smart Offline content into explicit content.

## 10. Storage summary

Before confirmation, show a simple summary:

- estimated new download size;
- resulting Jularr offline usage;
- configured Jularr offline limit;
- remaining capacity inside that limit.

When reliable, native clients may additionally show physical device free space separately.

Never confuse:
- download estimate;
- Jularr limit;
- browser quota;
- physical device free space.

No large analytics chart is needed in this dialog.

If size is not yet known, say so rather than inventing a number.

## 11. Admission and insufficient space

Before starting:
1. calculate/estimate selected package size where possible;
2. apply the current-device Jularr offline limit;
3. apply physical/browser storage constraints;
4. apply free-space safety reserve where supported.

If the selection does not fit:
- primary message **Nicht genügend Offline-Speicher / Not enough offline storage**;
- offer **Downloads verwalten**;
- offer **Offline-Einstellungen**;
- keep the user's current selection intact.

Do not automatically delete explicit downloads to make space.

Smart Offline speculative content may be evicted only through its canonical policy.

## 12. Network state

Show the effective connection rule near the bottom.

Normal examples:
- **Nur WLAN / ungemessen**
- **Jede Verbindung**

If the current connection is blocked by policy:
- do not show a hard error;
- explain that the download will be queued and starts when an eligible connection is available.

Where policy permits, an explicit one-time override may be offered:
- **Einmalig jetzt herunterladen**.

Smart Offline never gets this override from this explicit-download sheet.

## 13. Primary action

Primary button text should describe the actual action.

Examples:
- **Episode herunterladen**
- **3 Episoden herunterladen**
- **Staffel herunterladen**
- **Film herunterladen**
- **Buch herunterladen**
- **8 Kapitel herunterladen**
- **Hörbuch herunterladen**
- **Aktualisieren** when the selection is purely an update.

Avoid generic **OK** or ambiguous **Save**.

Footer also provides:
- Cancel;
- Offline Settings deep-link where useful.

Mobile sticky footer may show:
- estimated size;
- concrete primary action.

## 14. Download creation semantics

Confirming the dialog:
1. resolves the canonical package/media identity;
2. applies per-download overrides;
3. applies admission/network policy;
4. reuses already-valid local package parts;
5. promotes selected Smart Offline items to explicit where applicable;
6. creates or updates one logical local download selection;
7. closes the sheet/modal;
8. exposes progress through the global download indicator and Downloads & Offline manager.

Repeated confirmation must not create duplicate logical jobs.

## 15. Update semantics

When the selected content is already offline but stale:

Structured Reading:
- differential update only changed/new chapters/assets where supported.

Video/audio replacement:
- never splice two source versions;
- use the canonical update/redownload rule;
- retain the previous verified copy until the replacement safely finalizes where possible.

The dialog may say **Update available** and summarize the replacement size.

## 16. Error/degraded states

The same surface can render:
- source temporarily unavailable;
- storage unavailable;
- no offline rendition available;
- insufficient space;
- permission lost;
- browser storage not persistent.

Use concise recovery actions.

Do not expose:
- HTTP status;
- ETag/hash;
- server filesystem paths;
- downloader/indexer details.

## 17. Accessibility

- modal/sheet traps focus appropriately while open;
- close returns focus to the original Download trigger;
- episode/chapter selectors have proper labels and selected state;
- progress/storage bars have text equivalents;
- checkboxes, toggles, selects and buttons are keyboard accessible;
- mobile touch targets use shared sizing;
- state is not communicated by color alone.

## 18. Localization

All visible text uses the normal Jularr localization catalog.

The same semantic terms must match:
- Download;
- Offline available;
- Smart Offline;
- Keep offline;
- Update;
- Offline Settings.

Do not hardcode media-specific German/English labels in page-local code.

## 19. Explicitly not part of this dialog

Do not add:
- queue pause/resume/retry management;
- global Smart Offline policy;
- storage-location migration;
- account sign-out policy;
- notification configuration;
- download scheduler;
- bandwidth/concurrency controls;
- server acquisition/release selection;
- NAS paths;
- codec/container controls.

Those belong to their existing owners.

## 20. Acceptance criteria

- [ ] One shared dialog/sheet supports Anime, TV, Movie, Book/LN, Manga and Audiobook explicit downloads.
- [ ] Anime/TV supports current episode, multi-episode and season scope where available.
- [ ] Reading supports whole-work or selected-unit scope according to canonical structure.
- [ ] Quality appears only when meaningful.
- [ ] Audio/subtitle package defaults come from Offline Settings and can be overridden for one download.
- [ ] Learning-data inclusion can be overridden when supported.
- [ ] Existing explicit offline content is reused instead of blindly duplicated.
- [ ] Smart Offline content can be promoted to explicit without duplicate download.
- [ ] Storage and network consequences are clear before confirmation.
- [ ] Insufficient space does not silently delete explicit downloads.
- [ ] Confirmation creates/updates one logical local job.
- [ ] Desktop uses a focused modal and Mobile uses a full-height/near-full-height sheet.
- [ ] Primary action text names what will actually be downloaded.
- [ ] No Admin downloader/acquisition internals leak into the consumer UI.
