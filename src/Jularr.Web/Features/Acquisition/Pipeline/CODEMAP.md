# Anime acquisition (migrating onto the shared lifecycle)

Purpose: acquire Anime episodes. Anime is `WorkMediaType.Anime`; its canonical targets are `WorkSeason` / `WorkEpisode`.

Canonical owners (done)
- `AnimeCanonicalEpisodes` (+ `AnimeEpisodesWantedSource`, 10-minute gate `AnimeCanonicalEpisodesState`): creates a `WorkEpisode` for every slot the anime is expected to have (local episodes, AniList match, episode-range mappings read by `AnimeAcquisitionInventory`). It only creates; it fills a missing `SeasonId`; an anime without known slots gets nothing.
- Mapping evidence (AniList entry and remote episode of a slot) stays in `AnimeMetadataService` mappings; a Jularr season is never an AniList entry.
- Installed coverage: `CanonicalVideoStorageBackfillService` attaches legacy files to canonical Assets of the `WorkEpisode`.
- Monitoring: `AnimeMonitoring` (canonical decisions by season / episode number).

Still the old owners (to be replaced slice by slice)
- `AnimeAcquisitionScheduler` / `AnimeAcquisitionPipeline` (search, judge, ranking, grab), `SabnzbdAcquisitionService` + `SabnzbdAcquisitionStore` (submission and attempts), `AnimeMonitoringStore` attempt state, `AcquisitionOwnershipStore` (Sonarr ownership modes: kept as policy).

Tests: `AnimeCanonicalEpisodesTests`, `AnimeAcquisitionPipelineTests`, `AnimeSharedManagerTests`.

Wanted (done): anime episodes are reconciled by `WantedReconciler` like TV episodes (`WorkMediaType.Anime`, aired gate, installed = video asset on the `WorkEpisode`); `AnimeUpgradeAssessor` queues installed episodes below the profile's cutoff (profile assigned by legacy anime id). Nothing consumes the rows yet.
