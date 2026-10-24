# Shared download pipeline

## Approved directory layout

Every download is identified by its Jularr operation ID, never by a downloader-specific
directory name. The proposed **new-job** relative path below the configured
`incomplete` and `complete` roots is `{media-category}/j-{operation-id}/`.
Categories are `movie`, `tv`, `anime`, `books`, `manga`, `lightnovel`,
`music` and `games`. File names inside a job are not used to infer ownership.

The download source reports transport (Usenet, Torrent, HTTP or Browser); the
selection policy filters by compatible transport and enabled media types, then
applies priority (stable tie-break: connection ID). Provider categories do not
define a second independent import process.

## Ownership boundaries

- Downloader adapter: submit/pause/resume/query its remote job, write to or expose
  the assigned Jularr job directory, and report completed files.
- Central download manager: create the operation, validate mapped storage,
  track its state, only publish complete verified payloads, retry failures and
  schedule cleanup.
- Importer: accept operation ID, target work, media category and complete files.
  It must not parse provider names, prefixes or categories from the path.
- Cleanup owner: respect active jobs, seeding, hardlinks, path ownership and
  explicit deletion confirmations. Never delete libraries by matching names.

## Rollout compatibility

This PR implements the priority-selection rule on the existing Usenet/SABnzbd
route and the provider-neutral job **path contract with tests**. It does **not**
switch storage roots or move existing running jobs.

Follow-on wiring must preserve old `.jularr-<workspace>/<operationId>` folders
until their operations finish, then activate the new job layout for new jobs.
External SABnzbd and qBittorrent clients must confirm their own remote/local
mount mappings; a storage folder must not be moved while a torrent is seeding.
Incomplete and complete can be on different mounts: publish via atomic rename
only on the same filesystem, otherwise copy and verify before marking complete.

Dependency alignment:
- PR #1534 owns SABnzbd three-tab console, file browser and guarded cleanup.
  Do not replace its file-safety checks, ledger or UI.
- PR #1533 owns qBittorrent/Nyaa integration. On integration, reuse the shared
  selection and path contract. Its `0002_torrent_sources.sql` conflicts with
  PR #1534's `0002_sabnzbd_incomplete_paths.sql`; migration ordering must be
  reconciled without renumbering an already-deployed migration.
- Do not merge those two feature branches blindly or apply migration changes
  to an active database.

## Acceptance gates for full rollout

A single configured incomplete/complete root; every downloader writes or
exposes the matching media/job path; no source-specific import branches;
recovery after restart; multi-downloader priority/fallback; complete and
incomplete browsing with safe orphan cleanup; seeding-preserving import; and
live NAS/offline/permission tests. Those gates are not yet satisfied by this
first backwards-compatible slice.
