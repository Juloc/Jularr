# Original Android-TV legacy functions vs. clean-start API V1

**Scope:** independent Kotlin adapter for existing public `/api/v1` user routes,
without backend, Phone, TV UI or shared adapter edits. Reviewed the original TV
branch `feat/android-tv-integrated-906-20261009` against `clean-start`.
Base integration: `feat/android-tv-v1-ui-integration` (PR #1468).
The main agent owns integration and all UI/product decisions.

## Verified server-side contracts

| Use | Existing route and permissions | Wire result | TV implication |
| --- | --- | --- | --- |
| Playback asset identity + neighbours | `GET /api/v1/playback/assets/{mediaAssetId}`; modules Library, Playback; `profile.playback` | `PlaybackItemV1`, full object | NEW adapter `playbackAsset(assetId)`: real WorkId and MediaAssetIds (previous/next), nullable unit numbers/title/resume |
| Aggregated profile usage | `GET /api/v1/statistics?days=N`; modules Library, Statistics; `profile.media.read` | `ProfileStatisticsV1`, full object; `watchedMs`, `sessions`, `gamePlayedMs`, arrays `completed`, `topWorks` | NEW adapter `statistics(days)`, days 0..3650; **0 = all time**; topWorks at most 5; NOT a playback-history event list |
| Media-file inventory | `GET /api/v1/works/{workId}/availability?page=N&pageSize=N`; Library, `profile.media.read` | Paged `WorkAvailabilityV1` | **Already implemented** as `TvCatalogV1Client.availability(workId, page, pageSize)`; no duplicate adapter |
| In-progress media | `GET /api/v1/continue`; Library, `profile.media.read` | Array of `ContinueItemV1` | **Already implemented** as `TvCatalogV1Client.continueWatching()`; not historical playback rows |
| Start playback / NAS wake decision | `POST /api/v1/playback/assets/{mediaAssetId}/sessions`; Library, Playback; `profile.playback` | `PlaybackPlanV1` or failure such as `storage_waking`/`storage_unavailable` | **Already implemented** by `HttpTvPlaybackV1Client.start()`; backend may send WOL only on its configured WakeOnDemand policy |
| Save progress | `PUT /api/v1/playback/sessions/{playbackSessionId}` | `UpdateResult` | **Already implemented** in `HttpTvPlaybackV1Client.update()`, not a historical list |
| Playback textual subtitles | `GET /api/v1/playback/assets/{mediaAssetId}/subtitles/{key}` | WebVTT text, no word IDs | **Already implemented** in playback-extras; useful for ordinary captions only |
| Owner NAS controls (not TV APIs) | `POST /api/v1/admin/storage/mounts/{storageMountId}/wake` and `/check` | Owner/Admin permission `storage.update` | **Do not call from TV** or synthesize StorageMountId from old `rootId`; no delegation to profile |

`GET /api/v1/works/{workId}/availability` reads persisted inventory, not a
fresh NAS probe: `isAvailable` means a file is not marked missing, **not**
that the network share is currently reachable. The new playback asset identity
endpoint also does **not** contact storage.

### New optional TV adapter

Package: `de.juloc.jularr.core.api.tvlegacy` in `:core-api`.
Create it with **the same shared** `DeviceTokenStore` used for device pairing,
catalog and playback (including its existing Keystore-backed update callback).

```kotlin
val legacy: TvV1LegacyFeaturesClient =
    HttpTvV1LegacyFeaturesClient(serverOrigin, sharedDeviceTokenStore)

val asset: TvV1PlaybackAssetInfo = legacy.playbackAsset(realMediaAssetId)
val stats: TvV1ProfileStatistics = legacy.statistics(days = 30)

// Do not confuse asset.previousMediaAssetId/nextMediaAssetId with WorkUnitIds.
// Do not turn stats.topWorks or continueWatching into synthetic history records.
```

- Use `playbackAsset` in the original episode/movie **player's previous/next
  asset navigation** only when the returned MediaAssetId is non-null. Distinct
  WorkId vs MediaAssetId must be preserved.
- Use `statistics` in a separate aggregate stats panel only if the main
  agent/owner chooses that UX; **never silently replace** the historical
  Activity list with these totals or five top works.
- For offline/NAS issues, use existing `catalog.availability` and the
  `HttpTvPlaybackV1Client.start()` outcome, show an appropriate error/retry
  state only when backend permits it. The existing TV
  `TvStorageRecoveryPolicy` operates on old root/availability DTOs and
  **cannot be mapped** to V1 without a new, approved contract.

## Original legacy code and missing V1 contracts

### 1. Phone companion, remote control, "Open on phone"

Old entry points:
`TvCompanionRuntime`, `TvCompanionPairingOverlay`, `TvAppHost.currentCompanionUpdate()`,
`pushCompanionState()`, and
`core-session/PlaybackSessionClient` on
`feat/android-tv-integrated-906-20261009`.

The old client uses a **different** `/api/client/v1/playback-sessions` protocol:
- POST creates a shared episode session with companion URL and owner hub URL.
- PUT `/{sessionId}/state` uses `expectedRevision`, synchronizes playback
  state, cue ID/text/tokens, selected term and track.
- POST `/{sessionId}/pairing` issues a short-lived, single-use code and QR;
  POST `/{sessionId}/revoke` revokes companion phones; DELETE ends the session.
- SignalR owner hub sends `SessionState`, `Command`, `SessionEnded`
  events for remote play/pause/seek/track/word controls.

**Missing in the existing /api/v1 user contracts:** a shared multi-device
playback room/session with participant authorization and epoch/revision,
companion phone pairing/claim/revocation, command delivery/ack and a
server-validated on-phone handoff payload with precise episode/cue/asset
identities. Existing `/api/v1/devices/pairings` authorizes a **device login**
only: it is **not** a companion-session pairing or remote-control channel.
Similarly, `/api/v1/playback/sessions/{id}` saves progress for one media
playback session and is not a multi-device command bus. No SignalR hub route
is inferred. Do not reuse or expose device bearer tokens through QR codes.

**Owner decision:** design the cross-device permissions, pairing lifecycle,
transport and UX first. Until then, do not show a fake live-remote QR or open
legacy client routes as a compatibility fallback.

### 2. Interactive language-learning subtitles and word states

Old entry points:
`TvClientFlow.loadCueWindow(episodeId,trackId,fromMs,toMs)`,
`TvAppController.refreshCueWindow()/setTermState()`,
`TvAppHost.currentCompanionUpdate()`,
`TvPlayerScreen` word focus and learning overlay.

Old DTOs:
`CueResponse {trackId,fromMs,toMs,cues[]}`;
`SubtitleCue {id,startMs,endMs,text,tokens[]}`;
`CueToken {surface,termId,canonical,reading,meaning,state}`;
`TermDetail {id,canonical,reading,meaning,state}`,
and `TermStateResult` for `known`/`learning`.

**Missing in /api/v1 playback:** cue-window endpoint with stable persisted
`cueId` per subtitle stream and timed tokens; word-level canonical/reading/
meaning and profile-level `known/learning` state retrieval and mutation;
term detail/search ownership and handoff of selected `cueId + termId`.
`/api/v1/playback/assets/{id}/subtitles/{key}` returns **plain WebVTT text**.
It does not provide identifiers, canonical tokens or learning states. The
`/api/v1/reading/.../annotations` routes belong to **book reading**
and must **not** be repurposed as video-cue/term APIs.

**Owner decision:** whether and how to migrate the original learning logic,
subtitles segmentation and canonical learning state to V1. Do not derive fake
cue IDs by numbering WebVTT lines or map subtitle words to invented term IDs.

### 3. Offline video, NAS and wake on demand

Old entry points:
`TvClientFlow.getMediaAvailability(mediaFileId, fresh)`, `wakeRoot(rootId)`,
`TvAppController.refreshEpisodeStorage()/wakeEpisodeStorage()`,
`TvStorageRecoveryPolicy` and its `MediaAvailability` /
`RootAvailability` with `retryAfterMs,canWake,rootId,wakeUrl`.
Old offline routes in `ClientApiRoutes`: episodic offline download,
offline progress, offline-library manifest/chapter/asset/sync.

The reviewed `/api/v1` player can stream media and returns a
storage-related failure. The backend itself can **automatically** issue NAS
WOL during session creation *only if its configured mount authorizes this*.
The TV must not invoke privileged `/api/v1/admin/storage/mounts/.../wake`;
this requires `storage.update`, a `StorageMountId` and Owner authority.
No user-scoped `GET availability?fresh`, root test, explicit root wake,
`canWake/retryAfterMs` contract or secure per-asset offline video
manifest/download/progress/sync protocol matching the old app was found in
the reviewed service endpoints.

**Owner decision:** how TV should display `storage_waking` and retry without
unbounded requests; whether user-authorized manual WOL and offline caching
are intended. Do not alias rootId to StorageMountId or download arbitrary
session HLS playlists as permanent offline files.

### 4. Historical playback Activity

Old entry point: `TvClientFlow.loadPlaybackHistory()`; old
`PlaybackHistoryItem` contains
`id,episodeId,animeId,animeTitle,seasonNumber,episodeNumber,episodeTitle,
startedAtUtc,lastPlayedAtUtc,positionMs,durationMs,reachedEnd`.
`TvAppController` holds `activity` and `historyUnavailable`.

Available `/api/v1/statistics` provides **only aggregate data**:
`watchedMs,sessions,gamePlayedMs,completed[]{mediaTypeId,count},
topWorks[]{workId,title,durationMs}`. The server limits top works to five,
and supports 0..3650 day selection. `/api/v1/continue` lists **unfinished
progress**, not chronological playback sessions.

**Missing:** paged, profile-authorized playback event rows with real
`playbackSessionId` or event ID, MediaAssetId/WorkId/WorkUnitId, timestamps,
position/duration/completion and chronological ordering. The original
legacy `/me/playback-history` route must not be silently reused.

**Owner decision:** add a V1 history contract or present only explicitly
labeled aggregates, keeping the chronological Activity view unavailable.

## Verification boundary

Standalone contract unit tests cover actual JSON field names, real HTTP
routes, nullable asset navigation, no fake history rows, 401/200 concurrent
rotation, missing results and preventing redirect-token leakage.
No backend changes, no API guesses, no Phone or TV UI edits.
A green JVM suite proves local parsing/HTTP contracts, **not** a running
clean-start server, a real NAS or Android TV hardware. The main agent should
test the actual backend before promoting the Activity/companion/storage UI.
