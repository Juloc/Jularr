# Offline UX Cross-Spec / Coverage Audit

Status: **binding planning audit for the target Offline UX on `planning/offline-ux-20261004`**.

Date: 2026-10-04.

This audit checks the finished-product Offline UX against:
- the approved Offline mockup/spec family on this planning branch;
- current `dev` architecture/specs;
- `docs/OFFLINE_LIBRARY.md`;
- `docs/ANDROID_CLIENTS.md`;
- canonical Player/Reader/Progress direction;
- the separate canonical Notifications planning branch;
- relevant GitHub issues.

It is a coverage/integration audit, not implementation authorization. Screen `SPEC.md` files remain the binding screen contracts.

## 1. Executive verdict

The core device-local Offline UX is now substantially complete.

Approved/spec'd product flow:

```text
Media detail
  -> Offline download selection
  -> Global Download Indicator / Quick View
  -> Downloads & Offline manager
  -> Offline available content
  -> normal Player / Reader using verified local package
```

Supporting policy/recovery is also covered:

```text
Offline Settings
  -> network / quality / package defaults
  -> storage limits
  -> Smart Offline
  -> cleanup / updates / sync / sign-out
  -> storage-location migration
```

The following now have binding target specs and approved visual references in the Offline planning work:

1. Downloads & Offline manager.
2. Offline Settings.
3. Shared Download Selection dialog/sheet.
4. Global Download Indicator + Quick View.
5. Download event/toast examples.
6. Offline Storage Location + migration.
7. Player offline playback / unavailable-next state.
8. Reader offline / unavailable-next state.

No additional standalone screens are required for:
- pause/resume/retry;
- normal Wi-Fi waiting;
- verification;
- ordinary download completion;
- corrupt package recovery;
- removed storage;
- bulk cleanup;
- Smart Offline management.

Those states belong to the existing owners above.

## 2. Final required visual/product surface — completed

The final necessary Offline UX reference was:

### Offline cold-start / disconnected Home shell

The finished product must remain navigable when Jularr is launched from scratch while the server/network is unavailable.

This is not a new Offline application.

It is the normal Home/app shell in a disconnected state and should:
- load the safe cached/native app shell;
- preserve the current authorized local Profile/account boundary;
- expose verified local Offline content;
- allow navigation to `Downloads & Offline`;
- allow opening verified local Player/Reader content;
- avoid rendering online-only recommendation/request/provider sections as broken placeholders;
- clearly but quietly explain that Jularr is offline;
- provide Retry/reconnect behavior;
- never expose another Profile's local inventory.

The visual belongs to the existing:

`docs/mockups/home/`

The Home spec has now been expanded with the full cold-start/disconnected contract and the approved Desktop/Mobile visual reference is present in `docs/mockups/home/`.

No further required Offline mockup remains after this state.

## 3. Coverage matrix

| Concern | Canonical UX owner | Coverage |
| --- | --- | --- |
| Current-device active downloads | `downloads-offline` | Complete |
| Current-device ready Offline inventory | `downloads-offline` | Complete |
| Explicit download scope | `download-selection` | Complete |
| Video/audio/reading quality override | `download-selection` + `offline-settings` | Complete |
| Audio/subtitle package | `download-selection` + `offline-settings` | Complete |
| Offline Learning package | `download-selection` + `offline-settings` | Complete |
| Wi-Fi/metered policy | `offline-settings` | Complete |
| Background/power policy | `offline-settings` | Complete |
| Device Offline limit/reserve | `offline-settings` | Complete |
| Smart Offline | `offline-settings` + manager | Complete target contract |
| Smart Offline promotion to explicit | selection/manager/Quick View | Complete |
| Download live shell indicator | `download-quick-view` | Complete |
| Pause/resume/retry | manager + Quick View | Complete |
| Toast/Bell event semantics | `download-notifications` + canonical Notifications | Offline event ownership aligned; shared Notification branch remains canonical for presentation/runtime integration |
| Local storage-location change | `offline-storage-location` | Complete |
| Safe storage migration/restart | `offline-storage-location` | Complete |
| Offline video playback | `player` | Complete |
| Missing next offline episode | `player` | Complete |
| Offline reading | `reader` | Complete |
| Missing next offline chapter | `reader` | Complete |
| Offline exact progress | Player/Reader canonical progress owners | Complete target semantics |
| Offline bookmarks/annotations | Reader canonical owners | Complete target semantics |
| Corrupt local package | manager + Player/Reader | Complete |
| Removed/unavailable local storage | settings/migration + Player/Reader | Complete |
| Account/Profile isolation | offline owner + all surfaces | Semantics covered; implementation alignment still required |
| Cold-start PWA/app shell | Home + local repository | **Missing final visual/integration contract** |
| Detail-page local Offline action | shared selection + media detail specs | **Spec integration gap; no new mockup required** |
| Server acquisition vs device download wording | media detail + Offline specs | **Must be disambiguated** |

## 4. Implementation gap: true cold-start Offline is not finished

`docs/OFFLINE_LIBRARY.md` still records Part 2 follow-up work even though #221 is closed.

Current documented gaps include:
- cold PWA Reader launch while fully offline falls to the generic offline page;
- Reader chapter drawer still depends on network rather than the local manifest;
- per-chapter selection is not fully wired on all target clients;
- Android local progress/bookmark queues exist but Reader call sites are incomplete;
- Manga still needs to reuse the same manifest/chapter/asset/sync path.

This is not merely polish. Without cold-start:
- a user can possess verified Offline content;
- close/restart the browser/app;
- lose server/network connectivity;
- and still fail to reach the content from a fresh launch.

That breaks the finished-product meaning of Offline.

Durable backlog:
- **#839 — Complete true offline cold-start and unified local Reader repository**.

## 5. Cross-spec gap: server acquisition vs device-local Offline download

Existing consumer detail specs use states such as:
- Requested;
- Downloading;
- Preparing;

for server/library acquisition.

The new Offline product also uses download terminology for:
- copying already-available media to the current device.

These are different operations and must not collapse into one ambiguous consumer state.

### Required semantic distinction

Server/library acquisition remains its own availability/request state.

Device-local Offline action should use explicit local vocabulary, for example:
- **Offline herunterladen**;
- **Wird offline gespeichert**;
- **Offline verfügbar**;
- **Offline aktualisieren**;
- **Offline-Download erneut versuchen**;
- **Offline-Kopie entfernen**.

The local action opens the shared `download-selection` flow where selection is meaningful.

Affected target specs:
- Anime / TV detail;
- Movie detail;
- Reading detail;
- Audiobook detail.

No new per-media Download mockups are required. The approved shared selection reference is sufficient.

Durable backlog:
- **#840 — Integrate device-local offline actions into consumer detail surfaces**.

## 6. Notification contract integration

A separate planning branch now owns the complete canonical Notifications UX:

`planning/notifications-ux-20261004`

Canonical sources there include:
- `docs/NOTIFICATIONS.md`;
- Notification Settings;
- Notification Center;
- Bell / Quick View;
- Toast / Popup;
- Banner;
- Topic Editor;
- Quiet Hours;
- Digest.

Implementation follow-ups are already tracked by:
- #835;
- #836;
- #837;
- #838.

### Offline ownership after integration

The Offline subsystem should own only event facts/context such as:
- offline download Ready;
- offline download Failed;
- local storage requires attention;
- optional Smart Offline batch summary.

The canonical Notifications system must own:
- durable inbox projection;
- read/unread;
- Bell count;
- Toast attention policy;
- foreground/background deduplication;
- OS Push routing;
- Quiet Hours;
- Digest;
- canonical notification action resolution.

Do not implement a second Offline-specific toast queue or notification store.

### Current known visual/spec conflict

The Offline download notification reference shows a **bottom mobile snackbar**, while the canonical Notification Toast target specifies a **compact top in-app notification banner/toast on phone**.

The Offline event spec has now been cleaned up so:
- canonical Notifications placement explicitly wins;
- Offline owns event facts/context only;
- the Offline notification image is treated as an event-content example rather than shared Toast-placement authority.

The visual mismatch can be resolved naturally when the Notification planning branch is integrated. It does **not** require another Offline-specific mockup because the canonical Notification family already owns the component visuals.

## 7. Branch integration blocker: current planning branch is behind `dev`

Audit snapshot:

- `planning/offline-ux-20261004` and `dev` are diverged;
- Offline branch was **28 commits ahead** and **144 commits behind** `dev` at audit time.

Do not merge the Offline branch to `dev`/main without reconciling current `dev`.

### Reader is the critical conflict

Current `dev` added/expanded Unified Reader architecture after this Offline branch was created, including:
- format/layout resolution;
- fixed-layout EPUB;
- PDF Smart Book routing;
- scans;
- comics;
- magazines/artbooks;
- CBR/CB7/import-adapter direction;
- richer canonical Reader document routing.

`docs/UNIFIED_READER.md` on `dev` is also materially newer than the copy at this branch point.

The Offline Reader additions are valid, but integration must preserve **both**:
1. latest Unified Reader format/layout architecture from `dev`;
2. the new Offline Reader behavior from this branch.

Do not resolve that conflict by choosing one whole file over the other.

### Player

Player also changed on the Offline branch. Before integration, compare against current `dev` and retain all newer canonical Playback/Progress/ActiveSession work.

## 8. Existing issue alignment

### #221 — Offline Library & Reader Sync

Closed, but its architecture document still records unfinished Part 2 items.

Do not treat the closed issue as evidence that cold-start/local Reader integration is complete.

#839 now captures the remaining target completion.

### #225 — bounded offline playback

The target UX preserves its core rules:
- deliberate device-local copy;
- verified Ready state;
- local playback without server;
- local progress queue;
- no source-media modification;
- no DRM/license system.

The expanded end-state UX adds policy/selection/Smart Offline without changing those ownership rules.

### #415 — Smart Offline

Still the canonical Smart Offline backlog owner.

The new UX specifies its finished product behavior early so implementation does not create a second cache/queue later.

Explicit downloads remain higher priority and protected.

### #413 — Unified Activity Center

Offline download Quick View remains a focused consumer current-device surface.

If #413 later represents offline preparation/migration as a generic background operation:
- it must project the same underlying operation/download state;
- it must not create another queue;
- storage migration may appear there as a background operation;
- the consumer Download Indicator remains allowed as the direct high-frequency entry for device downloads.

## 9. Account/Profile/server-origin isolation

Current Android Offline ownership derives a stable local owner key from server origin + profile identity and locks/purges according to account policy.

Target UX consistently requires:
- another Profile/account must never see another owner's Offline inventory;
- retained content is inaccessible while its owner is not active/authorized;
- server-origin switch cannot accidentally reuse unrelated Offline bytes;
- progress/sync queues remain owner-scoped.

Before implementation completion, retention semantics must be explicit for:
- Profile switch;
- Account logout;
- same Account re-login;
- different Account login;
- server-origin switch;
- account/profile deletion or revocation.

The UI does not need a new screen for each case.

Offline Settings already owns the user-facing sign-out retention choice. Security policy may force removal on managed/shared devices.

## 10. PWA storage truthfulness

The target specs correctly distinguish:
- Jularr Offline limit;
- browser quota;
- native physical free space;
- native free-space safety reserve.

This distinction must survive implementation.

Required:
- PWA must not show a fake physical-device free-space value;
- persistent-storage denial is a degraded capability, not proof that content is immediately lost;
- native-only storage location/reserve controls remain hidden on unsupported browser clients.

No additional mockup is required unless browser behavior later needs a materially different user decision.

## 11. Smart Offline consistency

Smart Offline is consistently defined as:
- opt-in;
- bounded;
- device-aware;
- subordinate to explicit downloads;
- same package/queue format;
- disposable only within its speculative policy;
- promotable to explicit **Keep offline** without duplicate bytes/job;
- quiet in Notifications by default;
- never a trigger for canonical server-library deletion.

No new Smart Offline manager or Smart Offline queue screen is needed.

## 12. Progress consistency

Player and Reader Offline semantics now agree with canonical progress direction:

`CurrentItem != ResumePosition != CompletedThrough != ProviderProgress`

Offline behavior:
- exact progress writes locally first;
- completion uses the same canonical policy as online;
- provider write-back waits for synchronized completed state;
- reconnection never blindly lowers newer local progress;
- no offline-only progress database/domain truth is introduced.

This is aligned with #662.

## 13. Storage consistency

The Offline manager/settings/migration specs consistently separate:

**Device-local Offline copies**
from
**canonical server/NAS Library media**.

Removing/migrating/evicting a local copy must never:
- delete the server Asset/File;
- mutate the Work identity;
- clear canonical progress merely because local bytes were removed;
- invoke canonical Storage Lifecycle deletion.

Storage migration additionally requires:
- old verified copy retained;
- copy;
- verify;
- atomic ownership/location switch;
- only then delete old copy.

No new storage-manager mockup is required.

## 14. Visual-reference housekeeping

All planned Offline surfaces currently have uploaded images in their owning folders.

The early stale visual-status wording in `downloads-offline/SPEC.md` and `offline-settings/SPEC.md` was corrected during this audit; both now state that their approved references are present.

Some approved image filenames are generated `file_...` names rather than `image.png`. This is not semantically wrong because the folder + `SPEC.md` is the owner, but filenames may be normalized later for maintainability if desired.

No image move/rename is required for UX correctness.

## 15. Mockup decision table

| Possible extra mockup | Decision |
| --- | --- |
| Empty Downloads queue | No — text spec sufficient |
| Nothing Offline | No — text spec sufficient |
| Failed download | No — Quick View/manager + notification references sufficient |
| Waiting for Wi-Fi | No — already represented |
| Verifying | No — state contract sufficient |
| Storage full | No — existing components sufficient |
| Storage removed | No — migration/Player/Reader contracts sufficient |
| Corrupt package | No — text recovery state sufficient |
| Sign-out with downloads | No dedicated Offline mockup — use shared confirmation/settings patterns |
| Smart Offline empty/error | No |
| Movie-specific selection | No — shared selection shell |
| Manga-specific selection | No — shared selection shell |
| Audiobook-specific selection | No — shared selection shell |
| Offline Player | Already approved |
| Offline Reader | Already approved |
| **Cold-start/disconnected Home** | **Approved and specified — no further mockup required** |

## 16. Integration order

Do not merge or implement the Offline planning work as a pile of independent screens.

Recommended order:

1. **Reconcile branch with current `dev`**
   - preserve latest Reader/Playback architecture;
   - reapply Offline additions conflict-by-conflict.
   - Reader/Player/index reconciliation has been performed in this planning branch; branch ancestry still needs the final dev sync/merge before integration.

2. **Sync media-detail contracts**
   - implement #840 wording/action ownership;
   - no new mockups required.

3. **Align Offline notification events with canonical Notifications**
   - use Notification planning authority;
   - Offline now delegates shared Toast/Bell placement and attention policy to that canonical family;
   - map Offline events into #835/#838 implementation work.

4. **Implement true local-first cold-start**
   - #839;
   - PWA shell/repository;
   - Reader local manifests/Contents;
   - Android Reader state wiring;
   - Manga reuse.

5. **Implement/finish explicit Offline download UX**
   - selection;
   - manager;
   - Quick View;
   - settings;
   - storage migration.

6. **Smart Offline**
   - #415 after explicit Offline foundations are stable.

## 17. Final audit result

The Offline UX does **not** need another family of screens.

The architecture should converge on one set of canonical owners with multiple presentations.

All required Offline visual planning items are now covered, including Home cold-start/disconnected state.

Everything else found by this audit is:
- branch reconciliation;
- spec integration;
- terminology ownership;
- notification-system integration;
- implementation backlog.

Do not create new standalone Offline pages for those concerns.
