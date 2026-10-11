# Jularr dependency-ordered implementation roadmap

Status: **product/dependency roadmap, not the executable SQL cutover schedule**. The owner-approved target is [CLEAN_CUT_DATABASE.md](CLEAN_CUT_DATABASE.md); the technical A–H cutover, current non-Learning Phase-B scope and its explicit destructive-reset gate are tracked in [#880](https://github.com/Juloc/Jularr/issues/880) and the [execution plan](https://github.com/Juloc/Jularr/blob/docs/service-data-logic-contract-20261010/docs/DATABASE_CUTOVER_EXECUTION_PLAN.md). Learning is deferred from the current database baseline, not discarded as a future feature. Later feature phases below are long-range planning, not cutover prerequisites.

## Phase 0 — Freeze and contracts

1. Freeze new dependencies on duplicate Anime/Novel/Movie/TV/Audiobook/**Game** identities and per-type progress/acquisition models; new target features follow the clean-cut contract.
2. Finalize explicit Account/Profile identity, linked login identity, active-Profile selection and capability contracts.
3. Finalize canonical MediaCore semantics: Work, Structure, Edition, Version and canonical target references.
4. Finalize Library layer: Asset, Stored File, Track, technical analysis and LibraryRoot association.
5. Finalize unified Progress envelope: current target, exact resume locator/time, completed-through and history.
6. Finalize common Job model/runtime and stable error/diagnostic contracts.

Exit gate: migrations can target stable canonical contracts; no unresolved polymorphic-target or progress identity design.

## Phase 1 — PostgreSQL foundation (execution delegated)

The binding technical sequence and gate criteria live only in [#880](https://github.com/Juloc/Jularr/issues/880) and its execution plan, not this product roadmap. Finalize the non-Learning target DDL, test a fresh isolated baseline, implement Service/Data/Logic and migrate affected consumers, verify parity, then seek **separate authorization** before resetting any existing database. Do not silently reincorporate deferred Learning or use the old EF migration chain.

Exit gate: the separately approved technical cutover has passed all backend/client validation and no old-schema runtime bridge remains.

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

Current first-video vertical audit: `docs/implementation/request-to-play-readiness-audit.md`.

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
5. Complete the Unified Reader engine consolidation from `docs/implementation/unified-reader-engine-completion.md`: one shared interaction runtime plus ReflowText, FixedPage and ImageSequence renderers selected from parsed document layout/capabilities rather than file extension.
6. Migrate Book/LN/Manga/PDF exact reading state to canonical ReaderLocator/MediaProgress and remove legacy per-type progress runtime paths after validation.
7. Edition/language switching and Translation integration, including official/generated variants and source mapping.
8. Implement #834 progressive translation: one provider-neutral stable-block stream, validated restart-safe partial cache, shared/reconnectable runs, Codex delta support when available, bounded-block fallback for non-streaming providers and the corresponding Unified Reader partial/finalizing/error states.
9. Integrate #819 Smart PDF as a derived view of the same Reader/progress identity and feed its semantic blocks into #834; do not build another PDF shell.
10. Integrate #846 Manga/comic OCR/text-region identities with #834 only after stable regions exist; image pages remain canonical and no guessed whole-page translation path is added.
11. Implement the approved `continuation-surfaces/SPEC.md` only after canonical ActiveSession and Reader/MediaProgress state work; no separate mini-player/reading state store.

This phase resolves the foundation required by #403, #662 and the incomplete completion gates of #289. #819 supplies PDF-derived-document semantics and remains a dependency rather than a separate Reader architecture.

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

## Phase 9 — Clean-cut verification and removal

This is a **verification** phase, not a backfill of old Jularr database data. The initial cutover is test-only, requires explicit reset authorization, and happens atomically in Phase 1. Verify no old `Games.Id`, Anime/Novel/Manga roots, old progress, image, session, `WorkSourceLinks`, obsolete SQL tables or migrations remain. Verify all supported features use the new canonical owners and the required release/CI gates pass. If a capability is missing, fix the target implementation instead of restoring a second legacy path.

## Phase 10 — Migration Center and external-stack migration

After canonical schemas are stable, implement preview/dry-run adapters for supported Sonarr/Radarr/Readarr/Seerr/media-server sources. Never mutate source media by default.

## Phase 11 — Deferred enhancements

Only after foundations are stable: automatic upgrades/multi-version retention, Continue Anywhere, advanced playback/casting/watch-together, smart offline prefetch, storage cleanup/retention automation, advanced typography customization and deferred provider-framework polish.

Clean/Original Jularr skins and accent tokenization are **not** deferred enhancements; they belong to the shared design-system foundation.

## Global completion rules

Every vertical slice must use its binding screen spec, implement Light/Dark and specified platforms, cover loading/empty/error/partial/forbidden states, enforce authorization server-side, preserve browser/system Back context, reuse shared components, and report remaining legacy dependencies. Coding agents may not redesign architecture or UX to finish a slice faster.
