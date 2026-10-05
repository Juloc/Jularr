# Admin System & Diagnostics — V1

Status: approved planning direction. Current System/Health/Logs/Resources implementation is the starting point. Approved mockups uploaded to this folder are visual references; this text remains binding.

Global UX rules: `docs/UX.md`.
Global Admin density contract: `docs/mockups/admin-instance/SPEC.md`.

If an image and this specification conflict, this specification wins.

## Purpose

System & Diagnostics is the technical server/instance diagnostics area behind the live Admin Dashboard.

It answers:

- Is the Jularr stack healthy?
- Which required dependencies are available and compatible?
- How much CPU/RAM/network/disk-I/O is Jularr + PostgreSQL using?
- What happened in logs?
- Can an admin create a safe diagnostic report?
- Is a newer Jularr version available?
- Are global workers/queues/runtime services healthy?

It is **not**:
- the live operations Dashboard;
- Storage configuration;
- Downloader queue/configuration;
- Provider configuration;
- AI configuration;
- Activity/History;
- a generic host/server administration console.

## Current implementation

The current code already contains useful pieces that must be reused rather than replaced.

### `/Admin/Health`

Already exposes, among other things:
- PostgreSQL health
- Jularr data-volume/storage health
- configured root availability
- ffmpeg / ffprobe availability
- job-queue health
- AI status
- acquisition status
- current Jularr version
- manual update check

### `/Admin/Logs`

Already exposes structured operation logs with:
- timestamp
- log level
- module
- message
- operation link
- filters for level/module/search

### `/Admin/Resources`

Already measures the **Jularr + PostgreSQL stack**, not arbitrary host processes.

Existing resource data includes:
- combined CPU
- combined RAM
- Jularr CPU/RAM
- PostgreSQL CPU/RAM
- Jularr network receive/send
- Jularr/PostgreSQL disk read/write
- short resource history
- visible storage/root capacity summary
- optional GPU-unavailable state

The existing resource telemetry and render logic should be reused for the new Resources tab.

### `/Admin/System`

Currently mixes several responsibilities:
- LibraryRoot/storage management
- scans/reconciliation
- Wake-on-LAN
- subtitle/Sonarr/indexer/download-client/acquisition/AI links

This is transitional legacy UI.

Target ownership:
- Mounts/LibraryRoots/Wake-on-LAN/storage path operations -> **Admin Storage**
- Downloader/client transport -> **Admin Downloader**
- Indexers/metadata/subtitle/translation providers -> **Admin Providers**
- Acquisition policy/scoring -> **Admin Acquisition**
- AI configuration -> **Admin AI**
- system health/resources/logs/diagnostics/update/runtime -> **System & Diagnostics**

Do not keep the mixed System page as a second configuration hub.

## Target navigation

System & Diagnostics is one Admin destination with secondary tabs:

1. **Übersicht**
2. **Ressourcen**
3. **Datenbank**
4. **Abhängigkeiten**
5. **Logs**
6. **Diagnose**
7. **Updates**
8. **Runtime**

Desktop route target may remain `/Admin/System` with tab routing/state.

The existing `/Admin/Resources`, `/Admin/Health` and `/Admin/Logs` routes may temporarily remain for backwards compatibility, but once consolidation is implemented they must:
- redirect/deep-link into the corresponding System tab; or
- share the exact same underlying page components/data contract.

They must not become parallel duplicate screens.

## 1. Übersicht

Purpose:
- quick technical health summary
- navigation into deeper tabs
- no duplicated detailed resource dashboard

Show concise status for:
- Jularr version/build
- PostgreSQL
- overall health
- ffmpeg/ffprobe or media tool state
- global background/job subsystem
- Storage availability summary
- AI/Acquisition availability only as a concise integration state where useful
- update state
- current warnings/errors

### Resources on Overview

Overview may show only a **small current snapshot**, such as:
- CPU %
- RAM used
- network current rate
- one resource warning

Do not show large historical charts here.

Primary action:
- `Ressourcen öffnen`

### Problems

If warnings/errors exist:
- show them above healthy details;
- link directly to the owning tab/page;
- keep healthy state visually quiet.

Examples:
- PostgreSQL unreachable
- ffmpeg missing
- high stack memory
- storage unavailable
- worker blocked
- update check failed

## 2. Ressourcen

This tab absorbs the existing standalone Admin Resources page.

### Scope

Resource telemetry is restricted to:
- Jularr web/app service/container
- PostgreSQL service/container
- mounts/storage visible to the Jularr stack

Do not present host-wide CPU/RAM/process telemetry as if it belonged to Jularr.

### Current totals

Show:
- combined Jularr + PostgreSQL CPU
- combined RAM
- Jularr network download/upload
- combined/read-write disk I/O where measurable

### Per-service

Jularr:
- CPU
- RAM
- receive/send
- read/write I/O

PostgreSQL:
- CPU
- RAM
- read/write I/O
- network only if reliably measurable in the chosen deployment model

### History

Historical charts can switch between:
- CPU
- RAM
- Network
- Disk I/O

Minimum useful periods:
- short live window using currently collected data
- longer periods only when the backend actually persists reliable samples

Do not fake 24h/7d history from a five-minute in-memory buffer.

### Application performance (#860)

Resources also includes a performance-focused view over Jularr itself, not only CPU/RAM charts.

Show bounded aggregated evidence such as:
- route/endpoint request count and duration by stable route template;
- slowest/common/high-total-time endpoints;
- error/cancellation rate;
- important application use-case timings;
- background queue depth, queue wait and execution duration;
- worker saturation/throttling from #857;
- external provider HTTP latency/error/queueing by stable provider identity;
- .NET GC pause/allocation/heap and ThreadPool queue/lock-contention evidence where useful;
- optional privacy-conscious sampled browser/PWA performance trends when implemented.

Do not use raw resource URLs, user IDs, titles, file paths or arbitrary exception text as metric dimensions. Metric cardinality must stay bounded.

Use built-in ASP.NET Core/.NET metrics where they already provide the signal. Persist only bounded aggregate time buckets needed for history; do not write one permanent telemetry row for every normal request.

Database-specific statement/index/lock analysis remains in the **Datenbank** tab (#859).

### Container/resource limits

Where cgroup/container limits are available, show:
- current use
- configured limit
- percentage of limit

Examples:
- CPU allocation
- memory limit

If there is no limit, show `unbegrenzt`/unknown rather than inventing one.

### GPU

GPU/VRAM appears only when reliably measurable.

Possible states:
- available and measured
- available but attribution unavailable
- unavailable
- unsupported

No fabricated GPU percentage.

### Storage summary

Resources may show read-only mount utilization:
- mount name/path
- used/total/free
- online state

This is operational telemetry only.

Actions lead to **Admin Storage** for:
- path changes
- role/root changes
- mount configuration
- Wake-on-LAN
- scan/reconciliation policy

### Navigation change

Once the Resources tab is complete, remove the standalone `Resources` destination from the main Admin sidebar.

Existing `/Admin/Resources` can remain as a compatibility redirect/deep link.

## 3. Datenbank

Purpose:
- PostgreSQL performance/DBA diagnostics;
- show the SQL workload actually causing database cost;
- expose table/index/vacuum/I/O/locking evidence;
- preserve bounded historical samples so regressions are visible across PostgreSQL statistics resets/restarts.

This tab implements #859 and is Admin/System-only.

### Overview

Show concise evidence such as:
- PostgreSQL version and database size;
- active/idle connections;
- transaction commit/rollback context;
- cache/read/temp-I/O context;
- deadlock count/current lock waits;
- most expensive statement in the selected recent period;
- slowest frequently-called statement;
- largest table/index;
- autovacuum/analyze warnings when evidence exists.

Do not produce a fake one-number database health score.

### Statements

When `pg_stat_statements` is available, show normalized/query-id based statistics:
- calls;
- total and mean execution time;
- min/max where available;
- rows;
- shared block hits/reads;
- temp blocks;
- I/O timing when enabled;
- WAL/parallel metrics where supported.

Sort/rank by:
- total database time;
- mean execution time;
- call count;
- physical/temp I/O;
- rows processed.

Jularr persists bounded statement-stat deltas/history so an admin can compare hour/day/week periods and detect regressions. Handle PostgreSQL statistics reset epochs explicitly.

Never run arbitrary `EXPLAIN ANALYZE` automatically. It executes statements and may mutate data.

If `pg_stat_statements` is unavailable, show a clear setup/unsupported state. Do not grant PostgreSQL superuser to the normal application just to obtain diagnostics.

### Tables

Show PostgreSQL-native table evidence such as:
- table/data/index/total size;
- estimated live/dead tuples;
- sequential/index scan activity;
- insert/update/delete/HOT-update activity where useful;
- last vacuum/autovacuum;
- last analyze/autoanalyze;
- vacuum/analyze counts/times where supported.

Warnings are review hints only. Do not automatically run VACUUM FULL or rewrite tables.

### Indexes

Show:
- table/index name and type/definition where useful;
- index size;
- scan/use counters;
- tuples read/fetched;
- last scan where supported;
- whether the index backs PK/UNIQUE/constraint semantics where derivable.

Zero/low scans means "review candidate since the current statistics epoch", not "safe to delete".

PostgreSQL does not use a SQL Server-style fragmentation percentage as the normal index-maintenance model. Baseline diagnostics use physical size + workload evidence.

Optional deeper B-tree inspection via PostgreSQL `pgstattuple`/`pgstatindex` may expose density/fragmentation/page evidence **only on explicit Admin request** when the extension and privileges are available. Do not scan every index for bloat on every page load.

Any REINDEX recommendation is evidence-based. No blind nightly rebuild/reorganize job.

### Locks & waits

Use PostgreSQL activity/lock views and blocking relationships to show:
- long-running active queries/transactions;
- current lock waits;
- blocker -> blocked chains;
- wait type/event;
- age.

Cumulative deadlocks are shown from PostgreSQL statistics. Historical detailed deadlocks come from linked Jularr incidents/log evidence when available; a current lock snapshot cannot reconstruct a past deadlock.

No raw SQL editor. Cancel/terminate controls are a separate future safety decision and must not be implied by diagnostics alone.

### I/O / Vacuum

Where supported, show:
- PostgreSQL I/O by relevant context;
- physical reads/writes/bytes;
- I/O timing when configured;
- temp I/O;
- checkpoint/checkpointer/bgwriter evidence;
- WAL generation;
- autovacuum/analyze health.

`track_io_timing` has potential platform overhead and is not silently forced on every installation.

### History

PostgreSQL cumulative/runtime views are not a durable history. Jularr therefore stores bounded typed snapshot/delta tables for useful database metrics.

History:
- has explicit sample/reset epoch;
- uses bounded retention and rollups;
- is sampled by background work, not recalculated on every Admin read;
- follows #857 resource budgets;
- is sanitized and contains no bind parameter values/secrets.

### Security

Database diagnostics and exports follow #853 sanitization:
- no connection-string secrets;
- no bind values;
- normalized/bounded query text only;
- no tokens/passwords accidentally copied into retained diagnostics.

## 4. Abhängigkeiten

Purpose:
- technical dependency availability and compatibility
- explicit manual tests
- version visibility

### Required/core dependencies

Examples where applicable:
- PostgreSQL
- ffmpeg
- ffprobe
- unrar
- 7zip
- media probing/processing tools actually used by Jularr

Columns:
- component
- installed/detected version
- status
- last test
- concise health/result
- actions

Actions:
- Testen
- Details where meaningful

### Optional acceleration/runtime capabilities

Optional capabilities may include:
- NVIDIA/CUDA/NVENC
- Intel Quick Sync / QSV
- VAAPI
- AMD AMF

Only show capabilities the runtime can detect.

Unavailable optional acceleration is not automatically an error.

### Compatibility

Show:
- detected version
- minimum/known-supported version where Jularr defines one
- supported / warning / incompatible / unknown

Compatibility rules come from application contracts, not hard-coded UI guesses.

### Not here

Do not duplicate:
- Provider health
- Indexer health
- Downloader server/client health
- Storage root configuration

Those belong to their owning Admin areas and can be linked from Overview if degraded.

## 5. Logs

Logs remains the technical structured log explorer.

### Filters

Support:
- time range
- level
- module/category
- operation/job ID
- correlation ID
- free-text search

### Table

Default columns:
- time
- level
- module
- message
- correlation ID
- actions

Detailed mode may expose more columns/context.
Compact mode keeps one dense row per event.

### Detail drawer

Selecting a row opens a detail drawer/sheet with:
- exact timestamp
- level
- module
- operation/job
- correlation ID
- thread/process context where available
- message
- structured context
- exception/stack trace where available
- related actions

Related actions may include:
- open Job/Operation
- open related Work
- open related Session
- open related Provider/Downloader item
- create/open diagnostics filtered to the correlation ID

Only expose links supported by the actual event context.

### Secret safety

Logs must sanitize:
- API keys
- passwords
- authorization headers
- cookies/tokens
- provider secrets
- database credentials

Do not make `Debug` or trace-level data a secret bypass.

### Export

Log export must use the same sanitization policy as Diagnostics.

## 6. Diagnose

Purpose:
- create an admin/support diagnostic package without exposing secrets.

### Report categories

Selectable categories may include:
- Jularr version/build
- platform/runtime
- database version/state
- active instance modules
- dependency state
- masked relevant configuration
- runtime/worker state
- recent warnings/errors
- storage/mount availability summary
- selected sanitized logs

### Preview

Before download/export, show a structured preview.

The admin must be able to inspect what categories are included.

### Formats

Supported formats may include:
- JSON
- text
- ZIP bundle containing the structured report plus selected sanitized logs

Do not promise a format until the backend supports it.

### Sanitization

Always remove or mask:
- passwords
- API keys
- tokens
- cookies
- connection-string secrets
- personal AI credentials
- provider credentials
- session secrets

Where paths/usernames could be privacy-sensitive, the report contract may support optional redaction.

### Previous reports

If reports are persisted:
- filename/reference
- created time
- size
- format
- status
- download
- delete
- regenerate

Retention must be explicit.

If reports are generated transiently only, do not build fake history UI.

## 7. Updates

Purpose:
- show version information and check whether a newer Jularr release exists.

Show:
- installed version
- build/commit where available
- update channel when supported
- latest checked version
- last check
- state: current / update available / unavailable / check failed
- release link / release notes link

Actions:
- check now
- open release/release notes

### No automatic container update in V1

Jularr does not silently update its own Docker/container environment.

The page may explain:
- which version is running;
- which version is available;
- where to get release information.

Actual container/image update remains an operator/deployment action unless a future explicitly designed update subsystem is approved.

Do not render an `Install Update` action that cannot safely perform the deployment.

## 8. Runtime

Purpose:
- inspect Jularr's global worker/job runtime
- configure only genuinely global runtime limits backed by a canonical runtime-settings contract

### Status

Show:
- active jobs
- queued jobs
- blocked/retryable jobs
- failed recent jobs
- worker/service health
- scheduler state
- uptime where meaningful

### Worker/service table

Potential rows:
- operation/job worker
- scheduler
- event processor
- cleanup service
- metadata worker
- AI worker

Only show actual runtime services.

Columns:
- service
- status
- workers/threads where measurable
- current task
- last activity
- uptime/restart state where known
- details

### Queue

Show:
- queued
- running
- completed in selected period
- failed in selected period

A small historical queue chart is allowed.

Deep individual job details remain in Activity/Operations.

### Runtime settings

Only expose settings that are:
- global;
- actually implemented;
- centrally owned by the runtime/job subsystem.

Possible future settings:
- global maximum parallel jobs
- global worker count
- default job timeout
- retry count/backoff
- startup recovery behavior

Important:
- Downloader-specific connections/parallel downloads -> Downloader
- AI task concurrency/budget -> AI
- provider rate limits -> Providers
- scan/reconciliation schedules -> Storage/Library
- per-task feature policies -> owning module

If no canonical runtime-settings store exists yet, Runtime is read-only until one is implemented.

Do not create independent settings in Razor/page models merely to match the mockup.

## Dashboard boundary

The Admin Dashboard and System & Diagnostics intentionally overlap only at the **summary level**.

### Dashboard owns

Real-time operator view:
- overall health
- problems needing attention
- small current CPU/RAM/network snapshot
- active playback sessions
- active downloads
- active Jularr jobs
- storage warning summary
- live recent activity

### System & Diagnostics owns

Deep technical inspection:
- detailed stack resources/history
- dependencies
- structured logs
- diagnostic export
- version/update checks
- worker/runtime internals

Dashboard links into the corresponding System tab with filters/context.

Do not duplicate the entire Resources page on Dashboard.

## Storage boundary

Admin Storage owns:
- physical mounts
- LibraryRoots
- storage roles
- paths
- safe path browser
- write/read tests
- Wake-on-LAN
- capacity configuration/reserve
- reconciliation policy
- lifecycle policies / Review / optimization / tiering / physical migration (#414)
- storage forecast, reclaimable-space analysis and lifecycle history

System → Resources may display mount capacity/health read-only.

System → Overview may display a simple Storage health summary.

All storage mutations link to Admin Storage.

System must not become a second cleanup/optimization/policy editor. It may surface a storage warning/summary and link to the exact Storage view or Activity operation.

## Downloader boundary

Downloader owns:
- NNTP/server health
- technical download queue
- download throughput
- verify/repair/extract
- downloader concurrency/bandwidth/scheduling
- external-client adapters

System must not duplicate downloader queue configuration.

A downloader service failure may appear as a System warning with a link to Downloader.

## Activity / History boundary

Activity/To-Do owns current actionable operations and human intervention.

History owns completed operational record.

Logs owns technical event/log inspection.

Runtime owns global worker/queue health.

These surfaces can cross-link by Operation/Job/Correlation ID but must not become duplicate lists of the same purpose.

## Global Detailed / Compact mode

The shared Admin `Detailliert | Kompakt` preference applies to every System tab.

### Detailed

May show:
- summary cards
- descriptions
- contextual hints
- expanded charts
- side details

### Compact

Prefer:
- tables
- dense status rows
- smaller chart footprint
- reduced repeated labels/help text
- inline actions

Same data, permissions, validation and actions in both modes.

Compact never hides:
- failures
- warnings
- incompatibility
- secrets/redaction state
- destructive action context

## Light / Dark / Visual skin

Light and Dark are first-class.

Clean and Original Jularr visual skins share the same layout and semantics.

Operational diagnostics must remain readable; decorative skin elements may never reduce contrast or information density.

## Platforms

### Desktop

Primary experience for all tabs.

### Tablet

Supported:
- overview
- resources
- dependency tables
- logs
- diagnosis
- updates
- runtime status

Dense tables may horizontally scroll or become two-pane layouts.

### Mobile

Essential diagnostics only:
- health summary
- current resource state
- warnings
- dependency status
- log search/detail
- update state
- diagnostic creation/download where practical

Do not squeeze full desktop resource charts/tables into narrow layouts.

### TV

Unsupported.

## Loading / Empty / Error / Partial states

Required:
- loading health
- all healthy
- degraded
- critical failure
- dependency test running
- dependency missing
- unsupported dependency version
- resource sample unavailable
- collecting resource history
- stale resource sample
- no GPU
- logs empty
- no logs matching filter
- log detail unavailable
- update never checked
- update check running
- update available
- update check failed
- diagnostics generating
- diagnostics ready
- diagnostics generation failed
- runtime queue empty
- worker degraded/stopped
- permission denied

## Security / permissions

System & Diagnostics requires appropriate Admin/System capability.

Do not expose:
- secret values
- unrestricted filesystem data
- arbitrary environment variables
- arbitrary shell commands
- raw database editing
- other users' private tokens/credentials

Diagnostic and log exports are sensitive files and require authorization.

## Implementation migration

Target consolidation should be incremental.

1. Reuse existing Health, Resources and Logs services/models.
2. Introduce shared System tab shell/navigation.
3. Move the Resources presentation into the Resources tab.
4. Make `/Admin/Resources` a compatibility redirect/deep link.
5. Move Health content into Overview/Dependencies/Updates as appropriate.
6. Move Logs content into the Logs tab.
7. Move Storage mutations out of legacy `/Admin/System` into Admin Storage.
8. Move provider/downloader/acquisition/AI links/configuration to their owning Admin areas.
9. Remove duplicate standalone Admin navigation entries only after the new destination has parity.

No big-bang rewrite is required.

## Must not implement

- No duplicate full resource dashboard on Overview or Admin Dashboard.
- No host-wide telemetry presented as Jularr telemetry.
- No Storage configuration inside Resources.
- No Downloader configuration inside Runtime.
- No Provider/AI configuration inside System.
- No duplicate standalone Resources UI after consolidation.
- No secret values in logs/diagnostic exports.
- No arbitrary shell execution.
- No environment-variable dump.
- No direct EF/database editor.
- No fake GPU/resource/history values.
- No automatic Docker/container update in V1.
- No runtime-setting controls without a canonical backend contract.
- No normal-user access.
