# Download workspaces and release validation

New Jularr-owned native downloads use `incomplete/<media>/j-<operationId>` and
`complete/<media>/j-<operationId>` beneath their configured, protected workspace.
Existing numeric job folders remain readable. Existing jobs are never migrated
in place. Categories include movie, tv, books, anime, manga, lightnovel, music,
and games.

New qBittorrent torrents use `<client save root>/<media>/j-<operationId>`
as the client-visible save path, and Jularr maps the reported content path back
to the storage mount. The qBittorrent client still owns incomplete staging and
seeding: Jularr does not move or delete seeded payloads after import.

SABnzbd retains its own category, complete, and incomplete folder settings.
Jularr discovers configured complete folders and maps them explicitly. A
shared externally configured root for SABnzbd and qBittorrent is **not** yet
automatically provisioned or migrated. Existing mappings and downloads must
not be changed without verified mounting and provider tests.

## Selection and UI

`DownloadClientSelectionLogic` chooses compatible enabled downloaders by
transport and media type, excludes connections in retry backoff, and orders by
priority and stable connection ID. New torrent save roots are validated before
job reservation. Native and external downloads share the import owner and
Activity queue/history. The Activity **Files** view includes qBittorrent and
SABnzbd download jobs: torrent files are shown inline, while SABnzbd entries
link to its guarded queue/history/files browser. The two provider file
browsers have not yet been fully consolidated into a single component.

## Source cleanup safety

A source folder is eligible for automatic SABnzbd post-import deletion only if:

1. Its source scan completed and included at least one actual file.
2. Every listed file, including sidecars and previously handled manual
   imports, was accounted for in the import.
3. A SHA-256 fingerprint of the complete source inventory (relative paths,
   byte lengths, modification timestamps) was committed with the cleanup row.
4. The provider reports a completed history entry, not an active queue job.
5. The source is still under the explicitly mapped local path, is writable,
   and has the same fingerprint immediately before deletion.
6. The source directory is atomically renamed to an isolated sibling; the
   fingerprint and symlink/size protections are rechecked before deleting it.

A missing or changed fingerprint blocks automatic deletion and preserves the
file tree. Existing unverified cleanup rows are deliberately not authorized
to remove imported directories. Failed download job cleanup retains the
existing, separate policy. Manual orphan cleanup also uses an atomic
quarantine and a confirmed folder fingerprint; it refuses active or known
history jobs. A failed deletion is not restored as a partially deleted job.

Database migrations are **0002** (SABnzbd/incomplete/cleanup), **0003**
(torrent connection types) and **0004** (source-inventory fingerprint). Never
edit the frozen baseline or previously applied migrations.

## Release gates

- [ ] Fresh and existing database upgrades verified, backup/rollback tested
- [ ] Backend tests (including all shards), frontend, browser E2E, Docker image
- [ ] Real SABnzbd completed/failed/partially imported jobs and orphan cleanup
- [ ] Real qBittorrent seeding/restart/pause/delete behavior, existing torrents
- [ ] Real NAS offline, remount, shared mount mappings, read-only and low-space
- [ ] Final shared external incomplete/complete layout with a non-destructive
      rollout plan and complete Files UI, including migration of old settings
- [ ] Owner review of source cleanup, path safety, and container volume mapping

Do not merge only on the basis of green synthetic CI. This integration PR
overlaps #1533, #1534, #1548 and #1567; do not independently merge those
overlapping implementations without reconciling their behavior.
