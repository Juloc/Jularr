# Admin operations

Jularr keeps personal user settings and server administration separate.

## Routes

Personal settings:

- `/Settings` — signed-in user's learning preferences and personal integrations
- `/Settings/Learning`
- `/Settings/AniList`

Owner-only administration:

- `/Admin` — operational overview
- `/Admin/Users` — local account management and progress
- `/Admin/Operations` — active work, queue, downloads and history
- `/Admin/Scans` — library scan runs per media root with phase, counters and warnings
- `/Admin/Logs` — structured operation logs
- `/Admin/Resources` — stack CPU/RAM/disk, storage mounts and the application performance view (routes, background work, providers, budgets, runtime pressure)
- `/Admin/Database` — read-only PostgreSQL evidence (connections, locks, statements, table and index size, vacuum state)
- `/Admin/System` — media roots and server integrations
- `/Admin/Subtitles`
- `/Admin/Sonarr`
- `/Admin/Ai`
- `/Settings/DownloadClients` — the canonical download client list (SABnzbd; Jularr is usenet-only), shared by every media type, with priority, enable/disable, test and health (linked from Admin → System and Books → Acquisition settings)
- `/Settings/SonarrMigration` — per-anime Sonarr/Jularr ownership (linked from Admin → Sonarr)
- `/Settings/Naming` — anime naming profiles, default and per-library selection (linked from Admin → Sonarr); per-anime selection and the rename preview live on `/Library/Rename/{animeId}` (see [ANIME_NAMING.md](ANIME_NAMING.md))
- `/Settings/Indexers` — the canonical indexer list (Prowlarr, direct Newznab) for anime acquisition, with priority, enable/disable, test and health (linked from Admin → System)
- `/Acquisition` — anime acquisition overview: schedule, wanted episodes, downloads, imports that need a decision, interactive search and recent decisions (linked from Admin → System and each anime page; see [ANIME_ACQUISITION.md](ANIME_ACQUISITION.md))
- `/Library/AnimeRepair/{animeId}` — per-anime repair tools (linked from each anime page)

## Canonical operation state

The PostgreSQL tables `Operations` and `OperationLogs` are the canonical durable operational history.

The existing `BackgroundJobQueue` and `PlaybackJobQueue` remain the in-process dispatch lanes, but every queued work item receives a durable operation ID before entering the channel.

Operation lanes:

- `Interactive` — latency-sensitive playback work
- `Normal` — ordinary user/admin-triggered background work
- `Maintenance` — scans, batch preparation and maintenance work

The queues do not maintain a second durable status store.

## Lifecycle

Normal lifecycle:

`Queued → Running → Succeeded`

Other terminal states:

- `Failed`
- `Cancelled`
- `Interrupted`

When Jularr starts, local operations left in `Queued` or `Running` for a worker lane from the previous process are marked `Interrupted`. Anonymous .NET delegates are deliberately not serialized. This prevents phantom running jobs while preserving accurate history. Only operations created before the current process owned the queue are recovered, so work queued during startup (for example the startup library scan) is never mistaken for abandoned work.

External operations can persist an `ExternalProvider` + `ExternalId`. Those jobs are not marked interrupted by the local worker reconciliation because their authoritative work continues outside Jularr. Provider monitors resume after restart and keep the same canonical operation record current.

Retry is available while the current process still owns the original retryable local delegate. After a process restart, local history remains but that transient delegate is intentionally unavailable. Durable provider-backed jobs such as SABnzbd instead resume status monitoring from their persisted external reference, and library scans can be run again from their persisted `Details` (see below).

## Downloads

Downloads are ordinary operations with `IsDownload = true` and optional byte/progress fields. This allows one Downloads view without a parallel download database.

Tracked network/import work includes:

- Japanese subtitle/transcript preparation and explicit embedded-subtitle extraction
- episode learning preparation
- batch learning-text preparation
- novel source imports, table-of-contents/chapter refreshes, chapter downloads and AI chapter translation
- manual AniList episode/novel progress sync
- anime metadata match, episode-range match and refresh
- Manga CBZ/ZIP upload, mounted-path import, source refresh and AniList metadata match
- Discover handoffs for novel and Manga imports
- local EPUB upload and remote EPUB import
- light-novel EPUB volume uploads
- inbox scans of the Books, Light Novel and Manga inbox folders (`media-inbox-import`, see [READING_ACQUISITION.md](READING_ACQUISITION.md#inbox-folders))
- SABnzbd downloads for every media type, including live queue/post-processing state when a full SABnzbd API key allows queue/history access
- Sonarr artwork downloads

Light-novel EPUB uploads on `/Novels` and on an EPUB series page accept up to 20 files of at most 100 MB each; the raised limits apply only to the owner's upload handlers.

Manga uploads on `/Manga` and `/Discover/MangaImport` accept up to 200 CBZ/ZIP archives, at most 1 GB each and 4 GB in total. The raised request-body and multipart limits apply only to the owner's `Upload` handler on those two pages; every other request, including uploads attempted by non-owner accounts, keeps the ASP.NET Core defaults. A reverse proxy in front of Jularr must not cap request bodies below roughly 4 GB for these uploads (Caddy has no body limit by default; nginx needs `client_max_body_size`).

Synchronous request-bound work uses the shared `OperationRunner`, which writes the same `Operations` / `OperationLogs` lifecycle as queued jobs. Long work that can safely outlive the HTTP request continues to use `BackgroundJobQueue`.

Playback remux/transcode is currently streamed live by the media response path rather than pre-generated as a durable background preparation job. It is therefore not recorded as a separate completed operation. If a future UI adds explicit cached playback preparation, that producer must use the existing playback/operation lane instead of creating another task store.

Other job producers should use the same operation descriptor rather than adding their own history table.

### Structured details

`Operations.Details` is an optional, kind-specific JSON document (at most 8 000 characters) for the structured facts of one run that the generic columns cannot hold. It is the only place for such data; producers must not add a parallel table for it. Library scans are the first kind that uses it.

## Library scans

Every library scan — startup, the **Queue library scan** button, filesystem-change scans and periodic reconciliation — goes through `LibraryScanCoordinator` (`Features/Library`). There is no separate scan-run store: each run is one operation of kind `library-scan` on the `Maintenance` lane, and `/Admin/Scans` plus the per-root state on `/Admin/System` are views over `Operations`. While a run is queued or running, `/Admin/Scans` refreshes its history every few seconds.

`Details` of a scan run carries the root ID, the scanned folders (`null` for the whole root), the trigger (`Manual`, `Startup`, `Watch`, `Periodic`, `Retry`), the current phase (`Enumerating`, `Reconciling`, `Metadata`, `Artwork`, `Analyzing`, `Subtitles`, `Completed`), files processed/total and, once finished, the counters: media files, added, changed, removed, skipped, subtitle files, artwork imported, NFO files ignored, media analysed, media analyses failed, errors and warnings. Progress percent and the message are written through the normal operation progress at most once per second; phase changes are always written.

The `Artwork` phase runs `AnimeArtworkLibrary.ReconcileAsync` for every scanned anime folder. Canonical artwork is the file beside the media (series folder; season folder for season posters). The user's own files win over provider artwork Jularr persisted there (Sonarr over copies migrated from `/data/artwork/anime` over AniList); the remote AniList URL is only a page fallback until the image is persisted. Files Jularr wrote are recorded in `MediaArtworkAssets` by file name, size and last write; any other file, or a recorded file that changed, is the user's and is never replaced. Writes go through a temporary file in the target folder and are verified before the row is recorded, and a replaced image never leaves a stale extension behind. A missing media folder (unavailable NAS) is skipped without touching files, rows or derivatives. `/data/cache/artwork/anime` holds the WebP derivatives the pages serve and is rebuilt by the next scan when deleted.

Light Novel and Manga covers (#581) follow the same rule outside the scan. When AniList metadata is matched to a series (`NovelMetadataService.MatchAsync` and `MangaAniListService.MatchAsync`, which the automatic match, the manual match and a request's completed-download import all go through) and the series lives on that media type's library root, `ReadingCoverArtwork` stores the provider cover as `cover.*` in the series folder: the first folder below the Light Novel or Manga library root, taken from the EPUB volume paths (`NovelVolumes.SourceStoragePath`) or the Manga series' source path. Writes use the shared `BesideMediaArtworkStore` (`MediaArtworkAssets` scopes `light-novel` and `manga`), so a `cover.*` the user placed there, or one that changed since Jularr wrote it, is never replaced. The series then carries `/Novels/Cover/{id}` or `/Manga/Cover/{id}` as its cover URL, served as a cached WebP thumbnail from `/data/cache/artwork/beside` (`?w=` picks the width, default 512), which keeps working while the NAS is asleep or the library root was removed. Without a configured library root, for a series read in place outside it (or a Manga series that is a folder of loose page images, where a `cover.*` would become a page), or while the NAS folder is unavailable, nothing is written and the series keeps its AniList cover URL. A series matched before this change keeps its AniList URL until it is matched again.

Item-level findings (unmatched media files, ignored NFO files) are `OperationLogs` warnings of module `Scan` whose paths are relative to the root. Neither the logs nor the persisted error of a failed scan contain the host path of the root; the full exception goes to the application logger only.

Coalescing: a root has at most one active scan run, whatever its trigger. A request for a root whose run is still queued is merged into that run: a folder is added to its scope, a whole-root request widens it to the whole root, and the result is `Merged` with the ID of that run. While the run is executing, further requests are rejected as `AlreadyActive` (the **Queue library scan** button is disabled); the watcher keeps such changes pending and offers them again after the next quiet period, and the periodic pass retries on its next minute instead of waiting a whole interval. A run that starts while a file rename (`anime-rename`, see `ANIME_NAMING.md`) is active waits for it to finish ("Waiting for a file rename to finish."); the rename in turn refuses to start while a scan is queued or running, so a scan never sees a half-renamed folder.

Restart and retry: an abandoned running scan becomes `Interrupted` through the normal lane recovery. **Run again** on `/Admin/Scans` queues a fresh run for the root and folders recorded in the finished run's `Details`; this works after a restart, unlike the generic delegate retry. The newest 200 finished scan runs are kept; older ones and their logs are pruned after each completed scan.

### Filesystem watcher and periodic reconciliation

`LibraryWatchService` attaches a `FileSystemWatcher` to every enabled root that is readable and supports events. Callbacks only record the changed top-level folder (the anime directory); after the root has been quiet for 10 seconds its dirty folders are queued as one folder scan run (`Watch` trigger). A watcher error or buffer overflow queues a full reconciliation of that root instead and the watcher is re-attached on the next sync. Roots on mounts without event support, or roots that are offline, simply have no watcher; the service re-checks the root list every minute and attaches when the root becomes readable.

A folder scan run reconciles each of its folders through `LibraryScanner.ScanFolderAsync` one after another and records the summed counters. `ScanFolderAsync` runs the same reconciliation code as a full scan, restricted to media below that folder: media, episodes, NFO metadata, local artwork, media inventory analysis and subtitles of the folder end up exactly as a full scan would leave them, while media elsewhere in the root are untouched and `LastScannedAt` (the anchor of the periodic schedule) only moves on full scans. The library-wide Sonarr artwork sync runs only when the folder scan discovered a new anime, and learning-text preparation is queued when it added or changed media. A vanished folder reconciles as deletion of its media unless the whole root is empty, which is treated like the full-scan mass-deletion guard (an unmounted NAS).

Each root has one **Periodic reconciliation** interval (`LibraryRoots.ReconciliationIntervalMinutes`, default 30, `0` turns it off, edited on `/Admin/System`). A full scan of the root is queued when the last completed full scan and the last periodic attempt are both older than the interval. An unavailable root is skipped without creating an operation and is not retried before the next interval, so an offline NAS does not fill the history.

### Manual folder import mapping

**Library reconciliation** on `/Admin/LibraryReconciliation/{planId}` is the owner-operated five-step review for an existing folder or subtree that cannot safely be associated during a scan. It is opened from the configured root on **Admin → System** and is permanently bound to that root; the optional start folder is selected through the safe path browser and persisted root-relative. The plan records the complete scanned hierarchy, explicit group/file decisions and visible inherited mapping defaults. Filenames and folder names remain evidence only, never canonical identity.

The organization step is still non-destructive. The final preview re-checks observed file state, target writability, duplicate targets and pre-existing destinations before it allows execution. On execution, Jularr writes the resolved canonical file links and then performs only the reviewed moves/renames, with a rollback journal and an `Activity` operation for progress and failures. Unknown files stay untouched; optional source-folder cleanup removes only now-empty ancestors of successfully moved files and never removes the reviewed root or a directory containing an unknown entry.

## Per-anime repair tools

`/Library/AnimeRepair/{animeId}` (owner-only) lets one problematic anime be repaired without a full library scan or direct database edits. It reuses the same canonical services every other path uses; it does not add a second scanner, prober or matcher:

- **Rescan folder** resolves the anime's own folder from its already-known media files and queues it through `LibraryScanCoordinator` as a folder-scoped `Manual` request (`AnimeRepairService.RescanFolderAsync`) — the same entry point and per-root guard the watcher and `/Admin/Scans` use. Operation kind `library-scan`.
- **Refresh subtitles/NFO/artwork** re-imports sidecar subtitles (`SubtitleImportService`), re-reads local NFO titles (`NfoReader`) and reconciles the anime's artwork beside its media (`AnimeArtworkLibrary`, the same step as the scan's artwork phase) for the anime's already-known episodes, without adding, changing or removing `MediaFiles` rows. This keeps it distinct from a filesystem rescan. Operation kind `anime-repair-refresh-local`.
- **Re-analyse media** forces every media file of the anime to be re-probed by invalidating its `MediaAnalysis` rows (`MediaInventoryService.InvalidateAsync`) and bringing each back up to date through the existing `EnsureAnalyzedAsync` path — no second probe path. Operation kind `anime-repair-reanalyze-media`.
- **Optimize for Direct Play** queues the lossless playback optimization (below) for every existing media file of the anime, independent of the post-import setting. Operation kind `media-optimization`.
- **Identify / Fix Match** searches AniList (`AnimeMetadataService.SearchAsync`) and scores every candidate individually through the same matcher automatic matching uses (`AutomaticMediaMatcher.Select`), showing the score and evidence behind each candidate plus its provider/ID. A match is only ever applied when the owner explicitly confirms a candidate (`AnimeMetadataService.MatchAsync`, kind `anime-metadata-match`); nothing here overwrites an existing match automatically.
- **Refresh metadata** re-fetches the currently matched AniList entry (`AnimeMetadataService.RefreshAsync`, kind `anime-metadata-refresh`) — the same handler the anime page's "Refresh metadata" button uses. It never touches media files, keeping metadata refresh distinct from the filesystem scan.

## Local-first page loads

An ordinary page GET renders from PostgreSQL and local files only. It must not contact AniList, OpenLibrary, Gutendex, Jimaku, Codex, Prowlarr, SABnzbd or Sonarr, and must not start ffprobe, ffmpeg or Whisper, import files or run a library scan just because the page was opened.

- Optional remote state loads after first paint from a lazy page handler that sends `Cache-Control: no-store` and degrades to an "unavailable" state instead of failing the page. The AniList progress card (`_ExternalProgress` / `OnGetExternalProgressAsync`) on Anime, Episode, Manga series and Novel work pages is the reference pattern. The Manga and Novel readers do not load remote progress at all; it lives on the series/work page.
- Discover renders its shell locally; trending, top, My AniList and search results come from its `Results` handler, whose external lookup is the explicit purpose of that request.
- Explicit Search, Refresh, Sync, Import, Scan, Prepare and Translate actions do their intended external or heavy work, through `OperationRunner` or the queues described above.
- `LocalFirstPageGetTests` runs page GETs with external clients that fail when called. Add a case there when a page gains a new dependency on a remote service.

To measure page timings temporarily, set the log level `Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics` to `Information` (environment variable `Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics`); ASP.NET Core then logs every request with its elapsed time. Leave it at the default `Warning` otherwise.

## Media inventory

`MediaAnalyses` / `MediaAnalysisStreams` are the canonical technical analysis of each `MediaFiles` row and are owned by `MediaInventoryService`. Library reconciliation, playback, the client API and embedded subtitle/Whisper stream selection read them; `FfprobeMediaProbeRunner` is the only code that invokes `ffprobe`. Its output also includes chapters; `MediaProbeParser.ParseDetail` reads the complete view (every stream including attachments and data streams, dispositions, side data, chapters) for callers that must not lose anything, such as the lossless playback optimization.

- Each analysis records the source identity it describes (size, modification time and a SHA-256 fingerprint of the length plus the first and last 64 KiB) and the code-level probe version. It is re-probed only when the size changes, the modification time changes and the fingerprint differs, or `MediaInventoryService.CurrentProbeVersion` is raised. Raise that constant whenever the persisted analysis would change (the `ffprobe` arguments the inventory parser reads, or that parser's output); every file is then re-analysed on the next scan or first use.
- States: `Succeeded`; `Failed` (ffprobe rejected the file — the bounded diagnostic stays until the file changes, and the scan continues); `Pending` (ffprobe could not start or timed out — retried by the next scan or use once five minutes have passed).
- Each library scan logs how many files were analysed, failed, deferred or unchanged. Deleting a media file removes its analysis through the foreign-key cascade.
- Diagnostics replace the media path with the file name. There is no manual re-analyse action yet; touching or replacing the file (a changed size or content) forces a new analysis.

## Wake-on-LAN and Docker networking

Each library root (`/Admin/System`) can enable Wake-on-LAN with a MAC address and an optional broadcast address/port; `WakeOnLanService`/`StorageWakeCoordinator` send one magic packet per offline root and coalesce concurrent playback/import/manual wake requests into that single attempt (`Features/Storage/StorageWake.cs`, `Features/Storage/StorageAvailability.cs`).

- **Docker bridge networks break the default broadcast (#571).** `compose.yaml` runs Jularr on the default (bridge) network. The limited broadcast address `255.255.255.255` is only ever delivered on the sender's own local network segment; from inside a Docker bridge network that segment is the container's bridge subnet, not the host's physical LAN, so the target machine never receives the packet even though sending it does not fail.
- **Fix: configure a directed LAN broadcast.** Leave the *Broadcast IPv4:port* field on a library root empty only if Jularr runs with `network_mode: host` (or a macvlan network with a real LAN address). When Jularr runs in the default bridge network, set it to the directed broadcast address of the physical LAN that the NAS is on, for example `192.168.1.255` — the same subnet a tool like `wakeonlan` or Home Assistant would target from another LAN host. The Docker host's own routing/NAT forwards a packet addressed to that directed broadcast out through its physical LAN interface, unlike the limited broadcast. An optional `:port` suffix overrides the standard Wake-on-LAN port 9, for example `192.168.1.255:7`.
- **Never guessed automatically.** Jularr does not try to detect "the right" interface/broadcast for the container, because a Docker host with multiple NICs or VLANs makes that ambiguous and a wrong guess would silently keep using the Docker bridge subnet. The broadcast address/port is always the owner's explicit setting; `WakeOnLanService.TryResolveBroadcastEndpoint` is the single canonical parse of it.
- **Diagnostics.** Every send attempt logs the library root id, MAC address and the resolved destination endpoint. A library root in `Error` state distinguishes a send failure (`StorageDiagnosticCodes.WakeSendFailed`, for example `SocketException: Network is unreachable`, which can itself indicate the configured address is unroutable from the container) from a timeout waiting for the storage after a packet was sent (`StorageDiagnosticCodes.WakeTimeout`).
- **Host networking remains a documented fallback**, not a requirement: `network_mode: host` (or a macvlan network) gives the container a real LAN-facing interface, after which the default `255.255.255.255` broadcast works like it would on any other LAN host. This trades away Docker's network isolation for the `jularr` service and needs the owner's own `compose.yaml` change; Jularr does not switch this automatically.

## Storage insights and current safe cleanup

`/Admin/Storage` currently implements the first narrow slice of #414.

- **Usage** comes from the library inventory (`MediaFile.SizeBytes` from the last reconciliation, audiobook and book file sizes), never from an unbounded filesystem scan of library roots. A root's state is read from cached availability; a root that could be a sleeping Wake-on-LAN NAS and was never observed is shown as "Not checked" and is not probed. A root that is not online keeps its last-scan figures, marked "As of the last scan", without free space.
- **Jularr cache** lists rebuildable data on `/data`: prepared playback files, streaming sessions, seek previews, artwork derivatives, audio fingerprints and manga pages.
- **Current safe cleanup** only removes Jularr-owned cache leftovers it can prove unusable: prepared playback files and seek previews whose media file is gone/changed in the database, work files left by interrupted jobs, and streaming sessions idle for over an hour. Artwork derivatives, fingerprints and manga pages are measured only where their existing owner manages lifetime.
- Cleanup recomputes the candidate list at execution time and refuses anything outside its permitted cache area or at/above/inside a LibraryRoot or known media path.
- **Current implementation does not delete canonical library media automatically.**

### #414 target lifecycle (not yet equivalent to the current implementation)

The approved target in `docs/mockups/admin-storage/SPEC.md` adds explicit Off/Review/Automatic lifecycle policies for canonical media, plus Requirement-aware version pruning, optimization, cold tiering, physical root migration/evacuation, integrity/orphan analysis, true-physical-savings accounting, forecasts, What-if simulation, grace/undo and policy/audit history.

The future Automatic mode is only valid when an admin explicitly enables that policy. Storage pressure by itself never enables canonical-media deletion.

All future canonical-media actions must reuse #411 availability safety, #815 LibraryRoot routing/capabilities and the canonical Work/Asset/File/Track + Requirement model; they must not extend the existing cache cleanup by simply allowing arbitrary LibraryRoot paths.

Artwork distinction:
- rebuildable local derivatives remain safe cache;
- canonical artwork remains beside media by default;
- optional canonical-artwork downsizing is a separate explicit #414 lifecycle optimization with preview, validation, quality floor and custom-art protection.

Operational lifecycle jobs should use the shared Operations/Activity infrastructure rather than create a second job-history system.

## Lossless playback optimization

`MediaContainerOptimizer` (`Features/Media/Optimization`) rewraps a video into MP4 when that widens browser Direct Play without losing or changing anything. It never re-encodes: the only ffmpeg call is a stream copy of every stream (`-map 0 -c copy`, HEVC tagged `hvc1` for Safari, track names kept as MP4 handler names, chapters and metadata mapped).

- **Setting.** `/Settings/Acquisition` → *Lossless playback optimization*: `Off` (default) or `Safe only`. When enabled, every completed import queues one `media-optimization` operation for the files it imported; the import never waits for it. The per-anime repair page runs the same optimization on existing files.
- **Decision** (`MediaOptimizationPlanner`, from the actual streams). The compatibility model `MediaPlaybackCompatibility` evaluates Direct Play per client profile (every browser, Safari on iOS/iPadOS/macOS, Chrome/Edge, Firefox); playback's own Direct Play check uses the same model. A file is only remuxed when MP4 adds at least one profile. It is kept unchanged when it already plays directly wherever MP4 would, and the original is kept when anything could not be carried unchanged: ASS/SSA subtitles, attachments/fonts, subtitles that would need conversion (SRT, PGS), audio MP4 cannot hold as is (DTS, TrueHD), Dolby Vision, cover art, data streams, extra video streams. The reason is logged on the operation.
- **Verification** (`MediaRemuxVerifier`). ffmpeg writes `<name>.mp4.jularr-partial` next to the source; ffprobe then must show the same streams in the same order with the same codec parameters, HDR signalling and side data, languages, track names, presentation dispositions and default tracks, the same chapters (the MP4 chapter text track is allowed), the same duration and video frame count. Any difference discards the output.
- **Adoption.** The optimizer waits while a library scan, import or rename runs, re-checks Sonarr ownership like a rename, and verifies that the source did not change. It then renames the output into place and, in one transaction, moves the existing `MediaFiles` row (same id, so progress, segments and episode identity stay) and embedded/transcribed subtitle sources to the new path; acquisition ownership and import records follow. Only then is the source deleted.
- **Recovery.** `/data/media-optimization/<mediaFileId>.json` journals every optimization in flight. At start-up (and before the same file is optimized again) an interrupted remux is discarded; an interrupted commit is rolled back if the database still names the source, or completed if it already names the output. The original is never deleted before the output was verified and recorded.

## Playback transcoding and hardware encoders

Server-side remux and transcode limits live in **Admin > Transcoding** and are stored in `/data/playback/transcoding.json`: whether transcoding is allowed, the concurrent sessions per cost class (software video 2, hardware video 4, remux 6, audio only 8, and at most 3 per profile), the HLS cache folder (default `/data/playback-cache/hls`), its size budget (10 GiB) and the free space to keep on its volume (5 GiB). The cache folder must be empty or created by Jularr and may not contain `..` or `%`; Jularr marks it with `.jularr-hls-cache` and only ever deletes its own session directories there. When the budget or the free-space floor is exceeded, idle sessions go first (idle meaning two minutes without a request), then at most one running session per minute, the largest.

Hardware encoders (NVENC, QSV, VAAPI, AMF, in that order, then software) are detected at startup and on **Admin > Health > Detect again**: ffmpeg must list the encoder and a short test encode must pass. Three consecutive failures of a backend (counted only when the software fallback of the same request then succeeds) pause it for ten minutes.

An HLS video transcode is measured while it runs (ffmpeg's `-progress` speed; a progressive stream is only shown in the player diagnostics, because a player that stops reading would slow it down without any lack of server capacity): after 10 s of produced media a speed under 1.0x for 10 s means the server cannot keep up. The player is then re-planned in this order: a lower quality tier, another healthy encoder backend, and only then no playback with the reason "The server cannot convert this video fast enough right now" (a device that can play the original untouched gets it). Slowness never counts as a hardware failure and never opens the circuit breaker. While a running conversion is below real time, a new conversion of the same kind (software or hardware) is refused at once with a retry hint instead of queued (never a seek in the slow session itself, nor a re-plan that replaces a running conversion at the same or a lower bitrate; only sessions active in the last 90 seconds count, and the playback sweeper ends a too-slow session whose player is gone); what the server learned is forgotten after 10 minutes. The thresholds live in one typed policy with these defaults; making them editable here is a follow-up. Automatic quality also raises the quality again, one tier at a time, only after 60 s of stable delivery and never within 120 s of any change.

The shipped `compose.yaml` passes **no GPU device** to the container, so detection finds software only until the operator adds one. Nothing in Jularr turns this on by itself. Optional mappings for the owner's own compose file (the container runs as UID `1654`, so the render node's group must be added):

```yaml
services:
  jularr:
    # Intel Quick Sync (QSV) and AMD/Intel VAAPI: pass the render node and its group.
    # devices:
    #   - /dev/dri/renderD128:/dev/dri/renderD128
    # group_add:
    #   - "992"   # numeric gid of the host "render" group (stat -c %g /dev/dri/renderD128)
    #
    # NVIDIA NVENC: needs the NVIDIA Container Toolkit on the host.
    # deploy:
    #   resources:
    #     reservations:
    #       devices:
    #         - driver: nvidia
    #           count: 1
    #           capabilities: [gpu, video, utility]   # "gpu" is required by the Compose spec; NVENC needs "video"
```

**The image does not contain the GPU user-space drivers.** Its runtime stage installs Debian `ffmpeg` with `--no-install-recommends`, so `mesa-va-drivers` (AMD, older Intel) and `intel-media-va-driver` (Intel Quick Sync; in Debian `non-free`, `intel-media-va-driver-non-free` for full codec support) are absent, and passing `/dev/dri` alone makes the VAAPI and QSV test encodes fail. This is deliberate: the packages are architecture and vendor specific, `intel-media-va-driver` needs the `non-free` component, and they add to the image size for every install. Operators who want VAAPI or QSV build a small derived image:

```dockerfile
FROM ghcr.io/juloc/jularr:latest
USER root
RUN apt-get update && apt-get install -y --no-install-recommends mesa-va-drivers vainfo && rm -rf /var/lib/apt/lists/*
# Intel Quick Sync additionally: intel-media-va-driver (add the Debian non-free component first)
USER 1654
```

NVENC needs no driver package in the image: the NVIDIA Container Toolkit injects the host driver libraries. Whether ffmpeg lists NVENC or AMF depends on how the Debian build was configured and on the host; **Admin > Health** shows what the test encode found for each backend, with the reason when it failed. The HLS cache path is changed in the Admin page, never through an environment variable.

## Logs and security

`OperationLogs` contains structured operational events keyed by operation ID.

Persisted operational data must not contain:

- passwords
- API keys or access tokens
- cookies
- raw request headers
- private filesystem paths

Exceptions are persisted as bounded type/message summaries rather than stack traces. Full server diagnostics may continue to use the normal application logger.

## SABnzbd (download clients)

Jularr is usenet-only by owner decision: torrent download clients (qBittorrent or any other) are
intentionally unsupported. Every media type submits downloads through one abstraction,
`IDownloadClient` (`Features/Acquisition/DownloadClients`), with SABnzbd as its only
implementation; `Features/Acquisition/Sabnzbd` keeps SABnzbd's own protocol client and the
anime-specific attempt/blocklist relation. Submissions pick the
highest-priority enabled, healthy client and fail over to the next one if a submission is
rejected, so several SABnzbd connections can be configured for redundancy.

### Configuration

The owner configures every download client under `/Settings/DownloadClients`: name, base URL, API
key, one SABnzbd category per media type (Anime, Manga, Light Novels, Books), priority and enabled. Settings
are stored in `/data/acquisition/download-clients.json`; the secret is protected with ASP.NET Core
Data Protection. **Test** on each entry checks reachability and authentication and records the
result for the periodic health check (see [ANIME_ACQUISITION.md](ANIME_ACQUISITION.md)) — use the
full API key rather than the NZB-only key so Jularr can also track progress.

The canonical download-client list is the only supported configuration path. Torrent download clients and Torznab indexers are not supported.

### Jobs and state

Every submission goes through `DownloadClientSubmissionService` and creates one canonical operation (`IsDownload`, `ExternalProvider = sabnzbd`, `ExternalId = nzo_id` returned by SABnzbd). The job id and the routing details (download client, media type, category) are written in one statement. Anime uses kind `anime-sabnzbd-download`, Books requests `book-usenet-download`, Manga and Light Novel requests `reading-usenet-download`, and an NZB the owner sends by hand `sabnzbd-download`.

One hosted monitor projects SABnzbd queue/history onto those operations: progress, bytes, queue speed (when a single job is downloading), ETA, post-processing state, completion and failure. Failures are classified (incomplete download, corrupt/repair failed, extraction failed, password-protected, script failure) and the operation error states the reason. A job that disappears from both queue and history for 15 minutes fails. After a restart the monitor continues from the persisted external references.

A completed Books, Manga or Light Novel download is imported by the shared completed-download import (`CompletedDownloadImportService`, see [READING_ACQUISITION.md](READING_ACQUISITION.md#completed-downloads)); Anime completions go to the anime import. The monitor itself imports nothing. The operation detail page shows the reported path, the mapped local path, the destination, the import mode and the result.

The acquisition store (`/data/acquisition/sabnzbd-acquisitions.json`) keeps only the durable relation of an anime acquisition (anime, episodes, attempts with their operation IDs and release identities, untried accepted candidates) and the blocklist of failed release identities. Candidate NZB URLs are stored protected because indexer URLs can carry credentials. It never stores job status; that is always read from the operation.

When an anime download fails, its release identity is blocklisted and the next accepted, non-blocklisted candidate is sent, up to the acquisition's attempt limit (default 3). The failed operation's log records the replacement or why the acquisition stopped. On startup, any acquisition whose latest attempt failed before the process stopped is advanced once.

### Cancel and retry

The operation detail page (`/Admin/Operation/{id}`) cancels an active SABnzbd job (removed from SABnzbd queue/history including files) and retries a failed one through SABnzbd's retry, which requeues the same operation with the new `nzo_id`. Retrying an anime attempt is only allowed for the latest attempt of its acquisition and removes that release from the blocklist. Anime operations also show the anime, episodes, attempt number and blocklisted releases.

## Non-root container runtime and /data ownership

The Jularr image runs as a dedicated, unprivileged user and group, both named `jularr` and fixed at UID/GID **1654:1654** (the same fixed value the aspnet base image's own `app` user already has; the image renames `app` to `jularr`, or creates it fresh with that same UID/GID if a future base image drops `app`). Because the UID/GID are fixed rather than assigned per build, a `chown` an operator has already run against `1654:1654` keeps working across image upgrades. The container needs no privileged mode and no added Linux capabilities, so it can run with `cap_drop: [ALL]` and `security_opt: [no-new-privileges:true]`.

Writable locations:

- `/data` holds all persistent non-database state: Data Protection keys (`/data/keys`), protected integration settings, Codex credentials (`CODEX_HOME=/data/codex`), Whisper model, transcription/playback/artwork caches (including the `/data/cache/artwork` derivative thumbnails), manga, novel and book data, and acquisition state. The canonical database is PostgreSQL (a separate service and volume), not a file under `/data`.
- `/tmp` is scratch space, for example temporary Codex work directories and audio fingerprint windows.
- Media paths Jularr changes: library roots that receive imported downloads, file renames or artwork stored beside the media, and the completed-download folder (after remote path mapping) when the import mode is **Move**. These must be writable by UID `1654`, either through ownership or through a group added with `group_add: ["1654"]` (or `group_add: ["jularr"]`). **Copy** imports only need read access to the download folder. **Hardlink** imports also need the downloaded files to be readable and writable by UID `1654`, because most Linux hosts enable `fs.protected_hardlinks`.

The application under `/app`, the bundled `codex`, `whisper-cli`, `ffmpeg`/`ffprobe`, the MeCab dictionary and the JMdict data are root-owned and read-only for the runtime user. Read-only media mounts such as `/media/anime:ro` only need to be readable by UID `1654` (world-readable, or a matching `group_add` group). Jularr already reports read-only or unwritable libraries as failed imports or refused renames.

### Dynamic NAS mounts

When a NAS or other network filesystem can be powered off, do not bind the network mount itself as the Docker source. Docker resolves bind sources before Jularr starts, so a stale CIFS/NFS mount can make container creation fail before Jularr can report the library root as offline.

For this case the image supports `JULARR_MEDIA_ROOT=<absolute-container-source>`. `/media` remains the canonical application path, but is routed through an internal indirection to that source. The entrypoint changes only the internal symlink and **does not dereference the source**, so an unavailable NAS cannot block Jularr startup. Every existing child path is preserved unchanged, for example `/media/anime`, `/media/tv`, `/media/movies` and download folders.

Example for a NAS mounted somewhere below an always-present host parent:

```yaml
environment:
  JULARR_MEDIA_ROOT: /host-mounts/arr_bay4_media
volumes:
  - type: bind
    source: /mnt
    target: /host-mounts
    bind:
      propagation: rslave
      create_host_path: false
```

`rslave` is intentional: host-side submount changes can propagate into the already-running container, while mounts created inside the container do not propagate back to the host. Docker bind mounts also include existing nested mounts recursively by default, so a currently sleeping/stale NAS mount below the stable parent is carried into the container without Docker having to use that NAS path as the bind source. The container still needs no `SYS_ADMIN` capability or privileged mode. Bind propagation is a Linux-host feature and the selected host parent must support it.

Prefer a dedicated stable host parent when practical so the container does not see unrelated host mounts. The mapped media tree must have the normal read/write permissions required by Jularr.

A direct bind such as `/path/to/media:/media` is suitable for storage that is guaranteed to be available when Docker creates the container. Do not combine that direct bind with `JULARR_MEDIA_ROOT`.

### Startup ownership check

Before the application starts, the entrypoint (`/usr/local/bin/jularr-entrypoint`) checks that every directory and file under `/data` is readable and writable by the runtime user. If one is not, the container exits with code 1 and logs the first offending path plus the exact fix command below. It never changes ownership itself and never falls back to running as root.

### Fresh installations

A new, empty named volume is initialized from the image with `1654:1654` ownership, so no action is needed. A bind-mounted host directory must be owned by (or writable for) UID/GID `1654:1654`, for example:

```bash
sudo chown -R 1654:1654 /srv/jularr-data
```

### Custom runtime user

If the deployment sets `user: "<uid>:<gid>"` in Compose (for example to match NAS permissions), `/data` and the writable media paths must be owned by that UID/GID instead of `1654:1654`. The startup check and its fix command use the effective UID/GID of the container, whatever that is set to.

## PostgreSQL database

PostgreSQL is Jularr's canonical database. The connection string comes from `ConnectionStrings__Default`; Jularr applies pending PostgreSQL migrations at startup and refuses to start when the database is unavailable.

There is no built-in SQLite import or pre-Jularr upgrade path. The supported persistence boundary is the current epoch in `.agent/upgrade-policy.yaml`.

Back up PostgreSQL with a logical dump such as `pg_dump -Fc` or a stopped-volume snapshot. Back up `/data` separately for non-database state.

## Resource budgets and performance view

Interactive requests win over background work (#857). Governed background work runs in four classes with bounded slots: Import (2 slots, never held back for requests), Provider refresh (2), Scan (1) and Maintenance (1). Every class below Import waits while three or more requests are in flight, for at most 10 s (provider refresh), 15 s (scan) or 30 s (maintenance); constant load therefore delays background work but never starves it. The Wanted pass, metadata refresh, calendar, franchise and AniList loops, the background job worker (library scans and the other queued operations) and the completed-download import run through this one governor; Admin → Resources shows each class's limit, running and waiting counts and how often work was deferred.

The same page answers "what is making Jularr slow or busy" (#860) without an external collector: per route template, background operation and provider client name it shows calls, failures, mean, P95, maximum and total time over the last hour of the running process, plus requests in flight, GC pause share, allocation rate, heap, ThreadPool queue and refused rate-limited calls. Keys are fixed identities (route templates, operation names, registered client names), never URLs, ids or titles, and each category is capped at 200 keys.

Idle cost: the stack resource sampler (cgroup reads of Jularr and PostgreSQL) samples every 5 s only while an Admin page has asked for it in the last two minutes and once a minute otherwise; instances with Playback off do not probe ffmpeg or sweep the HLS cache.

Admin → Database (#859) reads PostgreSQL in a read-only transaction with a statement timeout; it never writes, explains or runs a statement, and shows query text without its literals. Table and index sizes are skipped while a table is exclusively locked. Statement statistics need `pg_stat_statements`: the bundled `compose.yaml` preloads it, then run `CREATE EXTENSION IF NOT EXISTS pg_stat_statements;` once as the database owner; without it the section says what is missing.

## Instance setup and branding

First-run Setup is three steps on the real settings, with no setup-only copy: the owner account (`/Account/Setup`), the instance step (`/Account/SetupInstance`, #877) and, while an enabled feature still needs an unusable provider, the provider step (`/Account/SetupProvider`). The instance step offers the starting points of Admin → Instance (Media Manager, Full Jularr, Custom) with the same module switches, so the resulting module state is visible before saving; a starting point sets the feature switches (Media Manager: Playback, Learning and Tracking off, Acquisition on) and the media types stay as chosen. It also takes the instance name and the owner's own theme mode and accent. Every value is saved through the stores behind Admin → Instance, Admin → Appearance and Settings → Appearance, and "Skip for now" changes nothing; the step can be opened again at any time.

Branding (#876) has one owner, `InstanceBrandingStore` (table `InstanceBranding`, one row): an optional instance name (at most 40 characters), an optional logo (PNG, JPEG, WebP or an SVG that carries no script, event handler or external reference, at most 512 KB, validated by its bytes) "Use brand colour" switch with a colour chosen by a picker (stored as a hue 0–359; a button takes it from the logo in the browser) and an optional "Recolour logo with brand colour" switch. Setup and Admin → Appearance write it; the layout, the brand mark (sidebar, header, login, register, setup), the page title, the browser icon and the web app manifest name read it. While the instance has its own name or logo, a small "by Jularr" stays beside it; without any, plain Jularr is shown without it. While the brand colour is on it is the accent for every profile (buttons, highlights and links; success, warning and error colours and media artwork keep theirs). Recolouring is presentation only: the uploaded file is never changed, the logo is drawn through its own alpha as a mask over the accent (shape, transparency and antialiased edges kept, no mixed source colours), and it falls back to the original for a logo without real transparency (JPEG, an opaque PNG or WebP) or in a browser without CSS masks. The admin preview repaints the page with the real derived accent and shows the original or the recoloured logo as the switches say. The logo is served at `/branding/logo?v=<version>`: public (the login page shows it), sandboxed, without content sniffing and cached by version. Not yet covered: separate light and dark logos (the recolouring rule would apply to whichever is rendered), raster resizing, and a recoloured browser or installed-app icon.
