# Offline Storage Location & Migration

This specification defines the consumer flow for changing the current device's managed offline-storage location and safely moving existing offline content.

The approved mockup for this flow belongs in this folder next to this specification. If image and text conflict, this specification wins.

Related:
- `docs/mockups/offline-settings/SPEC.md` — storage policy and entry point
- `docs/mockups/downloads-offline/SPEC.md` — current-device offline inventory
- `docs/mockups/download-quick-view/SPEC.md` — live download state
- `docs/mockups/download-notifications/SPEC.md` — completion/failure feedback
- `docs/OFFLINE_LIBRARY.md`
- `docs/ANDROID_CLIENTS.md`

## 1. Product role

This flow is opened from:

`Offline Settings -> Storage -> Storage location -> Change`

Its purpose is to:

1. select a supported managed storage destination;
2. validate that destination;
3. decide what happens to existing offline content;
4. safely migrate content when requested;
5. preserve recoverability across interruption/restart.

It is not:
- server LibraryRoot/NAS administration;
- a generic file manager;
- a media import flow;
- a queue/download-selection dialog;
- a cache-directory picker for arbitrary internal app data.

The flow only manages **client-local Jularr offline content on the current device**.

## 2. Platform capability

Render the control only when the current client can safely support managed storage selection.

Examples:
- Android internal app-managed storage;
- supported app-specific removable/external storage;
- future native Windows/macOS managed folder.

PWA/browser clients hide this flow when they cannot provide reliable filesystem-location semantics.

Do not expose a non-functional storage picker simply for visual parity.

## 3. Entry state

Offline Settings shows the current location and, when reliable:
- Jularr-managed offline usage;
- available capacity;
- action **Change location / Ändern**.

Example:

**Internal storage**  
`18.4 GB Jularr content · 236 GB free`

Selecting **Change location** opens the focused migration dialog/sheet.

## 4. Responsive shell

### Desktop

Use a centered modal over Offline Settings.

Target:
- approximately 640–760 px wide;
- single vertical flow;
- no secondary navigation inside the modal;
- footer actions remain visible.

### Mobile

Use a near-full-height bottom sheet or full-screen sheet according to the shared mobile sheet primitive.

Keep:
- title;
- destination list;
- validation state;
- existing-content decision;
- storage summary;
- sticky footer.

Do not squeeze the Desktop settings sidebar into the mobile sheet.

## 5. Header

Title:

**Offline-Speicherort ändern / Change offline storage location**

Description:

`Wähle aus, wo Jularr Offline-Inhalte auf diesem Gerät speichern soll.`

Desktop:
- close icon in the top-right.

Mobile:
- sheet drag handle where supported;
- close/back according to the shared sheet component.

Closing before confirmation leaves current storage unchanged.

## 6. Destination list

Show only destinations that the client can actually manage safely.

Each destination row may include:
- storage-type icon;
- user-facing label;
- reliable available capacity;
- current-location marker;
- selection radio.

Reference entries:

**Interner Speicher**  
`236 GB frei · 18.4 GB verwendet`  
badge: **Aktuell verwendet**

**SD-Karte**  
`118 GB frei`

**Anderen Ordner auswählen …**  
`Einen benutzerdefinierten Speicherort wählen.`

Do not expose:
- raw mount identifiers;
- UUIDs;
- server/NAS paths;
- Linux/Android internal implementation paths;
- removable-volume technical metadata unless needed in diagnostics.

## 7. Current destination

The current storage location:
- remains visible;
- is clearly labeled **Aktuell verwendet / Currently used**;
- cannot cause a migration when reselected unchanged.

If the user selects the current destination, primary action remains disabled or resolves to **Cancel/Done** with no write.

## 8. Custom folder

A custom folder option is available only on clients with a safe native folder-access model.

The native platform picker owns raw filesystem navigation.

Jularr receives/retains the minimum durable access descriptor required by the platform.

After selection, Jularr returns to this dialog and validates the destination.

Do not build a second custom file browser inside Jularr.

## 9. Automatic destination validation

Selecting a new destination triggers validation before confirmation.

Validate where the platform exposes the information:
- destination is reachable;
- access permission is valid;
- destination is writable;
- destination is supported for durable managed offline content;
- effective free space is sufficient;
- configured device-space safety reserve can still be honored;
- Jularr can persist/recover access after restart where required.

While checking:

**Speicher wird geprüft … / Checking storage …**

Do not enable the final migration action until required validation has resolved.

## 10. Validation success

Show a compact success block:

**Speicherort kann verwendet werden**

`Der ausgewählte Speicher ist verfügbar und beschreibbar.`

This is a capability validation, not proof that every future write can never fail.

## 11. Validation failure

Examples:

### No permission

**Kein Zugriff auf diesen Speicherort**

Action:
- **Zugriff erneut erlauben**
- or choose another location.

### Read-only

**Speicherort ist schreibgeschützt**

Action:
- choose another location.

### Unsupported location

**Dieser Speicherort kann für Offline-Inhalte nicht verwendet werden**

Action:
- choose another location.

### Destination disappeared

**Speicher nicht verfügbar**

Action:
- **Erneut prüfen**
- choose another location.

Do not surface raw filesystem exceptions.

## 12. Existing offline content decision

When existing managed offline content is present, show:

**Vorhandene Offline-Inhalte**

Preferred/default option:

### **Vorhandene Downloads verschieben (empfohlen)**

Summary:

`18.4 GB werden zum neuen Speicherort übertragen.`

Safety copy:

`Die bisherigen Kopien werden erst gelöscht, nachdem die neuen Dateien erfolgreich überprüft wurden.`

Optional alternative, only when the implementation can safely support multiple managed locations:

### **Nur neue Downloads dort speichern**

Summary:

`Bestehende Offline-Inhalte bleiben am bisherigen Speicherort.`

Do not render this second option unless the runtime can:
- resolve content across both locations;
- preserve access after restart;
- report storage truthfully;
- clean up each location safely.

If multi-location operation is not a supported canonical capability, the migration flow exposes **move existing content** only.

## 13. No existing offline content

If no managed offline content exists:
- hide the existing-content decision;
- show the destination validation only;
- primary action becomes:

**Speicherort verwenden / Use storage location**

No fake zero-byte migration step is needed.

## 14. Migration size summary

Before confirmation, show a concise summary:

**Speicherübersicht**

- **Zu verschiebende Daten:** `18.4 GB`
- **Am Ziel verfügbar:** `118 GB`
- **Nach Verschieben frei:** `99.6 GB`

Values must be derived from real local metadata/platform capacity where available.

If the exact migration size is not yet known, use an honest estimate label:

`ca. 18.4 GB`

Do not fabricate precision.

## 15. Temporary migration overhead

Admission must account for migration semantics.

Because the old verified copy is retained until the new copy is verified, the destination must have enough capacity for:
- all bytes being moved;
- temporary/finalization overhead when required;
- configured safety reserve.

Do not assume the old copy can be deleted first to make the new destination fit.

## 16. Insufficient capacity

When the destination cannot safely accept the migration:

**Nicht genügend Speicherplatz**

Example:

`Für den Umzug werden weitere 6.2 GB benötigt.`

Primary:
- **Anderen Speicher wählen**

Secondary:
- **Downloads verwalten** where reducing current offline content can resolve the issue.

Do not permit confirmation by ignoring the safety reserve.

## 17. Confirmation

Desktop footer:

- **Abbrechen**
- primary: **18.4 GB verschieben**

Mobile sticky footer:
- Cancel;
- same concrete primary action.

Do not use vague labels such as:
- Save;
- OK;
- Apply.

The button describes the actual consequence.

When no content must move:
- **Speicherort verwenden**.

## 18. Migration safety model

The required logical sequence is:

1. retain old verified local copy;
2. create/copy the new managed copy;
3. validate the new copy using the owning offline package/file verification rules;
4. atomically switch the authoritative local-location reference;
5. only then remove the old verified copy;
6. persist completion state.

Never use:

`delete old -> attempt copy -> hope`

A failed migration must leave the last verified usable copy intact whenever physically possible.

## 19. Per-item/package integrity

Migration operates through the existing canonical offline package ownership.

For Games, migration delegates to the Games-owned managed install/package owner (#851). The shared storage flow coordinates destination choice/progress only; it does not move ROM/disc/install resources behind the Games owner's back.

It must not:
- create a second Work/Edition/Asset identity;
- reset MediaProgress/Reading progress;
- duplicate bookmarks;
- create a second logical download;
- alter explicit-vs-Smart-Offline ownership.

Only the local storage location changes.

## 20. Migration execution

After confirmation, the modal/sheet closes.

Do not block the user behind a long-running modal.

Offline Settings may show a compact operation row:

**Offline-Inhalte werden verschoben**

`7.2 von 18.4 GB`

Progress bar

`Neuer Speicher: SD-Karte`

Optional action:
- **Details**

The user can navigate elsewhere while migration continues, subject to platform execution capability.

## 21. Global Download Indicator relationship

Storage migration is **not a media download**.

It must not inflate the global Download Indicator badge as if 12 episodes were downloading.

If the product later needs a global background-operation indicator, migration can participate in that separate shared primitive.

For this target contract, migration status is exposed through:
- Offline Settings;
- relevant storage/error notifications;
- Downloads & Offline when a local item's availability is affected.

## 22. New downloads during migration

Canonical behavior:

- do not race writes blindly across old/new roots;
- after migration confirmation, new admission uses the new destination only once that destination is activated safely by the storage owner;
- existing content being moved keeps its old verified copy usable until cutover;
- if activation must wait for a transaction boundary, UI may show **Speicherwechsel wird vorbereitet**.

Implementation must define one deterministic write owner at any point in time.

## 23. Playback/reading during migration

Whenever safe:
- currently verified old copies remain playable/readable;
- active Player/Reader sessions should not be interrupted merely because another item is moving.

For an item currently being migrated:
- either keep the old verified source active until new verification/cutover;
- or defer that item's move until the active read/playback handle is released.

Do not invalidate an active session by deleting its backing file prematurely.

## 24. Pause/interruption

Migration may pause because of:
- destination removal;
- app/platform execution constraints;
- power policy;
- access permission loss;
- transient I/O failure.

State remains durable.

Do not silently restart from zero if already verified copied units can be safely reused.

## 25. Destination removed during migration

If removable/external storage disappears:

**Speicher nicht verfügbar**

`Die Verschiebung wurde pausiert. Deine ursprünglichen Offline-Inhalte bleiben erhalten.`

Actions:
- **Erneut prüfen**
- **Speicherort ändern**

Do not:
- delete the original;
- mark the migration successful;
- silently redirect all remaining content to internal storage.

## 26. App/device restart recovery

After restart:
- recover migration state from durable local metadata;
- identify old verified copy vs candidate new copy;
- resume or repair deterministically;
- never treat an unverified candidate as authoritative;
- clean orphaned temporary files only after ownership is resolved.

The UI must not depend on an in-memory modal state to know whether migration was running.

## 27. Verification failure

If copied bytes fail verification:

**Verschiebung konnte nicht abgeschlossen werden**

`Die neue Kopie konnte nicht überprüft werden. Deine bisherige Offline-Kopie bleibt erhalten.`

Actions:
- **Erneut versuchen**
- **Anderen Speicher wählen**

Technical details belong in diagnostics/logs, not the consumer dialog.

## 28. Source-location failure during migration

If the old/source location disappears before a unit has been safely copied:
- do not claim that unit is preserved;
- show the affected item as **Storage unavailable / Needs attention**;
- recover from the new copy only if that candidate independently verifies.

If neither valid copy is available:
- item is not Ready;
- offer re-download/repair when possible.

## 29. Successful completion

When all intended content has switched safely:

Toast:

**Offline-Speicher verschoben**

`18.4 GB befinden sich jetzt auf der SD-Karte.`

Offline Settings updates to:

**Speicherort**  
`SD-Karte · 99.6 GB frei`

The old location is no longer considered the active managed root after its migrated content has been safely removed.

## 30. Partial migration with multi-location mode

Only applies when **Only store new downloads there** or intentionally split storage is supported.

The storage owner must maintain explicit location metadata per local package/file.

For Games, location metadata remains Games-owned and must keep multi-file/multi-disc releases logically consistent.

The UI must be able to truthfully explain that existing content remains on the previous location.

Do not present a single-location label if content is intentionally split and the product cannot explain/manage that split.

If this complexity is not implemented, do not expose multi-location mode.

## 31. Removing the old location after split mode

If multi-location mode exists and the user later wants to remove/deauthorize the old location:
- Jularr must identify content remaining there;
- offer Move or Remove offline copies;
- do not silently orphan packages.

This can reuse this migration flow.

## 32. Storage-limit semantics

Changing physical/local storage location does not change:
- Jularr offline storage limit;
- Smart Offline budget;
- safety-reserve policy;
- media quality defaults.

Those remain owned by Offline Settings.

The migration admission calculation uses the current effective policies.

## 33. Smart Offline

Smart Offline packages migrate through the same storage owner.

Rules:
- explicit/pinned status is preserved;
- prefetched origin is preserved;
- migration does not promote/demote content;
- speculative content may be evicted by its normal storage policy before/after migration only when policy allows.

Do not use migration as an excuse to preferentially delete explicit content.

## 34. Account/profile isolation

The migration operation must preserve local account/profile ownership.

Changing storage location must not:
- expose another profile's titles;
- merge package manifests;
- reuse another account's protected local copy without explicit supported deduplication semantics.

Any shared-byte optimization remains below the consumer identity boundary.

## 35. Sign-out during migration

If sign-out occurs while migration is active:
- apply the existing offline sign-out retention policy;
- resolve/pause migration safely before destructive cleanup;
- never leave half-switched package references.

The storage-location flow does not invent a second sign-out policy.

## 36. Permissions

If a platform-granted directory/storage permission is later revoked:
- mark the location unavailable;
- preserve metadata;
- offer **Zugriff erneut erlauben** or **Speicherort ändern**;
- do not report the content as Ready until access is restored/verified.

## 37. Notification behavior

Do not create progress notification spam.

Relevant events:
- successful migration -> one completion toast;
- action-required pause/failure -> Bell/system notification according to central notification settings;
- temporary recoverable I/O retry -> normally silent.

Reuse the global consumer notification routing rules.

## 38. Accessibility

- modal/sheet has a clear title and focus trap;
- destination rows have proper radio semantics;
- current-location badge is exposed to assistive technology;
- capacity summaries have text equivalents;
- validation success/error is announced without relying on color;
- primary action includes the migration size/consequence;
- Escape/close returns focus to the originating **Change location** control;
- mobile touch targets follow shared minimum sizing.

## 39. Localization

All visible copy uses Jularr's localization catalog.

Capacity formatting is locale aware.

Use the same semantic storage terms across:
- Offline Settings;
- Downloads & Offline;
- storage alerts;
- this migration flow.

## 40. Visual direction

Approved reference direction:
- Jularr Clean Purple Light;
- Desktop Offline Settings in the background with a centered migration modal;
- Mobile matching bottom/full-height sheet;
- three destination examples:
  - Internal storage, marked current;
  - selected SD card;
  - custom folder;
- successful validation block;
- **Vorhandene Downloads verschieben (empfohlen)** selected;
- optional **Nur neue Downloads dort speichern** alternative;
- compact storage summary;
- primary action **18.4 GB verschieben**.

The reference illustrates the decision state, not every failure/progress/completion state simultaneously.

Dark mode preserves the same hierarchy and semantic success/warning/error colors.

## 41. Explicitly out of scope

Do not include:
- server LibraryRoot selection;
- NAS configuration;
- SMB/NFS credentials;
- media import;
- Admin Storage management;
- cache-directory internals;
- codec/transcode settings;
- download-quality selection;
- Smart Offline policy configuration;
- arbitrary file browsing inside Jularr;
- a second local media identity model.

## 42. Acceptance criteria

- [ ] Flow is shown only on clients with safe managed-location capability.
- [ ] Current destination is clearly identified.
- [ ] New destination is validated before confirmation.
- [ ] Existing offline content can be moved safely.
- [ ] Optional split-location behavior appears only when truly supported.
- [ ] Capacity calculation honors destination free space and safety reserve.
- [ ] Old verified copies are retained until new copies verify successfully.
- [ ] Migration is restart-safe and recoverable.
- [ ] Active playback/reading is not invalidated by premature source deletion.
- [ ] Removed/unavailable removable storage pauses/reports rather than silently falling back.
- [ ] Migration does not alter canonical media identity, Games Game/GameRelease identity, progress/save ownership, Smart Offline origin or explicit ownership.
- [ ] Jularr-managed Game packages migrate only through the Games-owned storage/install participant and preserve profile saves.
- [ ] Long-running migration does not trap the user inside a blocking modal.
- [ ] Migration does not count as ordinary media-download badge activity.
- [ ] Success and action-required failures use existing toast/Bell semantics.
- [ ] No server/NAS/Admin storage concepts leak into the consumer flow.
