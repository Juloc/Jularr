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

### Database Overview — binding first-screen UI contract

This subsection defines the first detailed database screen to implement/mock up.

Canonical destination:

`Admin -> System & Diagnostics -> Datenbank -> Übersicht`

Preferred route/state:

`/Admin/System?tab=database&view=overview`

A different internal route is acceptable if the shared Admin shell requires it, but Database remains a secondary System & Diagnostics destination rather than a new main-sidebar item.

Approved future mockup asset name:

`docs/mockups/admin-system-diagnostics/database-overview-light.png`

The first approved mockup is **Desktop / Light / Clean / Detailed mode**. Dark, Original Jularr and Compact reuse the same information architecture.

#### Primary question

Within roughly five seconds an admin should be able to answer:

1. Is PostgreSQL reachable and behaving normally?
2. Is database pressure contributing to a slow Jularr instance?
3. Which SQL workload is consuming the most database time?
4. Are locks, long transactions, deadlocks, temp I/O or vacuum/analyze pressure present?
5. Which table/index is consuming unusual space or work?
6. Is this a current spike or a recent regression?

The screen is operational diagnostics, not a generic analytics dashboard and not a database-management console.

#### Page shell

Use the normal shared Admin shell.

Top area, in order:

1. shared page title: **System & Diagnostics**;
2. primary System tabs, with **Datenbank** active;
3. Database secondary navigation:
   - Übersicht
   - Statements
   - Tabellen
   - Indizes
   - Locks & Waits
   - I/O & Vacuum
   - Verlauf
4. one compact toolbar for time/freshness context.

Do not repeat another large `Datenbank` hero title under the active tab. A small section heading or concise context line is enough.

#### Database toolbar

Desktop right side:

- time range selector for historical/aggregate context:
  - 15 min
  - 1 h — default
  - 24 h
  - 7 d
  - 30 d when retained history supports it;
- live/freshness indicator:
  - green dot + `Live` while current lightweight state is fresh;
  - `Updated 12 s ago` or localized equivalent;
  - stale/reconnecting state when sampling is delayed;
- optional overflow/info action for collection capability/status.

The selected time range affects historical and statement-aggregate panels. It does **not** pretend current lock/session state is historical.

Do not add a large primary `Refresh` button when automatic bounded sampling is healthy. A retry/refresh action appears only when live data is unavailable/stale or the backend exposes a real manual resample action.

#### Problem banner

When there is a material current problem, place one concise problem strip directly below the toolbar and above healthy summaries.

Examples:
- PostgreSQL unreachable;
- connection saturation;
- active blocking chain;
- unusually long transaction;
- deadlocks increased in the selected period;
- autovacuum materially behind;
- statement statistics unavailable because `pg_stat_statements` is not configured.

Rules:
- highest-severity current issue first;
- one-line primary message;
- optional concise evidence;
- direct action/link into the owning Database subview;
- multiple lower-priority issues collapse behind `+N weitere`;
- when healthy, do not reserve a large empty warning card.

Historical anomalies that are no longer active should appear in the History/Trend area, not as a permanent red current-state banner.

#### Summary band

Below problems, use one dense horizontal summary band rather than large KPI cards.

Desktop Detailed mode contains six compact cells:

1. **PostgreSQL**
   - Online / Warning / Error
   - detected version as secondary text.

2. **Datenbankgröße**
   - current database size;
   - small selected-period delta only when reliable history exists.

3. **Verbindungen**
   - active + idle/total context;
   - current pressure state;
   - do not present application Npgsql pool occupancy here as if it were PostgreSQL session count. Pool detail belongs to Application Performance (#860).

4. **DB-Zeit**
   - total statement execution time accumulated in the selected period from persisted deltas;
   - optional tiny trend indicator;
   - label must make clear this is aggregate database execution time, not wall-clock uptime.

5. **Locks / Deadlocks**
   - current waiting sessions / blockers;
   - deadlock count in selected period;
   - healthy zero stays visually quiet.

6. **Vacuum / Analyze**
   - Healthy / Attention / Unknown;
   - concise count of tables requiring attention, not a fabricated overall percentage.

Compact mode turns the same data into one dense status row/table. Mobile stacks it into two-column or single-column rows while keeping touch targets accessible.

No vanity metrics and no unexplained `health score 83%`.

#### Main desktop grid

After the summary band, use a **12-column responsive grid**.

Desktop:
- left/main column: 8 columns;
- right/diagnostic column: 4 columns.

At narrower tablet widths, sections become one column while preserving order.

##### Main panel A — SQL workload

Position: first, left 8 columns.

Title:
**SQL-Workload**

Purpose:
show the few statements currently responsible for most database cost without requiring the admin to open the full Statements page.

Header controls:
- segmented sort:
  - Gesamtzeit — default;
  - Durchschnitt;
  - Aufrufe;
  - I/O;
- `Alle Statements` link opens Statements preserving selected range + sort.

Table rows: maximum 5 on Overview.

Columns in Detailed Desktop:
- Statement
- Aufrufe
- Gesamtzeit
- Ø Zeit
- Reads / I/O signal

Statement cell:
- one or two lines of normalized, sanitized SQL;
- monospace;
- parameter values never shown;
- no horizontal page-wide code overflow;
- truncate visually with full bounded normalized text available in detail drawer;
- optional small owning/app correlation label only when reliably known, e.g. `Library.Browse`.

Highlighting:
- do not color a statement red merely because it is first;
- warning color only if a real threshold/regression/problem rule is met;
- show a small regression indicator when recent history establishes a meaningful change from baseline.

Row activation opens the **Statement quick-detail drawer** described below.

If `pg_stat_statements` is unavailable, replace the table body with a compact capability state explaining that statement-level statistics are unavailable. Keep all other database panels functional.

##### Side panel B — Current pressure

Position: first, right 4 columns.

Title:
**Aktueller Druck**

Use a compact key/value/status list, not six mini cards.

Rows when data exists:
- aktive Queries;
- wartende Queries;
- längste aktive Query;
- längste Transaktion;
- physical-read pressure;
- temp I/O;
- cache-hit context;
- rollback rate/context where useful.

Each row:
- label;
- current value;
- quiet Healthy / Warning / Critical semantic state only when a threshold is justified;
- optional tiny sparkline only for metrics with persisted recent samples.

Do not show metrics merely because PostgreSQL exposes them. Every row must answer a diagnostic question.

##### Main panel C — Locks & long transactions

Position: second row, full width when active; otherwise compact left/main placement.

When active blockers/waits or materially long transactions exist:
- promote this panel directly below SQL Workload/Current Pressure;
- show up to 4 active rows.

Columns:
- Waiting / Transaction
- Age
- Wait event
- Blocked by
- Statement/operation summary
- Details action

A blocker chain should read humanly, e.g.:

`Library browse query -> waits for transaction 812 -> Work update`

Do not expose raw parameter values.

When there are no waits and no problematic long transaction:
- do not render a large empty table;
- show one compact calm line such as `Keine aktiven Lock-Waits` with `Locks & Waits öffnen`.

Deadlocks are historical events and belong in the selected-period summary/history unless an incident from #853 is currently relevant.

##### Main panel D — Tables & maintenance attention

Position: next left/main 8 columns.

Title:
**Tabellen & Wartung**

Purpose:
surface only tables that deserve attention.

Maximum 5 rows, sorted by strongest evidence:
- high dead-tuple pressure;
- autovacuum/analyze concern;
- unexpectedly heavy sequential workload on a materially large table;
- unusual growth;
- another explicitly modeled PostgreSQL warning.

Columns:
- Tabelle
- Größe
- Live / Dead rows context
- Scan pattern
- Vacuum / Analyze
- Status

Healthy tables do not fill this table merely to reach five rows.

Empty healthy state:
a compact `Keine Tabellen benötigen aktuell Aufmerksamkeit` row plus link to `Alle Tabellen`.

Do not place `VACUUM FULL`, `REINDEX` or other maintenance execution buttons on Overview.

##### Side panel E — Space

Position: next right 4 columns.

Title:
**Speicher**

Show:
- DB total;
- table data;
- indexes;
- largest table;
- largest index.

Use a restrained horizontal stacked bar only if the values can be measured reliably and it improves comparison.

Below it:
- `Tabellen öffnen`;
- `Indizes öffnen`.

Do not show WAL/archive disk usage here unless Jularr can attribute it reliably to the database/deployment.

##### Full-width panel F — Verlauf / regression glance

Position: bottom full width.

Title:
**Letzte Entwicklung**

This is not the full History page. It provides a compact glance over the selected time range.

Desktop contains up to four small aligned trend plots in one visual band:
- DB execution time / rate;
- physical reads or read pressure;
- temp I/O;
- database size.

Rules:
- all plots share the same selected time axis;
- deployment/Jularr version markers appear when that information is reliably available;
- statistics-reset boundary appears as a visible discontinuity/annotation rather than connecting false deltas;
- no fake smooth interpolation across missing samples;
- no decorative chart if there is insufficient history.

Action:
`Verlauf öffnen` opens the full History subview with the selected range.

#### Statement quick-detail drawer

Activating one SQL Workload row opens a right-side drawer on Desktop/Tablet.

Header:
- `Statement`;
- QueryId/short stable identifier;
- current classification (Normal / Warning / Regression) only when modeled;
- close action.

Sections:

1. **Normalized SQL**
   - bounded sanitized SQL block;
   - copy action may copy only this sanitized normalized SQL.

2. **Selected period**
   - calls;
   - total execution time;
   - average;
   - min/max when available;
   - rows;
   - block hits/reads;
   - temp I/O;
   - I/O timing when enabled.

3. **Trend**
   - small history chart for execution time/calls;
   - reset/version markers.

4. **Related Jularr context**
   - route/use-case names from #860 only when reliably correlated;
   - never guess ownership from SQL text.

Footer actions:
- `In Statements öffnen`;
- optionally `Diagnose öffnen` when there is a real diagnostic workflow.

Must **not** contain:
- Execute;
- Edit SQL;
- Kill;
- automatic `EXPLAIN ANALYZE`;
- bind parameter values;
- arbitrary raw database console.

Mobile uses a full-height sheet/page instead of a narrow drawer.

#### Refresh/sampling behavior

The Overview is assembled from independently sampled sources.

Target UX behavior:
- lightweight live connection/wait/session state updates roughly every 5–10 seconds when practical;
- statement/statistics aggregates normally sample on a slower bounded cadence such as 30–60 seconds;
- table/index size and maintenance data may refresh substantially less often;
- exact backend cadence remains centrally configurable/implementation-owned and must not be hard-coded independently in the page.

Every panel carries enough freshness metadata internally to distinguish:
- current;
- stale;
- collecting;
- unavailable.

A slow/unavailable panel must not blank the rest of the page.

Incoming updates must not:
- reset selected time range;
- change selected workload sort;
- close a Statement drawer;
- jump scroll position;
- reorder a table while the admin is actively selecting/copying unless the update can be applied without interaction loss.

#### Detailed vs Compact

Both modes use the same data contract and actions.

Detailed:
- summary band;
- short contextual subtitles where useful;
- 5-row Overview tables;
- trend band;
- Statement drawer with expanded metric labels.

Compact:
- summary as one dense status table/strip;
- fewer secondary descriptions;
- 3–5 dense rows;
- smaller trend band;
- secondary values move into row detail/drawer;
- warnings and actions remain fully visible.

No feature or diagnostic signal exists only in Detailed mode.

#### Light / Dark / skins

The first mockup is Clean Light.

Clean Light:
- neutral off-white app background;
- white or subtly separated surfaces;
- restrained purple Jularr accent;
- thin neutral borders/dividers;
- semantic Warning/Error colors only for real state;
- monospace SQL distinct but not placed in a dark terminal block by default.

Dark uses the same hierarchy and density with semantic dark tokens.

Original Jularr may apply the shared Original skin, but Database diagnostics remain restrained. Do not place mascot artwork, watercolor backgrounds or decorative sakura elements behind dense SQL/tables/charts where they reduce scanability.

#### Responsive behavior

Desktop is the primary DBA experience.

Tablet:
- Database secondary navigation may horizontally scroll;
- 8/4 grid collapses to one column;
- Statement drawer may become wider/two-pane;
- tables retain horizontal scroll only when column reduction would lose essential meaning.

Mobile:
- Overview is supported for essential diagnostics;
- summary cells become compact stacked rows;
- SQL Workload shows Statement, total/average time and one secondary metric; remaining columns move into detail;
- Tables Attention shows table + status + primary evidence;
- charts become one-at-a-time compact rows;
- Statement details use full-screen sheet;
- no tiny desktop table squeezed below usable width.

TV: unsupported.

#### Loading / partial / empty / error states

Required explicit states:

- initial Overview loading;
- PostgreSQL online;
- PostgreSQL unreachable with historical data available;
- PostgreSQL unreachable with no history;
- statement statistics available;
- `pg_stat_statements` unavailable/not configured;
- statement statistics collecting first sample;
- PostgreSQL statistics reset detected;
- history collecting;
- selected range has no history;
- one metric source unavailable due to privilege/configuration;
- no active lock waits;
- active blocker chain;
- long-running transaction;
- deadlocks in selected period;
- maintenance attention;
- all maintenance healthy;
- stale sample;
- sampler delayed/throttled by #857;
- permission denied.

Partial failure is a first-class state. For example, if statement statistics are unavailable, the page can still show DB health, connections, size, locks and table maintenance.

#### Threshold semantics

The UI must not invent universal PostgreSQL thresholds solely for visual coloring.

A warning/critical state must come from:
- a defined Jularr threshold with documented rationale;
- instance/resource budget context;
- a known correctness/availability condition;
- or a meaningful regression against reliable retained baseline.

Examples:
- `1 sequential scan` is never automatically bad;
- `0 index scans` never automatically means remove index;
- cache-hit ratio alone is not a universal red/green score;
- high DB execution time may be normal if call volume also rose;
- table dead tuples require context such as table size/churn/autovacuum state.

Tooltip/help may explain why a row is flagged.

#### Security / permissions

Requires Admin/System diagnostics authorization.

The Overview and drawer never expose:
- database password/connection string secrets;
- bind parameter values;
- auth tokens/cookies/API keys;
- arbitrary environment variables;
- unrestricted raw SQL execution;
- a raw database editor.

Normalized SQL still passes the same sanitization/redaction policy as #853 before persistence/display/export.

#### Accessibility

- all state colors also have text/icon meaning;
- tabs and segmented controls keyboard accessible;
- sortable table headers expose current sort;
- charts have accessible text summaries;
- drawer focus is trapped/restored correctly;
- SQL code block supports keyboard selection/copy;
- warning icons have meaningful labels;
- live updates use non-disruptive announcements and do not continuously spam screen readers.

#### Overview acceptance criteria

The Database Overview is complete only when:

- it uses the shared System & Diagnostics/Admin shell;
- Database secondary navigation exists and Overview is the default Database view;
- the time range and freshness state are clear;
- current critical DB problems are prioritized without permanent warning clutter;
- summary band distinguishes PostgreSQL sessions from Npgsql application pool metrics;
- top SQL workload is visible and can open safe detail;
- `pg_stat_statements` absence degrades only the statement panel;
- current lock/blocking problems are immediately visible;
- maintenance attention is evidence-based;
- storage composition is understandable;
- bounded history shows real gaps/reset/version markers rather than fabricated continuity;
- partial failures do not blank unrelated panels;
- all visible SQL is normalized/sanitized;
- no destructive/raw SQL action is exposed;
- Detailed/Compact use the same contract;
- Desktop/Tablet/Mobile behavior follows this spec;
- the approved mockup matches this information order and density.

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
