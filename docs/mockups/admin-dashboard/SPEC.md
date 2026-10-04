# Admin Dashboard — Clean Live Operations

Status: approved UX direction.

Binding global UX rules: `docs/UX.md`

Approved current mockup image:
`docs/mockups/admin-dashboard-clean-live.png`

> The image is currently stored at the mockups root. The canonical folder layout for future assets is `docs/mockups/admin-dashboard/`; when the image is moved/re-uploaded, use `desktop.png` without changing this UX contract.

## Purpose

The Admin Dashboard is a live operational and health surface. It is not a media-library statistics page.

It must answer within a few seconds:

1. Is Jularr healthy?
2. What is running right now?
3. How loaded is the system?
4. What is broken or degraded?
5. Does the admin need to act?

Do not fill the page with vanity metrics such as total Anime/Manga counts, weekly media growth or decorative collection statistics.

## Visual baseline

During architecture/UX stabilization, use the clean design baseline:

- neutral/light surface
- restrained accent color
- clear hierarchy
- generous whitespace
- compact cards/tables
- no decorative Japanese/Jularr background artwork
- healthy state stays visually quiet
- warnings/errors use color for state, not decoration

The richer Jularr visual theme may be applied later without changing information architecture or interactions.

## Live behavior

The dashboard is continuously updated; it must not depend on a manual page refresh.

Prefer server push such as SignalR for:
- playback sessions
- transcodes/remuxes
- downloads
- active jobs/tasks
- provider/service health
- alerts/warnings

Short-interval telemetry:
- CPU
- RAM
- GPU/VRAM when available
- network in/out
- system load
- optional temperature

Slower-changing values such as storage capacity may refresh less frequently.

Live updates must not:
- reset scroll position
- close drawers/dialogs
- clear filters
- reset selected tabs
- interrupt an admin action

Show a subtle Live/last-update state. If the live channel is disconnected, show a reconnecting/degraded state.

## Layout order

Recommended desktop information order:

1. Overall health / problems
2. Service & connection health
3. System resources + network
4. Active streams / playback
5. Downloads
6. Active Jularr tasks
7. Storage
8. Live recent activity

If a critical problem exists, it may move above normal healthy sections so it is immediately visible.

## Services & connections

Health items are separate concepts and must never be merged just because they are external integrations.

Examples:
- Jularr Web/API
- PostgreSQL
- NAS / configured storage roots
- SABnzbd
- native downloader and configured external download-client adapters
- Indexers / release-search providers
- metadata providers
- subtitle providers
- translation providers
- AI providers
- notification integrations

Each health item can expose:
- Online / Warning / Error
- latency where useful
- connected/healthy count, e.g. `10/12 online`
- free-space warning where storage-related
- concise reason when degraded

**Indexer status is not download status.** Indexers, download clients and metadata providers stay separate throughout the UI.

## System resources

Show current server pressure, not broad historical analytics.

Required where measurable:
- CPU utilization
- RAM used / total
- GPU utilization
- VRAM used / total
- system disk utilization
- system load
- network download
- network upload
- optional temperature
- short-window sparklines

Threshold breaches feed the Problems & Warnings area.

## Network

Show:
- current inbound throughput
- current outbound throughput
- short live history
- explicit units, e.g. MB/s or Mbit/s

If the backend can reliably attribute traffic, detailed views may separate:
- playback
- acquisition/downloads
- other Jularr traffic

Never invent per-process attribution when the host/platform cannot measure it reliably.

## Active streams / playback

This is one of the main operational sections.

Each active session row shows:

- media title/unit
- user
- device/client
- playback mode: `Direct Play`, `Remux`, or `Transcode`
- video conversion where relevant, e.g. `4K HEVC → 1080p H.264`
- attributable CPU/GPU cost where measurable
- transcode speed, e.g. `2.1x`
- network bitrate/throughput, e.g. `18.4 Mbit/s`
- optional compact live sparkline
- two compact actions

Do not use a generic Status column that only contains elapsed minutes. Runtime/start time belongs in details.

### Row actions

Each stream row has exactly two primary compact actions:

1. **Stop** — destructive; confirmation required; terminates the session.
2. **More / Details** — opens a drawer/dialog without navigating away.

### Stream details drawer/dialog

Show at least:

- artwork + title/unit
- user
- device/client/app/version
- connection type
- client/IP information where authorized
- current playback mode
- source container
- video track
- audio track
- subtitle track
- input/output codec
- input/output resolution
- quality/bitrate
- playback position / duration
- session start / runtime
- buffer/health data where available
- current network bitrate
- attributable CPU/GPU load where measurable
- transcode speed/FPS
- hardware encoding state
- diagnostics/log link

### Stream controls

Authorized admins may control a running session only where the backend actually supports the action:

- Stop stream
- Set/adjust maximum bandwidth or quality
- Adjust transcode priority
- Toggle hardware encoding for that session only if it is technically safe and implemented

Never show controls that do not actually affect the running session.

## Downloads

Downloads are a dedicated section for download-client work only.

For each active download show:
- release/media name
- download client
- progress
- current speed
- ETA
- state

Queued/waiting items may be shown compactly.

Failed downloads become warnings and link to details/retry where safe.

Do not mix Indexer health into the Downloads section.

## Active Jularr tasks

This is separate from Downloads and Playback.

Supported task categories include:
- Import
- Scan
- Metadata
- Subtitle
- Translation
- AI
- Rename/organize
- maintenance

Every task row must answer what Jularr is doing:

`Type → concrete object → current step → progress → ETA/state`

Examples:
- `Import → Solo Leveling LN 12 → Dateien kopieren (12/24) → 50% → 8 min`
- `Metadata → Dandadan S2E2 → AniList + TMDB → 40% → 6 min`

Filters/tabs may group categories, but failures must remain obvious.

## Storage

System storage and media/NAS storage are separate.

### NAS / media storage

Show:
- online/offline
- total / used / free
- configured root folders
- free-space warnings
- unreachable mounts
- wake/retry state where relevant

Example roots:
- anime
- series
- movies
- novels
- manga
- books

### System storage

Where measurable, distinguish:
- OS/app
- cache/temp
- logs
- other relevant Jularr storage

## Problems & warnings

Operational problems are first-class and ordered by severity/recency.

Examples:
- NAS/root unavailable
- storage almost full
- indexers offline
- metadata/provider unavailable
- provider rate-limited
- PostgreSQL latency abnormal
- failed imports/jobs
- download client disconnected
- unexpected software transcode when hardware acceleration should be used
- excessive CPU/GPU pressure

Every problem should link to the corresponding filtered admin view or diagnostic detail.

When healthy, collapse the area to a small calm state such as `All systems operational` instead of reserving a large empty card.

## Live recent activity

A compact activity stream at the bottom shows what just happened.

Each entry:
- timestamp
- category
- concrete object
- concise event/result

Categories may include:
- Stream
- Download
- Import
- Metadata
- Subtitle
- Translation
- AI
- Maintenance

This is not a replacement for full logs or a second job store. `View all` opens Activity / To-Do / History or System & Diagnostics with the proper filter.

## Interaction and state requirements

Every card/table must define:
- loading
- empty
- ready
- degraded
- error

Additional requirements:
- live row updates should not cause layout jumping
- tables remain readable at normal desktop widths
- no badge clutter
- actions respect server-side permissions
- destructive actions require confirmation
- links preserve context and open the correct filtered admin surface
- opened stream details stay open while telemetry continues updating
- data shown in an open details drawer updates live as well

## Responsive scope

The approved mockup is desktop-first.

Admin remains primarily web/desktop oriented.

Tablet may adapt tables to wider stacked rows or two-pane layouts where needed.

Mobile Admin may provide essential diagnostics/actions but must not squeeze dense desktop tables into an unusable phone layout.

TV does not need the full Admin Dashboard.

## Acceptance criteria

The screen is considered implemented only when:

- service health is live and categories are correctly separated
- CPU/RAM/GPU/network/load update automatically
- active streams appear/disappear automatically
- Direct Play/Remux/Transcode are explicit per session
- stream rows show measurable system/network cost
- Stop works with confirmation
- More opens live session details
- supported throttle/quality controls work rather than being decorative
- downloads are separate from indexers
- background tasks explain object + step + progress/state
- storage health and capacity are visible
- warnings are prioritized
- live activity updates without reload
- reconnection/degraded live-update state exists
- no UI interaction is reset by incoming live updates
