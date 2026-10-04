# Jularr UX mockups

Approved and work-in-progress UX mockups live here.

## Canonical structure

Use one folder per substantial screen. `SPEC.md` is the binding screen-specific behavior/information hierarchy/state contract; images are visual references once approved. If text and image conflict, the text spec wins. Light and Dark are always first-class. `docs/UX.md` owns global/shared rules.

Agents must not redesign an approved screen during implementation without updating its spec and approval.

## Original Jularr mascot

Canonical visual reference:
- `docs/assets/original-j/jularr-mascot-reference.png`
- rules: `docs/assets/original-j/README.md`

Whenever an Original Jularr mockup intentionally uses the Jularr mascot/anime girl, it must use that canonical character identity. Different poses, expressions, crops and screen-appropriate props are allowed; replacing her with a different character is not. The mascot is optional and Clean does not use her by default.

## User screen contracts

- `home/SPEC.md`
- `library/SPEC.md` — binding cross-media Library contract
- `collection-detail-edit/SPEC.md` — Manual/Smart/Linked Collections, rule builder and linked sync
- `discover/SPEC.md`
- `anime-series-detail/SPEC.md`
- `movie-detail/SPEC.md`
- `reading-detail/SPEC.md` — shared Book / Light Novel / Manga detail family
- `audiobook-detail/SPEC.md`
- `player/SPEC.md`
- `reader/SPEC.md`
- `calendar/SPEC.md`
- `learning-home/SPEC.md`
- `course-detail/SPEC.md` — Learning curriculum / Chapter / Lesson navigator
- `lesson-review/SPEC.md`
- `vocabulary-sentences/SPEC.md` — shared Vocabulary / Sentences learning library
- `progress-achievements/SPEC.md` — Learning progress, statistics and achievements
- `script-trainer/SPEC.md` — generic Script Trainer with Japanese Kana as current toolkit
- `user-settings/SPEC.md`
- `notifications-settings/SPEC.md` — profile notification channels/topics/delivery overview
- `notifications-topic-editor/SPEC.md` — per-topic event/channel/timing editor
- `notifications-quiet-hours/SPEC.md` — timezone-aware Quiet Hours
- `notifications-digest/SPEC.md` — Digest cadence/routes and disable-resolution flow
- `notifications-center/SPEC.md` — durable In-App Notification Center
- `notifications-bell/SPEC.md` — global Bell + Desktop Quick View
- `notifications-toast-popup/SPEC.md` — transient event/action feedback surfaces
- `notifications-banner/SPEC.md` — persistent state-driven Warning/Critical banners
- `profile-activity/SPEC.md`
- `add-request-flow/SPEC.md`
- `request-status-details/SPEC.md` — consumer Request status/detail sheet
- `language-edition-selector/SPEC.md` — shared language/Edition dialog/sheet
- `person-creator/SPEC.md` — secondary Person/Creator view
- `login-profile-selection/SPEC.md` — account login, external identities and Profile selection
- `continuation-surfaces/SPEC.md` — persistent Now Playing and Continue Reading surfaces
- `error-permission-states/SPEC.md` — shared 404/403/module/resource/session/500 states
- `media-preview/SPEC.md` — cinematic Quick View for discovery/recommendations
- `games/SPEC.md` — shared Games UX contract
- `games-library/SPEC.md` — Games Library/Home
- `game-detail/SPEC.md` — Game detail
- `game-play-options/SPEC.md` — per-title play/runtime options
- `game-player/SPEC.md` — browser game-player shell
- `game-player-nintendo-ds/SPEC.md` — Nintendo DS player composition
- `game-player-playstation/SPEC.md` — PlayStation player composition
- `game-touch-controls/SPEC.md` — mobile/touch controls

## Admin screen contracts

- `admin-dashboard/SPEC.md`
- `admin-media-detail/SPEC.md`
- `admin-wanted/SPEC.md`
- `admin-requests/SPEC.md`
- `admin-activity/SPEC.md`
- `admin-history/SPEC.md`
- `admin-manual-search/SPEC.md`
- `admin-download-assignment/SPEC.md`
- `admin-folder-import-mapping/SPEC.md`
- `admin-storage/SPEC.md`
- `admin-downloader/SPEC.md` — shared native-downloader contract
- `admin-downloader-overview/SPEC.md`
- `admin-downloader-queue/SPEC.md`
- `admin-downloader-servers/SPEC.md`
- `admin-downloader-processing/SPEC.md`
- `admin-downloader-speed-schedule/SPEC.md`
- `admin-downloader-settings/SPEC.md`
- `admin-downloader-external-clients/SPEC.md`
- `admin-providers/SPEC.md`
- `admin-ai/SPEC.md`
- `admin-users-permissions/SPEC.md`
- `admin-devices-sessions/SPEC.md` — live playback sessions, known devices and authentication/security activity
- `admin-backup-restore/SPEC.md`
- `admin-migration/SPEC.md`
- `admin-acquisition-settings/SPEC.md` — integrated Acquisition Profiles, quality/upgrade policy, reusable Release Rules, wait/source policy and score testing
- `admin-system-diagnostics/SPEC.md`
- `admin-instance/SPEC.md` — instance module surface + global Admin Detailed/Compact density contract
- `admin-general-settings/SPEC.md` — instance identity, language/locale/timezone and metadata/regional defaults
- `admin-appearance/SPEC.md` — instance default visual style, profile override policy, Admin density shortcut and theme preview
- `admin-notifications/SPEC.md` — instance notification channels, canonical events and delivery diagnostics
- `admin-games/SPEC.md` — Games admin overview
- `admin-games-runtimes/SPEC.md` — runtime inventory/configuration
- `admin-games-runtime-editor/SPEC.md` — runtime editor
- `admin-games-bios/SPEC.md` — BIOS/firmware inventory
- `admin-games-bios-editor/SPEC.md` — BIOS/firmware editor
- `admin-games-import-resolution/SPEC.md` — unresolved game import mapping
- `setup-wizard/SPEC.md`

## Planning references

- `../PLANNING-AUDIT.md` — coverage, architecture consistency and open-issue audit.
- `../CROSS-SPEC-CONSISTENCY-AUDIT.md` — cross-document semantic consistency and remaining product decision.
- `../IMPLEMENTATION-ROADMAP.md` — dependency-ordered implementation sequence.

Only create platform images that are actually needed. Important screens should receive approved Desktop/Mobile/Tablet/TV references according to their SPEC before their implementation is considered complete.
