# Information architecture

Target UX sections in this document are authoritative together with `docs/UX.md` and binding screen specs. Historical/current-implementation inventory tables below describe implementation state only; they do **not** override the target navigation or canonical domain model.

## 0. Account entry and active Profile

Before the consumer shell, Jularr resolves:

```text
Account authentication -> active Profile -> permission-derived shell
```

- Account login and Profile selection are separate.
- Multiple Profiles use the shared picker defined in `docs/mockups/login-profile-selection/SPEC.md`.
- Exactly one usable Profile may direct-start on Desktop/Mobile.
- TV should show the picker whenever multiple Profiles are available because it is commonly shared.
- `Switch profile` returns to the picker without logout.
- The selected Profile determines personal progress/history/ratings/settings and the media capabilities used to derive navigation.
- External login providers authenticate a local Jularr Account; personal provider Connections/sync remain Profile-scoped and separate.

Canonical reference for the consumer/admin split, the media model, provider mapping, admin
navigation and the Sonarr/Radarr/Bazarr/Readarr parity gap. Source: [#510](https://github.com/Juloc/Jularr/issues/510)
(epic) and its comments. Implementation is split into [#517](https://github.com/Juloc/Jularr/issues/517)
(navigation), [#518](https://github.com/Juloc/Jularr/issues/518) (admin dashboard, sessions),
[#519](https://github.com/Juloc/Jularr/issues/519) (consumer pages), [#520](https://github.com/Juloc/Jularr/issues/520)
(Home rows/Discover filters), [#521](https://github.com/Juloc/Jularr/issues/521) (roles/permissions),
[#522](https://github.com/Juloc/Jularr/issues/522) (Android TV navigation). This document (#516)
does not change code.

Related docs, not repeated here: [ADMIN_OPERATIONS.md](ADMIN_OPERATIONS.md),
[ANIME_ACQUISITION.md](ANIME_ACQUISITION.md), [ANIME_NAMING.md](ANIME_NAMING.md),
[READING_ACQUISITION.md](READING_ACQUISITION.md), [MEDIA_SEGMENTS.md](MEDIA_SEGMENTS.md),
[ANDROID_CLIENTS.md](ANDROID_CLIENTS.md).

## 1. Consumer vs Admin

A normal consumer must not need to understand Sonarr/Radarr/Bazarr/Readarr-style internals such as provider IDs, root folders, naming profiles, indexers, download clients, scans, mapping conflicts, remux jobs or acquisition pipelines.

Consumer and Admin use the same design system but remain structurally distinct.

### Desktop / wide Tablet

Persistent consumer navigation:

- Home;
- Library;
- Games;
- Calendar;
- Learning;
- global Search/Discover access.

Bottom/account area:
- Profile;
- Settings;
- Admin only when authorized.

Do not create permanent top-level destinations for:
- individual media types;
- Collections;
- Activity;
- Downloads.

Collections lives inside `Library -> Collections`.

Activity/history belongs under Profile. Operational jobs/downloads belong in Admin.

### Mobile

Primary bottom navigation:

- Home;
- Library;
- Calendar;
- Learning;
- Profile.

Search remains globally accessible from the top/app bar.

Library is a real destination and is not merged into Home.

Collections remains an internal Library subview.

Games does not occupy a permanent Mobile bottom-navigation slot. When Games is available, its dedicated route is reached contextually through Home Games/Continue Playing surfaces, global Search/Discover Games context, Game Detail/back-navigation and deep links. Home must keep the Games destination discoverable even when there is no recent Game activity.

Admin/settings/detail workflows use contextual navigation rather than trying to fit desktop sidebar structures into the bottom bar.

### TV

Remote-first primary destinations:

- Home;
- Library;
- Games when the Games destination is available to the active profile/installation;
- Calendar where useful;
- Search;
- Profile.

Learning appears only where the TV interaction is useful. Optional module entries disappear when unavailable to the active Profile.

Library uses large remote-friendly media cards and an internal `Library | Collections` switch. Collections is not a second TV top-level destination.

Games/Player-specific TV behavior follows its own binding specs when enabled; normal media Library remains independent.

### Home and Discover

Home is personalized continuation/recommendation context.

Discover/Search is the explicit surface for finding media not already in durable Library context.

Home recommendation/genre/media rows may deep-link into Discover with a pre-applied facet. This does not merge Home and Library or make Library an external discovery surface.

Global search:
- empty query may show Discover;
- typed query searches canonical/local/provider-resolvable results according to the Discover contract;
- selecting provider-native presentation still resolves to canonical Work identity.

Normal Library search stays scoped to durable Library context and does not silently trigger external discovery.

### Library and Collections

Library is one cross-media consumer catalog.

Inside it:

`Library | Collections`

Library media scope:
- All;
- Anime;
- Series;
- Movies;
- Manga;
- Light Novels;
- Books;
- Audiobooks.

Collections is not a media type and not top-level navigation.

Consumer Library has no generic Add/import menu. New media is found through Discover and requested through the shared Request flow. Manual file/folder import and reconciliation belong to Admin.

Watchlist/Reading List is represented as profile-state filtering/deep-linking inside Library rather than another top-level destination or a fake Collection.

## 2. Media model

Four layers per #510. Mapped to what exists in `src/Jularr.Web/Data` and `Features/` today:

| Layer | #510 description | State | Where |
| --- | --- | --- | --- |
| 1. Storage/Admin structure | Root, work folder, season/special folder, media file/sidecars | **Exists** | `LibraryRoot`, `MediaFile` (`Data/AppDbContext.cs`); root config and wake state on `/Admin/System` |
| 2. Jularr internal structure | Work, season/unit, episode/chapter/volume, stable internal IDs | **Exists** | `Anime`, `Episode`, `MediaFile` for video; `NovelWork`/`NovelVolume`/`NovelChapter`, `BookEdition`/`BookFile` for reading. Manga is file-based (`MangaModels.cs`: `MangaSeriesItem`/`MangaChapterItem`), not a DB entity — its "stable ID" is a derived series key, not a row id |
| 3. Provider mappings | AniList, TVDB, TMDb, IMDb, MAL, future providers | **Exists** | `AnimeMetadata` (AniList match: provider+external id, cover/banner, unique per anime) and `AnimeLocalMetadata` (TVDB/MAL ids, NFO-sourced) exist; `NovelAnimeMapping` cross-references novel↔anime. Provider **roles are now independently configurable** (display metadata, episode structure, acquisition identity, progress tracking, artwork, cross-reference IDs — `Features/Mapping/MappingProviderRoles.cs`) with a global default per role plus a per-anime override, stored via `ProviderRoleAssignmentStore` (raw-SQL migration `20260929150000`, no EF entity). With nothing stored the resolved roles reproduce Jularr's implicit behaviour (AniList = display/progress/artwork, local numbering = structure/acquisition, TVDB = cross-reference); assigned on `/Settings/MappingReview`. See [#525](https://github.com/Juloc/Jularr/issues/525) |
| 4. User presentation groups | Seasons, parts, cours, story arcs, specials, person/week/round groups (reality shows), independent of files/provider coordinates | **Exists** | A `PresentationGroup` is a per-work free-text name plus an ordered list of inclusive internal-unit ranges (episode `Number` / chapter / volume number) with a group order; it is keyed by (`MediaType`, `WorkId`) and is media-type-agnostic. Stored in `PresentationGroups`/`PresentationGroupRanges` (raw-SQL migration `20260929130000_AddPresentationGroups`, accessed via `Features/Presentation/PresentationGroupStore.cs` as derived state — no EF entity, so it never touches episode identity, file paths or provider mappings). `PresentationGrouping.Arrange` derives the display sections. The shared owner-only editor (`Pages/Library/PresentationGroups.cshtml`, linked from each detail page's `_ManageSheet` Files group) previews and saves anime episode, manga volume, EPUB light-novel volume, or web-novel chapter groups. Consumer pages render those sections when defined and otherwise retain their existing plain episode/chapter/volume layouts. See [#524](https://github.com/Juloc/Jularr/issues/524), [#567](https://github.com/Juloc/Jularr/issues/567) |

Reality-show example ("Anna: E01-E05" over S01E01-E20) and anime-cour example (AniList Part 1
E01-E11 / Part 2 E01-E12 over one local season) are both now expressible through layer 4 for anime;
the same mechanism is designed to cover reading media (manga/novel volumes and chapters) once its
editor UI is wired.

**Universal media core (#592) and its workflow layer.** A provider-independent `Work` (movie, series,
anime, book, manga, light novel) carries a stable internal id with external provider identities,
titles, structure, editions/versions, typed relations and per-field provenance hanging off it
(`Features/MediaCore`: `Work`, `WorkExternalIdentity`, `WorkTitle`, `WorkFieldProvenance`,
`WorkService`/`WorkQueryService`; migration `MediaCoreFoundation`). Legacy per-type records
(Anime/NovelWork/BookEdition/MangaSeries) are bridged non-invasively through `WorkSourceLink`, so
watch progress, notes, wanted and collections stay attached to the legacy id.

- **Identity correction / merge / split (#432).** `WorkService.ReassignExternalIdentityAsync` moves a
  provider id to another work; `SplitExternalIdentityToNewWorkAsync` peels one into a fresh work;
  `MergeWorksAsync` absorbs one work into another (moving its bridges, identities, titles, relations,
  structure and provenance) and preserves progress by repointing the `WorkSourceLink`. Every change
  is written to the append-only `WorkIdentityChange` log (migration `WorkIdentityChanges`, no FK so it
  outlives an absorbed work). Duplicate suggestions come from
  `WorkQueryService.FindDuplicateSuggestionsAsync` (shared normalized title within one media type;
  already-related pairs suppressed). **Exists.**
- **Field-level provenance (#435).** Each displayed field records its source and precedence
  (`WorkFieldProvenance` + `MetadataFieldSources`: manual > preferred provider > secondary >
  local/NFO > filename). `LegacyWorkBridge` records provenance as it mirrors per-type titles, and the
  owner can pin a field as a manual override (`WorkService.SetManualFieldOverrideAsync`) so a provider
  refresh cannot overwrite it. **Exists** at the bridge/core level; routing every legacy per-type
  provider write through the ladder lands with the individual #556 library children.
- **Merge/Duplicate Review Center (#437).** Owner-only `/Admin/MergeReview`
  (`Pages/Admin/MergeReview/Index.cshtml(.cs)`, `mapping.edit` policy) lists duplicate suggestions and
  flagged identity conflicts with merge / split / reassign / confirm actions plus the field-source
  pin, and shows the identity-change history. Linked from Metadata & Mapping
  (`/Settings/MappingReview`). **Exists.**

## 3. Anime multi-provider mapping

**Provider roles** (display metadata, episode structure, acquisition identity, progress tracking,
artwork, cross-reference IDs) are now an owner-configurable setting, not just de facto behaviour.
`Features/Mapping/MappingProviderRoles.cs` defines the six roles and their allowed providers;
`ProviderRoleAssignmentStore` (raw-SQL table `ProviderRoleAssignments`, migration `20260929150000`,
no EF entity) stores a global default per role and a per-anime override, resolved most-specific-first
(work override → global default → built-in). The built-ins reproduce the previous implicit behaviour
(AniList = display/progress/artwork, local/absolute numbering = structure/acquisition, TVDB =
cross-reference), so nothing changes until a role is reassigned on `/Settings/MappingReview`.
**Exists.**

**Range mapping**: `AnimeSequenceMappingPlanner` (`Features/MediaMapping/AnimeSequenceMapping.cs`)
plans local-season-to-AniList-part ranges from contiguous local numbering and an anchor AniList
entry; `AnimeSpecialMapping.cs` covers specials/OVA/ONA separately. `NovelAnimeMapping` and
`ReadingSegmentMappingStore` (chapter-range ↔ external id) do the equivalent for reading media.
**Exists** for the planning/automatic-match mechanics; **partial** for the owner-facing workflow
(below).

**`/Settings/MappingReview`** (`Pages/Settings/MappingReview.cshtml(.cs)`, backed by
`MediaMappingReviewStore`) still lists `MediaMappingReviewTask` items (provider, external id, title,
score and evidence per candidate) and keeps **Dismiss**, but it is now also the anime range-mapping
apply workspace (`?animeId=`). For a selected anime it shows the match candidates with confidence,
a local-vs-provider side-by-side coverage table with per-episode **exact/partial/missing/conflict/
unmapped** states (classified by the pure `AnimeMappingPlanner`), a range form for per-episode
overrides and specials (season 0), an explicit **Mark unmapped** action, **Preview** before **Apply**,
and the per-anime provider-role overrides. Applying goes through `AnimeMappingApplyService`, which
replaces the work's ranges in the canonical episode-mapping store (`AniListAccountStore`, which
`AnimeMetadataService.ResolveEpisodeAsync` already consumes — no second source of truth) and writes
a durable **audit** entry (`MappingAuditStore`, table `AnimeMappingAuditEntries`). Remapping is
progress-safe: watch progress keys on the stable `EpisodeId` while mappings key on `AnimeId` + local
range, so changing a mapping only rewrites provider coordinates. **Exists**
([#525](https://github.com/Juloc/Jularr/issues/525)).

**`/Settings/MappingSegments`** (`Pages/Settings/MappingSegments.cshtml(.cs)`, backed by
`ReadingSegmentMappingStore`) maps local chapter ranges (`LocalChapterStart`/`End`) to an external
provider's chapter numbering (`RemoteChapterStart`) for Manga/Light Novels. It is **not** an anime
episode-range mapping tool despite the adjacent name — anime range mapping now has its own owner-facing
workflow on `/Settings/MappingReview?animeId=` (see above); this page remains the reading-media
equivalent. **Exists** (reading media here, anime on Mapping Review).

## 4. Admin navigation

The binding target Admin information architecture is `docs/UX.md` §17 plus the individual Admin screen specs.

Permanent owning destinations are consolidated around:
- Dashboard;
- Activity / To-Do, with History as the third tab;
- Library, Wanted and Requests;
- Downloader;
- Providers, including the Reading Sources family and its enablement/priority/fallback configuration;
- Acquisition Profiles;
- Storage;
- AI;
- Users & Permissions;
- Devices & Sessions;
- Notifications;
- Backup & Restore;
- Migration;
- System & Diagnostics;
- Instance;
- General Settings;
- Appearance;
- conditional Games administration when Games is available.

Manual Search, Media Detail, assignment/reconciliation editors, Setup and Games editors/import-resolution are contextual flows rather than extra permanent sidebar destinations.

Reading Sources do not get another permanent Admin destination. The current `/Admin/ReadingSources` surface is an implementation-migration input to `Admin → Providers → Reading Sources`; future reading-source adapters must extend that shared Provider family instead of adding pages.

Legacy `/Admin/Resources`, `/Admin/Health`, `/Admin/Logs`, `/Admin/Sessions`, `/Admin/Devices`, `/Admin/History`, `/Admin/Usenet`, `/Admin/Sonarr`, `/Admin/Scans` and older `/Settings/*` configuration routes are implementation migration inputs. They may redirect/deep-link into their target owner after feature parity; they do not define target ownership.

The table below is therefore a **current implementation inventory against the older #510 grouping**, not the target navigation contract:

| Target group | Current route | State |
| --- | --- | --- |
| Dashboard | `/Admin` (admin-overview) | Exists, but not the "is anything broken / who's watching / what's transcoding" landing page #510 wants — see §6/§7. #518 |
| Library | `/Library`, `/Library/AnimeRepair/{id}` | Exists as consumer+admin hybrid (repair tools are owner-only but live under the consumer Library route) |
| Acquisition | `/Acquisition` (admin-anime-acquisition), `/Settings/Acquisition` (admin-import) | Exists |
| Wanted / Missing | inside `/Acquisition` | Exists (not a separate nav entry, but present as a section) |
| Queue / Downloads | inside `/Acquisition`, Operations `IsDownload` rows | Exists (no standalone "Downloads" nav entry; folded into Acquisition and Operations) |
| Metadata & Mapping | `/Settings/MappingReview`, `/Settings/MappingSegments` (admin-mapping) | Exists — configurable provider roles and the anime range-mapping apply/preview/audit workflow, see §3 |
| Subtitles | `/Admin/Subtitles`, `/Settings/Subtitles` (admin-subtitles) | Exists |
| Media Processing | no dedicated nav entry (optimizer/trickplay/segments run as background Operations, surfaced only in `/Admin/Operations`) | Partial |
| Playback & Sessions | `/Admin/Sessions`, `/Admin/Devices` | Exists as separate current surfaces; target consolidation into **Devices & Sessions** is pending. #518 |
| Storage | Target owner is Admin **Storage** per `docs/mockups/admin-storage/SPEC.md`: physical Mounts/LibraryRoots, safe path/routing, capacity/reserve plus #414 lifecycle policies, Review, optimization, tiering/physical migration, integrity analysis, forecast and history. Current `/Admin/System` still exposes roots/wake/health; current `/Admin/Storage` implements inventory insights + safe Jularr-cache cleanup only. | Partial — #414 lifecycle target is substantially larger than the current insights/cache-cleanup implementation. |
| Calendar / Releases | consumer `/Calendar` only; no admin releases nav entry | Partial |
| Jobs / Activity | `/Admin/Operations`, `/Admin/Scans`, `/Admin/Logs` (admin-operations, admin-scans, admin-logs) | Exists |
| Integrations | `/Admin/Usenet`, `/Admin/Sonarr` (admin-usenet, admin-sonarr) | Partial — Usenet/Sonarr only, no general integrations hub (Prowlarr health lives under Usenet) |
| Users & Permissions | `/Admin/Users`, `/Admin/Requests`, `/Admin/Capabilities` (admin-users, admin-requests) | Partial — user management, request policy and a per-media-type capability matrix (Hidden/Browse/Request/Instant, §8) exist; role assignment lives on the per-user page. `/Admin/Capabilities` is linked from `/Admin/Users`, not from the shell nav catalog. #521 #436 |
| Settings | `/Settings/*` section (Admin AI, API keys, Localization) | Exists |
| Diagnostics | `/Admin/Logs`, `/Admin/Ai`, `/Admin/Health` (admin-ai, admin-health) | Exists — operation logs plus dependency/service health and version/update diagnostics (§5, Sonarr/Radarr table). #528 |


**Admin → Instance** is the owner-only server-wide module surface. A module appears there only when
its full runtime gate is implemented. The instance switch is the hard upper bound.

**User Settings → Modules & Features** is the personal module surface. For modules that the instance
has enabled and the current user is permitted to use, the profile may independently turn the module
On or Off. A personal toggle can only narrow availability; it never bypasses instance policy,
role/permission checks or media capabilities.

Effective user-facing resolution is:

```text
instance module -> authorization/capability -> profile module preference -> detailed feature setting
```

Instance-disabled or forbidden modules are hidden rather than shown as switches the user could
re-enable. Personal Off preserves data and detailed settings, hides the module for that profile and
stops profile-specific work where applicable; it does not disable shared instance work for other
users. Re-enabling restores the preserved profile state subject to current policy.

All current instance modules are exposed in Admin → Instance: Anime, Movies, TV, Manga, Novel, Books,
Audiobooks, Learning, Acquisition and Tracking. Their personal toggles appear only where the module is
meaningful and permitted for that profile.

## 5. Parity matrices

State legend: **exists** (built and reachable today), **partial** (built but incomplete against
the #510 description), **missing** (not built). Verified by grep against `src/Jularr.Web` — see
each row's Where.

### 5.1 Sonarr/Radarr

| Capability | State | Where | Issue |
| --- | --- | --- | --- |
| Root folders / storage state | Exists | `LibraryRoot`, `/Admin/System` | — |
| Monitored/unmonitored works | Exists | Media-type-agnostic `MonitoringEngine`/`MonitoringStore` (one JSON file per kind under `/data/acquisition`; anime keeps `monitoring.json`, `AnimeMonitoring*` are transitional aliases) | — |
| Monitored seasons/episodes (granular) | Partial — the shared engine tracks whole-item, season and episode granularity (`MonitoringGranularity`, declared per kind via `MediaAcquisitionRegistry.MonitoringGranularityFor`); anime is wired at episode granularity (`AnimeAcquisitionInventory`). Non-anime pipeline wiring is a follow-up | `MonitoringEngine`, `AnimeAcquisitionInventory` | #396 |
| Missing / cutoff unmet | Exists | Wanted-episode logic, quality-profile upgrade cutoff | — |
| Rescan/refresh | Exists | `LibraryScanCoordinator`, per-anime repair (`AnimeRepairService`) | — |
| Rename preview + execute | Exists | `/Library/Rename/{animeId}`, `AnimeRenameService` | — |
| Manual import | Exists | `/Acquisition` "Needs a decision", `AnimeImportExecutor` | — |
| Move/organize files | Exists | Naming profile + `ImportFileTransfer` | — |
| Quality/language inventory (dedicated report) | Partial — data exists per file (`MediaAnalysis`) but no cross-library inventory view | `MediaInventoryService` | #421 |
| Duplicate detection | Partial — media-core duplicate/merge review exists (`/Admin/MergeReview`: shared-title suggestions, manual merge/split/reassign, identity-change history); import-time existing-file detection still separate, and detection is title-based (no cross-provider evidence merge yet) | `WorkQueryService.FindDuplicateSuggestionsAsync`, `WorkService.MergeWorksAsync`, `Pages/Admin/MergeReview`, `AnimeImportPlanner` | #437 |
| Health/problems (unified) | Partial — per-root and per-indexer/client health exist separately, no single "problems" view | `/Admin/System`, `AcquisitionHealthStore` | #518 |
| Indexers/Prowlarr integration | Exists | `/Settings/Indexers` | — |
| Download clients/SABnzbd | Exists | `/Settings/DownloadClients` | — |
| Automatic + interactive search | Exists | `AnimeAcquisitionScheduler`, `/Acquisition?search=` | — |
| Release scoring | Exists | `ReleaseParser` + `ReleaseScorer` (media-type-agnostic; anime is one registration) | — |
| Quality profiles (assign) | Exists | `QualityProfileStore` (keyed by media type) | — |
| Quality profiles (edit UI) | Missing — documented limit, assignment only | ANIME_ACQUISITION.md "Limits" | #396 |
| Language profiles (acquisition scoring) | Missing — no separate language-weighted profile beyond naming tokens | — | #396 |
| Preferred/rejected terms, upgrade rules | Exists | Quality profile required/forbidden terms, upgrade cutoff | — |
| Queue / history / blocklist / retries | Exists | Operations (`IsDownload`), `AcquisitionHistoryEntry`, blocklist | — |
| Import decisions / rejected reasons | Exists | "Needs a decision", search operation log | — |
| Naming profiles, preview, collision checks | Exists (anime only) | `/Settings/Naming`, `AnimeNamingFormatter` | — |
| Naming for Movies/TV/Books/Manga/Novels | Exists — Books/Manga/Novels have per-kind naming profiles (#529); Movies/TV place files with built-in defaults (`Title (Year)`, `Series/Season NN/Series - S00E00`) on the shared `NamingTemplateEngine` (#593/#594); configurable Movie/TV profiles are a follow-up | `Features/Movies/MovieNaming`, `Features/Tv/TvNaming`, `Features/Naming` | #396 |
| Sidecar handling on rename | Exists | subtitle/NFO sidecars follow the plan | — |
| Media processing: ffprobe inventory, remux, verification, rollback | Exists | `MediaInventoryService`, `MediaContainerOptimizer`, `MediaRemuxVerifier` | — |
| Media processing: dedicated transcode/optimize job (beyond lossless remux) | Partial — only the lossless MP4 remux is a durable job; lossy transcode happens live during playback, not as a background optimization job | `MediaContainerOptimizer`, playback-plan `transcode` mode | #403 |
| **Movies and TV library** | **Partial — first-class `Movie` and `TvSeries` entities bridged to the universal media core, completed-download + inbox import adapters on the shared spine, and per-kind naming/library roots exist (#593/#594); the consumer library grid + discovery UI (#595/#395) and playback wiring (#403) are pending** | `Features/Movies/**`, `Features/Tv/**`, `AppDbContext` `Movies`/`TvSeries` `DbSet` | #593 #594 |
| Requests & approvals (Overseerr/Jellyseerr-style) | Partial — request lifecycle, the owner queue with auto-approval rules (`/Admin/Requests`), a per-user request history (`/Requests`), anime request options (whole series, seasons or episodes, audio/subtitle preference, requester-selectable quality profile at `/Requests/New`) and an availability badge (requested / in library / available) on Media Banner cards exist; request versus instant follows the per-media-type capability. Audio/subtitle preferences are shown to the approver but not yet enforced in release scoring; `/Requests/New` is not yet linked from the Discover cards | `AcquisitionRequestService`, `AutoApprovalEvaluator`, `RequestHistoryQuery`, `MediaAvailability`, `/Admin/Requests`, `/Requests` | #597 #436 |
| Notifications (events/destinations) | Missing | — | #429 |
| Clients & devices inventory (admin) | Partial — `/Admin/Devices` lists known clients/devices across every account (kind, label, app version, first/last seen, online state, live playback method) with a Revoke action that ends the device's live session and forgets it; `/Profile/Devices` lets a user self-manage their own devices the same way. No capability/app-version negotiation beyond what a client already reports, and Jularr's cookie auth has no per-device token, so revoke cannot block a future reconnect from the same browser/app | `Features/Devices/KnownDeviceRegistry`, `Pages/Admin/Devices`, `Pages/Profile/Devices` | #510 |
| Transcoder resources dashboard | Missing | — | #403 |
| Remote access & security overview | Partial — `/Admin/Devices` shows recent sign-in success/failure activity (user name, remote address, timestamp) from an in-memory ring, alongside the devices inventory above; no persisted audit log, IP geolocation, trusted-device flagging or access-policy controls yet | `Features/Devices/SecurityEventLog`, `Pages/Admin/Devices` | #510 |
| Migration/coexistence: Sonarr | Exists | SONARR_MIGRATION.md, `SonarrParallelSafety` | — |
| Migration/coexistence: Radarr/Bazarr/Readarr/Plex/Jellyfin | Missing | — | #433 |
| Backup/export/restore (full app state) | Partial — acquisition-store bundle only | `/Settings/Acquisition` export/restore | #416 |
| Retention & cleanup (recycle/trash, orphaned files) | Partial — scan prunes its own old runs/logs only, no library-wide cleanup preview | `LibraryScanCoordinator` retention | #414 |
| API/webhooks/automation | Partial — REST automation API exists for acquisition only, no generic webhooks | `Features/Acquisition/Api` | #438 (provider framework) / #429 (webhook destinations) |
| System health & updates | Exists | `/Admin/Health` | — |

### 5.2 Bazarr

| Capability | State | Where | Issue |
| --- | --- | --- | --- |
| Sidecar subtitle import (per-episode, per-folder) | Exists | `MediaSegmentSidecarImporter`-adjacent `SubtitleImportService`, sidecar formats | — |
| Embedded subtitle extraction | Exists | `EmbeddedSubtitleExtractor` | — |
| Generated transcription subtitle | Exists | Whisper-based transcription (`LearningTextPreparation.cs`) | — |
| Forced/SDH detection | Exists — owner-facing forced/SDH preference per wanted language, on top of the existing extraction-ordering detection | `SubtitleLanguageProfileItem` (`Forced`/`Sdh`), `EmbeddedSubtitleExtractor.cs` (`IsForced`), `Settings/Subtitles.cshtml(.cs)` | — |
| Missing-subtitle tracking | Exists — per-episode complete/cutoff-met/missing-N state against the resolved language profile, from embedded + external tracks (a provider import carries the searched item's language/forced/SDH, so it satisfies exactly that item) | `SubtitleCompletenessService.cs`, `/Admin/Subtitles` completeness panel | — |
| Subtitle language profiles | Exists — owner-managed ordered wanted-language profiles with forced/SDH preference and a cutoff, assignable per media type and per library root (fallback media-type → global default) | `SubtitleLanguageProfile(Item)`, `SubtitleLanguageProfileService.cs`, `Settings/Subtitles.cshtml(.cs)` | — |
| External subtitle provider search/download | Partial — Jimaku still covers only the Japanese learning subtitle; the general `ISubtitleProvider` abstraction and owner-only manual-search UI now have one concrete provider, OpenSubtitles (search + download through the #438 provider framework: retries, Retry-After, response cache, health; the owner's API key and account are protected under `/data/integrations/opensubtitles.json`). A provider is offered through an `ISubtitleProviderSource` only while configured, so an unconfigured server still shows "no providers configured"; further account-based providers plug in the same way | `SubtitleProviders.cs`, `Subtitles/OpenSubtitles/*`, `SubtitleProviderServiceCollectionExtensions.cs`, `Pages/Settings/Subtitles.cshtml(.cs)` (connection panel), `Pages/Admin/Subtitles.cshtml.cs` (manual search panel) | #560 |
| Subtitle sync/validation | Missing — no timing-sync or validation tool; not part of #526's scope | — | — |
| Replace/remove subtitle | Exists | Rename/repair "Refresh subtitles" path re-imports; per-track removal via subtitle sources | — |
| Per-media subtitle diagnostics | Exists | Episode subtitle sources partial (`_EpisodeSubtitleSources.cshtml`) | — |

### 5.3 Readarr (books/manga/novels)

| Capability | State | Where | Issue |
| --- | --- | --- | --- |
| Work/volume/chapter identity — Novels | Exists | `NovelWork`, `NovelVolume`, `NovelChapter`, `NovelTranslation` | — |
| Work/edition/file identity — Books | Exists | `BookEdition`, `BookFile` | — |
| Series/chapter identity — Manga | Partial — file-derived (`MangaSeriesItem`/`MangaChapterItem`), not a durable DB entity like anime/novels; blocks a Manga equivalent of `/Library/Rename` for *existing* files | `Features/Manga/MangaModels.cs`, `MangaRepository` | #563 |
| Acquisition (search/download/import) | Exists | `Features/ReadingAcquisition`, `AcquisitionRequestService` | — |
| Monitoring/wanted | Exists | `WantedAcquisitionService` | — |
| Metadata/provider mapping | Exists (AniList) | Manga/Novel AniList match services | — |
| Configurable Light Novel search sources | Exists for current enable/disable + priority — Narou, AniList, BOOK☆WALKER, WebNovel and Internet Archive are configured at `/Admin/ReadingSources`, with capability/licensing/health and rights-aware Internet Archive filtering. **Target:** consolidate this into `Admin → Providers → Reading Sources`, add explicit Normal vs Fallback-only participation (including Internet Archive as a configurable fallback), media/language applicability and schema-driven future adapters without new pages. Acquisition Profiles may narrow/prefer the configured providers but cannot override adapter rights/capability limits. See [READING_ACQUISITION.md](READING_ACQUISITION.md#reading-sources) and [admin-providers/SPEC.md](mockups/admin-providers/SPEC.md). | `Features/ReadingSources`, `Features/ReadingDiscovery`, `Pages/Admin/ReadingSources.cshtml` | #477, #438 |
| Naming/organization | Exists — one naming-template profile per reading media type (Books, Manga, Light Novels), applied when a release is placed into its NAS library root; live preview and token reference on `/Settings/ReadingNaming` (sibling of anime's `/Settings/Naming`) | `Features/Naming`, `Pages/Settings/ReadingNaming.cshtml(.cs)` | — |
| Reading progress | Exists | `NovelProgress`, `MangaProgressItem`, bookmarks/highlights | — |
| Multiple editions/formats | Exists (Books) | `BookEdition`/`BookFile` (EPUB, PDF) | — |
| Chapter-range provider mapping | Exists | `ReadingSegmentMappingStore`, `/Settings/MappingSegments` | — |
| Cross-media anime↔novel mapping | Exists | `NovelAnimeMapping` | — |

Radarr's Movies/TV capabilities: Jularr now models Movies and TV as first-class media types —
`Movie` and `TvSeries` entities bridged to the universal media core (`WorkSourceKind.Movie`/`.Series`,
TV reusing `WorkSeason`/`WorkEpisode`), completed-download + inbox import adapters on the shared
`ICompletedDownloadImportAdapter`, and per-kind naming/library roots (#593/#594). The remaining
Radarr-parity rows are the consumer library grid + discovery (#595/#395), playback polish (#403) and
richer metadata providers (#438); #396 delivered the shared, media-type-agnostic acquisition engine
they build on.

## 6. Pipeline observability

Target timeline (#510): release found → scored → accepted/rejected → sent to downloader →
download started/completed → import matched → target path calculated → file moved → ffprobe →
subtitle discovery → metadata mapping → artwork refresh → optional remux/optimization → library
reconciliation → ready.

What `Operations`/`OperationLogs` (see [ADMIN_OPERATIONS.md](ADMIN_OPERATIONS.md)) record today,
by operation kind:

| Pipeline step | Recorded today | Operation kind / lane |
| --- | --- | --- |
| Release found / scored / accepted-rejected | Yes, in the search operation's log (module `Acquisition`) | `anime-search` |
| Sent to downloader / download started-completed | Yes | `anime-sabnzbd-download` (Anime), `sabnzbd-download` (Books) |
| Import matched / target path / file moved | Yes | `anime-import` (module `Import`) |
| ffprobe | Yes, as part of library reconciliation, not its own operation | folded into `library-scan` / `anime-repair-reanalyze-media` |
| Subtitle discovery | Yes, folded into scan | `library-scan` |
| Metadata mapping | Yes | `anime-metadata-match`, `anime-metadata-refresh` |
| Artwork refresh | Yes, folded into scan's Artwork phase | `library-scan` |
| Remux/optimization | Yes | `media-optimization` |
| Library reconciliation / ready | Yes | `library-scan` |

Every step above already has a durable operation and log; what is **not** built is a single
cross-operation timeline view that stitches one release's full journey (search → download →
import → optimize) into one visual sequence — today an admin must find the related operations
separately (by anime, by time). That combined view is part of the admin dashboard scope (#518).

## 7. Sessions

**Admin view (missing today):** #510 wants a Plex-style live sessions view — user, media/episode,
client/device, IP, start time, position, bitrate/resolution, tracks, playback method (Direct
Play/Direct Stream/Transcode), transcode reason/speed, bandwidth, errors/rebuffers, and an
authorized stop-session action. `Features/PlaybackSessions` (`PlaybackSessionStore`,
`PlaybackSessionCoordinator`, `PlaybackSessionHub`) already models session state for the TV↔phone
companion feature (`sessionId`, position, tracks, revision — see
[ANDROID_CLIENTS.md](ANDROID_CLIENTS.md) §10.1), but there is no admin page rendering it and no
transcode-diagnostics fields on the model. #518 builds this.

**User view (missing today):** a restricted Plex-like self-service view of the user's own sessions/
devices, playback history, progress, downloads/offline devices, with the ability to end their own
sessions — no other user's or admin's data. `EpisodePlaybackHistoryEntry` and
`ProfilePlaybackPreferences` already hold the durable history/preference data this page would read;
no page exposes it as a session/account view yet. #518 builds this.

## 8. Permissions matrix

Today only two roles exist (`Features/Auth/OwnerAccount.cs`): `AccountRole.Owner` and
`AccountRole.User`. There is no `Media manager` role. #521 implements the middle tier and the
policy checks below; the table states the target state, not today's binary Owner/User split.

| Area / action | Owner | Media manager | User |
| --- | --- | --- | --- |
| Consumer pages (Home, Library including Watchlist/Reading List filtered views, Calendar, Discover, playback, reading) | Full | Full | Full |
| Own account settings, own sessions/history | Full | Full | Full |
| Request media | Full | Full | Allowed per the media-type capability; the consumer action is always `Request`, while Instant capability means the Request may be auto-approved immediately |
| Per-media-type capability: Hidden/Browse/Request/Instant (`/Admin/Capabilities`, #436) | Unrestricted (always Instant) | Configurable (default Instant) | Configurable (default Request), with per-user overrides |
| Approve/reject requests | Full | Full | No |
| View admin dashboard, Operations, Scans, Logs | Full | Full (read) | No |
| Delete media / library files | Full | No (per #510's sensitive-action list) | No |
| Rename/move files | Full | No | No |
| Mapping changes, merge/duplicate review (MappingReview/MappingSegments, MergeReview) | Full | Full | No |
| Acquisition settings (indexers, download clients, quality profiles) | Full | Full | No |
| User management (create/disable accounts, roles) | Full | No | No |
| Stop another user's playback session | Full | Full | No |
| Storage/root/integration settings | Full | No | No |
| API keys / automation | Full | No | No |

`Media manager` is intended to run the day-to-day media-operations workflows (acquisition,
mapping, request approval, session moderation) without the account-management and system-settings
authority reserved for Owner. Sensitive-action gating listed above follows #510's own list
verbatim (delete, rename/move, mapping changes, acquisition settings, user management, stopping
another user's session, storage/settings/integration changes).

### 8.1 Per-media-type capability matrix (#436)

Orthogonal to the role/policy table above, each media type (`WorkMediaType`: Movie, Series/TV,
Anime, Book, Manga, Light Novel) resolves to one ordered capability per profile:
`Hidden < Browse < Request < Instant`. These are authorization/policy levels, not four different consumer buttons. The owner edits the matrix on `/Admin/Capabilities`
(owner-only, `admin.system`): a default per configurable role (Media manager, User) plus sparse
per-user overrides. The policy is the canonical JSON settings store
`Features/Auth/MediaCapabilityStore` (`/data/auth/media-capabilities.json`) — no EF table.
Resolution precedence: **Owner is always Instant (unrestricted); otherwise a per-user override
wins over the role default.** Features consume the resolved capability through
`IMediaCapabilityService` (`Features/Auth/MediaCapabilityService`) instead of re-deriving rules:
`GetViewAsync`/`GetEffectiveCapabilityAsync` (request experience #597 — whether Request requires approval or may auto-approve),
`GetVisibleMediaTypesAsync` (permission-derived shell #598 and discovery categories #595 — Hidden
removes a media type entirely), and `EnsureCapabilityAsync` (server-side per-media-type guard).

`AcquisitionRequestService` (#597) is the request-side consumer: both Request and Instant capability use the same consumer `Request` action. `Request` capability creates a request subject to approval policy; `Instant` capability permits immediate/auto-approved processing. Browse/Hidden cannot create acquisition requests. This replaces the former per-media-type "adding from
search" rule (`UserAddMode`), which #597 retired in favour of the capability matrix; its
`AcquisitionAccessPolicies.UserAddMode` table column was dropped by the Movie/TV schema migration
(#593/#594). On top of a Request capability the
owner's auto-approval rules (`Features/Acquisition/Access/AcquisitionRequestSettingsStore`,
`/data/acquisition/request-settings.json`, edited on `/Admin/Requests`) can approve a request without
the queue: a rule matches on media type and requester and may carry a per-requester quota within a
number of days; the first matching rule with quota left approves, otherwise the owner decides.

### 8.2 Permission-derived shell (#598)

Navigation is derived from what a profile may browse, per media type, on top of the policy checks
that already gate Admin and Settings. A **Hidden** media type is *absent*, never greyed out: a
Books-only profile sees a pure book app.

- **One resolution per request.** `IAppShellService` (`Features/Shell/AppShellService.cs`, scoped)
  turns `IMediaCapabilityService.GetViewAsync` into a `ShellMediaAccess`: `VisibleMediaTypes`,
  `Capability(type)`, `IsVisible(type)`, `IsAnyVisible(types)` and `CanOpen(routeRoot)`. The sidebar,
  the Library tabs, Profile and the route gate share that one answer. **Discovery's shelf board (#595)
  now consumes `IAppShellService.GetMediaAccessAsync(User)`** (`Features/Discovery/DiscoveryShelfService.cs`
  — a Books-only profile gets book shelves only), and the request experience (#597) should too, instead
  of re-reading the policy: `VisibleMediaTypes` is "which media types exist for this user".
- **One route table.** `UiNavigationCatalog.LibraryTabs` (`Features/Localization/UiShellNavigation.cs`)
  ties each consumer route root to the media types it serves (`UiMediaRoute`): `/Library` → Anime,
  `/Novels` → Light Novel, `/Manga` → Manga, `/Books` → Book, and the `/Reading` hub → Manga or Light
  Novel. The `library` sidebar destination is the hub of those tabs: shown while any tab is
  reachable, opening the first reachable one. The Library tab strip lists only reachable tabs and is
  hidden when fewer than two remain. Movies and series have no consumer pages yet; a tab for them is
  one more catalog entry and the sidebar, tabs and gate follow.
- **Route gate.** `Program.cs` registers `Conventions.AddMediaTypeGates()` (`Features/Shell/MediaTypeRouteGate.cs`),
  which attaches an authorization filter to every page folder in that table. A profile that cannot
  at least browse the type gets **404** (the type does not exist for them) before the page model is
  constructed; the owner is unrestricted through the capability policy. The gate is not a second
  policy: it reads the same `MediaCapabilityView`.
- **Not yet media-scoped (follow-ups).** Home type chips and Continue rows, the `/Discover` category
  tab strip and browse grid (the provider-driven shelf board #595 is capability-scoped; the manual
  category tabs/grid are not yet), Watchlist/Calendar/Franchise content, and the ClientApi surface
  (`/api/client/v1/...`) still list every media type the data contains; they should narrow by
  `ShellMediaAccess`.

## Canonical work vs provider presentation in search

The layer boundary also applies to search/discovery presentation:

- Jularr identity is the canonical Work and its internal seasons/episodes.
- Provider mappings may be one-to-many. A single Anime Work may map to several AniList entries representing seasons, cours/parts, specials or other provider-specific splits.
- Default/global search returns the canonical Work once and groups results by media type.
- With the Anime type selected, the UI may explicitly switch between **Jularr** grouped results and **AniList** provider-native results.
- Provider-native selection resolves to the canonical Work plus the relevant season/presentation target; it does not create a parallel Work.
- Exact season/part searches may surface/deep-link that target directly.
- Admin mapping/review owns corrections. Consumer surfaces consume the resolved mapping and do not expose provider-coordinate complexity.

## Storage lifecycle target (#414)

Binding Storage UX/spec ownership is in `docs/mockups/admin-storage/SPEC.md`.

Important boundary:
- #411 = canonical storage availability/integrity-safety state;
- #815 = LibraryRoot routing/default placement for future imports;
- #414 = retention, cleanup, optimization, candidate/review policy, tiering, physical LibraryRoot migration/evacuation, forecast/accounting and lifecycle history;
- #433 Migration Center = external-system/configuration migration into Jularr, not physical root-to-root storage migration;
- #415 = disposable client/offline-prefetch copies, never canonical-media eviction.

The target allows explicitly enabled Automatic canonical-media policies, but low free space alone never enables them. All destructive actions remain Requirement-aware, preview/audit driven and preserve logical Work/user state unless an explicit domain operation changes that state.

Current implementation is narrower: `/Admin/Storage` only has storage insights and safe cleanup of Jularr-owned disposable cache leftovers. Documentation describing current behavior must not imply the full #414 target is already implemented.
