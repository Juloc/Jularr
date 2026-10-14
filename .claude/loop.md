# Jularr overnight loop — 14/15 October 2026

This file is Claude Code's built-in /loop default prompt. Execute it only when the repository owner explicitly starts /loop. It is not a deployment authorization and it is not a promise that every Jularr feature can be finished overnight.

## Target and deadline

By Thursday morning, 15 October 2026 (Europe/Berlin), aim to deliver a truthful, runnable **clean-start release candidate for hands-on testing**: first-run/setup, library, real video/music playback, book/light-novel/manga reading, progress/resume, and polished, functional Clean UI. Include a repeatable isolated Docker/Compose deployment and evidence from real Chromium Playwright runs. The long-term complete Jularr/Arr+ parity scope remains in docs/BACKEND_NEXT.md and docs/COMPLETION_PLAN.md; do not label it finished without its acceptance tests. Morning cutoff for this unattended run: 09:00 Europe/Berlin on 2026-10-15. At or after the cutoff, stop starting new implementation slices and prepare a factual handoff.

## Authority and startup

- Follow AGENTS.md, .agent/project.yaml, docs/CONVENTIONS.md, docs/FRONTEND_CONVENTIONS.md and the relevant CODEMAPs/specs. Later owner decisions and accepted screenshots override older plans. Read rules once per continuous session; inspect only relevant code and tests.
- Work from origin/clean-start with clean independent feature branches, one bundled commit per coherent package and a separate PR. Do not switch into legacy dev/main, force-push, rewrite unrelated history or discard uncommitted work. Never modify a live database.
- Existing PRs #1120 (K24b) and #1121 (feature matrix / B1-B4 / D1 plan) are dependencies/context, not proof of a finished E2E pipeline. Re-check their state only when relevant; do not overwrite parallel work.
- Verify actual current repo status and existing functionality before changing it. No duplicate framework, progress store, Player/Reader owner, or separate mandatory container per worker.
- Use only existing approved dependencies. If a genuinely new dependency, destructive action, production deployment or security decision is required, record the precise blocker and continue with an independent task.

## Each /loop invocation: one bounded improvement

1. Read the current acceptance gaps in the sections below and relevant existing tests/PRs. Identify the highest-priority unverified gate. Do not waste a cycle reproducing finished work.
2. If a coherent change is feasible, implement **one** vertical slice completely, including wiring, UI behavior and focused regression tests. Do not stop after documenting a missing control. Do not claim a feature working just because markup exists.
3. Check UI with Playwright in a real Chromium page, including accessible focus/keyboard behavior and JS/console/5xx errors, wherever the slice is user-facing. Capture screenshots for real visual issues.
4. Run affected build, frontend checks and focused tests; review the diff and rules, then one commit + PR. Wait for required checks, audit and integrate into clean-start only if green and allowed by AGENTS.md. Never bypass hooks or CI or merge red/unfinished work.
5. Re-check the next relevant gap on the next cycle. If blocked, record evidence, leave a safe reviewable state and choose the next independent gate. Do not repeatedly poll an unchanged blocker, spawn competing agents, or start costly full suites on every cycle.
6. Keep a concise verified checklist and the exact commands/results for the final handoff; do not mark any gate green without an observed result.

## P0 — deployable morning candidate (in this order)

### A. Isolated deployment and test infrastructure
- Verify or add a minimal reproducible Docker/Compose **test** stack from the current clean-start build, without touching active instances. One Jularr application image/container; PostgreSQL may be a separate service until the approved supervised one-container process design (#1119) is implemented. No separate mandatory worker microservice. Use persistent test volumes and no embedded secrets. Workers must actually run or be explicitly started for acquisition/scan tests.
- Cover a fresh database, Setup/Owner, profile, enabled modules, storage/library creation, health/readiness, startup/restart, database availability and clear failure states.
- Extend existing tests/Jularr.E2E.Tests, not a competing harness. Use small lawfully generated media fixtures and fake TMDB/Newznab/SABnzbd where needed; no dependence on public provider accounts, paid APIs or live Usenet.
- Produce documented exact build/launch/reset/test commands suitable for the owner to execute tomorrow; isolated test data cleanup must be safe and bounded.

### B. Video playback
- In real UI: scan a locally readable video; open the correct Work and playable version; Play/Pause, seek/timeline, volume, fullscreen where supported, audio and subtitle track selection, speed, repeat/skip and next episode, progress and resume after logout/login or restart.
- Test both Direct Play and one actual FFmpeg conversion/remux route where supported, without fabricating playback success. Check subtitle timing after seek/speed change and failure/recovery paths. Include actual chapters/skip intro/recap/outro/credits/trickplay when the file supplies canonical markers.
- Do not regress central on-video transport layout or approved desktop design. A visible button must work or be visibly unavailable with an accurate explanation.

### C. Reading
- With real local fixtures: EPUB/book and light novel reading, chapter navigation, progress at an exact locator, reopen/resume, preferences, search/annotations when exposed; manga/comics CBZ/CBR/CB7 pages, RTL/LTR, spreads, lazy loading and resume; phone and desktop.
- For PDF: verify the actual conversion path if implemented. If conversion fails or is not present, use only the agreed original/browser-viewer/download fallback and disclose that Jularr Reader progress is unavailable for that fallback. Do not silently claim universal PDF support.

### D. Music
- With real FLAC/MP3 fixtures: artist → album → track, Play/Pause, seek, queue/next track, track-level progress/resume and background/mini-player lifecycle where present. Verify audio plays, not merely the listing.
- Waveform/wave bar, gapless/crossfade, synced lyrics/karaoke, EQ and ReplayGain are owner-requested follow-ups, but not a reason to fail the basic launch gate if still unimplemented. Never present decorative animated audio as actual track data.

### E. Browser end-to-end coverage
- Extend the agreed B1–B4 sequence: real Setup → library/media scan → discover/request → Wanted/search/download/import using fake services → Play/Read → saved progress → sign out/in → resume. Cover video and reading first, then music and the remaining media types if time permits.
- Cover deterministic negative tests: unavailable NAS, database restart, interrupted download, missing metadata, ambiguous release, audio/subtitle track error, conversion failure, forbidden profile action, JS exception and HTTP 5xx. Verify visible recovery rather than silent retries.
- Release smoke: build Docker image, start isolated stack, browse application through a published port, open/play/read actual fixtures, restart and verify persistence. Record which paths were truly tested.

## P1 — user-approved quality and advanced controls (after P0 is green)

- Polish real Clean Purple UI across Desktop 1440px, Mobile 390px and TV-width 1920px; verify light/dark, responsive flow, cover visibility, existing rounded/pill controls, accessible focus and no stray white backgrounds, dummy actions, clipped controls or horizontal overflow. Use existing component tokens and newer approved reference designs; avoid an unrelated redesign.
- Player corrections: independent A/V audio delay, subtitle delay, subtitle timing-rate / FPS drift (distinct from playback speed), two-point alignment where feasible. Allow an authorized Admin to save per-media-file and per-track correction for every profile, without editing the source file; personal override > file-level Admin correction > original. Validate exact file/track identity and version invalidation.
- Video chapters and intro/recap/outro/credits/preview segment controls: respect existing canonical manual/provider/detector precedence. Auto-skip must be opt-in per profile and manual skip never disappears if automatic detection is disabled.
- Music waveform: generate reduced cached peaks once on the server with existing FFmpeg and draw client-side without decoding the entire track on every client. Live visualizer is separate from seek waveform. Advanced lyrics, karaoke and audio effects are later independent features.
- Do not trade basic P0 readiness for speculative advanced features.

## Morning acceptance / stop criteria

A gate is PASS only with actual evidence:
- Isolated Compose stack builds/starts with declared volumes and health checks; no live data touched.
- Playwright setup/login and media Play/Read/Resume paths pass with real media; failures have readable states, screenshots/logs and reproducible steps.
- UI passes actual desktop/mobile checks, with no broken primary action and no new severe accessibility/JS/HTTP errors.
- Relevant focused tests and the final release validation required by .agent/project.yaml pass. Full suite is a morning release gate, not a step repeated on each slice.
- PRs required for the tested build are integrated after green checks. A branch-only change is not "deployed".

If a gate fails or time runs out: **do not say Jularr is finished, deployed or fully tested**. Produce a concise morning report: exact tested revision/branch, build and launch commands, PASS/FAIL per P0 gate, screenshot/test artifacts, merged PRs and pending PRs, reproducible blockers, and the next highest-value step. Do not perform a production deployment or reset data to improve a green count.

Stop making changes after the morning cutoff and provide the factual handoff. The loop must not run indefinitely or wake the owner for routine decisions.
