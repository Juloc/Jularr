# Next backend slices

Owner decisions and implementation order. Follow [conventions](CONVENTIONS.md);
current owners and actual implementation are in [CODEMAP](CODEMAP.md).
The supplied clean-cut snapshot is the starting model, not the legacy code layout.
It marks many media columns incomplete: resolve those before writing baseline SQL.

## Order and ownership

| Slice | Services / Logic | Persistence |
| --- | --- | --- |
| Admin foundation | Central role gate; manual media management, Requests and Wanted; Owner-only configuration, Accounts and localization | Reuse Accounts, AccountRoleTypes, AccountProfiles, AccountSessions and localization tables. No parallel role/permission engine. |
| Storage / Library | Owner configures NAS connections (SMB, NFS, Synology API), permitted container paths and LibraryRoots through typed services and Frontend; Logic owns safe paths/imports | Physical StorageMounts remain distinct from logical LibraryRoots. Complete the agreed Work / edition / asset / file / inventory chain. |
| User / Home | Profile-scoped library/detail reads, recent items, continue-playing, watchlist; Home assembles bounded reads, not its own media model | Reuse media entities; add agreed MediaProgress, format-specific positions and WatchlistEntries. No duplicate Home tables. |
| Player | Jularr-hosted playback; preserve Dev behavior for direct play, remux, transcoding, seeking and source/track selection in the new framework | PlaybackSessions is separate from AccountSessions. Reuse assets/files/tracks; MediaPlaybackHistory is independent of current MediaProgress. |
| Arr+ | Jularr owns search, ranking, selection, monitoring, acquisition and import. Native downloader or optional SABnzbd/other adapters use one pipeline | Reuse canonical media/provider identities, Wanted and request/operation lifecycle. No separate pipeline per downloader. |

Each slice adds its versioned Data, declared Service permissions, static reads,
canonical Logic writers, fresh-baseline tables/fields/enums where needed, focused
isolated PostgreSQL tests and updated navigation in one reviewable change.
External I/O stays outside long database transactions; final mutations revalidate
and commit together. No migration, active-database installation or extra server.

## Boundaries already decided

- Roles are Owner, MediaManager and User. Service areas Public/User/Admin/System
  are not additional Account roles; Admin access must not implicitly mean Owner.
- MediaManager may only manually manage media and edit Requests/Wanted for now.
  Account/role administration, storage/provider credentials, rules, localization
  and pipeline configuration stay Owner-only. No wildcard administrative grant.
- Resource IDs/FKs stay long. Session/token proof remains separate from playback.
- NAS is configurable through Frontend -> Services -> Logic/Integrations, supporting
  SMB, NFS and Synology API. Docker Compose can expose additional permitted host
  paths to the same Storage/Library owner. No arbitrary filesystem browser or
  implicit Docker socket/privileged-container access.
- Synology management API and file access are distinct responsibilities in
  Integrations, not additional Backend projects or canonical media systems.
- Jularr is a standalone Arr system. Sonarr/Radarr/Prowlarr are optional one-time
  takeover sources only: preserve monitoring state, roots, naming and indexer
  settings where supported, preview unsupported mappings and require explicit
  selection. Never send acquisition/monitoring requests or run them as a dependency.
  Legacy coexistence/parallel-manager and source-write behavior is superseded.
- Playback is hosted by Jularr, not supplied by Plex. Dev implementation is a
  behavior/technical reference, not a structure to copy into the new framework.
- Profile PIN is optional, protects selection/switching and must support numeric
  TV/remote interaction. It never replaces account authentication.
- Admin localization edits use existing Logic. AI generation remains explicitly
  requested only; do not translate during ordinary page reads.
- Permission overrides are conditional in the supplied model, not permission to
  create a second authorization system.

## Remaining decisions and prerequisites

1. First-Owner installation proof and isolated preview database configuration.
2. Optional PIN length/retry/cooldown/recovery policy; profile creation limits.
3. Exact instance/profile preference catalogs and incomplete media/storage columns.
4. SMB/NFS runtime access: align implementation with container capabilities and
   the approved Storage mount model; never silently grant host mount privileges.

Role authorization and session revocation/rotation are implemented; administrative
domain operations, Storage/NAS, media pages, standalone Arr, takeover and Player
remain unfinished. Implement the shared owners in that order, with the same pipeline
for native and external downloaders and the same Storage locations for imports/playback.

Dev reference owners: [Acquisition core](https://github.com/Juloc/Jularr/blob/dev/src/Jularr.Web/Features/Acquisition/Core/CODEMAP.md),
[Playback plan](https://github.com/Juloc/Jularr/blob/dev/src/Jularr.Web/Features/Playback/Decision/PlaybackPlan.cs),
[Storage contract](https://github.com/Juloc/Jularr/blob/dev/docs/mockups/admin-storage/SPEC.md).
