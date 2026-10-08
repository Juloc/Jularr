# Anime acquisition (shared request lifecycle)

Purpose: acquire Anime episodes. Anime is `WorkMediaType.Anime`; its canonical targets are `WorkSeason` / `WorkEpisode`. One request per anime (AniList entry) runs the same lifecycle as the other media: Wanted → search → `AcquisitionCore` → grab → Wanted pass follows the Operation → `CompletedDownloadImportService` → `AnimeImportExecutor` → coverage → next episode / upgrade.

Canonical owners
- `AnimeCanonicalEpisodes` (+ `AnimeEpisodesWantedSource`, 10-minute gate `AnimeCanonicalEpisodesState`, `AnimeUpgradeAssessor`): a `WorkEpisode` for every slot the anime is expected to have (local episodes, AniList match, episode-range mappings read by `AnimeAcquisitionInventory`); creates only, fills a missing `SeasonId`; `WantedAsync` reads the Wanted queue of anime episodes (Admin Wanted, panel, media detail).
- Mapping evidence (AniList entry and remote episode of a slot) stays in `AnimeMetadataService` mappings; a Jularr season is never an AniList entry.
- Installed coverage: `CanonicalVideoStorageBackfillService` attaches legacy files to canonical Assets.
- Monitoring: `AnimeMonitoring` (canonical decisions by season / episode number).
- Request: `AnimeAcquisitionRequestExecutor` (creates the series, applies the requested scope, then `AnimeAcquisitionEngine.SearchAndGrabAsync`), `AnimeRequestPayload` (episodes of the grab in flight, release, search back-off), `AnimeRequestDrafter` (requests for monitored anime), `AnimeRequestStarter` ("Search now": reconcile + request + make due), `AnimeWantedRequestHandler` (due search, failed download → blocklist unless the client's own storage failed, import done → next episode).
- Judge: `AnimeReleaseJudge` (title and aliases, wanted-episode / season-pack / absolute coverage through the AniList mapping, Sonarr ownership blocks); ranking and selection through `AcquisitionCore`.
- Sonarr coexistence stays policy: `AcquisitionOwnershipStore` + `SonarrParallelSafety` (read-only / parallel / Jularr-managed, job and path ownership; the ownership job id is the download Operation id).
- `AnimeManualGrabService`: the owner's grab of an interactive search result, claimed on the anime's request through `ManualGrabCoordinator` (nothing is grabbed twice).
- `AnimeAcquisitionScheduler` has no loop: the Wanted pass calls `AdvanceAsync` (startup recovery, owner "search now" runs, the AniList list auto-monitor every 30 minutes). Searching has no schedule setting: requests carry their own back-off.

Legacy and state
- `AnimeLegacyAcquisitionMigration` (run by the scheduler's startup recovery) moves downloads the old pipeline had in flight (`sabnzbd-acquisitions.json` relations) onto the anime's request and drops the relation; the file keeps only the blocklist (`SabnzbdAcquisitionStore`). Old Operations keep their kind `AnimeAcquisitionEngine.LegacyOperationKind`.
- `AnimeMonitoringStore` (`monitoring.json`) holds per-anime settings only; wanted episodes come from `WantedItems`, the episode states shown in admin detail and the calendar from `AnimeEpisodeStates` (Wanted rows + the request payload).
- `AnimeAcquisitionPipeline` stays as the interactive search, panel, overview and settings facade.

Tests: `AnimeLegacyAcquisitionMigrationTests`, `AnimeManualGrabTests`, `AnimeCanonicalEpisodesTests`, `AnimeAcquisitionPipelineTests`, `AnimeSharedManagerTests`, `AnimeRequestLifecycleTests`, `AnimeRequestAiredScopeTests`, `AnimeAcquisitionRequestExecutorTests`.
