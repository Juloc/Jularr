# Activity: queue, history and live progress

The admin Queue, History and Blocked views, media history and the live event stream.
Index: [CODEMAP](../CODEMAP.md).

## Decisions

Owner decisions; change them only with the owner.

- The ARR group starts with **Queue** (`/Admin/Queue`, the default), whose views are **History** (`/Admin/Queue/History`) and **Blocked**
  (`/Admin/Queue/Blocked`), each under the normal media-type capsule (`type=a,b` stays in every view's address), then **Wanted**, the indexers, the
  quality profiles and the sources.
  The Queue holds downloads and their own imports only; its menu figure is the open downloads.
  The Queue is **one table**, no tiles and no groups, with Sonarr's columns: the state as an icon in its phase's tone (its word for assistive
  technology and on the phone card), the title (the Work, under it the release), the episode (`S01E05`, `S01E05–E07`, the season of a pack; volume and
  chapter for books), the episode's title, quality, the languages the release names, protocol (Usenet, Torrent, Direct by the client), time left,
  progress (bar and percent) and the actions; the medium width drops episode title, language and protocol, the narrow one episode and quality; on a
  phone each row is a card with the bar and time left under the title and the facts in one line.
  What waits for a person comes first.
  A **failed download is not in the Queue**: a fault of the release (the client failed it, Jularr's Usenet could not repair or unpack it, the import
  found no video, only extras or another title, or a folder with nothing the import takes: empty, or only archives and release files) blocks the
  release, removes the job with its files (SABnzbd where the connection removes failed ones, qBittorrent with its data, Jularr's own Usenet and
  torrent downloaders by clearing the job's folders), makes the target due again and keeps "Download failed" with its reason in the History; an
  interruption or a grab problem not caused by the release (client unreachable or refusing, link gone) goes to the History too and is searched again
  later.
  An **import that waits for a person** (unclear title, quality not allowed by the profile, other episodes, unreadable names, files that cannot be
  placed, partial import, a check at the source, a storage or setup problem) stays as a row with the orange warning icon and its reason under the
  title, with Import (the manual import of its folder), read again or the source check.
- A **grab its indexer limited** is no failure (owner's decision "Rate-Limit = wartet", Sonarr's pending release): it waits as an open row ("Waiting
  until 14:20") until the indexer's pause ends, then the same release is fetched again; it is never failed, blocked or searched again, and the History
  shows no failure for it.
  Failed rows from before are settled once by the same rule by the download monitor's cleanup pass.
  Blocked lists the releases that are not taken again (blocked by hand or by a fault) with **Allow again**.
  The old addresses (`/Admin/Activity` with its query, `/Admin/History`, `/Admin/Activity/Blocked`) lead there permanently.

## Backend

- Media history ([Library.sql](../../database/Library.sql) `ActivityEvents`,
  [ActivityLogic.cs](../../src/Jularr.Backend/Logic/Library/ActivityLogic.cs)): every grab, failed download, import, upgrade (the worse file it
  replaced), rename, deletion kept only for a chosen version, manual assignment, blocked release and fetched subtitle adds one event that keeps its
  texts (release, quality, indexer, client, path, who) after its Work, release or file is gone; `PruneOperationsV1` trims it by the history's days and
  size.
  Administrators read it with `ReadActivityEventsV1` (`/Admin/Queue/History`, a Work's own from its page); a requester sees the steps of their own
  request (`ReadRequestStepsV1`, the latest on `/Requests`) without releases, indexers or paths.
  Test `AcquisitionTests.Movie_ManualSearchGrabMonitorAndImport_CompletesTheRequest`.
- Live progress: [LivePhases.cs](../../src/Jularr.Backend/Logic/Live/LivePhases.cs) is the one owner that maps a request, its target, running
  downloads and the client's unpacking to a phase, percent, pace and time left;
  [ReadLiveV1](../../src/Jularr.Backend/Service/User/Live/V1/LiveServices.cs) gives a profile its own requests (no release, indexer, client or error),
  [ReadLiveAdminV1](../../src/Jularr.Backend/Service/Admin/Live/V1/LiveServices.cs) every request, the queue and the targets,
  [ReadLiveVersionV1](../../src/Jularr.Backend/Service/System/Live/V1/LiveServices.cs) one fingerprint row for
  [LiveChanges.cs](../../src/Jularr.Backend/Service/Core/LiveChanges.cs) (stream counts and caps, wakes the streams on a different version).
  [LiveWorker.cs](../../src/Jularr.Workers/LiveWorker.cs) reads the fingerprint only while a stream is open (1 s with a running download, 2 s with
  another job, else 5 s) and [AcquisitionWorker.cs](../../src/Jularr.Workers/AcquisitionWorker.cs) reads the clients every second meanwhile; the
  Frontend streams it ([Frontend CODEMAP](../../src/Jularr.Frontend/CODEMAP.md)).
  Focused tests: `LiveTests.cs`.
- [Activity services](../../src/Jularr.Backend/Service/Admin/Activity/V1/ActivityServices.cs): scans and downloads by one `ActivityFilterType` (All,
  Active, Waiting, Import, Errors, History) over the view `OperationQueueStates` ([Library.sql](../../database/Library.sql); a paused download stays
  Active, Waiting is `AttentionKey`, Import is the client unpacking, Errors are undismissed failures; what needs a person first) and, with
  `QueueOnly`, the Queue (open downloads and a grab its indexer limited while the target's next search is ahead; `WaitUntil`, the release's
  `QualityKey`, units, the single unit's title and the `Languages` its name says), and `ReadActivitySummaryV1` (downloads only: active, pace, import
  waiting, errors, also in the live stream as `queue`; plus `TaskHelpCount`, Jularr's own tasks that failed or wait, the Jobs entry's figure);
  `TasksOnly` reads only those tasks (the Jobs entry's Tasks page); per download
  [DownloadActionServices.cs](../../src/Jularr.Backend/Service/Admin/Activity/V1/DownloadActionServices.cs) removes it (SABnzbd is told first and
  deletes the job's files; with the release blocked, for every media type the reject of a doubtful download, the release keeps its reason and date and
  the target is due at once and the search woken through `AcquisitionSearchSignal`, else in 30 minutes; `MangaTests.Manga_RejectedDownload_*`), pauses
  or resumes it (SABnzbd), or reads a waiting one again; retry/dismiss in
  [OperationLogic.cs](../../src/Jularr.Backend/Logic/Operation/OperationLogic.cs).
  A failed download leaves the queue at once (`FailDownloadAsync` puts it aside, records `DownloadFailed` except for a limited grab,
  `acquisition_rate_limited`); release faults (`ReleaseFaultSql`) block the release and make the target due; the cleanup pass
  (`DownloadImport.ProcessCleanupAsync`) removes the job of a release fault with its files where `RemovesFailed` is set (`QueueFailedCleanupAsync`,
  keys by `ReleaseFaultKeysSql`): SABnzbd by its job, qBittorrent with its payload, Jularr's own Usenet and torrent downloaders by their job folders
  (`DownloadStoragePaths.RemoveCompleted`, ledger rows keyed `operation-<id>`); an interruption keeps job and files and settles failed downloads left
  in the queue by the same rule (`SettleFailedDownloadsAsync`, 64 a pass; tests `AcquisitionTests.FailedDownloads_LeaveTheQueueOnce_*`,
  `ReleaseFaults_RemoveTheJobAndItsFiles_InEveryClient_*`).
  Pause and resume work for SABnzbd jobs and for Jularr's own downloaders (a paused own job is not run, `DownloadActions.SetPausedAsync`) one by one
  or for the whole queue (`PauseActivityQueueV1`); removing a job asks first on the page (the button opens the one that deletes its partial files).
  An import that fails after placing files removes what it placed (`DownloadImport.RollbackAsync`), so a retry does not find its own leftovers; a
  crash in between is not covered.
  The Downloads page reads the same owners: `ReadDownloadClientsV1` gives one tab per switched-on client (`DownloadClientKind`: each SABnzbd and
  qBittorrent, Jularr Usenet, Torrent and Direct as one each) with its open, active, paused and warning counts, pace and remaining bytes;
  `ReadActivityV1` with `DownloadsOnly` lists one client's open downloads or finished ones (`DownloadClientKindId`, `AcquisitionConnectionId`,
  `MediaTypeId`, `FailedOnly`); `PauseActivityQueueV1` takes one client; `ApplyActivityDownloadsV1` pauses, resumes, reads again, removes or moves
  first the chosen downloads (`DownloadBulkActionType`, shared `DownloadActions.RemoveAsync`/`MoveAsync`).
  What each client can be told is `DownloadClientKinds` (priorities and reordering for SABnzbd and Jularr's own downloaders, only move to top for
  qBittorrent; speed limit for all but direct sources; NZB or torrent/magnet adds).
  [0026](../../database/migrations/0026_download_queue_controls.sql) adds `Operations.DownloadPriorityTypeId` (Low, Normal, High, Force) and
  `QueuePosition`, which Jularr's own downloaders take their jobs by (`ReadActiveDownloadsAsync`) and SABnzbd's monitor mirrors from its queue, and
  `DownloadStorageSettings.MaxBytesPerSecond`: `PrioritizeActivityDownloadsV1`, `MoveActivityDownloadV1` (before another download, taking its
  priority; SABnzbd `priority`/`switch`, qBittorrent `topPrio`), `Read`/`SetDownloadSpeedLimitV1` (SABnzbd `speedlimit`, qBittorrent
  `transfer/setDownloadLimit`, the workspace's limit for the Usenet and torrent engines, held by the shared
  [ByteRateLimiter](../../src/Jularr.Integrations/Logic/Common/ByteRateLimiter.cs) in
  [DownloadSpeeds](../../src/Jularr.Backend/Service/Core/DownloadSpeeds.cs)), `DeleteActivityHistoryV1`/`ClearActivityHistoryV1` (finished download
  operations; ActivityEvents stay), `AddActivityDownloadV1` (an NZB, torrent or magnet address for a wanted title through
  `WantedAcquisition.GrabAsync` to the named client, without an indexer), `ReadNewsServersV1` (each news server's last pass: connections, pace,
  missing articles, from `DownloadSpeeds`); `ReadDownloadClientsV1` adds each client's short speed line (in memory, `DownloadSpeeds.Record`).
  A forced download keeps running when its client or the queue is paused.
  Tests `AcquisitionTests.DownloadsPage_*`, `QueueAction_ChosenDownloadsAndOneClient_*`, `QueueAction_SabnzbdPriorityMoveLimitAndAdd_*`,
  `DownloadSpeedTests`.

## Frontend

- [Live stream](../../src/Jularr.Frontend/Modules/Live/LiveEndpoint.cs) (`/live/events`, Server-Sent Events: snapshot first, then an event only when
  the entities differ, heartbeat, no-buffering headers, capped per account, session checked again for every read),
  [entities](../../src/Jularr.Frontend/Modules/Live/LiveEntities.cs) (service results as ready texts; a person gets no release, indexer, client or
  error), [client](../../src/Jularr.Frontend/Modules/Live/Live.ts) (patches only elements marked `data-live`, `data-live-text`, `data-live-value`;
  closes in a hidden tab, reconnects with backoff, binds again after a page swap and takes in elements a script adds later),
  [LiveProgress](../../src/Jularr.Frontend/Framework/Components/LiveProgress.cs) (progress block, step bar,
  [LivePhasePill](../../src/Jularr.Frontend/Framework/Components/LiveProgress.cs), shared phase and speed texts; pages opt in with
  `LiveProgress.entry.ts`).
  Shown live: the request pill on Details (the primary button with a white icon and a word per phase, the percent only in a ring; a pointer over a
  running request turns it into Withdraw with a confirming dialog, one that finishes while the page is open plays or reads through `?handler=Play`,
  one finished before leaves it to the hero's own actions; requesting stays on the page), Requests with its steps, Admin Requests, Activity (queue
  rows and the four tiles, entity `queue`), a title's downloads on its Work page and on a target.
  [Tests](../../src/Jularr.Frontend/Tools/live.test.mjs), `LiveTests`, `FailureTests.RestartDuringDownload_AndLostDatabaseConnections_Recover`.
- [History](../../src/Jularr.Frontend/Pages/Admin/History/History.cshtml.cs) (`/Admin/Queue/History`, also one Work's): what happened to the media,
  newest first.
- [Queue](../../src/Jularr.Frontend/Pages/Admin/Queue/Queue.cshtml.cs) (`/Admin/Queue`, first entry of the ARR group;
  [History](../../src/Jularr.Frontend/Pages/Admin/History/History.cshtml.cs) and
  [Blocked](../../src/Jularr.Frontend/Pages/Admin/Blocked/Blocked.cshtml.cs) at `/Admin/Queue/History` and `/Admin/Queue/Blocked` are the entry's
  other views; the old `/Admin/Activity`, `/Admin/History` and `/Admin/Activity/Blocked` redirect there with their query
  ([QueueRedirect](../../src/Jularr.Frontend/Pages/Admin/QueueRedirect/QueueRedirect.cshtml.cs)); shared
  [QueueViews](../../src/Jularr.Frontend/Pages/Admin/Queue/QueueViews.cs): the `type=a,b` choice of the
  [MediaTypeBar](../../src/Jularr.Frontend/Framework/Components/MediaTypeBar.cs) kept in every view's address, the read filters
  `MediaTypeIds`/`MediaSubtypeIds` of `ReadActivityV1`, `ReadActivityEventsV1` and `ReadBlockedReleasesV1`, and the row of the three views).
  The Queue reads the open downloads once (`ReadActivityV1` with `QueueOnly`) and shows them as one table like Sonarr's (state icon, title with the
  release or the orange reason under it, episode, episode title, quality, language, protocol, time left, progress, icon actions retry import / import
  by hand / source check / pause / resume, and the drawer of one entry at `?operation=`); a grab its indexer limited waits as a row until the pause
  ends; failed downloads are the History's; the torrent files stay a view at `?files=true`.
  The former filters, KPI tiles and operations history are gone (`?Filter=History` redirects to the History, other `Filter` values open the Queue).
  Blocked lists the releases blocked by hand or by a fault and allows one again through `MarkWantedReleaseV1`.
  Focused tests: `AcquisitionTests.Queue_FiltersAndTilesFollowTheStatesOfTheJobs`, `ActivityViews_HistoryAndBlocked_*`, `AdminMenuTests`; E2E
  `FailureTests`, `QueueAndTasksTests`.
