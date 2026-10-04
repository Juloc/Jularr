# Jularr planning audit

Cross-spec consistency: `CROSS-SPEC-CONSISTENCY-AUDIT.md`.
Implementation readiness: `implementation/request-to-play-readiness-audit.md`.

Status: **current planning coverage audit**. This document does not authorize bypassing architecture, migration, security, visual-approval or implementation gates.

## 1. Authority order

1. `DOMAIN.md` defines canonical domain meaning.
2. `DOMAIN-AUDIT.md` defines legacy transition/classification.
3. `ARCHITECTURE.md` defines module ownership/contracts.
4. `UX.md` and `INFORMATION_ARCHITECTURE.md` define global navigation/product behavior.
5. Screen `SPEC.md` files define screen behavior; their text wins over visual references on conflict.
6. Approved mockups are visual references only where they do not conflict with the binding text.

## 2. Consumer screen-contract coverage

### Core media

Binding/planned contracts exist for:

- Home;
- Library;
- Collections landing/detail/edit, Smart rules and Linked sync;
- Discover/Search;
- Media Preview / Quick View;
- Anime/Series Detail;
- Movie Detail;
- shared Reading Detail for Book/Light Novel/Manga;
- Audiobook Detail;
- Player;
- Reader;
- Calendar;
- Profile/Activity;
- User Settings;
- Request flow;
- Request Status/Details;
- Language/Edition selector;
- Person/Creator;
- Login/Profile selection;
- Now Playing / Continue Reading continuation surfaces;
- Error/Permission/Availability states.

Library is normalized to `docs/mockups/library/SPEC.md`. The previous `library/README.md` draft is obsolete.

### Learning

V1 contracts exist for:

- Learning Home;
- Course Detail;
- Lesson / Review;
- Vocabulary / Sentences;
- Progress / Achievements;
- Script/Kana Trainer.

Learning remains an optional module and its screen availability follows instance capability, authorization and Profile preference.

### Games

A separate Games planning track exists with contracts for:

- shared Games UX;
- Games Library/Home;
- Game Detail;
- Play Options;
- Game Player;
- Nintendo DS player composition;
- PlayStation player composition;
- Touch Controls.

The shared `games/SPEC.md` still describes itself as a planning scaffold. This is not a missing generic consumer screen; Games remains subject to its own page-by-page approval/implementation scope.

## 3. Admin screen-contract coverage

Contracts now exist for the previously listed Admin gaps:

- Dashboard;
- Media Detail;
- Wanted;
- Requests;
- Activity/Jobs;
- History;
- Manual Search;
- completed-download assignment repair;
- folder/library reconciliation and import mapping;
- Storage + safe Path Browser;
- Downloader overview/queue/servers/processing/schedule/settings/external clients;
- Provider Settings;
- AI Admin;
- Users & Permissions;
- Devices & Sessions;
- Backup / Restore;
- Migration Center;
- Acquisition Settings;
- System / Diagnostics;
- Instance settings;
- General settings;
- Appearance;
- Notifications;
- Setup Wizard.

Games Admin also has contracts for:
- Games overview;
- Runtimes;
- Runtime Editor;
- BIOS/Firmware;
- BIOS/Firmware Editor;
- import-resolution flow.

Some Admin specs are approved visual directions while others are planning baselines. Existence of a spec does not imply its implementation is complete.

## 4. Current UX planning gaps

There is **no longer a generic missing core consumer page category** from the earlier audit list.

Mobile primary navigation is now locked to `Home · Library · Calendar · Learning · Profile`. Games remains a dedicated route reached contextually on Mobile and does not consume a bottom-navigation slot.

The next work should therefore not invent additional pages merely to continue planning.

The Admin cross-spec consistency pass is now complete at planning-contract level.

Implementation handoff: `docs/implementation/admin-navigation-route-consolidation.md`.

Remaining work is primarily:

- implement the consolidated Admin shell/legacy-route redirects without creating duplicate owners;
- implement the first complete Anime/Movie/TV Request-to-Play vertical according to `implementation/request-to-play-readiness-audit.md`;
- keep Games page-by-page implementation separate from core media completion;
- keep implementation dependent on the canonical persistence/application contracts in the roadmap.

Explicit approval gates that genuinely remain:
- Calendar: final owner review of status/filter semantics, event interaction, responsive behavior and Light/Dark before implementation merge;
- PlayStation 1 Game Player: visual mockup approval is still pending.

Profile/Activity, Admin Users & Permissions and Admin Media Detail no longer carry stale “waiting for first mockup” status language; their existing visual directions/specs are sufficient planning baselines.

A new screen should be added only when a concrete workflow cannot be represented cleanly by an existing page, dialog, sheet or state.

## 5. Canonical consistency findings

All media-facing screens must use:

`Work -> Structure -> Edition -> Version -> Asset/File -> Track`

No screen may introduce Anime/Movie/Book/Audiobook-specific persistence as a competing identity root.

User progress targets canonical Work/structural identities through unified `MediaProgress`. Player and Reader share canonical identity/progress contracts but retain platform-specific presentation.

Search/Discover/provider records are external evidence until resolved. Provider-native entries may influence presentation/deep links but do not create duplicate canonical Works.

Provider data fetched for durable Jularr features is persisted as local evidence/snapshots where defined by the domain/architecture. Linked Collections render from local Jularr state rather than requiring a live provider request.

Collections reference canonical Work IDs. Manual, Smart and Linked are membership modes; cross-media and franchise are not competing media identity models.

Admin acquisition operates on:

`WantedItem -> ReleaseCandidate -> DownloadJob -> ImportJob -> Version/Asset/File/Track`

Manual Search is an inspection/selection surface over that same acquisition pipeline, not a second acquisition engine.

Storage screens operate on `LibraryRoot` and physical files. Logical media identity does not derive from storage paths.

Consumer Library is distinct from Discover and Admin:
- no generic consumer Add/import menu;
- no stats/operations dashboard;
- Request comes from the canonical Request flow;
- Quick View uses the shared Media Preview contract.

## 6. Open-issue alignment notes

### Aligned with the target when interpreted through current specs

- #403 playback: use current Player + PlaybackPlan/ActiveSession/File/Track/MediaProgress contracts.
- #662 progress: exact resume and completed-through remain separate semantics inside canonical MediaProgress.
- #389 acquisition/import: all media types converge on the shared Wanted/Download/Import pipeline.
- #396 Wanted/monitoring: target canonical Work/unit/Edition references, not permanent per-media roots.
- #427 Smart Collections: current Collection spec binds rules to local/cached canonical facts and Work membership.
- #440 Audiobooks: use Work/Edition/Version/Asset/File/Track rather than a parallel Audiobook core.
- #441 Learning: bounded Learning domain now has V1 screen contracts; media links still resolve to canonical Work/Episode/Chapter.
- #638 AI: AI remains a capability/provider layer; persisted outputs require canonical source identity/provenance.
- #433 Migration and #416 Backup/Restore: both have Admin screen contracts but still depend on stable canonical schema/configuration semantics.

### Legacy/conflicting assumptions

- #395 visual/IA assumptions are superseded where they conflict with current Library, Discover and Detail specs: no duplicated media-type sidebar, no Library stats side panel, no permanent filter-chip wall.
- #396 legacy examples naming separate Movie/Anime/LightNovel persistence roots are conceptual only; canonical Work/Structure/Edition rules win.
- #440 must not create parallel Audiobook/AudiobookFile acquisition or progress infrastructure.
- #69 predates current naming/UX and is reusable only where consistent with current Player/TV and canonical contracts.
- #413 must not create a second job/activity store beside the shared Job/Admin Activity model.
- #421 must use canonical Version/Asset/File and acquisition/import history rather than introduce another release/version model.
- #434/#595 provider-native Anime presentation remains mapping/presentation over canonical Work/Season/Episode identity.

## 7. Implementation dependencies

Key dependencies remain:

- Player -> MediaCore + Library File/Track + Progress + Playback + Devices/capabilities;
- Reader -> MediaCore + Library Asset/File + Progress + Reader + Translation; Learning optional;
- Library/Collections -> canonical Work identity + availability/language facts + Profile state;
- Linked Collections -> provider snapshots + identity resolution + Profile Connection;
- Request/Wanted/Manual Search/Imports -> MediaCore + Acquisition + Storage + Jobs + provider health;
- Backup/Restore -> stable PostgreSQL schema + configuration/secrets policy + migration versioning;
- Migration -> canonical schema + backfill validators + progress/file/provider-identity preservation;
- Users/Permissions -> explicit Account/Profile/capability contracts.

## 8. Planning gate

The former gate of “missing Player/Reader/Learning specs” is satisfied at the documentation level.

Broad implementation still must not bypass:
- unresolved canonical persistence/application contracts;
- migration/backfill safety;
- required screen-specific approval gates;
- security/authorization requirements;
- platform-specific behavior defined by each binding spec.

The planning process should now favor consistency audits and dependency-ordered implementation over inventing additional generic screens.
