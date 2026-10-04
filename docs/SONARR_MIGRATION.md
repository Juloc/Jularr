# Sonarr → Jularr migration and coexistence

Status: **implementation bridge to the generalized Migration Center contract**.

Binding product/UX contract:
- `docs/mockups/admin-migration/SPEC.md`

This document preserves the existing Sonarr safety behavior and implementation seams while the legacy Anime-specific integration is generalized. Where this document describes legacy routes/types, the Migration Center specification owns the target UX and generalized model.

## Target ownership model

Manager ownership is resolved at **Work level**.

User-facing modes:

- **Extern verwaltet** — Sonarr owns acquisition/import/naming for the Work; Jularr observes and must not perform conflicting library mutations.
- **Gemeinsam** — Sonarr and Jularr may both perform supported operations, but Jularr checks ownership before every mutation. This is a permanent advanced mode, not only a temporary migration state.
- **Jularr verwaltet** — Jularr owns acquisition/import/naming. Sonarr may remain connected for read-only observation/conflict detection.

Existing implementation enum/state maps as:

| Existing Sonarr state | Target mode |
| --- | --- |
| `ReadOnlyCoexistence` | **Extern verwaltet** |
| `ParallelAcquisition` | **Gemeinsam** |
| `JularrManaged` | **Jularr verwaltet** |

The existing per-Anime state is transitional. It must migrate to the generic per-Work ownership contract rather than becoming a second permanent ownership model.

## Defaults and overrides

Defaults are configured per **Sonarr integration instance × media/content type**.

Example:
- Sonarr + Series/TV -> **Extern verwaltet**
- Sonarr + Anime -> **Jularr verwaltet**

A Work may explicitly override that default.

Resolution order:
1. explicit per-Work override;
2. integration + media/content-type default;
3. Jularr-managed fallback only when no external-manager default applies.

Changing a default does not overwrite explicit Work overrides unless the admin deliberately resets them.

For one Work, Jularr supports at most **one external manager plus Jularr**. `Gemeinsam` never means multiple external managers sharing the same Work.

## Handover

Changing ownership is explicit, previewed and audited.

When switching to **Jularr verwaltet**:
- Jularr imports the source monitoring state into canonical Jularr monitoring where a safe semantic equivalent exists;
- Work/Season/Episode monitoring granularity is preserved where supported;
- unsupported semantics are shown instead of guessed;
- **Externes Monitoring deaktivieren** is offered as an explicit handover option and is checked by default when the adapter can safely perform the change;
- if Jularr changed Sonarr monitoring, that fact is recorded so a later revert restores only state Jularr itself changed;
- Jularr never deletes the Sonarr series or source media.

Ownership remains Work-level even though monitoring may be finer-grained.

## Sonarr observation

Jularr observes the Sonarr v3 API for safety evidence, including where available:

- `/api/v3/series` — linked series, root folders and monitoring state;
- `/api/v3/episodefile?seriesId=…` — episode-file paths;
- `/api/v3/queue` — active releases/download ids/output paths/episodes;
- `/api/v3/history` — recent grabs, imports, renames and failures.

Observation does not transfer ownership.

### Fail-closed behavior

For **Gemeinsam**, if Sonarr cannot be observed reliably, Jularr fails closed for new mutations on the affected Work/scope. Read-only display/diagnostics may continue, but new grab/import/rename/replace/delete work that depends on coexistence safety must not start.

For **Jularr verwaltet**, Sonarr may remain connected in read-only observation mode. If Jularr needs current Sonarr state to prove a mutation safe, an unavailable/ambiguous observation also blocks that mutation rather than guessing.

The UI reports the reason explicitly, for example:
- `Gemeinsam · Sonarr nicht erreichbar · Änderungen pausiert`;
- `Jularr verwaltet · Sonarr beobachtet`;
- `Jularr verwaltet · Sonarr nicht erreichbar`.

## Path translation

Sonarr/Jularr path translation belongs to the **Sonarr migration/coexistence adapter**.

Use it only when Sonarr and Jularr see the same underlying files under different path prefixes.

Required resolver behavior:
- longest matching prefix wins;
- path-boundary aware;
- slash/backslash normalization where applicable;
- deterministic overlap behavior;
- local target resolves inside permitted Storage;
- live test uses the same resolver as coexistence/import safety.

Do **not** keep the old Anime remote-path mapping list in `/Settings/Acquisition` as the target owner.

Legacy Anime mappings must be migrated to the appropriate owner:
- Sonarr source/coexistence mapping -> Sonarr Migration adapter;
- external download-client mapping -> that Downloader external-client adapter.

No global Remote Path Mapping / Import & Routing page is created.

## Safe rollout

A safe rollout can still be Work-by-Work:

1. connect/test Sonarr observation;
2. map Sonarr identities and Storage paths;
3. choose the integration × media-type default;
4. keep individual Works on **Extern verwaltet** while validating mappings;
5. use **Gemeinsam** for selected Works when parallel operation is intentionally desired;
6. hand a Work to **Jularr verwaltet** only after current Sonarr/Jularr activity is conflict-free;
7. optionally disable Sonarr monitoring through the explicit handover option;
8. keep Sonarr connected for read-only observation until the admin deliberately removes the integration.

The system never automatically disconnects Sonarr after successful migration/handover.

## Conflict rules

The current Sonarr safety rules remain the minimum implementation baseline.

Jularr must not:
- grab a release already actively owned by Sonarr;
- start conflicting acquisition for a unit Sonarr is downloading;
- import/rename/replace/delete a path Jularr cannot prove it may mutate;
- mutate through an unresolved path mapping;
- guess ownership when Sonarr evidence is unavailable;
- create a rename loop with recent Sonarr activity.

When both managers appear to act on the same release/download/path, Jularr surfaces a conflict and stops/pauses the Jularr mutation.

Conflict families include:
- `release` / `path`;
- `download`;
- `monitoring`;
- `sonarr-activity`;
- `rename-loop`;
- `unverified`.

Conflicts are surfaced through Migration and actionable Activity / To-Do diagnostics, not only raw logs.

## Target Admin surface

Target owner:
- **Admin → Migration → Sonarr adapter**

The existing routes are transitional implementation surfaces:
- `/Settings/SonarrMigration`;
- `/Admin/Sonarr`;
- Sonarr-related sections under legacy Acquisition settings.

They may remain temporarily as redirects/deep links while feature parity is moved into Migration Center, but must not remain parallel configuration owners.

## Current implementation seams

The current code remains valuable and should be generalized rather than discarded:

- `SonarrMigration` ownership state;
- `SonarrParallelSafety`;
- Sonarr observation/snapshot service;
- conflict guards in grab/import/rename;
- migration/ownership audit log;
- explicit handover/revert behavior;
- path translation and tests.

Current Anime-specific call sites are migration targets, not target architecture. Their safety semantics must be preserved while they move behind generic Work/manager integration contracts.

## Must not regress

- No duplicate grabs caused by coexistence.
- No mutation of unowned paths.
- No silent ownership transfer.
- No automatic deletion of Sonarr media/series.
- No automatic external-manager disconnect after handover.
- No multi-external-manager ownership for one Work.
- No ownership below Work level.
- No source path mapping stored in generic Acquisition Profiles.
- No fail-open behavior in **Gemeinsam**.
