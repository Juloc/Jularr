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
