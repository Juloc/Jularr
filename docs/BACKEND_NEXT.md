# Next backend slices

Plan requested by the owner; these features/tables are not implemented by this
cleanup. Follow [conventions](CONVENTIONS.md); current owners are in [CODEMAP](CODEMAP.md).
The supplied clean-cut snapshot is the starting model, not the legacy code layout.
It marks many media columns incomplete: resolve those before writing baseline SQL.

## Order and ownership

| Slice | Services / Logic | Persistence |
| --- | --- | --- |
| Admin foundation | Admin Account list/read/update/approval; role-aware shared gate; session revoke/logout; manual locale/description/translation administration | Reuse Accounts, AccountRoleTypes, AccountProfiles, AccountSessions and localization tables. No parallel role/permission engine. |
| Provider / Library | Admin provider configuration, library roots, access and bounded synchronization; backend Logic owns imports; Integrations owns external I/O | Reuse Providers; complete LibraryRoots and the agreed Work / edition / asset / file / inventory chain. Persist provider identities against that chain. |
| User / Home | Profile-scoped library/detail reads, recent items, continue-playing, watchlist; Home assembles bounded reads, not its own media model | Reuse media entities; add agreed MediaProgress, format-specific positions and WatchlistEntries. No duplicate Home tables. |
| Player | Authorized playback start/read/update/end; source/track selection; progress and activity writes | PlaybackSessions is separate from AccountSessions. Reuse assets/files/tracks; MediaPlaybackHistory is independent of current MediaProgress. |
| Arr+ | Provider-specific transport in Integrations; Backend Admin/User services orchestrate approved requests, sync and results | Reuse canonical media/provider identities. Decide request/job lifecycle and fields before introducing its tables. |

Each slice adds its versioned Data, declared Service permissions, static reads,
canonical Logic writers, fresh-baseline tables/fields/enums where needed, focused
isolated PostgreSQL tests and updated navigation in one reviewable change.
External I/O stays outside long database transactions; final mutations revalidate
and commit together. No migration, active-database installation or extra server.

## Boundaries already decided

- Roles are Owner, MediaManager and User. Service areas Public/User/Admin/System
  are not additional Account roles; Admin access must not implicitly mean Owner.
- Resource IDs/FKs stay long. Session/token proof remains separate from playback.
- NAS belongs in Integrations: manufacturer administration and file access are
  distinct concerns, not provider-specific Backend projects.
- Admin localization edits use existing Logic. AI generation remains explicitly
  requested only; do not translate during ordinary page reads.
- Permission overrides are conditional in the supplied model, not permission to
  create a second authorization system.

## Decisions before implementation

1. Admin grants: which Account/library/provider/locale operations may
   MediaManager perform, and which are Owner-only? How is first-Owner setup
   authorized without opening Public registration as a bootstrap?
2. First Library slice: Plex inventory only, local/NAS inventory, or both?
   For NAS: file access (SMB/NFS), manufacturer API, or both; which vendor/protocol?
3. First Player slice: Plex-provided playback or direct file playback; is
   transcoding included? Do not create either pipeline until decided.
4. Arr+: which integrations first (e.g. Sonarr/Radarr), and read/sync only or
   also user requests and acquisition? These authorize different external writes.

Implement Admin foundation first after its grants/bootstrap decisions, then the
minimum Library slice needed by Home/Player. No speculative full media schema
or NAS/Arr client scaffolding before those decisions.
