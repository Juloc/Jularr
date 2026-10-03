# Admin History — V1

Status: approved operational direction; History is the past-result view used by Activity / To-Do.

Global UX rules: `docs/UX.md`

## Purpose

History is the append-style Admin operational record of completed, resolved, cancelled or attempted actions.

It answers:
- What happened?
- When?
- To which media/item?
- What was the result?
- Was it automatic or manual?
- Who/what performed it?

History is not live Activity and is not a separate import queue.

## Relationship to Activity / To-Do

- **Activity** = currently downloading/processing.
- **To-Do** = requires manual attention or repair.
- **History** = completed/resolved/cancelled past work.

When a download/import finishes successfully, it leaves Activity and appears in History.

When a job needs intervention, it moves to To-Do. After resolution/import success, its final outcome appears in History.

History is the third tab of the Activity / To-Do operational destination. A deep-linkable `/Admin/History` route may remain for compatibility or large-history workflows, but after shell consolidation it is **not** a separate permanent sidebar destination and must use the same underlying event model.

## Category filters

Examples:
- All
- Acquisition
- Downloads
- Imports
- Remux
- Repack/Replace
- Subtitle
- Translation
- Metadata
- AI
- Maintenance

Use date range and sort controls.

## Desktop layout

Use:
- Admin shell
- search
- category/date filters
- structured history table
- pagination/virtualization where needed

Recommended columns:
- Time
- Type
- Title / details
- Result
- Performed by
- Actions

Results:
- Success
- Warning
- Failed
- Cancelled

Performed by may distinguish:
- system/automatic
- named admin/user
- provider/job where useful

## Mobile layout

Use chronological stacked cards.

Each card shows:
- timestamp
- event type
- media/title
- concise result/details
- actor/system
- details action

## Import history details

For completed/resolved imports, details may show:
- source download/job
- source file(s)
- canonical Work / Structure/unit
- Edition / Version assignment where applicable
- resulting Asset/File/Track linkage
- destination
- release group / quality / language interpretation
- automatic vs manual assignment
- actor
- timestamps
- prior ambiguity/failure if relevant

## Details

A history event may open:
- related Work/unit
- source/target
- before/after where meaningful
- provider/indexer/download client
- linked job/request/import
- result/error
- actor
- timestamps
- relevant diagnostics/logs

## Visual language

- restrained Admin design
- quiet categories/status treatment
- no saturated badge wall
- errors/warnings stand out through icon/text/border, not large color blocks

## Retention

Retention policy is a backend/system setting and must not be implied by the mockup.

The UI must handle large histories through filtering and pagination/virtualization.

## Acceptance criteria

- History is separate from live Activity state.
- Successful downloads/imports are discoverable here.
- Import outcome links to resulting canonical media/file state.
- Manual vs automatic resolution is visible.
- Category/date filtering exists.
- Actor and result are visible.
- Desktop and Mobile use the same event model.
