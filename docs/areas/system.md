# System: dashboard, health, backups and deployment

Owner dashboard, health checks, performance and logs, backups and the Docker image.
Index: [CODEMAP](../CODEMAP.md).
Specs: [RELEASE](../RELEASE.md), [LOCAL_PREVIEW](../LOCAL_PREVIEW.md).

## Backend

- Dashboard, health check, performance and logs (Owner): [HealthChecks.cs](../../src/Jularr.Backend/Service/Core/HealthChecks.cs) keeps the last
  answer for a minute and `HealthProbes` asks what is configured (storage worker's stored state, what each provider's client last got from
  [ProviderObservations.cs](../../src/Jularr.Backend/Service/Core/ProviderObservations.cs), Plex/Jellyfin/Emby, download clients and indexers through
  the one [AcquisitionConnectionProbe](../../src/Jularr.Backend/Service/Admin/Acquisition/V1/AcquisitionConnectionProbe.cs) that also serves the
  connection Test; an indexer's or news server's stored test counts 30 minutes; one in its backoff is `Paused` until it ends and is not asked);
  [HealthCheckLogic.cs](../../src/Jularr.Backend/Logic/Instance/HealthCheckLogic.cs) lists only configured, switched-on services and judges them; the
  `health` job ([HealthWorker](../../src/Jularr.Workers/HealthWorker.cs), every two minutes) fails its pass when a service does not answer, which the
  job mechanism turns into one Activity notice; [HealthServices](../../src/Jularr.Backend/Service/Admin/System/V1/HealthServices.cs) (`ReadHealthV1`
  never asks anybody, `CheckHealthNowV1`), [DashboardServices](../../src/Jularr.Backend/Service/Admin/System/V1/DashboardServices.cs) (works, files,
  open requests, storage per connection), [LogServices](../../src/Jularr.Backend/Service/Admin/System/V1/LogServices.cs) (`ReadLogsV1`:
  ActivityEvents, failed job notices and, with Diagnostics, the 1000 newest warnings and errors of
  [RecentLogs.cs](../../src/Jularr.Backend/Service/Core/RecentLogs.cs), filtered and paged), `ReadResourceSamplesV1` and `ReadServiceTimingsV1`
  (Diagnostics module).
  [ServiceTimings.cs](../../src/Jularr.Backend/Service/Core/ServiceTimings.cs) measures every service call per hour for 24 hours with one lock per
  slice; [ResourceHistory.cs](../../src/Jularr.Backend/Service/Core/ResourceHistory.cs) samples processor and memory once a minute (1440 samples);
  [PerformanceHistoryServices](../../src/Jularr.Backend/Service/System/Instance/V1/PerformanceHistoryServices.cs) write the new samples and changed
  slices as one batch every five minutes (`PerformanceSamples`, `ServiceTimings` in [Instance.sql](../../database/Instance.sql)) and read them back
  after a restart.
  Tests: `DashboardTests`, `ServiceTimingsTests`, E2E `DashboardTests`.
- Deployment: [Dockerfile](../../Dockerfile) (Jellyfin FFmpeg with Intel, AMD and NVIDIA hardware encoders, Chromaprint's static fpcalc pinned by
  hash, 7-Zip, par2, DjVuLibre (`djvulibre-bin`: ddjvu, djvused), PostgreSQL 18 client, wireproxy pinned by hash for the VPN module, OpenAI's Codex
  CLI pinned by hash for the AI connection; native libraries for the image platform only; `DataPath=/data`, libraries `/media`) and
  [compose.yaml](../../compose.yaml) with image, port and volumes only, smoke-tested by [image.yml](../../.github/workflows/image.yml); a `v*` tag
  publishes the image through [release.yml](../../.github/workflows/release.yml), a `tv-v*` tag the TV APK through
  [tv-release.yml](../../.github/workflows/tv-release.yml) ([RELEASE.md](../RELEASE.md));
  [DatabaseBaseline.cs](../../src/Jularr.Backend/Service/Core/DatabaseBaseline.cs) fills an empty database,
  [Executables.cs](../../src/Jularr.Integrations/Logic/Common/Executables.cs) finds the tools, `ServiceApi` puts keys, backups, trickplay, translation
  and OCR under `DataPath`; Plex sign-in is an Owner setting (`InstanceSettings.Plex*`, `ReadPlexSignInV1`/`UpdatePlexSignInV1`,
  [PlexLogic.cs](../../src/Jularr.Backend/Logic/Provider/PlexLogic.cs)) on the connected Plex server.
- Backups: [Admin backup services](../../src/Jularr.Backend/Service/Admin/Backup/V1/BackupServices.cs) (list, queue, delete, restore before setup
  completes), [System backup services](../../src/Jularr.Backend/Service/System/Backup/V1/BackupServices.cs) (the operation and the schedule,
  [BackupWorker.cs](../../src/Jularr.Workers/BackupWorker.cs)), folders, manifest (schema fingerprint, kind: automatic, update or manual; pruning to
  the keep limit counts automatic and update backups only) and the key ring sealed with `Backup:Passphrase` in
  [BackupFiles.cs](../../src/Jularr.Backend/Service/Core/BackupFiles.cs), `pg_dump`/`pg_restore` in
  [PostgresBackupTools.cs](../../src/Jularr.Integrations/Logic/Database/PostgresBackupTools.cs); tests `BackupTests.cs`,
  `DatabaseMigrationTests.Upgrade_UpdateBackup_*`.

## Frontend

- [Backup](../../src/Jularr.Frontend/Pages/Admin/Backup/Backup.cshtml.cs): backups with their kind (automatic, update, manual) as a tag beside the
  time, their schema version, back up now, delete; under `/Setup/Backup` also restore.
- [Dashboard](../../src/Jularr.Frontend/Pages/Admin/Dashboard/Dashboard.cshtml.cs) (`/Admin`, `/Admin/Dashboard`, sidebar group Overview): four
  figures (system status from the health check, media, running downloads with errors live, open requests), the load of 24 hours, the last four
  activities, every configured service with its state and last check (Check now asks again), and the volume of each library connection;
  [Performance](../../src/Jularr.Frontend/Pages/Admin/Performance/Performance.cshtml.cs) (processor, memory, response time, error rate, the chart and
  the services by runtime; Diagnostics module) and [Logs](../../src/Jularr.Frontend/Pages/Admin/Logs/Logs.cshtml.cs) (level chips, source menu,
  search, paged) in the System group; [System](../../src/Jularr.Frontend/Pages/Admin/System/System.cshtml.cs) (`/Admin/System`) keeps the server's
  numbers and tools.
  Backend owners: [docs/CODEMAP](../CODEMAP.md).
