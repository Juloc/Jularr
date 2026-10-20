# Prowlarr parity

Owner decisions of 19 October 2026. Jularr manages its indexers itself and takes Prowlarr over once (import only).
Indexer settings follow the [Sonarr decisions](PARITY_SONARR.md); notifications, system and access as decided there.
Build state is tracked in the [feature matrix](FEATURE_MATRIX.md).

## Same

Newznab indexers, also without a key, with presets, priorities, categories, capabilities, media scope, manual-only,
daily query and grab limits, a pause after failures and a test search; Prowlarr's indexers taken over once.

## Wanted

| Area | Prowlarr | Jularr today | Decision |
| --- | --- | --- | --- |
| Search | A search over all indexers: free text or TV, film, music and book search with ids, categories, grab one or many | Interactive search per target | Like Prowlarr; a grab without a matching Work goes to the manual import |
| Statistics | Queries, grabs, failures and response time per indexer over 7, 30 or 90 days | – | Like Prowlarr, plus a notice before a VIP membership expires |
| History | Every query with type, parameters, results and elapsed time; grabs, RSS, authentication, failures | Grabs in the media history | Like Prowlarr |

## Later

| Area | Prowlarr | Decision |
| --- | --- | --- |
| Proxies | HTTP, SOCKS4/5 and FlareSolverr per indexer | All later |

## Not

| Area | Prowlarr | Reason |
| --- | --- | --- |
| Apps | Syncing indexers to Sonarr, Radarr and the other apps; sync profiles | Jularr is the app |
| API | Newznab endpoint per indexer for other programs | Jularr replaces the *arrs; they keep their own indexers while running in parallel |
| Indexers | Torrent and Cardigann definitions | Usenet only |
