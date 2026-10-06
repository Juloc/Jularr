# Request-to-Play Implementation Readiness Audit

Date: 2026-10-04  
Baseline: `dev@e7a302a5e620d18e6f0ffe0cfd2fa320e5e5abf3`  
Scope: Anime + Movie + TV/Series consumer path

`Discover -> Request -> Wanted -> Search -> Download -> Import -> Library -> Detail -> Play -> Progress/Resume`

Status: **source-level readiness audit complete**. This document records what exists in current `dev`, what remains legacy/partial and the implementation order required for the first complete cross-video vertical. It is not a substitute for the final integration/E2E gate in #813.

## Status legend

- **Ready foundation** — canonical implementation exists and is suitable for the vertical; legacy compatibility may still remain.
- **Substantially implemented / verify** — implementation and focused tests exist, but issue closure still needs current-dev integration verification or a small remaining contract fix.
- **Partial** — meaningful implementation exists, but the approved vertical breaks here.
- **Legacy compatibility** — old path still exists and may be read/dual-written during migration; new code must not expand it.
- **Missing** — required consumer behavior is not implemented on the canonical path.

## 1. Executive result

The backend foundations have moved materially beyond the older issue audits.

Already present on `dev`:
- canonical Work/Structure/Edition/Version media core;
- canonical video `MediaAsset -> StoredFile -> MediaTrack/MediaTechnicalAnalysis`;
- TMDB-backed Movie/TV Discover and canonical provider identity;
- canonical Movie/Anime/TV `PlaybackVideoTarget`, `PlaybackPlan` and shared PlaybackDecisionEngine;
- canonical video `MediaProgress`, playback history and `ActiveSession`;
- Movie/TV shared Request/Wanted/search/download/import execution with focused tests;
- canonical Movie/TV import attachment to playable Asset/File identity.

The first full vertical is nevertheless **not ready to call complete** because the consumer layer still breaks in several important places:
1. Discover/Request UI still exposes legacy `Add` vs `Request` labels depending on `Instant` capability.
2. the consumer Library read model is still Anime-only and still reads legacy Anime Episode/File/Progress tables;
3. Movie Detail and Series Detail are not implemented as canonical consumer pages;
4. the Movie/TV acquisition issue #812 has implementation on `dev`, but still needs current-dev closure verification and a canonical result/detail destination;
5. Admin Movie/TV control-plane parity remains #814;
6. no final Anime/Movie/TV request-to-play integration/E2E gate has proven the complete path (#813).

## 2. End-to-end readiness matrix

| Stage | Anime | Movie | TV / Series | Current owner / finding |
| --- | --- | --- | --- | --- |
| Canonical identity | Ready foundation + legacy bridge | Ready foundation | Ready foundation | `MediaCore` Work/Structure plus migration-only `LegacyWorkBridge` |
| Provider Discover/Search | Ready/legacy-mixed | Ready foundation | Ready foundation | AniList + TMDB through Discovery; durable actions resolve canonical identity |
| Consumer Request UI | **Partial / conflict** | **Partial / conflict** | **Partial / conflict** | backend Request exists, but Discover and older per-media pages can still show `Add` for Instant capability |
| Request approval | Ready foundation | Ready foundation | Ready foundation | one `AcquisitionRequestService`; Instant remains policy, not a second desired UI action |
| Wanted/monitoring | Ready but Anime legacy concepts remain | Substantially implemented / verify | Substantially implemented / verify | shared Wanted service; Movie/TV handlers now exist on dev |
| Release search/scoring | Ready/legacy-mixed | Substantially implemented / verify | Substantially implemented / verify | shared registry/scorer/indexer path; Movie/TV video engine exists |
| Download | Ready foundation | Ready foundation | Ready foundation | shared download-client submission/operations |
| Completed-download dispatch | Ready foundation | Ready foundation | Ready foundation | shared dispatcher/import service |
| Import -> canonical playable file | Ready + migration bridge | Ready foundation | Ready foundation | canonical `MediaAsset/StoredFile`; Movie/TV adapters attach canonical playable identity |
| Consumer Library | **Partial / legacy** | **Missing** | **Missing** | `Library/Index` calls only `GetAnimeEntriesAsync`; query uses legacy Anime/Episode/File/Progress |
| Consumer Detail | Existing Anime page, legacy-heavy | **Missing** | **Missing / shared Anime-Series spec not bound to a Series page** | #593/#594 remain consumer blockers |
| Player orchestration | Ready foundation + legacy Anime adapter | Ready foundation | Ready foundation | one canonical player/decision path; old Anime overload remains compatibility-only |
| Progress/history/session | Ready foundation + legacy dual path | Ready foundation | Ready foundation | canonical `MediaProgress` + `ActiveSession`; legacy EpisodeProgress still transitional |
| Continue Watching | Canonical service exists | Canonical service exists | Canonical service exists | consumer surfaces still need to consistently bind it |
| Admin parity | strongest current path | **Partial** | **Partial** | #814 |
| Full E2E proof | Partial regression coverage | **Missing final gate** | **Missing final gate** | #813 |

## 3. Canonical identity and storage

### Ready foundation

Current MediaCore owns:
- `Work`;
- `WorkSeason`;
- `WorkEpisode`;
- `WorkVolume`;
- `WorkChapter`;
- `WorkEdition`;
- `WorkVersion`;
- provider identity/provenance.

Current Library storage owns:
- `MediaAsset`;
- `StoredFile`;
- `MediaTrack`;
- `MediaTechnicalAnalysis`.

Movie targets a Work. Anime/TV playable episodes target WorkEpisode.

`CanonicalMediaStorageService` resolves and attaches playable video without making a filesystem path the media identity.

### Transitional debt

`WorkSourceLink` / `LegacyWorkBridge` still keep per-type legacy objects usable.

This is acceptable only as controlled migration compatibility. New Library/Detail/Request/Player work must not expand the bridge as a permanent product API.

## 4. Discover / Search

### Implemented

TMDB-backed Movie/TV discovery exists through the provider/discovery framework.

Movie and Series appear as Discover categories, and Request submission selects TMDB identity for those kinds instead of forcing AniList.

Canonical provider identities are persisted through Work identity resolution rather than using provider IDs as product IDs.

### Remaining consumer mismatch

The current Discover card model still carries an `AddAction` with values such as `add` and `request`.

Current UI can render:
- `discover.card.add` for Instant-capability users;
- `discover.card.request` for Request-capability users.

That violates the binding Request spec.

Required fix:
- the visible acquisition action is always **Request**;
- Instant remains authorization/approval policy only;
- internal compatibility names such as `CanAdd` / `AddCreatesRequest` may survive temporarily if changing them is risky, but no consumer copy/branch may expose Add as a different acquisition product action.

The same stale pattern also exists in older Book/Manga/Novel/Requests surfaces and should be normalized by the shared Request/UI work rather than perpetuated.

## 5. Request

### Backend

The canonical request service exists and already separates:
- caller/profile attribution;
- authorization/capability;
- approval/auto-approval policy;
- executor dispatch;
- durable request state.

This is compatible with the approved UX once the UI stops presenting Instant as Add.

### UX gap

The approved Request flow is one resolved-media dialog/sheet with:
- Request;
- optional Scope;
- derived Included content;
- language/Edition where relevant;
- success/status.

Existing legacy request/add pages must be treated as implementation debt, not authority over the approved spec.

## 6. Movie/TV Wanted -> Search -> Download -> Import

### Current dev finding

The older #812 audit is stale.

`dev` now contains:
- `VideoAcquisitionEngine`;
- `MovieAcquisitionRequestExecutor`;
- `TvAcquisitionRequestExecutor`;
- `MovieWantedRequestHandler`;
- `TvWantedRequestHandler`;
- registrations in `Program.cs`;
- canonical TV scope using WorkSeason/WorkEpisode IDs;
- Movie whole-Work acquisition;
- shared Indexer/DownloadClient/ReleaseScorer/QualityProfile/ReleaseRequestTracker usage;
- shared completed-download import path;
- focused tests for Movie completion, TV current+future monitoring, Custom episode/season scope, retry and cancellation.

This is **substantially implemented** on current dev.

### Why #812 should not be blindly closed yet

Remaining closure checks:
- run/retain a green integration test on current dev after concurrent acquisition/storage changes;
- verify manual approval and auto-approval enter exactly the same executor path;
- replace the current successful video acquisition result URL (`/Search?q=...`) with the canonical Library/Detail destination once Movie/Series Detail exists;
- verify restart/recovery with the final storage routing from #815/#816;
- ensure Admin projections in #814 consume the same lifecycle.

Open PR #830 is stale/diverged from current dev and duplicates implementation that has already landed through newer commits. It must not be merged wholesale.

## 7. Import / storage

Movie/TV completed-download adapters and canonical playable attachment exist.

Current canonical video storage is the correct target:
`Work/WorkEpisode -> WorkVersion -> MediaAsset -> StoredFile -> MediaTrack`.

The remaining storage concern is routing/configuration ownership, not another media file model. #815/#816 is the active LibraryRoot content-routing work and must be reconciled before acquisition import settings are declared final.

## 8. Consumer Library — blocking gap

This is currently the clearest consumer blocker.

`Pages/Library/Index.cshtml.cs` loads:
`LibraryMediaCardQuery.GetAnimeEntriesAsync(...)`

The query is Anime-only and reads legacy:
- `Anime`;
- `Episode`;
- `EpisodeProgress`;
- legacy media-file/analysis joins;
- Anime-specific artwork/provider metadata.

Therefore the approved cross-media Library shell exists visually, but the read model is not yet a canonical cross-media Library implementation.

Required next implementation:
- create one canonical Library read/query contract over Work + canonical availability/progress/file/track facts;
- support at least Anime, Series and Movie first;
- bind media-type filtering to the same read model;
- use canonical `MediaProgress`, not legacy `EpisodeProgress`;
- derive language availability from canonical MediaTrack;
- keep partial/request states as projections, not separate Library state;
- keep set-based queries/no N+1;
- preserve Anime behavior while migrating it to canonical reads.

Also remove consumer Library management shortcuts such as direct Sonarr import/root-management actions from the normal Library header. Those concerns belong to Admin.

## 9. Movie / Series Detail — blocking gap

The specs exist:
- `movie-detail/SPEC.md`;
- `anime-series-detail/SPEC.md`.

The corresponding canonical Movie/Series consumer pages are not yet present in the current Library page family.

Required:
- canonical Work-based detail query;
- availability/language projection from Asset/File/Track;
- state-derived Play/Continue/Request;
- Series seasons/episodes from WorkSeason/WorkEpisode;
- Movie versions/languages without fake Track-as-Edition;
- Request status from the shared request lifecycle;
- canonical Play target only.

Do not build MovieDetail/TvDetail persistence models.

## 10. Player

### Ready foundation

`PlaybackPlanService` accepts canonical `PlaybackVideoTarget`:
- Movie -> Work;
- Anime/TV -> WorkEpisode.

All use the same `PlaybackDecisionEngine` for Direct Play / Direct Stream / Transcode.

Focused tests cover Movie, Anime and TV through the same planner and canonical file inventory.

### Transitional debt

The legacy Anime episode-id overload remains as a compatibility adapter.

It is allowed only until old routes are migrated. New Movie/TV/client paths must never call it.

## 11. Progress / ActiveSession

### Ready video foundation

Current canonical video state includes:
- profile-scoped `MediaProgress`;
- exact resume time;
- completion state;
- history;
- cross-video Continue Watching;
- WorkEpisode navigation;
- `ActiveSession` separate from consumption progress.

Tests explicitly cover:
- Movie profile isolation;
- idempotent/concurrent updates;
- Movie + TV Continue Watching;
- no fake Movie Next;
- opening ActiveSession does not create consumption progress;
- legacy Anime progress/history backfill.

### Remaining #662 scope

Video is no longer the primary missing foundation.

Remaining work is mainly:
- keep AniList write-back verified against completed-through semantics;
- finish canonical Reader locator/progress migration;
- remove legacy EpisodeProgress runtime ownership only after validated migration;
- keep exact resume and completed-through separate.

## 12. Admin parity

#814 remains a real blocker for operational parity.

Still required on the final shared Admin path:
- Movie/TV Wanted actions/state;
- Movie/TV Manual Search release candidates and score/rejection reasoning;
- monitoring scope/profile controls;
- actual storage/import settings;
- reachable manual inbox/import flow where supported;
- Movie/TV Activity/Operation projection.

Do not create Movie Admin or TV Admin apps.

## 13. Final E2E gate

#813 remains the completion gate.

Required proof must cover:

### Anime regression
`Discover -> Request -> Search -> Download -> Import -> Library -> Detail -> Play -> Resume/Complete -> AniList completed-only sync`

### Movie
`TMDB Discover -> Request -> Search -> Download -> Import -> Library -> Movie Detail -> Play -> Resume/Complete`

### TV
`TMDB Discover -> Request scope -> Search -> Download -> Import -> Library -> Series Detail -> Play/Next -> Resume/Complete -> future monitoring`

The final gate must verify:
- no duplicate Work;
- no raw path as identity;
- no separate Movie/TV progress/player/scheduler;
- retry/restart idempotency;
- server-side permission enforcement;
- consumer states come from canonical backend state.

## 14. Open PR handling

### PR #830 — closed as superseded

Closed on 2026-10-04. The branch was heavily behind current dev and duplicated Movie/TV acquisition code already present on dev through newer commits. #812 remains the current closure-verification owner.

### PR #716 — closed as superseded

Closed on 2026-10-04. The branch was very stale and current dev already contained the evolved Users & Permissions spec and visual reference.

### PR #816

Active canonical LibraryRoot routing work. It is relevant to final import/storage configuration and should be reconciled normally; this audit does not supersede it.

### PR #842

Explicit WIP Notifications branch. Unrelated to this request-to-play vertical and should remain isolated.

## 15. Priority implementation order

1. **Request-only consumer semantics**
   - remove visible Add/Instant acquisition variants;
   - bind approved Request dialog/status behavior.

2. **Canonical cross-media Library read model**
   - Anime + Movie + Series first;
   - migrate Anime Library reads off legacy EpisodeProgress/File ownership.

3. **Movie Detail + Series Detail**
   - Work/WorkEpisode based;
   - canonical availability/language/request/play state.

4. **#812 closure verification**
   - current-dev integration/restart check;
   - canonical result destination;
   - reconcile final LibraryRoot routing.

5. **#814 Admin parity**
   - Wanted/Manual Search/monitoring/import/settings/activity.

6. **#813 E2E gate**
   - Anime regression + Movie + TV full flow.

7. **Controlled legacy removal**
   - only after parity/backfill validation;
   - stop legacy reads/writes before dropping bridge/tables.

## 16. Readiness conclusion

The architecture is no longer blocked on the basic video storage/progress/player foundations; #808, #809, #810 and #811 correctly represent completed foundation work.

The next critical path is **consumer integration**, not another parallel architecture:
- Request-only UX;
- canonical Library reads;
- Movie/Series Details;
- acquisition closure verification;
- Admin parity;
- E2E proof.

Do not start another generic media/player/progress model to solve these gaps.
