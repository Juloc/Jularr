# Admin Activity / To-Do — V1

Status: approved operational direction. This screen owns live operational work and manual attention flows, including import review.

Global UX rules: `docs/UX.md`

Existing visual reference:
- `file_00000000f010821098793669d1a356ca.png` — Activity / To-Do Admin screen shell and layout reference.

The existing screen reference remains valid for Activity / To-Do. The Import Review / Assignment dialog is part of this screen flow and may receive its own focused dialog mockup; it is not a separate page.

## Purpose

Activity / To-Do is the Admin operational surface for work that is currently happening or requires human intervention.

It is not a media library page, not a raw log viewer, and there is no separate permanent Imports page in V1.

The screen must answer:
- What is currently downloading or processing?
- What requires admin attention?
- What failed and can be repaired?
- What completed successfully?
- Which concrete media/file/job is affected?

## Navigation ownership

Activity / To-Do is the permanent Admin operational destination. **History is its third tab**, not a second permanent sidebar destination.

A compatibility/deep-link route such as `/Admin/History` may remain for large-history workflows, but it must render/use the same History contract and canonical event model.

## Primary tabs

Use three primary tabs:

1. **Activity**
   - currently downloading
   - queued/running processing
   - import in progress
   - remux/repack
   - subtitle/translation work
   - metadata/AI/maintenance work where relevant

2. **To-Do**
   - jobs that require manual attention
   - failed imports that can be repaired
   - ambiguous media/file identification
   - conflicts
   - missing/invalid metadata required before import
   - storage/path availability problems that require action
   - retryable failures

3. **History**
   - completed/successful work
   - resolved failures
   - cancelled work
   - past import outcomes

Do not create separate top-level tabs for Running, Failed or Imports when the same information can be expressed through Activity / To-Do / History plus filters.

Filters may still expose states such as Running, Failed, Queued, Importing, Downloading, Completed and Cancelled.

## Activity tab

Activity is live.

Typical rows/cards:
- Download
- Import
- Remux
- Repack / Replace
- Subtitle processing
- Translation
- Metadata refresh
- AI generation
- Scan / analysis
- Rename / organize
- Maintenance

For downloads show, where available:
- media target
- release/download name
- download client
- progress
- speed
- ETA
- size
- state
- source acquisition link
- post-download/import state

A completed download normally transitions into import processing without creating a second unrelated user workflow.

Live behavior:
- jobs appear/update without reload
- progress updates in place
- state transitions preserve scroll/filter context
- a completed successful job leaves Activity and appears in History
- a problem that requires intervention leaves Activity and appears in To-Do

## To-Do tab

To-Do is for actionable problems, not every technical warning.

Examples:
- import could not identify the Work/unit confidently
- episode/season/volume/version is ambiguous
- file metadata is incomplete or parser output needs correction
- target storage unavailable
- duplicate/conflict requires decision
- download completed but import failed
- manual mapping required
- retryable provider/download/import failure

Each To-Do item shows:
- job/problem type
- media/download title
- concise reason
- current detected target if any
- confidence/problem state
- age
- priority where meaningful
- actions

Primary actions may include:
- Resolve / Assign
- Retry
- Ignore / Dismiss where safe
- Cancel
- Open related media
- View logs/details

## Manual assignment flows

To-Do can open two distinct mapping flows. They share canonical mapping concepts but are not the same UI.

### Download Assignment

Binding spec:
- `docs/mockups/admin-download-assignment/SPEC.md`

Use when a completed download job contains one or more files whose canonical episode/unit or release metadata could not be resolved automatically.

Desktop uses a compact editable table:
- one downloaded file = one row
- multiple files in the same download = multiple rows

The row directly exposes Work, Season/Structure, Episode/Unit, Release Group, Quality, Language, Audio, Subtitles, Version and Source/Type as applicable.

Do not add large artwork, side-by-side summary panels or a second technical-info layout around this table.

### Library Reconciliation / Folder Import Mapping

Binding spec:
- `docs/mockups/admin-folder-import-mapping/SPEC.md`

Use when Library Scan / Reconciliation finds existing folders/files inside a configured LibraryRoot that cannot be associated reliably.

This opens a dedicated multi-step Admin page rather than a dialog.

The wizard covers:
- scan scope/result
- expandable folder tree and recognition state
- split/merge of logical groups
- folder defaults + per-file mapping
- batch/range mapping
- organization policy
- rename/move preview
- dry-run and execution

Recognized subfolders may be collapsed by default but must remain expandable and correctable.

It must not be collapsed into the compact Download Assignment dialog.

## History tab

History is the completed/resolved operational record.

Successful downloads/imports appear here after completion.

History rows/cards show:
- time
- type
- media/title
- result
- actor/system
- source/download client/provider where relevant
- concise details
- link to resulting media/file or failure details

Import history may show:
- source download
- detected/resolved canonical target
- resulting Version / Asset / File
- final destination
- whether the resolution was automatic or manual

## Desktop layout

Use:
- persistent Admin sidebar
- title
- Activity / To-Do / History tabs
- search/filter bar
- dense table
- detail drawer/dialog for jobs/problems

Recommended columns vary by tab, but may include:
- Type
- Title / medium
- Step / problem
- Progress
- Status
- Age/time
- Priority
- Actions

Do not create a permanent column wall. Use configurable columns where needed.

## Mobile layout

Do not squeeze the desktop table.

Use touch-friendly cards with:
- media artwork/icon where useful
- type
- title
- current step/problem
- progress/status
- concise key metadata
- overflow/details

Import Review becomes a full-screen sheet on Mobile.

## Visual language

- operational Admin styling
- compact, calm, information-dense
- no decorative hero artwork
- warnings/errors use color sparingly
- important state always has text/icon, not color only
- avoid badge walls

## States

Required:
- loading
- empty Activity
- empty To-Do
- empty History
- live/running
- queued
- downloading
- importing
- needs attention
- ambiguous
- conflict
- storage unavailable
- retrying
- completed
- cancelled
- partial provider/download-client failure
- forbidden/permission error

## Domain / architecture constraints

- Import resolution maps into the canonical `Work -> Structure -> Edition -> Version -> Asset/File -> Track` hierarchy.
- Download/import jobs are operational state, not parallel media models.
- A filename/parser result is evidence, not canonical identity.
- The canonical media assignment is explicit before final import.
- Parsed release metadata and file-probed technical metadata remain distinct concepts.
- Successful import creates/links the appropriate canonical Version/Asset/File/Track state.
- History records the result; Activity/To-Do do not become permanent media ownership stores.

## Must not implement

- No separate permanent Imports navigation/page in V1.
- No separate media-type import queues.
- No raw database-ID editing.
- No arbitrary destination path entry as normal UX.
- No canonical identity derived from filename alone.
- No silent parser learning from one-off manual corrections.
- No silent destructive source-file operations.
- No duplicate Activity/Failed/Imports pages for the same job states.


## Storage Lifecycle operations (#414)

Long-running Storage Lifecycle work uses the same canonical Activity/Operations surface.

Examples:
- integrity scrub;
- optimization/transcode/remux;
- duplicate consolidation;
- tiering;
- LibraryRoot migration/evacuation;
- final trash purge.

Ownership remains split:
- **Storage** owns policy, candidate/reason, Requirement impact, target root and lifecycle audit;
- **Activity / To-Do** owns live queued/running/waiting/failed execution and actionable operation failure;
- **History** owns completed operational runs.

A Storage operation may show:
- affected Work/root;
- action type;
- progress;
- expected/measured savings where known;
- waiting reason such as storage offline, active playback, maintenance window or resource limit;
- link back to the exact Storage policy/review item.

Do not expose another profile's private watch/read history or Requirements merely to explain an operation. Storage may provide a bounded admin-safe explanation such as `blocked by active requirement`.

A failed lifecycle job does not independently change its policy mode. Retry/cancel semantics come from the underlying operation.
