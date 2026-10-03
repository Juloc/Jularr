# Jularr dependency-ordered implementation roadmap

Status: planning roadmap. This does not authorize skipping screen/spec approval gates.

## Phase 0 — Freeze and contracts

1. Freeze new dependencies on legacy Anime/Novel/Movie/TV/Audiobook identity and per-type progress/acquisition models.
2. Finalize explicit Account/Profile identity, linked login identity, active-Profile selection and capability contracts.
3. Finalize canonical MediaCore semantics: Work, Structure, Edition, Version and canonical target references.
4. Finalize Library layer: Asset, Stored File, Track, technical analysis and LibraryRoot association.
5. Finalize unified Progress envelope: current target, exact resume locator/time, completed-through and history.
6. Finalize common Job model/runtime and stable error/diagnostic contracts.

Exit gate: migrations can target stable canonical contracts; no unresolved polymorphic-target or progress identity design.

## Phase 1 — Canonical persistence foundation

1. PostgreSQL-first module-owned EF configurations/indexes/constraints.
2. Add missing canonical Account/LoginIdentity/Profile/ProfileConnection/Asset/File/Track/Progress/Job/Collection schemas plus persistent provider evidence/snapshot storage needed by durable linked features.
3. Build reversible backfill/validation tooling and migration audit reports.
4. Keep legacy bridges read-compatible but migration-only.

Exit gate: canonical records can represent all existing media/files/tracks/progress without data loss.

## Phase 2 — Shared application/API contracts

1. Canonical Work/detail/query DTOs.
2. Library availability/language/version contracts.
3. Progress/session contracts.
4. Provider health/configuration contracts.
5. Permission-derived navigation/API capability evaluation.
6. Shared UI design tokens/components/state views, including Clean/Original Jularr skins, Light/Dark/System and semantic accent hue shifting.
7. Account login/external-identity/Passkey contract and server-authoritative active-Profile selection.

Exit gate: clients do not need EF/legacy table shapes.

## Phase 3 — Consumer read/browse verticals

Implement approved specs using canonical reads only:
1. Login/Profile Selection and authenticated consumer shell.
2. Home.
3. Library + approved Manual/Smart/Linked Collections, including local-only Smart evaluation and local Linked rendering.
4. Discover/Search + Media Preview / Quick View.
5. Anime/Series, Movie, shared Reading Detail (Book/LN/Manga), and Audiobook detail.
6. Calendar.
7. Profile/Activity and User Settings.

Do not add acquisition internals to consumer pages.

## Phase 4 — Playback and Reader foundations

1. PlaybackPlan/capability engine over canonical File/Track.
2. ActiveSession + exact progress updates.
3. Web/Desktop/Mobile/Tablet/TV/iOS-WebKit Player compositions from the Player spec.
4. Reader document/locator contract and exact autosave/restore.
5. Edition/language switching and Translation integration.
6. Implement the approved `continuation-surfaces/SPEC.md` only after canonical ActiveSession and Reader/MediaProgress state work; no separate mini-player/reading state store.

This phase resolves the foundation required by #403 and #662.

## Phase 5 — Learning user flows

Planning contracts must be completed before implementation:
- `docs/LEARNING_PEDAGOGY.md` — teaching sequence and pedagogical rules;
- `docs/LEARNING_EXERCISES.md` — exercise payload, answer, specialization and publishing contract;
- `docs/LEARNING_PROGRESS.md` — exact resume, completion, aggregate progress and progression policy;
- `docs/LEARNING_PRACTICE_REVIEW.md` — LearningCard activation, due Review vs Extra Practice and FSRS interaction;
- `docs/LEARNING_GAMIFICATION.md` — activity, XP, learning time, Daily Goal, Streak and Achievements.

Implementation order:
1. Preserve existing Learning bounded-domain data.
2. Course/enrollment/progress contracts.
3. Exercise/content and pedagogy contracts.
4. Learning Home.
5. Course Detail and Lesson/Review.
6. Vocabulary/Sentences and Script Trainer.
7. Progress/Achievements only for metrics with canonical sources.
8. Media-context links to canonical Work/Episode/Chapter.
9. Optional AI/TTS integrations through shared capability contracts.

Do not implement mockup-only XP/time/Streak/Achievement values before their canonical event/state contracts exist.

Planning exit gate: Phase 1 Learning architecture is planned only while these five contracts and the approved Learning screen specifications remain mutually consistent. Implementation agents may refine code shape inside the existing Learning bounded domain but may not invent parallel curriculum, progress, review, activity or gamification stores.

## Phase 6 — Acquisition core

1. Canonical WantedItem targets and AcquisitionProfile.
2. ReleaseCandidate normalization/identity matching/scoring.
3. Download-client abstraction and category/routing mapping.
4. ImportJob + shared completed-download dispatcher.
5. Safe Storage/LibraryRoot/import modes.
6. AcquisitionEvent/history and idempotent jobs.

Exit gate: one pipeline serves every media type.

## Phase 7 — Admin acquisition/operations UI

Implement against Phase 6/Job contracts:
1. Admin Dashboard live operations.
2. Wanted.
3. Requests.
4. Manual Search.
5. Imports.
6. Activity/Jobs and History.
7. Admin Media Detail.
8. Acquisition Settings.

## Phase 8 — Admin platform/configuration UI

Implementation pack for navigation/route ownership:
- `docs/implementation/admin-navigation-route-consolidation.md`

Order:
1. Admin navigation Phase A: make `UiNavigationCatalog.Admin` the single Admin navigation source without changing behavior/stores.
2. Storage + safe Path Browser.
3. Provider Settings.
4. AI Admin.
5. Users & Permissions.
6. System/Diagnostics, then consolidate Resources/Health/Logs compatibility routes.
7. Devices & Sessions consolidation.
8. Backup/Restore.
9. Setup Wizard for fresh instances.
10. Add Migration/Notifications/General navigation only when those real routes exist.

Legacy route redirects are parity-gated; do not replace a functioning page with a redirect before its canonical owner can perform the same supported work.

## Phase 9 — Controlled legacy migration

For one legacy domain at a time:
1. backfill Work/Structure/Edition;
2. migrate files/technical analysis to Asset/File/Track;
3. migrate subtitles/segments;
4. migrate progress/history/bookmarks/highlights;
5. migrate acquisition references;
6. switch reads, then writes, to canonical contracts;
7. validate counts/IDs/files/progress/provider mappings;
8. remove legacy write path;
9. remove bridge/read path only after validation;
10. drop obsolete schema after supported migration window.

Suggested order: Anime/Series video -> Books/LN/Manga -> Movies/TV legacy additions -> Audiobooks, adjusted by actual production data risk.

## Phase 10 — Migration Center and external-stack migration

After canonical schemas are stable, implement preview/dry-run adapters for supported Sonarr/Radarr/Readarr/Seerr/media-server sources. Never mutate source media by default.

## Phase 11 — Deferred enhancements

Only after foundations are stable: automatic upgrades/multi-version retention, Continue Anywhere, advanced playback/casting/watch-together, smart offline prefetch, storage cleanup/retention automation, advanced typography customization and deferred provider-framework polish.

Clean/Original Jularr skins and accent tokenization are **not** deferred enhancements; they belong to the shared design-system foundation.

## Global completion rules

Every vertical slice must use its binding screen spec, implement Light/Dark and specified platforms, cover loading/empty/error/partial/forbidden states, enforce authorization server-side, preserve browser/system Back context, reuse shared components, and report remaining legacy dependencies. Coding agents may not redesign architecture or UX to finish a slice faster.
