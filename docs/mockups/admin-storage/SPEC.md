# Admin Storage + Safe Path Browser — V1

Status: approved visual/interaction direction for Desktop; current mockup is the binding layout reference once uploaded to this folder.

Global UX rules: `docs/UX.md`.

If the visual reference and this specification conflict, this specification wins.

## Purpose

Storage manages **physical mounts** and the **logical storage roles** Jularr places on them.

It answers:
- Which filesystems/mounts are available?
- How much physical capacity is actually free?
- Which LibraryRoots live on each mount?
- Where does Jularr's native downloader keep incomplete/completed/work files?
- Where do generic/unclassified downloads end up?
- Which paths may Jularr safely browse/write?

Do not model every LibraryRoot as if it were its own physical disk.

## Storage model

### 1. Physical Mount

Examples:
- `/media` -> `/dev/sdb1` -> ext4 -> 8 TB
- `/nas/media` -> NFS -> 16 TB
- `/fast` -> SSD -> 2 TB

Capacity, free space, filesystem health and mount availability belong to the Mount.

If Anime, Movies and Downloads are all under `/media`, capacity is shown once for `/media`.

### 2. Storage Role / Root

A path inside an allowed Mount can be assigned one or more Jularr roles.

Required role families:

#### LibraryRoot
Final canonical library destination.

Examples:
- Anime -> `/media/anime`
- Series -> `/media/series`
- Movies -> `/media/movies`
- Manga -> `/media/manga`
- Books -> `/media/books`
- Audiobooks -> `/media/audiobooks`
- Games -> `/media/games`

A LibraryRoot may support one or multiple media/content types.

Games is a specialized managed library content type. Identified Games are imported through the Games importer into a Games-capable LibraryRoot; they are not Generic Downloads.

#### Native Download Workspace
Working storage for Jularr's built-in Usenet downloader.

Example:
- `/media/.jularr/downloads`

Jularr manages internal subdirectories beneath the workspace, such as:
- incomplete/article assembly
- completed/staging
- verification/repair
- extraction/post-processing

The normal UI configures the workspace root, not every internal subdirectory separately.

Advanced configuration may allow the heavy repair/extract workspace to live on a different permitted Mount, e.g. a fast SSD.

#### Generic Downloads Root
Final destination for downloads that do not map to a specialized library.

A download already known as `ContentKind.Game` does **not** belong here. It is handed to the Games importer and placed in a Games-capable LibraryRoot.

Examples:
- software/installers
- archives
- datasets
- downloaded backup/archive files that are merely generic content, **not** Jularr Backup/Restore archives
- other uncategorized/admin-generic content

Example path:
- `/media/downloads`

This is different from the temporary Native Download Workspace.

#### Other managed roles
Future/optional roles may include:
- backup target;
- cache/temp;
- transcode workspace;
- restricted Games BIOS/Firmware storage when the Games runtime subsystem requires it.

These must remain explicit roles rather than being confused with LibraryRoots.

### Games BIOS/Firmware role boundary

When Games needs BIOS/Firmware artifacts:
- Storage owns the restricted managed path, capacity/availability and safe file access;
- Games Admin owns BIOS/Firmware requirements, validation, artifact binding and runtime compatibility;
- BIOS/Firmware is never a Games LibraryRoot and never a Generic Downloads destination;
- Games screens reference the Storage-managed role and do not expose arbitrary host paths.

## Native downloader storage flow

Target flow:

```text
NZB / Usenet
  -> Native Download Workspace
     -> incomplete/article assembly
     -> verify/repair
     -> extract/post-process
  -> identify
  -> import
     -> specialized LibraryRoot
        -> Games importer -> Games LibraryRoot when ContentKind.Game
        -> Media/Reading/etc. importer for their own LibraryRoots
     OR
     -> Generic Downloads Root only for genuinely generic/unclassified content
```

Storage owns the physical paths and safe file operations.

The Usenet/downloader module owns queueing, NNTP transport, verification/extraction orchestration and download state.

Acquisition owns why the item is wanted and the transition into import.

## Same-mount optimization

The UI should make it visible when:
- download workspace and target LibraryRoot are on the same filesystem
- target is on a different filesystem/mount

Same-filesystem placement may allow cheaper/safer move semantics.

Cross-filesystem import may require copy + verify + delete semantics.

The UI should not expose implementation details as mandatory user choices, but should surface warnings when a selected layout causes expensive copies or insufficient temporary space.

## Page structure

Desktop:

1. Mount list
2. Selected Mount detail
3. capacity/health
4. roles/roots on this Mount
5. add/edit role/root
6. safe Path Browser

The selected Mount should show a single table of paths/roles rather than duplicating physical capacity per role.

Recommended role table columns:
- Name
- Role
- Path
- Content types / purpose
- Used size where known/cached
- Last check/scan
- Status
- Actions

## Approved Desktop composition

The approved Desktop layout is:

1. left column: physical Mount list
2. right top: selected Mount identity, health and capacity
3. right middle: single table `Pfade auf diesem Mount`
4. add/edit path opens a focused multi-step dialog
5. safe Path Browser is embedded in that dialog

### Mount list

Each Mount row shows:
- mount point
- device/share + filesystem/protocol
- online/offline state
- one physical usage bar
- used / total capacity
- compact actions

Selecting a Mount updates the right side.

Do not list Anime, Movies, Downloads, etc. as separate physical storage devices when they share the same Mount.

### Selected Mount header

Show:
- mount point
- device/share
- filesystem/protocol
- online/offline/read-only state
- total
- used
- free
- optional capacity composition bar

Capacity composition may distinguish:
- LibraryRoots
- Native Download Workspace
- Generic Downloads / other managed roles
- unclassified/other used space
- free space

These segments are informative only; filesystem free space remains authoritative.

### Paths on this Mount table

Use one table for every logical role/path on the selected Mount.

Default columns:
- Name
- Role
- Path
- Size where known/cached
- Last Scan / Last Check
- Status
- Actions

Roles are shown as compact labels, including:
- Library Root
- Download Workspace
- Generic Downloads
- Other managed role

LibraryRoot media/content types may be visible as secondary text or optional column.

Typical actions:
- scan/reconcile for LibraryRoot
- edit
- test
- more

## Add Path wizard

`Pfad hinzufügen` opens a three-step dialog.

### Step 1 — Rolle

Choose exactly one primary path role:

- **Library Root**
  - final destination for specialized managed libraries
- **Download Workspace**
  - temporary workspace for Jularr's native downloader
- **Generic Downloads**
  - final destination for generic/unclassified downloads
- **Other**
  - explicit managed role such as backup/cache/transcode or restricted Games BIOS/Firmware storage where supported

The role determines which settings appear in Step 3.

Do not ask the user to configure unrelated options.

### Step 2 — Pfad

First choose the physical Mount.

Then browse only within that Mount using the safe Path Browser.

The browser shows:
- breadcrumb/current path
- folders
- read/write capability where useful
- existing Jularr-role conflicts
- selected path

The user selects a directory, not an arbitrary unrestricted filesystem path.

### Step 3 — Einstellungen

Role-specific configuration.

#### Library Root

Show:
- display name
- supported media/content types
- enabled state
- automatic scan/reconciliation option
- write/import requirement
- naming/organization policy reference where applicable
- optional default-for-type/routing role where supported

A single LibraryRoot may support multiple content types.

#### Download Workspace

Show:
- display name
- minimum free-space reserve
- optional quota
- cleanup/retention for completed temporary data
- optional separate repair/extract workspace
- test write/delete

Do not expose internal incomplete/complete/repair folders as separate mandatory user configuration.

#### Generic Downloads

Show:
- display name
- final organization/naming policy
- optional category subfolders
- scan/index behavior
- write requirement

#### Other

Only show settings that belong to the selected managed role.

### Save validation

Before save:
- path must remain inside the selected Mount
- required write access must be available
- path-role conflicts are shown
- dangerous overlap is blocked or explicitly explained
- duplicate role/path configuration is rejected where invalid
- workspace capacity/reserve constraints are validated

## Mount data

Show:
- mount point
- device/share identity
- filesystem/protocol
- online/offline/sleeping
- read/write capability
- total/used/free capacity
- reserved space where configured
- last health check
- optional wake/retry state
- filesystem capabilities useful for safe file operations

Do not imply separate free-space numbers for child LibraryRoots.

## LibraryRoot configuration

Allow:
- name
- path
- supported content/media types
- enabled state
- scan/reconciliation policy
- naming/organization policy reference
- write/import capability
- default-for-content-type assignment
- import placement policy

### Default destination / routing ownership

Storage owns the default final destination for specialized managed content, including Games.

For each enabled content/media type, including Games:
- one LibraryRoot may be marked as the default destination;
- a LibraryRoot may be default for multiple compatible content types;
- a Work may explicitly override the default target root where the canonical library/monitoring contract supports it;
- identified Games route to a Games-capable LibraryRoot through the Games importer;
- unidentified/generic content falls back only to a configured Generic Downloads Root.

This is the routing contract for final physical placement. Do not create a separate permanent `Import & Routing` Admin page that asks the admin to select the same LibraryRoots again.

Acquisition Profiles decide **which release to acquire**, not its filesystem destination.

### Import placement policy

The target LibraryRoot owns how an already completed, identified source file is placed into that root.

Supported policy values:
- **Hardlink wenn möglich, sonst Copy** — preferred safe default where applicable;
- **Hardlink** — require same-filesystem hardlink support; fail clearly instead of silently copying;
- **Copy** — keep the source and create a separate library copy;
- **Move** — move into the library; cross-filesystem execution becomes copy + verify + delete internally where needed.

The UI must show whether the selected source/target layout can actually hardlink.

Rules:
- never claim hardlink support across filesystems;
- do not silently fall back from explicit `Hardlink` to Copy;
- `HardlinkOrCopy` may fall back by definition;
- destructive source deletion occurs only for explicit Move semantics after destination verification;
- placement policy belongs to the final LibraryRoot/Storage contract, not Acquisition scoring.

A global Storage default may exist for newly created LibraryRoots, but the effective policy must always be visible at the LibraryRoot.

## Native Download Workspace configuration

Allow:
- workspace path
- enabled/healthy state
- minimum free-space/reserve policy
- optional maximum workspace quota
- optional separate repair/extract workspace
- cleanup retention for completed temporary data
- test write/delete capability

Do not configure NNTP servers, connection counts, bandwidth or queue policy here. Those belong to downloader/Usenet settings.

## Generic Downloads configuration

Allow:
- final Generic Downloads Root path
- organization/naming behavior for generic content
- optional category subfolders
- scan/index behavior where supported

Generic Downloads is a final destination, not the native downloader's staging folder.

## External download clients

External download clients remain optional compatibility/migration adapters.

Their remote-path mappings belong to the external-client adapter configuration under Downloader because the mapping only exists when that external client and Jularr see the same storage under different paths.

Storage:
- owns the local permitted Mount/role target selected by that mapping;
- validates that the resolved local side stays inside permitted Storage;
- does not own or duplicate the external client's remote prefix.

The native Jularr downloader normally needs no remote-path mapping because it already operates on Jularr-owned Storage roles.

Migration/coexistence adapters such as Sonarr may use the same shared mapping semantics inside Migration when the external manager reports different paths.

## Safe Path Browser

The browser is server-side and restricted to configured/permitted Mount boundaries.

Behavior:
- user selects a Mount first
- browser starts inside that Mount
- user can navigate only permitted descendants
- current path is always visible
- show writable/readable state
- prevent traversal outside the permitted root
- allow creating a directory only when policy permits

No unrestricted filesystem root browser.

No arbitrary path text field as the primary UX.

## Actions

Mount:
- refresh/test
- wake/retry where supported
- inspect health

Role/root:
- add through the three-step role/path/settings wizard
- edit
- disable
- test read/write
- browse/select path
- scan/reconcile LibraryRoot
- remove role mapping without silently deleting media

Workspace:
- test
- cleanup disposable completed/temp data with preview
- move workspace only through an explicit migration operation

## Storage Lifecycle Management (#414)

Admin Storage is also the single owner of **storage lifecycle policy**. Do not create separate permanent Admin pages for cleanup, optimization, tiering, migration or storage forecasting.

These are secondary surfaces/workflows inside Storage and contextual entry points from an individual Admin Media Detail.

Recommended internal surfaces:
- **Overview** — mounts, roles, capacity, forecast, recent growth and reclaimable-space summary;
- **Policies** — lifecycle rules, scope, protection, quality floors, maintenance/resource limits and policy revisions;
- **Review** — candidate queue and policy conflicts;
- **Optimize** — selected Work/library/root optimization and Dry Run;
- **Migration** — physical LibraryRoot move/tiering/evacuation workflow;
- **History** — audited lifecycle actions and measured savings.

Activity / To-Do remains the owner of running/failed background operations. Storage links to those operations rather than duplicating the global job queue.

### Lifecycle policy modes

Every destructive canonical-media policy is explicitly one of:
- **Off** — analysis only;
- **Review** — generate candidates, require admin action;
- **Automatic** — execute only after the admin explicitly enabled that policy.

Low free space, a forecast, an import burst or a reserve violation may rank/prepare candidates but must never silently turn Review/Off into Automatic.

### Eligibility and protection

Candidate evaluation may combine:
- inactivity since last watch/read/listen/play;
- never-started age;
- completed + inactive age;
- active Watchlist/Reading List presence;
- request/acquisition age;
- aggregate use across profiles;
- physical/logical size;
- storage pressure/reserve;
- reacquisition confidence;
- version redundancy;
- cold-tier suitability.

Protection can include:
- active hard user Requirements;
- in-progress media;
- active Watchlist/Reading List;
- recent requests/acquisitions;
- active playback/read/download/import/transcode;
- Work/item `Keep`, temporary Keep or `Never delete`;
- protected tags/libraries/media types/profiles;
- quality floors;
- rare/unknown reacquisition guard.

Effective rule precedence is safety-first:
1. storage/integrity safety;
2. active hard Requirements;
3. explicit item protection;
4. admin exclusions and quality floors;
5. lifecycle eligibility;
6. ranking, reserve and cleanup-target optimization.

The Review UI must explain every matched rule, the winning rule and why an action is proposed or blocked.

### Review actions and delete semantics

Review actions can include:
- Keep;
- Keep for 30/90/365 days or custom expiry;
- Never delete;
- Ignore/defer;
- Optimize;
- Move to colder storage;
- Delete local media.

Default canonical-media deletion removes physical bytes but retains:
- Work/Edition/Version identity where still logically valid;
- metadata/provider identities;
- user progress/history;
- list state;
- request/history evidence.

The item becomes not locally available and can later use the normal acquisition pipeline again.

Intentional cleanup must suppress accidental immediate reacquisition by Jularr/Sonarr/Radarr/external manager monitoring until the effective policy/demand actually requires the media again.

### Requirement-aware version/component pruning

Before deleting, downgrading, optimizing, pruning tracks or removing a MediaVersion, Storage Lifecycle must calculate hard-Requirement coverage before/after.

Never automatically remove the only version/track satisfying an active hard Requirement.

The preview shows affected users/requirements without exposing unrelated private profile activity.

Version pruning may remove redundant releases only after equivalence/coverage is safe.

### Optimization

Optimization is a separate action from deletion.

Supported targets may include:
- artwork/posters/covers/banners/backdrops/thumbnails;
- video/audio bitrate/resolution/codec/container;
- redundant optional audio/subtitle/commentary/embedded-artwork tracks;
- regenerable previews/derived cache.

Use:
`source -> temporary optimized output -> verify/probe/decode -> atomically adopt -> remove/retain old source according to policy`.

Never destructively overwrite before verification.

Books/comics/PDF optimization must remain format-safe and preserve readable content.

Quality floors are explicit and may vary by library/media type/profile.

#### Artwork boundary

Normal behavior remains:
- canonical durable artwork beside media;
- compact rebuildable derivatives under local Jularr cache.

Derivative/cache artwork may be compressed/evicted aggressively.

Replacing canonical artwork with a smaller canonical copy is allowed only as an explicitly enabled #414 lifecycle action with Dry Run, quality/dimension floor, validation, custom-art protection and configured grace/undo semantics.

### True physical savings and dedupe

Show logical bytes separately from measured/estimated physically reclaimable bytes.

Where supported and trustworthy, account for:
- hardlinks;
- reflinks/COW sharing;
- filesystem dedupe;
- snapshot-retained blocks.

Do not promise physical savings that shared blocks/snapshots prevent.

Byte-identical/provably equivalent duplicates may be consolidated with filesystem-native hardlink/reflink/dedupe capabilities. Filename/title similarity is never sufficient.

Canonical identity ambiguity belongs to the duplicate/merge review contract, not to storage dedupe.

### Integrity, orphan and missing states

Keep these separate:
- **orphan file** — physical file exists but no canonical stored-file ownership;
- **missing reference** — canonical file expected but absent while its Storage is confirmed Online;
- **corrupt file** — present but fails configured integrity validation;
- **offline storage** — availability state, never mass-missing media.

Unknown/orphan files are never silently deleted.

Integrity scrub may use existence/size/mtime, stored checksum and format/media probing where useful. Heavy scrub runs only while storage is Online unless explicitly requested and is throttled.

Corrupt media is protected from ordinary automatic cleanup until replacement/recovery/discard is explicitly resolved.

### Cold storage, migration and evacuation

Lifecycle may move existing media between compatible configured LibraryRoots without changing canonical Work identity.

Normal future import routing remains owned by the LibraryRoot default contract (#815). A lifecycle move never silently changes that default.

Physical migration flow:
`scope -> destination -> capacity/capability/conflict check -> Dry Run -> copy/move/reflink -> verify -> switch canonical location -> optional source cleanup`.

Evacuation mode plans all remaining dependencies before a root is removed/replaced and proves which items could not be migrated.

Source deletion occurs only after destination verification.

### Forecast, simulator and targets

Storage may show:
- growth over 7/30/90 days;
- estimated time-to-full;
- largest growth contributors;
- Top Waste categories;
- reclaimable bytes by action type;
- historical savings.

Forecasts are estimates.

What-if simulation changes policy inputs without changing media.

Cleanup may stop after a configured target such as:
- reclaim 500 GB;
- restore >=20% free;
- optimize at most N Works.

Reserve targets may be absolute bytes or percent and may reserve working space for downloader/import/repair/transcode operations.

Known queued import sizes may trigger burst-pressure planning, but never silently enable deletion.

### Reacquisition risk

Keep reacquisition confidence separate from cleanup score:
- Easy;
- Likely;
- Uncertain;
- Rare/protected;
- Unknown.

Policies may permit automatic deletion only for selected confidence levels. Future availability is never guaranteed.

### Resource/energy-aware execution

Heavy lifecycle work supports:
- maintenance windows;
- CPU/GPU/IO/concurrency limits;
- pause/yield during playback;
- pause/yield during high import/download activity;
- restart-safe resume.

Prefer running while the required NAS/GPU is already active. Do not wake a sleeping NAS only for passive analytics or low-priority optimization.

Optimization preview may expose approximate ROI (expected saving vs. work/cost) and rank/skip low-value jobs.

### Notifications and pre-action notice

Storage lifecycle events may use the canonical notification system:
- warning/critical free-space threshold;
- cleanup available for review;
- automatic policy result;
- optimization/migration failure;
- forecast threshold;
- upcoming trash final purge.

Notifications are deduplicated/configurable.

An optional policy may notify affected users before archive/delete/downgrade. It is not a mandatory approval step and must not reveal another user's history/requirements.

### Trash / grace / undo

Destructive cleanup may use an optional grace period.

During grace:
- media is excluded from normal local availability;
- metadata/history remains;
- automatic reacquisition stays suppressed;
- pending reclaim is distinguished from already reclaimed space;
- restore is available where technically reversible.

History labels actions as reversible, pending purge, irreversible, or reacquirable-only.

### Policy revision, import/export and audit

Every lifecycle action records:
- initiator;
- policy revision;
- target;
- reason/rules;
- before/after size;
- measured/estimated savings;
- outcome;
- reversibility/reacquisition state.

Policy changes are versioned. Historical actions keep their original explanation.

Policy sets may be exported/imported without secrets, filesystem-specific IDs or private user state. Instance-specific LibraryRoot mappings require explicit remap + preview before activation.

### Storage impact by requirements

Admin may inspect aggregate storage cost caused by additional quality/language/version Requirements for capacity planning.

This is explanatory only. It does not create automatic per-user quotas or justify bypassing active hard Requirements.

### Safety invariants

- removing/disabling a Storage role never deletes canonical media;
- sleeping/offline storage never becomes mass-missing;
- no destructive bulk action without Dry Run unless an already approved Automatic policy is executing;
- all canonical-media lifecycle actions are auditable;
- active operations are protected from races;
- migration/tiering/optimization are restart-safe/idempotent where practical;
- Jularr-owned disposable cache/temp may use more permissive cleanup than canonical media;
- per-Work actions and bulk actions use the same policy engine and safety checks.

## Light / Dark

Both first-class Admin surfaces.

Use compact operational styling:
- no decorative hero art
- restrained status colors
- capacity bars belong to physical Mounts
- child roles use simple table/list rows

## Platforms

Desktop primary.

Tablet:
- Mount cards/list + selected detail

Mobile:
- stacked Mounts and role lists
- Path Browser as full-screen sheet
- advanced bulk storage management remains desktop-oriented

TV unsupported.

## States

Required:
- loading
- online
- sleeping/offline
- read-only
- permission denied
- nearly full
- insufficient workspace free space
- unavailable mount
- stale metrics
- workspace unhealthy
- LibraryRoot unavailable
- cross-filesystem import warning
- empty Mount/no roles
- path no longer exists
- test running/failure
- forbidden

## Domain / architecture constraints

- Storage owns physical placement, paths, capacity and safe file operations.
- LibraryRoot belongs to Storage infrastructure and is referenced by Library/import workflows.
- Native Download Workspace is temporary operational storage, not canonical media identity.
- Generic Downloads is a final content destination, not a staging area.
- Games is a specialized LibraryRoot content type and never becomes Generic solely because it was acquired through the generic downloader pipeline.
- Paths must never become canonical Work identity.
- The native downloader must not require SABnzbd/NZBGet for normal operation.
- External download clients are optional adapters only.

## Loading / Empty / Error / Partial interaction states

In addition to the general states below, the add/edit workflow must handle:
- no Mount selected
- Mount contains no selectable directories
- selected path already has a conflicting role
- selected path is read-only
- path disappeared during configuration
- permission changed during save
- Mount went offline during browsing
- LibraryRoot content types not selected
- Download Workspace reserve exceeds available capacity
- successful save with immediate table update

## Must not implement

- No separate physical-capacity card for every LibraryRoot on the same mount.
- No assumption that each content type requires its own filesystem.
- No separate capacity accounting per LibraryRoot when roots share a Mount.
- No path-role configuration split across unrelated pages when it belongs to Storage.
- No use of a LibraryRoot as the native downloader's incomplete/staging workspace implicitly.
- No mixing temporary download workspace with final Generic Downloads.
- No NNTP/server/bandwidth settings on the Storage page.
- No unrestricted `/` browser.
- No user-controlled traversal outside permitted Mounts.
- No arbitrary path entry as the primary path UX.
- No media identity stored as path.
- No automatic canonical-media deletion from removing a Storage role.
- No destructive cleanup without explicit preview/semantics.
- No NAS wake solely for passive analytics unless explicitly requested.
