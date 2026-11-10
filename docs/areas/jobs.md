# Background jobs and tasks

Declared background jobs, their schedule, the operation worker and the Jobs and Tasks pages.
Index: [CODEMAP](../CODEMAP.md).

## Decisions

Owner decisions; change them only with the owner.

- The **Jobs** entry (Operations) has **Tasks** (`/Admin/Jobs/Tasks`, first) and **Schedule** (`/Admin/Jobs`, the background jobs with pause and run
  now, and the history of finished tasks): Tasks lists Jularr's own work, scans, backups, media list imports, searches, the book being translated
  (read-only, leading to the book) and the notices of failed background jobs (a failed "Discover cache" or "Subtitles" job), in *Needs your help*
  (Retry where the task can run again, Dismiss; a job leads to the Schedule), *Running* and *Waiting*; the Jobs figure counts the tasks that need
  help, once.

## Backend

- Background jobs: [WorkerJobs.cs](../../src/Jularr.Backend/Service/Core/WorkerJobs.cs) declares every job (key, worker group, interval, start offset,
  run now, waiting while Jularr is used) and holds its state (due, paused, run now, last and next run, last error);
  [WorkerJobRunner.cs](../../src/Jularr.Backend/Service/Core/WorkerJobRunner.cs) runs one pass for its worker and reports it;
  [WorkerJobLogic.cs](../../src/Jularr.Backend/Logic/Worker/WorkerJobLogic.cs) keeps the Owner's choices and the last pass in `WorkerJobs`
  ([Instance.sql](../../database/Instance.sql)) and turns a failure into one `Operations` notice of type Job (Activity To-Do) that closes when the job
  runs well again; [System services](../../src/Jularr.Backend/Service/System/Worker/V1/WorkerJobServices.cs) load and record it for the workers,
  [Admin services](../../src/Jularr.Backend/Service/Admin/Worker/V1/WorkerJobServices.cs) list jobs and pause, resume, run now and set waiting while
  used.
  Tests: `WorkerJobTests` (registry, pause, run now, staggered start, failure notice, permissions) and E2E `JobsTests`.
  The Owner's intervals (backup interval and number kept, Plex/Jellyfin/Emby sync, wanted search; `InstanceSettings` in
  [Instance.sql](../../database/Instance.sql), edited on the Instance page) are read once by the registry (`UseSettings`, refreshed when the settings
  are saved) and by [BackupServices.cs](../../src/Jularr.Backend/Service/System/Backup/V1/BackupServices.cs); the Jobs rows show the effective
  interval.
  One pass of [ProviderSyncWorker.cs](../../src/Jularr.Workers/ProviderSyncWorker.cs) runs
  [SyncProviderV1](../../src/Jularr.Backend/Service/System/Provider/V1/ProviderSyncServices.cs) for Plex, Jellyfin and Emby in turn; each provider is
  a part job (`providerSyncPlex`, ...) with its own pause switch, result and failure notice, matching through `MatchWorksByIdentityAsync` and
  unlinking only after a complete read.
  [DownloadWorker.cs](../../src/Jularr.Workers/DownloadWorker.cs) tracks downloads on their own loop,
  [AcquisitionWorker.cs](../../src/Jularr.Workers/AcquisitionWorker.cs) only searches.
  Workers wait for work instead of polling: [WorkSignals.cs](../../src/Jularr.Backend/Service/Core/WorkSignals.cs) wakes the scan lane, the backup
  lane (claimed apart, so a backup runs beside a scan: `ClaimOperationV1` takes one operation type) and download tracking when work is queued or
  grabbed, with a slow safety tick.
  Tests: `BackgroundWorkTests`, `MediaServerTests.ProviderSync_*`, `AcquisitionTests.ProviderSync_*`, `BackupTests.Schedule_*`.
- [OperationWorker.cs](../../src/Jularr.Workers/OperationWorker.cs), [System operation
  services](../../src/Jularr.Backend/Service/System/Operation/V1/OperationServices.cs): hosted background scans and backups through request-free
  System services; [StoredFileLogic.cs](../../src/Jularr.Backend/Logic/Library/StoredFileLogic.cs) and
  [OperationLogic.cs](../../src/Jularr.Backend/Logic/Operation/OperationLogic.cs) write inventory and operation state;
  [MediaPathParser.cs](../../src/Jularr.Backend/Logic/Library/MediaPathParser.cs) extracts titles, episode units and provider IDs from full paths
  (including Plex-style ID tags).
  Video scans resolve unknown IDs via bounded, cached TMDB external-ID lookups.
  Work-level `tvshow.nfo` and `movie.nfo` IDs are read within a strict size/DTD limit and cross-checked with the path.
  Series `/.plexmatch` hints for IDs, title, year, season and explicit single-episode file mapping are read with a 32 KiB limit; unsupported pattern
  hints fail closed.
  Conflicts and multi-episode/daily episode filenames remain for review.
  Scans do not automatically move files.
  `ParseEpisodeRange` yields bounded ranges without associating a file with a single episode.
  `MediaAssetAdditionalUnits` and `MediaAssetUnitCoverage` are added to the fresh Media.sql baseline but not yet wired into playback/progress; only
  isolated fresh baselines are changed; no active-database migration is performed.

## Frontend

- [Jobs](../../src/Jularr.Frontend/Pages/Admin/Jobs/Jobs.cshtml.cs) (`/Admin/Jobs`, the Schedule of the Jobs entry, an example of the list kit): state
  chips with icon and count (`state`) and a filter field (`q`), then every background job as a row with its interval (the Owner's setting where there
  is one, edited on the Instance page), state with icon, last and next run, pause or resume, run now and a drawer of details (last error, what the job
  is for, switches for enabled and whether it waits while Jularr is used); Pause all and Resume all act on every job; the provider sync lists Plex,
  Jellyfin and Emby indented below it, each with its own switch and result, and below it the newest finished tasks (no downloads) as a second list; a
  failed job also appears in [Tasks](../../src/Jularr.Frontend/Pages/Admin/Tasks/Tasks.cshtml.cs).
  Backend owners: [docs/CODEMAP](../CODEMAP.md).
- [Tasks](../../src/Jularr.Frontend/Pages/Admin/Tasks/Tasks.cshtml.cs) (`/Admin/Jobs/Tasks`, the Jobs entry's first page, as Sonarr's System → Tasks):
  Jularr's own open work from `ReadActivityV1` with `TasksOnly` (scans, backups, media list imports, searches, notices of failed background jobs) in
  three groups, needs help (Retry through `RetryActivityV1` where the task can run again, Dismiss), running (also the book
  `ReadBookTranslationProgressV1` translates now, read-only) and waiting; the state is the Queue's
  [OperationState](../../src/Jularr.Frontend/Pages/Admin/Queue/OperationState.cs).
  The Jobs figure is `ReadActivitySummaryV1.TaskHelpCount`.
  Focused tests: `AcquisitionTests.Queue_FiltersAndTilesFollowTheStatesOfTheJobs`, `AcquisitionTests.AutomaticSearch_*`, `WorkerJobTests`,
  `AdminMenuTests`; E2E `QueueAndTasksTests`.
