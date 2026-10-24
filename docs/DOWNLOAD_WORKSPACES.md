# Download workspaces

A download belongs to a Jularr operation ID, not a downloader-specific folder. New native jobs use the same category plus ID under incomplete and complete storage:

```text
incomplete/books/j-41/...
complete/books/j-41/...
incomplete/anime/j-42/...
```

The same category rule covers movies, TV, books, anime, manga, light novels, music and games. Completed files still enter the existing generic library importer. New jobs do not acquire SABnzbd-, qBittorrent-, HTTP- or browser-specific directory layers.

## Scope and migration safety

This PR builds on #1566 and updates the **native Usenet and Direct downloader** paths only. The existing settings currently keep separate protected native `.jularr-<workspaceId>` prefixes; moving to one configurable shared root must happen after coordination with the SABnzbd and qBittorrent integrations. Old numeric job folders remain readable/cleanable, and no active directories are moved. Only bounded operation-owned workspace paths are eligible for native deletion. Seeded torrent files and external-client paths are not altered.

- #1534 owns SABnzbd queue/history/files and safely confirmed deletion; retain its protections.
- #1533 owns qBittorrent/Nyaa; connect its adapter to this job identity before shared folder rollout.
- #1548 owns failure cleanup; it must keep calling the canonical workspace cleanup.
- #1533 and #1534 both add `0002` migrations and must be reconciled for an ordered schema chain before merging.

A real NAS/SABnzbd/qBittorrent integration test and shared-root configuration remain separate release gates.

## Downloader selection and overlap

Compatible transport and supported media types are filtered **before** ascending priority and stable connection-ID tie breaking. This contract is centralized in `DownloadClientSelectionLogic`; the previous special preference for media-scoped connections is removed. This incorporates priority-selection work from #1567 without introducing a second download-folder layout. #1567 is an overlapping open PR and must **not** be merged independently in its current form.

## Consolidated integration branch

The consolidated draft #1572 brings together #1533 (qBittorrent/Nyaa), #1534 (SABnzbd console and persistent cleanup), #1548 (terminal direct staging cleanup) and the downloader selection from #1567. The database upgrades are ordered as SABnzbd version 2 then torrent connection types version 3. This does **not** migrate previously created jobs or client-owned paths.

**Current transport boundaries:** native Usenet/Direct use the media-scoped Jularr job directories; qBittorrent and SABnzbd still receive their own configured save/location mappings. All paths eventually arrive at the existing import owner. Do not infer that the external client payloads already have the new folder structure, and do not delete live torrent payloads or leftovers from partially imported SABnzbd jobs.

**Release prerequisites:** frontend/backend build and tests on the integration head, both numbered migrations on a fresh and upgraded isolated database, real external client + NAS/remount checks, explicit policy for leftover files and seeding, plus the final unified Files experience. Nothing here authorizes an automatic merge.

## Partial import protection

Automatic SABnzbd source deletion requires every enumerated source file to have been included in the import plan. Finishing a manual import never schedules deletion of unselected source files. Recovery backfills failed-job cleanups only: it cannot infer that a completed import had no leftovers, and never retroactively deletes completed sources.
