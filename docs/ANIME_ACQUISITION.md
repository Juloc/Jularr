# Anime acquisition

Jularr can find, download and import missing anime episodes itself: wanted episode → indexer
search (Prowlarr and/or direct Newznab) → release parsing and scoring → ownership check → a
download client (SABnzbd) → Operations tracking → import into the library with the naming
profile → library reconciliation. Everything runs in-process; there is no separate service.

Jularr is usenet-only by design: torrent acquisition (Torznab indexers, qBittorrent or any other
torrent client, magnet links or `.torrent` handling) is intentionally unsupported. Prowlarr may
still aggregate torrent indexers on its own side; a torrent-protocol result it returns is scored
but never grabbed (see Limits).

Code: `Features/Acquisition/Pipeline` (inventory, pipeline, scheduler) and
`Features/Acquisition/Import` (import store, destination, executor). The pipeline only wires the
existing cores: release parser, quality profiles and scorer (`Quality`), indexer search
(`Indexers`, one `IIndexer` interface with Prowlarr and direct Newznab implementations),
monitoring engine (`Monitoring`), Sonarr ownership (`Ownership`), download client submission
(`DownloadClients`, one `IDownloadClient` interface with SABnzbd as its only implementation;
`Sabnzbd` keeps the anime-specific attempt/blocklist relation and the raw SABnzbd protocol client),
completed-download planner (`Import`) and naming (`Naming`). Periodic health checks
(`Features/Acquisition/Health`) test every enabled indexer and download client; an unhealthy entry
is skipped by the search coordinator/client selector with a logged reason. The direct Newznab
client's HTTP calls run through the shared external-provider framework (`Features/Providers`, #438 —
timeouts, bounded retries, rate-limit/Retry-After handling, response caching and per-provider
health/circuit) so new provider families do not re-implement networking; per-entry indexer/client
health stays canonical in `AcquisitionHealthStore`.
`Features/Acquisition/Api` exposes the owner-only automation API described below; it calls the same
services and adds no state of its own beyond the API keys themselves.

## Setup (owner)

1. **Indexers** — `/Settings/Indexers`: add Prowlarr and/or direct Newznab connections (URL, API
   key stored encrypted, categories, priority, enable/disable). The search coordinator queries
   every enabled, healthy indexer and merges the results; only Prowlarr honors a per-anime
   indexer-ID restriction.
2. **Download clients** — `/Settings/DownloadClients`: add one or more SABnzbd connections (URL,
   API key stored encrypted, categories, priority, enable/disable). The pipeline and Books
   submissions pick the highest-priority enabled, healthy client and fail over to the next one on
   submission failure. SABnzbd's completed-job folder must be visible to Jularr under the path
   SABnzbd reports (see [ADMIN_OPERATIONS.md](ADMIN_OPERATIONS.md#sabnzbd)).
3. **Management mode** — anime start in read-only Sonarr coexistence, where Jularr never
   searches, grabs or imports. Choose *Parallel acquisition* or *Jularr-managed* per anime under
   `/Settings/SonarrMigration` (see [SONARR_MIGRATION.md](SONARR_MIGRATION.md)).
4. **Per anime** — the **Acquisition** section on the anime page (`/Library/Anime/{id}`):
   *Monitored*, *Search when monitoring starts*, quality profile and optional Prowlarr indexer IDs.
5. **Naming** — imported files are named with the anime's naming profile
   ([ANIME_NAMING.md](ANIME_NAMING.md)).

The overview is `/Acquisition` (linked from Admin → System and from every anime's acquisition
section): schedule, connections, imports that need a decision, active downloads, wanted episodes,
monitored anime, recent decisions and recent imports.

## Where state lives

| Fact | Canonical store |
| --- | --- |
| Monitored flag, search-on-add, per-anime indexer IDs, target root, wanted episodes, search attempts/backoff, schedule | `/data/acquisition/monitoring.json` (`AnimeMonitoringStore`) |
| Quality profile per anime | `/data/acquisition/quality-profiles.json` (`QualityProfileStore`, keyed by media type; the default profile is not stored as an assignment) |
| Indexer connections (Prowlarr, direct Newznab) | `/data/acquisition/indexers.json` (`IndexerStore`) |
| Download client connections (SABnzbd) | `/data/acquisition/download-clients.json` (`DownloadClientStore`) |
| Indexer/download-client health (reachable, auth ok, last error, last check) | `/data/acquisition/health.json` (`AcquisitionHealthStore`) |
| Acquisition ↔ anime/episodes/attempts, untried candidates, blocklist | `/data/acquisition/sabnzbd-acquisitions.json` |
| Download status, progress, failure reason | the download client's Operation (`anime-sabnzbd-download`) |
| Import plan, per-file result, manual-import state | `/data/acquisition/imports.json` (`AnimeImportStore`) |
| Ownership (mode, Jularr/Sonarr jobs, owned paths) | `/data/acquisition/ownership.json` |
| Default import mode of the media types without a LibraryRoot, per-media-type folders and remote path mappings, lossless playback optimization | `/data/acquisition/import-settings.json` (`AnimeImportSettingsStore`) |
| Per-profile AniList Current/Planning auto-monitor opt-in | `/data/acquisition/anilist-auto-monitor.json` (`AniListAutoMonitorSettingsStore`) |
| Per-episode grab/delay/import/upgrade history | the database (`AcquisitionHistoryEntry`, via `AcquisitionHistoryService`) |
| Automation API keys (name, SHA-256 hash, created/last-used/revoked) — never the raw key | the database (`AcquisitionApiKey`, via `AcquisitionApiKeyService`) |
| Episodes and files | the library database, updated only by the library scanner |

`/Settings/Acquisition` is the one place to edit the default import mode, playback optimization, remote path mappings, the current profile's AniList auto-monitor rule, and to export or
restore a JSON backup of every store above except `health.json` (runtime health state, not a
setting, so it is never part of the backup). Indexer and download client API keys/passwords travel
in that backup encrypted with this installation's Data Protection keys, never in plain text.

When a rename changes an anime's key (series folder rename), the monitoring and import stores
are rekeyed together with the ownership and SABnzbd acquisition stores.

## Wanted episodes

`AnimeAcquisitionInventory` builds the expected episodes of an anime from the library and the
AniList data: explicit AniList episode-range mappings first (extended to the AniList episode count),
otherwise the matched AniList entry's episode count for a single local season. Several local
seasons without mappings, or an anime without AniList match, only track existing files (the anime
page and the run notes say why). Each expected episode keeps its local season/episode and the
AniList (absolute) episode number, and is searched with the titles of the AniList entry it maps to.

An episode is wanted when it is monitored and has no file (*Missing*), or when its file's parsed
quality is below the profile's upgrade cutoff and upgrades are allowed (*Upgrade wanted*). A file
whose quality cannot be parsed is never offered for upgrade.

## Scheduler

Anime has no loop of its own. The shared Wanted pass (every 2 minutes, `WantedAcquisitionService`) calls
`AnimeWantedSource`, which asks `AnimeAcquisitionScheduler` to advance: it recovers after startup once, runs the
queued owner requests (a requested search wakes the pass at once, so Search now and search-on-add do not wait for its next turn), and otherwise runs the pipeline for all monitored anime when the one canonical interval
(`AnimeMonitoringSchedule`, default every 30 minutes, 5 minutes to 24 hours, editable on
`/Acquisition`) has elapsed; switching it off keeps owner-requested searches working. The first run starts
45 seconds after startup. Because the pass owns the cadence, an interval is honoured to the pass granularity
(2 minutes) and a disabled Anime or Acquisition module is cold: the pass skips the source.

- Runs never overlap: periodic runs, **Search all now**, **Search wanted now** for one anime,
  search-on-add and interactive grabs all go through one gate.
- Each run searches at most 30 episodes, 6 per anime; the rest wait for the next run.
- Owner requests are queued (up to 50) and processed in order; a full run covers queued per-anime
  requests.
- A failed search (no accepted release, Prowlarr error, rejected submission) backs off
  exponentially (5 minutes, doubling up to 160 minutes). **Search wanted now** ignores the backoff but never
  searches an episode that is pending or already grabbed.

## Requests

A Discover request for an anime (`AnimeAcquisitionRequestExecutor`) only creates or finds the series, puts it under
monitoring with the requested scope and queues a search; it never grabs or imports itself. The request then follows the
shared request lifecycle, read back from this pipeline by the shared Wanted pass (`IMonitoredAcquisitionExecutor`,
`AnimeRequestObservation`, applied through `AcquisitionRequestService.FollowMonitoredAsync` with the conditional status
transition), which never starts a search:

- **Approved** (consumer: looking for media): nothing is downloading yet, or the series is Sonarr-managed read-only. The request is not
  linked to any download in this state.
- **Downloading**: an acquisition covering a missing requested episode has an active download Operation (linked to the request).
- **Importing**: that download finished and its import is running or waiting.
- **Failed**: the importer ended with a decision only the owner can make (the importer's reason is shown). The request keeps that download
  linked and the pass keeps reading it: when the owner resolves the import (manual import, dismiss) the next pass completes it, or puts it
  back to Approved when episodes are still missing. A request that failed for another reason (no library root, unknown AniList entry) is not
  read back; the owner retries it.
- **Completed**: every requested episode that has aired has a file, and at least one does.

**What is requested.** The request covers the episodes of its scope (whole series, seasons or episodes) that are monitored and that the
pipeline tracks (the inventory's slots: the episodes the AniList count or mappings expect, plus the ones the library has), minus the
ones known not to have aired yet. An episode counts as aired when it has a file, or when its number is at most the highest number known
to have aired for its AniList entry: every number of a finished entry, otherwise everything up to the latest past release in the cached
release calendar (the AniList airing schedule, no provider call) and everything before its next upcoming release. That monotone rule also
covers episodes older than the calendar's roughly month-long window, so a request made at episode 14 of a long run still needs episodes
1 to 13. An entry the calendar has no row for (a releasing series whose airing data has not been fetched yet) keeps all its tracked
episodes in the request, so it is never reported complete before every one of them has a file. Episodes that air later are picked up by
the series' monitoring and never keep the request open, also for a request of the whole series. Specials are only requested through the
files they have: a missing special is not expected. A request is Completed when every requested episode has a file and at least one does.

**Nothing tracked.** A matched entry without an episode count (or a series with several local seasons and no mappings) expects no episodes,
so the pipeline tracks and searches only what the library has. Such a request is never reported Completed when nothing is tracked for its
scope, or when the calendar shows more episodes aired than the library has (an entry that is finished without a count says nothing about
how many, so it does not count). It stays Approved with a message of its own, "Not available yet. There is no episode list for this
title, so nothing can be searched yet.", rather than "Looking for the requested episodes." When no indexer is set up, an otherwise
searching request says "Not available yet. Searching is not set up on this server."

**Approval and execution.** Creating the series and starting monitoring is the executor's job. An Approved request whose series does not
exist yet is therefore left as it is by the pass (it may be waiting for the executor, for example right after approval). One that has
waited `WantedAcquisitionService.StaleSearchingAfter` untouched is run again by the pass (a crash after the approval, an Anime module that
was off when it was approved), a bounded number per pass; only a request already past Approved reports a missing series as Failed. When
the executor fails a request for good it unlinks any download the request had, so the failure is not overwritten by a read-back; a failure
it throws (the monitoring state could not be read, say) may be transient and keeps the link, so the owner's pending decision survives.

**Cost and bounds.** Per pass the monitoring state, ownership, acquisition relations and import records are loaded once and shared by all
requests, and the observation reads the episode slots without titles, so it makes no provider call. Open requests are read in batches of
`WantedAcquisitionService.FollowBatchSize` by id, so every request is reached however many there are. A request whose observation throws
is logged once with its request id and cause and left as it is; it never stops the other requests or the manual-download import after it.

**Notifications.** The requester gets one "release available" notice per download, when the request enters Downloading from Approved;
following the same download again, a retry while it runs, or the move between its stages does not repeat it.

Completed is never reported earlier, so a requested title is not shown as available before its media exists. Requests that were
completed under the earlier behavior (on start) are left as they are.

### Known gaps

- **TV and Anime disagree on "current + future".** A TV request with future monitoring (`VideoRequestPayload.MonitorFuture`, "All current +
  future") stays open and Approved while it waits for the next episode (`KeepOpen`, asserted in `RequestToPlayTvTests`), whereas an Anime
  request completes once its aired scope is satisfied and monitoring continues on the series. The status surface specifies "Available" and
  a separate "Monitoring future releases" state, but no consumer state for the latter exists in `src`. Proposed unification: both complete
  when the aired requested scope is satisfied, and the consumer acquisition projection (the Instant Play backend slice) exposes
  "Available now" together with "Monitoring future releases" from the monitoring state rather than from the request status.
- **Entries without an episode count are not searched.** The legacy pipeline only tracks existing episodes for them
  (`AnimeAcquisitionInventory`), so such a request cannot make progress by itself and shows the "no episode list" message until files appear
  by other means or the count becomes known. The proper fix belongs to a pipeline slice: seed the expected slots from the calendar's
  highest aired number, so the pipeline searches them and the request's rule needs no special case.
- **The aired maximum is derived, not stored.** After roughly a week of failed calendar refreshes the cache no longer lists upcoming
  releases and the request can complete early on stale data; an airing that is rescheduled can lower the maximum again, because nothing
  persists the highest number seen; a CANCELLED or HIATUS entry without calendar rows keeps all its tracked episodes in the request, so it
  stays open until every one has a file.
- **Notice race.** If the Wanted pass and an executor run move the same request into Downloading at the same moment, the requester may
  receive two "release available" notices for one download (same dedup key, so one entry that becomes unread again). Fixing it needs the
  notified operation persisted on the request.

## Search, scoring and grab

For each wanted episode the pipeline creates an `anime-search` operation, queries every enabled,
healthy indexer entry (Prowlarr and direct Newznab, narrowed by the profile's allowed sources) with the episode's titles, and evaluates every result:

1. usenet with an NZB link, otherwise rejected;
2. the parsed series title must match one of the anime's titles;
3. the release must cover the wanted episode (season/episode, or the AniList absolute number);
4. the quality profile must accept it (allowed qualities, sizes, required/forbidden terms, score);
5. the release must not already be pending/grabbed and `SonarrParallelSafety.CanGrab` (with a fresh
   Sonarr snapshot) must allow it;
6. for an upgrade, it must be better than the current file.

Every decision is written to the search operation's log (module `Acquisition`) with its reason,
quality, score and indexer; `/Acquisition` shows the recent ones. Accepted releases, best first, go
to `SabnzbdAcquisitionService.StartAsync`, which sends the first non-blocklisted one and keeps the
rest as fallbacks. The pipeline then registers an Jularr ownership job for the acquisition and
marks the covered episodes as grabbed.

A new grab for an episode is refused while an earlier acquisition for it is still downloading or
its completed download waits for (manual) import. This check reads the acquisition relation and
Operations, so it also holds after a restart or with a lost monitoring state.

**Interactive search** (`/Acquisition?search=…`, from a wanted episode or an anime) shows the same
evaluation. **Grab** sends a release; **Grab anyway** sends a release the profile rejected. Ownership
and duplicate protection always apply.

## Download tracking

The existing SABnzbd monitor projects queue/history onto the download operation (progress, ETA,
failure reason). A failed download is blocklisted and the next candidate is sent, up to 3 attempts
(see [ADMIN_OPERATIONS.md](ADMIN_OPERATIONS.md#sabnzbd)). When all candidates are exhausted the
episode backs off and is searched again later. Cancelling a download on its operation page stops it without trying
another candidate; the episode also backs off and is searched again later unless it is unmonitored.

## Import

Anime uses the same completed-download spine as every other media type. When an anime download
completes, the SABnzbd monitor calls the shared `CompletedDownloadImportService`. It asks the exact
download client recorded on the download for the completed path, applies the **Anime** remote path
mappings, hands the files to the Anime adapter (`AnimeImportExecutor`, an
`ICompletedDownloadImportAdapter` behind `CompletedDownloadDispatcher`) and records the reported path,
mapped path, destination, import mode and result on the download operation. The shared layer also
lists and classifies the download's files (`CompletedDownloadFiles`) and commits each file
(`LibraryFilePlacer`); the Anime adapter keeps the episode mapping, Sonarr ownership, naming and
library reconciliation:

1. Wait if a library scan or rename is running (the import stays *Importing* and continues before
   the next scheduler run). Renames likewise refuse to start while an import runs.
2. List the files of the completed folder (shared).
3. Plan with `AnimeImportPlanner` (#298): map every file to the requested local
   episodes (season/episode or AniList absolute number), score it, detect existing files and apply
   `SonarrParallelSafety.CanImport`/`CanMutateLibraryPath`. Only confident single-anime matches are
   imported automatically; everything else needs a decision.
4. Build the destination with the naming profile resolved for the anime (anime, library root,
   default): the anime's existing series folder (a new folder is named by the series folder
   template), the season folder and the episode template. The name must scan back to the same
   anime key and episode; otherwise the file needs a decision.
5. Claim the destination as an Jularr path and check `CanMutateLibraryPath` again; Sonarr-owned
   or Sonarr-active paths are never touched. An existing destination is never overwritten.
6. Place the file with the import mode (shared `LibraryFilePlacer`): the matching subtitle/NFO sidecars
   follow it, and an existing worse file is deleted only after the new file is in place.
7. Reconcile only that anime's folder with the library scanner, so the episode appears with the
   planned numbering.
8. When *Lossless playback optimization* is enabled, queue one `media-optimization` operation for
   the imported files. It runs after the import, remuxes to MP4 without re-encoding only when that
   widens browser Direct Play and nothing is lost, and otherwise keeps the file as downloaded
   (see [ADMIN_OPERATIONS.md](ADMIN_OPERATIONS.md#lossless-playback-optimization)).

The result is kept as an import record and logged on an `anime-import` operation (module `Import`).

### Needs a decision

Files the planner could not map confidently, destinations that exist or are owned by Sonarr,
names that would scan as another episode, and failed moves (read-only or unwritable library
folders) are listed under **Needs a decision** on `/Acquisition` with the reason. The owner can
import a file as a chosen season/episode (ownership and destination checks still apply) or dismiss
the import; dismissing leaves the downloaded files untouched. The episode stays grabbed while its
import waits for a decision, so it is not downloaded again.

## Restart recovery

On startup, and before every scheduler run, the scheduler:

- resumes imports that were interrupted or deferred,
- imports anime downloads that completed within the last 7 days while Jularr was not running
  (through the shared import step: the path comes from the download's own SABnzbd connection, so a
  download whose files cannot be located is retried and, 24 hours after it finished, ends as a failed
  import on **Needs a decision**),
- reconciles search attempts with the acquisition relation and Operations: an interrupted search
  whose release SABnzbd already accepted is recorded as grabbed (and its ownership job registered)
  instead of being searched again, finished imports clear the attempt, and failed or exhausted
  acquisitions back off.

The SABnzbd monitor separately advances failed downloads that were not handled before a restart.

## Import mode and remote path mapping

How a completed download becomes a library file is the placement policy of the LibraryRoot it goes to, set in
Admin → Storage like for Movie, TV and Music (`/Settings/Acquisition` lists each root's placement and links
there): **Move** (removes the source), **Copy** (keeps the
source), **Hardlink** (no extra disk space, keeps the download seeding; fails the import with a
clear reason when the source and the library root are on different filesystems) or **Hardlink or
copy** (the explicit choice to fall back to a copy only on a different filesystem — no other mode
falls back silently). A new anime goes to the root assigned to it, else to the Anime default root of Admin →
Storage (the only Anime root when there is exactly one); with several Anime roots and no default the import
waits until the owner chooses one. An anime that already has a folder keeps importing into its root. The
`/Settings/Acquisition` page's remote path mappings (`RemotePrefix` → `LocalPrefix`) belong to a
media type: the mappings of a media type rewrite a path the download client reports for that media
type before anything reads it, and the **Anime** mappings also rewrite every Sonarr-observed path
(series folder, episode file, queue output, history) the same way, so Sonarr ↔ Jularr path matching
(ownership checks, rename-loop detection) still works when the two containers mount the shared
storage differently — closing the "different mount paths" gap from
[SONARR_MIGRATION.md](SONARR_MIGRATION.md). `AnimeImportSettingsState.TranslatePath(kind, path)` is
the one translation everything uses. Earlier versions had one global list; it is copied into every
media type once when the settings file is first read, so existing setups keep translating exactly as
before and each media type's list can then be pruned.

The same import modes and remote path mappings apply to Manga, Light Novels and Books through the
shared `ImportFileTransfer` and `CompletedDownloadLocationResolver`; their folders are set under
Settings → Acquisition → Media folders (see [READING_ACQUISITION.md](READING_ACQUISITION.md)).
Every completed download, Anime included, shows the reported path, the mapped local path, the
destination and the import mode on its operation page.

### Choosing folders: the folder browser

Every local path field on `/Settings/Acquisition` (the library and inbox folder of each reading
media type and the local side of a remote path mapping) has a **Browse** button. All of them open
the one shared folder browser (`Features/Storage/FolderBrowse`, partials `_PathField` and
`_FolderBrowser`); typing the path stays possible. It is an Owner-only view of the file system as
the Jularr container sees it, not a file manager:

- The starting points are the volumes mounted into the container (read from `/proc/self/mountinfo`
  at request time, so nothing about an installation is hardcoded) and `/data`, each with its state:
  writable, not writable, read-only mount, not readable or not responding. The image's own system
  mounts (`/proc`, `/etc`, `tmpfs`, the image layers, `/`) are not offered. A host path that is not
  mounted into the container does not exist for the browser.
- Only folders are listed, never files. Every path is normalized, `..` is refused and every symbolic
  link on the way is resolved: a link that leads out of the mounted storage is neither listed nor
  followed, for browsing, checking and creating alike.
- **New folder** works only where the runtime user (UID 1654) may write and the mount is not
  read-only. Names are trimmed and validated on the server (no separators, no `:*?"<>|`, no control
  or direction-override characters, no trailing dot, at most 255 bytes); a bad name is refused, never
  rewritten.
- Under each field a check shows what the container sees at the typed path (exists, readable,
  writable). Library and inbox of one media type must not be the same folder (saving is refused) and
  should not be nested inside each other (a warning).
- Below the remote path mappings, **Test with a path** takes a path exactly as Sonarr or SABnzbd
  reports it, applies `AnimeImportSettingsState.TranslatePath` (the importers' translation — a
  mapping still being typed is applied as adding it would apply it) and shows whether the mapped path
  exists inside the container. A media manager sees the mapped path, but not whether it exists.

## Multiple root folders

An anime's imports go to the library root its existing files already live in. A brand-new anime
with no folder yet uses its assigned **target root** (anime page → Acquisition, defaulting to
"anime's current root") if one is set, otherwise the first enabled root.

## Richer acquisition history

Every grab, delay, import and upgrade is recorded per episode (release, score, quality, indexer,
reason, timestamp) and never trimmed by rekey (it is keyed by the anime's database ID, not its
string key). Shown on `/Acquisition` (recent, across anime) and on the anime page's Acquisition
section (that anime only).

## AniList list auto-monitor

Per profile, `/Settings/Acquisition` can opt in to monitoring anime that are on that profile's
AniList Current/Planning lists and already exist locally, checked once per full acquisition run.
Off by default; never adds an anime that is not local yet, and never touches an anime Sonarr
manages (read-only coexistence).

## Backup and restore

`/Settings/Acquisition` exports one JSON bundle of every canonical acquisition store listed above
(indexers, download clients, monitoring, import settings, policy, ownership, etc. — but never
`health.json`, which is runtime state, not a setting) and restores it with a dry-run preview
(per-file exists/valid-JSON/would-change) before anything is written; an invalid or
unsupported-version bundle is refused entirely. Indexer and download client API keys/passwords
travel in the bundle already encrypted with this installation's Data Protection keys; restoring on a
different installation keeps everything else but may require re-entering those credentials if they
cannot be decrypted there.

## Automation API

`Features/Acquisition/Api` exposes an owner-only REST API at `/api/acquisition/v1` for an external
automation client (a script, a Sonarr-style scheduler, a dashboard). Every write goes through the
exact same services the pages above call (`AnimeAcquisitionPipeline`, `AnimeAcquisitionScheduler`,
`AnimeImportExecutor`) — there is no second code path, so the API can never desync from the owner
UI. Errors are RFC 7807 problem-details JSON (`application/problem+json`) with a clear `detail`.

### Authentication

Two ways to authenticate, both resolving to the owner account:

- **Cookie**: the normal browser session (`/Account/Login`), same as every other owner page.
- **API key**: an `X-Api-Key: <key>` header. Keys are created, listed and revoked on
  `/Settings/ApiKeys` (owner only). The raw key is shown exactly once, right after creation; only a
  SHA-256 hash is ever stored, so it cannot be recovered or shown again — only revoked and
  replaced. A key is scoped to this API alone: it is wired into no other endpoint group, so a
  leaked key cannot sign into the browser UI or the native client API.

An invalid, malformed or revoked key returns `401`; an authenticated non-owner session returns
`403`. Requests are rate-limited (`acquisitionApi` policy, 60/minute per key or per IP for a cookie
session).

### Endpoints

| Method & path | Purpose |
| --- | --- |
| `GET /monitored` | Monitored anime with management mode, assigned quality profile, wanted-episode count and Prowlarr indexer-id restriction. |
| `GET /anime/{animeId}/monitoring` | One anime's monitoring settings: monitored, search-on-add, quality profile, indexer ids, target root, management mode. |
| `PUT /anime/{animeId}/monitoring` | Sets the same fields (same call as the owner's Acquisition settings form). Refused with `409` for an anime in read-only Sonarr coexistence. |
| `GET /wanted?animeId=` | Wanted episodes (missing or upgrade-wanted), optionally filtered to one anime. |
| `POST /search` | Queues a search for every monitored anime (same request "Search now" sends) and returns a tracking operation id. `409` when the scheduler's request queue is full. |
| `POST /anime/{animeId}/search` | Queues a search for one anime's wanted episodes. `409` for a read-only Sonarr-coexistence anime or a full queue. |
| `GET /operations?status=&kind=&limit=` | Recent search/grab operations (add `kind=anime-import` for import operations). |
| `GET /operations/{operationId}` | One operation's snapshot and log entries. |
| `GET /history?animeId=&limit=` | Richer per-episode acquisition history (grab/delay/import/upgrade/skip), optionally for one anime. |
| `GET /imports?attention=true` | Manual-import records; `attention=true` limits to those needing a decision. |
| `POST /imports/{recordId}/resolve` | Imports a file as a chosen season/episode (`{"sourcePath","season","episode"}`), same as the owner's manual-import form. |
| `POST /imports/{recordId}/dismiss` | Dismisses a manual-import record; downloaded files are left untouched. |
| `GET /health` | Indexer and download-client health summary (enabled, reachable/auth-ok, last error, last checked). |

### Examples

```bash
curl -H "X-Api-Key: $KEY" https://jularr.example/api/acquisition/v1/monitored

curl -H "X-Api-Key: $KEY" -X POST https://jularr.example/api/acquisition/v1/anime/$ANIME_ID/search
# => 202 {"operationId":"...","message":"Search for 'frieren' queued. ..."}

curl -H "X-Api-Key: $KEY" -H "Content-Type: application/json" \
  -X PUT https://jularr.example/api/acquisition/v1/anime/$ANIME_ID/monitoring \
  -d '{"monitored":true,"searchOnAdd":true,"qualityProfileId":null,"indexerIds":[],"tagIds":["dub"],"targetRootId":null}'

# Sonarr-owned anime: refused, not silently ignored.
curl -i -H "X-Api-Key: $KEY" -X POST https://jularr.example/api/acquisition/v1/anime/$ANIME_ID/search
# HTTP/1.1 409 Conflict
# Content-Type: application/problem+json
# {"type":"...","title":"Refused.","status":409,
#  "detail":"'frieren' is in ReadOnlyCoexistence mode; Sonarr owns this anime, ..."}
```

## Limits

- Jularr is usenet-only by owner decision: torrent acquisition (Torznab indexers, qBittorrent or
  any other torrent client, magnet links or `.torrent` handling) is intentionally unsupported and
  will not be added. The anime pipeline searches every enabled, healthy indexer (Prowlarr and
  direct Newznab) and scores every result, but a release is only ever grabbed when its protocol is
  usenet; a torrent-protocol release Prowlarr itself returns (from a torrent indexer configured on
  Prowlarr's side, outside Jularr) is always shown as rejected ("not a usenet release").
- No RSS feed polling: wanted episodes are found by the scheduled search.
- Quality profiles can be assigned per anime; editing profiles has no UI yet.
- AniList auto-monitor only enables monitoring for anime that already exist locally; it does not add
  a new anime to the library.
- An anime's own `IndexerIds` selection (set on the anime page) only ever narrows within a single
  Prowlarr entry's own sub-indexer aggregation; it does not choose which whole indexer entries
  (Prowlarr/Newznab) are searched. Only the profile's allowed sources operate at that entry
  level.

## P2/P3 (issue #302): intentionally not implemented

Every P1 item from #302 is done (hardlink imports, remote path mapping, richer
upgrade scoring/history, multiple root folders, AniList
list-driven monitor, backup/restore, Prowlarr/SABnzbd health checks and the automation API — see
the sections above). The P2/P3 list is deliberately left unimplemented; no concrete home-server use
case justifies the added surface, and the issue itself rules out Sonarr checkbox parity:

- **Broad import-list compatibility** (Trakt, MyAnimeList, arbitrary custom lists, etc.) — the
  AniList list auto-monitor already covers the one list a single-owner anime server actually needs;
  adding other providers would duplicate that mechanism for no described benefit.
- **Custom scripts** (arbitrary post-import/on-grab script execution) — this would let a settings
  value execute arbitrary code with the Jularr process's filesystem access; without a concrete
  requirement the risk is not worth taking.
- **Generic webhooks/notifications** — the Operations log and the `/Acquisition` overview already
  surface every grab/import/failure; nobody has described an external system to notify.
- **Obscure Sonarr compatibility switches** — out of scope by the issue's own rule; would only be
  added if a specific one becomes necessary.

If a real need for one of these appears later, open a fresh, narrowly-scoped issue for it rather
than reopening #302.

## Canonical Work resolution before acquisition

Acquisition never starts from a provider record as if it were Jularr identity. A Search/Discover selection is resolved first to the canonical Jularr **Work -> Season -> Episode** structure plus any provider/presentation target.

For Anime, one Work may map to multiple AniList entries for seasons, parts/cours or specials. An AniList-native search result may therefore identify which season/range the user intended, but it must not create a separate acquisition tree. Monitoring, requirements, release search, queueing, import and file matching all operate on canonical Jularr media items after resolution.

