---
name: jularr-acquisition
description: Jularr Arr/acquisition work (Sonarr/Radarr/Readarr/Lidarr, Anime, Manga/LN, Wanted, Manual Search, profiles, upgrade/cutoff, import) on the single shared pipeline. Extends jularr-task.
---

# Jularr acquisition

Extends `jularr-task`. Read only the SPEC that owns the touched stage: `docs/ACQUISITION_SEARCH_PLANNER.md` (search), `docs/AUTOMATIC_RELEASE_SELECTION.md` (selection), `docs/ANIME_ACQUISITION.md`, `docs/READING_ACQUISITION.md`, `docs/MEDIA_CORE.md`; owner code is `src/Jularr.Web/Features/Acquisition` and Library/Storage. Jularr is usenet-only: never add torrent clients or indexers.

## One pipeline for all media

Request / Monitor -> Wanted target -> Search Planner -> normalized Release Candidate -> Selection Engine -> download submission -> SABnzbd -> Completed Download -> Import -> LibraryRoot -> canonical Library.

Media-specific code supplies facts only: identity, numbering (volume/chapter, season/episode, album/track), language, parsed release facts, format support. It must not create another final scorer, scheduler, downloader monitor, Operation model, retry system, import lifecycle, Wanted store or progress state machine. Movie/TV/Books/Music are not copies of Anime.

## Rules per stage

- **Search**: shared Planner; bounded paging; capability aware; provenance; dedupe; timeouts and backoff. Manual Search and automatic search use the same planner and evaluator.
- **Selection**: identity/hard constraints first, then Require/Reject, quality/fallback, Prefer/Avoid, coverage, bounded reliability, deterministic tiebreak, explainability. A positive score never repairs invalid identity.
- **Import**: shared completed-download dispatch; media adapters normalize and validate; Storage owns the destination through LibraryRoot; remote path mapping is shared.
- **Long-running state**: persisted, restart-safe, idempotent (double submit, retry after restart, duplicate completion).
- **Upgrade**: use the canonical Version/Asset/File state, no parallel upgrade architecture. A temporarily acceptable version may stay Wanted; cutoff/final state stops churn.
- **Failures**: an infrastructure or storage outage (SABnzbd down, root unavailable, path mapping error) is never a bad release and must not blacklist or reject candidates.
- **Consumer vs admin**: the consumer sees intent-based actions (see `jularr-ui`); acquisition details stay behind them.

## Tests

Focused behavior tests on the changed stage (planner/evaluator/state transition/importer) per the `jularr-task` budget; PostgreSQL-specific behavior on real PostgreSQL; no real SABnzbd/indexer calls.
