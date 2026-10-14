# Next backend slices

Owner decisions and implementation order. Follow [conventions](CONVENTIONS.md);
current owners and actual implementation are in [CODEMAP](CODEMAP.md).
The [original table snapshot](https://github.com/Juloc/Jularr/blob/docs/sql-cutoff-schema-json-20261011/docs/SQL_CUTOFF_ORIGINAL_TABLES.json)
defines the agreed media structure, not the legacy code layout. Complete missing
column definitions within those contracts; do not restart architecture planning.

## Order and ownership

| Slice | Services / Logic | Persistence |
| --- | --- | --- |
| Admin foundation | Central role gate; manual media management, Requests and Wanted; Owner-only configuration, Accounts and localization | Reuse Accounts, AccountRoleTypes, AccountProfiles, AccountSessions and localization tables. No parallel role/permission engine. |
| Storage / Library | Owner configures NAS connections (SMB, NFS, Synology API), permitted container paths and LibraryRoots through typed services and Frontend; Logic owns safe paths/imports | Physical StorageMounts remain distinct from logical LibraryRoots. Complete the agreed Work / edition / asset / file / inventory chain. |
| User / Home | Profile-scoped library/detail reads, recent items, continue-playing, watchlist; Home assembles bounded reads, not its own media model | Reuse media entities; add agreed MediaProgress, format-specific positions and WatchlistEntries. No duplicate Home tables. |
| Player | Jularr-hosted playback; preserve Dev behavior for direct play, remux, transcoding, seeking and source/track selection in the new framework | PlaybackSessions is separate from AccountSessions. Reuse assets/files/tracks; ended PlaybackSessions are the history, independent of current MediaProgress. Direct play, HLS remux/transcoding, resume and completion are implemented; track selection and segment caching across sessions are next. |
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
- Support both general mounts and separately exposed Compose library paths. The
  Owner may instead configure the NAS connection in Setup without prebinding each
  library in Compose. Verify required runtime mounting capabilities explicitly;
  connection configuration alone does not grant host privileges.
- Choose NAS access by measured throughput, reliability and consumer needs. Prefer
  system mounts when they are necessary or better for streaming/imports; direct
  SMB/NFS remains possible. Make mount options, permissions and platform support
  explicit. Compose paths are a separate supported input.
- NAS failure must not block account/admin/catalog browsing. Retain compressed
  display covers in PostgreSQL and interoperable artwork beside the media on NAS
  for Plex coexistence and a later return to Sonarr. NAS stores one standard cover;
  Jularr keeps locale-specific variants. Research compatible artwork names/formats;
  bounded display-cache sizes and pending-write/recovery follow the image owner.
- Synology management API and file access are distinct responsibilities in
  Integrations, not additional Backend projects or canonical media systems.
- Conversions are delivered as HLS; the owner approved `hls.js` (light build, loaded
  only for conversions) for browsers without native HLS.
- Retro games play in EmulatorJS 4.2.3 (GPL-3.0), approved by the owner on
  13 October 2026 with all its cores (Jularr is non-commercial, so non-commercial
  core licenses such as snes9x apply), self-hosted and pinned by npm integrity in
  [emulator-assets.mjs](../src/Jularr.Frontend/Tools/emulator-assets.mjs). Only the
  browser files of the loader and 13 cores are kept; the npm packages themselves
  would pull every core and the EmulatorJS server tooling.
- Owner decisions of 12 October 2026: the Owner sets per NAS connection whether
  Wake-on-LAN is Owner-only or automatic when a profile opens media. Games use the
  same unified Arr+ search, planner, download and import as every media type; only
  presentation and genuine game specifics differ. Tables stay few: entities with the
  same responsibility share one table (for example WorkUnits for all structure units).
- Owner decisions of 13 October 2026 ([completion plan](COMPLETION_PLAN.md)): Anime
  (Series and Movies, one switch), Manga and Light Novels are subtypes of Series/Movie
  and Book, never separate Works. Classification: heuristic proposal, AniList confirms,
  manual choice wins. Anime maps to AniList by title, year, format and episode counts
  along the sequel chain (the community mapping lists carry no license); uncertain
  matches go to review, never automatic Works. Every active type
  and subtype needs at least one library; several are allowed and a Work may name its
  library. The Owner restricts media types per profile; profiles may also hide types.
  Provider sync (AniList, Plex, Jellyfin, Emby): progress takes the maximum except an
  explicit rewatch, status/rating the newest change, Jularr wins ties. Offline PWA copies
  follow a device check (server rendition or original), original download optional,
  behind a profile permission. The mini-player uses a small own navigation script with a
  full-page fallback. Translation uses Bergamot models (optional owner). Retro games play
  through EmulatorJS (all cores, see the 13 October decision below), other platforms are downloads.
  E2E uses Playwright against a Compose test stack. Setup step 2 offers a new instance or
  a Jularr backup restore; migration stays with the stopped Phase B; the AI step waits
  for its own package.
- Jularr is a standalone Arr system. Sonarr/Radarr/Prowlarr are optional one-time
  takeover sources only: preserve monitoring state, roots, naming and indexer
  settings where supported, preview unsupported mappings and require explicit
  selection. Never send acquisition/monitoring requests or run them as a dependency.
  Legacy coexistence/parallel-manager and source-write behavior is superseded.
- Playback is hosted by Jularr, not supplied by Plex. Dev implementation is a
  behavior/technical reference, not a structure to copy into the new framework.
- Profile PIN is optional, protects selection/switching and must support numeric
  TV/remote interaction. It never replaces account authentication.
- PIN length is 3-6 digits; confirm automatically when the configured length is
  entered. Five failed attempts cause a one-minute lockout, then attempts become
  available again. Failed-attempt state must survive error responses and restarts.
- First Owner is entered directly in the startup Setup Wizard, without an
  installation code or separate proof. Creation is allowed exactly once, with
  database concurrency protection; subsequent public requests cannot create Owners.
- Media metadata and artwork share canonical media identities, not one Work per
  language. The Owner chooses a fixed instance metadata locale or each profile's
  effective locale, and independently whether covers/artwork follow that locale.
  Keep metadata and artwork variants for enabled instance locales, synchronized
  through the canonical import/provider owner, not provider requests during page
  rendering. Preserve manual edits; store provider language/region and provenance.
  Missing translations/artwork require explicit fallback; never invent unavailable
  localized assets. Locale IDs stay canonical; provider codes belong at the adapter.
- Admin localization edits use existing Logic. AI generation remains explicitly
  requested only; do not translate during ordinary page reads.
- Permission overrides are conditional in the supplied model, not permission to
  create a second authorization system.

## Capability coverage

The owner requires the following coverage beyond the first connected pages:

- Delivery and identity: Setup, Docker, PostgreSQL, modules, Accounts, Profiles,
  Sessions and permissions.
- Media and storage: Discover/search, Library, Watchlist, Requests, configurable
  NAS/SMB/NFS/WOL, scanning/import, metadata and duplicate resolution on the shared
  Work/structure/edition/version/asset/file/track identities.
- Acquisition: unified Arr+, Wanted, monitoring, Search Planner, native Newznab,
  release parsing/scoring, SABnzbd or native downloader, queue/retry/recovery,
  automatic imports, hardlinks and path mapping. Prowlarr remains an optional
  one-time takeover source under the existing owner decision, not a runtime dependency.
- Playback: self-hosted direct play/remux/FFmpeg/HLS, demand pacing, HLS/transcode
  caches, hardware acceleration, resource limits, cleanup and prioritized background
  media optimization; subtitles, dual subtitles and explicitly requested AI translation.
- Clients and formats: casting/Chromecast/AirPlay/PWA; video, Manga, Book and
  Light Novel readers; Music, Audiobooks and conditional Games; progress/resume,
  history and Collections.
- Operations: complete Admin UI, monitoring/logs, backup, explicit migration,
  security, automatic recovery and E2E coverage against Alpha-90 functionality.

[ALPHA90_RECOVERY_PLAN.md](ALPHA90_RECOVERY_PLAN.md) is the owner's recovery/parity reference, not a complete
specification of extensions. These targets are not implemented capability claims.
Latest decisions, fresh schema contracts, dependency approvals and the stopped
Phase B still apply; legacy parity does not authorize active-database migrations.

## Remaining decisions and prerequisites

The owner authorizes continued implementation of agreed topics in coherent
packages: one bundled commit, own feature branch/PR, then clean-start integration
after green checks. Collect unresolved questions while progressing independent
work; framework limitations and new dependencies require discussion, not workarounds.
Instance settings, Library configuration, User capabilities and public name/language
consumption are connected. Library gates actual Services through their existing
context/transaction. Media types and subtypes switch per instance and per profile
(Owner blocks, profile hides); Arr+/Playback/Tracking module implementations and full branding remain open.

The agreed functional module catalog is Library, Requests, Arr+, Playback,
Readers and Tracking. Requests has a whole-Work slice: active-profile language
intent, moderation, atomic Wanted targets and pause/resume. The Arr+ core runs on
those targets under its own Acquisition (Arr+) switch: Owner-managed Newznab indexers and SABnzbd
clients, manual search and grab, automatic search for missing movies with backoff,
download monitoring (tracked job ids, reserved grabs recovered after interruptions),
failed-release exclusion, mapped and link-free import paths and hardlink/copy import
into the first writable library of the media type without taking over existing files.
Requested languages order releases of equal quality. TMDB (key in the Providers
registry) creates movies and series with tmdb/tvdb/imdb mappings and the episode
catalog. Wanted series are searched per season: a fully aired, empty season as a
pack, otherwise episode by episode, several downloads in parallel; requests complete
once every aired regular episode exists. Books and Light Novels come through Open Library and
direct sources; Manga through AniList with volumes as units on the same unit pass as series
episodes; Music through MusicBrainz with the album as the Work and its tracks as units. Prowlarr, Sonarr and Radarr can be taken over once (indexers, clients, missing
titles). Games: IGDB metadata with shared platforms (G1), Arr+ per requested platform (G2) and
ROM identification by hash against the libretro No-Intro DATs (G3) are connected; ScreenScraper
(developer account) and artwork/PC enrichment (G4) follow, as does the remaining profile editing. The
remaining modules are not advertised as available. Tracking must be rebuilt from
the desired progress/resume/history behavior, not copied from the buggy legacy
implementation. Library's physical scanning/import integration still needs its
focused import contract. Connections, library paths, background scans, inventory,
manual identification and stored availability are implemented; see
[Storage runtime](STORAGE_RUNTIME.md).

1. Additional versions per Work (owner decision 13 Oct): by default one version and one quality profile per Work, as in Sonarr; an advanced switch, off by default, will let a request that an existing version does not satisfy (other language or quality) add as many versions as needed.
2. Exact instance/profile preference catalogs; complete the agreed media/storage columns.
3. NAS health/capacity probes and real-host SMB/NFS deployment verification. The
   owner approved a separate Linux mount helper; `ffprobe` is used within the
   requested FFmpeg scope. Further third-party/native dependencies require approval.
4. Whether one library path may hold several media types, as the older storage spec
   allows; the current contract fixes one media type per path.

Role authorization, session revocation/rotation, Owner account directory/edit/approval
and Owner catalog description/manual translation editing are implemented. Account
updates protect the last enabled Owner and invalidate sessions after role/status
changes. Account replacement uses last-write-wins without a pre-read or timestamp
input; catalog edits retain their version protection. These use existing
baseline tables. Media pages, standalone Arr, takeover and Player track selection remain
unfinished. Implement the shared owners in that order, with the same pipeline
for native and external downloaders and the same Storage locations for imports/playback.
Owner account/profile associations, account session revocation, provider registry
enablement and language registry administration are now connected to real Razor
pages. These reuse existing baseline entities. Provider connections/capabilities,
Synology administration, metadata providers and import placement remain open.

The first media slice now provides canonical Works/WorkTitles and profile-keyed
WatchlistEntries. Admin Works/Work and User Details/Watchlist consume typed
services; manual title edits protect revisions and retain other title sources.
The Request Users page reads the existing account directory without duplicating
account management. Localized metadata policy,
inventory, monitoring, complete Requests/Wanted lifecycle, quotas, storage roles and playback remain open;
these pages are not the completed media/Arr feature set.

Dev reference owners: [Acquisition core](https://github.com/Juloc/Jularr/blob/dev/src/Jularr.Web/Features/Acquisition/Core/CODEMAP.md),
[Playback plan](https://github.com/Juloc/Jularr/blob/dev/src/Jularr.Web/Features/Playback/Decision/PlaybackPlan.cs),
[Storage contract](https://github.com/Juloc/Jularr/blob/dev/docs/mockups/admin-storage/SPEC.md).

Provider references: [TMDB metadata locales](https://developer.themoviedb.org/docs/languages),
[TMDB artwork languages](https://developer.themoviedb.org/docs/image-languages).
Image language support is not identical to regional text support; keep language-neutral
artwork as eligible fallback. Direct access candidates (not installed dependencies):
[SMB client examples](https://github.com/TalAloni/SMBLibrary/blob/master/ClientExamples.md),
[NFS client APIs](https://github.com/sahlberg/libnfs).
