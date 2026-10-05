# Android, Android TV and Companion Architecture

Status: **accepted future architecture**  
Tracking issue: **#69**  
Decision date: **2026-09-22**

This document is the implementation contract for Jularr's first-party Android phone and Android TV clients. It intentionally fixes the product and architecture decisions before implementation so future work does not rediscover or reinterpret them.

If an implementation detail below must change because of a platform/API constraint, change this document and #69 in the same PR. Do not silently create an alternative architecture.

## 1. Product model

Jularr remains one product with one self-hosted server.

The server remains authoritative for:

- library/media identity
- episode/track/cue identity
- vocabulary and learning state
- dictionary and grammar/context data
- metadata
- playback-session state used for TV/phone companionship
- server-side remux/transcode fallback

Clients do not create competing durable copies of those facts.

Supported front ends are:

| Surface | Shell | Playback | Learning interaction |
| --- | --- | --- | --- |
| Browser | existing responsive Razor UI | web player | full |
| Android phone | existing Jularr web UI inside same-origin WebView | native Media3 player | full |
| Android TV | native Compose for TV UI | native Media3 player + MediaSession | remote-optimized + handoff to phone |

The phone app is deliberately **not** a second implementation of Library, Learn and Settings. The TV app is deliberately **not** a WebView wrapper.

## 2. Non-negotiable rules

1. Original anime media remains read-only.
2. No second database or client-side source of truth for learning/media state.
3. Native clients use a versioned Jularr API and never scrape Razor HTML.
4. Device playback is preferred. Server video conversion is fallback behavior.
5. No mandatory pre-encode step before Play.
6. Web, phone and TV use one player vocabulary and one visual design contract.
7. Subtitle learning uses normalized Jularr subtitle cues, not platform-specific subtitle text parsing.
8. TV/phone pairing uses Jularr as the coordinator. Bluetooth, Chromecast or vendor APIs are not the state layer.
9. Pairing is session-scoped and does not grant general server access.
10. Android APK signing credentials exist only in protected GitHub secrets/environment.
11. Browser use remains fully supported when no Android app is installed.

## 3. Android repository layout

All native Android code lives in one Gradle build:

```text
clients/android/
├── settings.gradle.kts
├── build.gradle.kts
├── gradle.properties
├── gradle/
│   └── libs.versions.toml
├── app-mobile/
├── app-tv/
├── core-api/
├── core-model/
├── core-player/
├── core-session/
├── core-design/
├── core-tts/
├── core-tts-sherpa/        (optional, see docs/TTS.md)
└── core-update/
```

Modules:

- `app-mobile`: phone application, WebView shell, native player host, app settings.
- `app-tv`: Android TV application, browse/detail/player surfaces, remote focus handling.
- `core-api`: HTTP + SignalR transport for the versioned Jularr client API.
- `core-model`: API DTOs and shared immutable client models.
- `core-player`: Media3/ExoPlayer integration, direct/fallback selection, audio tracks, playback state.
- `core-session`: playback-session/pairing/companion protocol.
- `core-design`: generated player theme values and reusable native player controls.
- `core-tts`: provider-neutral TTS contract, resolver, system (`TextToSpeech`) provider and offline-neural model manager (docs/TTS.md).
- `core-tts-sherpa`: optional sherpa-onnx offline-neural binding; excluded from the build unless `-PjularrNeuralTtsEnabled=true` because it needs a manually downloaded AAR (docs/TTS.md).
- `core-update`: self-update implementation shared by `app-mobile` and `app-tv` (#490) — an
  unauthenticated GitHub Releases check, semver comparison against `BuildConfig.VERSION_NAME`,
  phone-vs-TV asset selection, download, SHA-256 verification and the Android package-installer
  intent. Draft/pre-release releases are never offered (`/releases/latest` already excludes
  them), and a release that is not newer than the installed version is never treated as an
  update. Each app keeps its own device-local toggle/last-check/dismissed-version state
  (`UpdatePreferences`) and its own `FileProvider` authority for the staged APK.

Application IDs:

- phone: `de.juloc.jularr`
- TV: `de.juloc.jularr.tv`

Baseline:

- Kotlin
- Gradle Kotlin DSL
- Java 17 toolchain
- minSdk 26
- current stable compile/target SDK when implementation begins
- dependency versions pinned in `libs.versions.toml`
- AndroidX Media3/ExoPlayer
- Jetpack Compose for native UI
- Compose for TV components for TV surfaces

Do not introduce a separate Android repository.

## 4. Server API boundary

Native clients use `/api/client/v1`. Razor page handlers remain for web UI and are not the native-client contract.

Core v1 endpoints:

```text
GET  /api/client/v1/capabilities
GET  /api/client/v1/me
GET  /api/client/v1/library
GET  /api/client/v1/anime/{animeId}
GET  /api/client/v1/episodes/{episodeId}
GET  /api/client/v1/episodes/{episodeId}/player
GET  /api/client/v1/episodes/{episodeId}/cues?trackId={trackId}&fromMs={fromMs}&toMs={toMs}
GET  /api/client/v1/media/{mediaFileId}/availability
GET  /api/client/v1/media/{mediaFileId}/content
GET  /api/client/v1/episodes/{episodeId}/subtitle-tracks/{trackId}/cues
GET  /api/client/v1/episodes/{episodeId}/fallback?mode={device|server}&startSeconds={seconds}&audioTrackId={trackId}&quality={cap}
GET  /api/client/v1/episodes/{episodeId}/hls?startSeconds={seconds}&audioTrackId={trackId}&quality={cap}
GET  /api/client/v1/terms/{termId}
PUT  /api/client/v1/terms/{termId}/state

Owner-only storage operations:

GET  /api/client/v1/library-roots/{rootId}/availability
POST /api/client/v1/library-roots/{rootId}/test
POST /api/client/v1/library-roots/{rootId}/wake
```

All endpoints except `/capabilities` use the normal Jularr authenticated account and therefore the same profile-scoped learning state as the web UI. API authentication failures return JSON `401/403` responses instead of redirects to Razor login pages.

Playback continuity endpoints (additive v1, advertised by `episodeFlow`, `continueWatching` and `playbackHistory`):

```text
GET    /api/client/v1/episodes/{episodeId}/progress
PUT    /api/client/v1/episodes/{episodeId}/progress        { positionMs, durationMs, completed }
PUT    /api/client/v1/episodes/{episodeId}/watched         { watched }
GET    /api/client/v1/episodes/{episodeId}/flow            previous/next local episode + autoplayNext
GET    /api/client/v1/continue-watching
GET    /api/client/v1/me/playback-preferences
PUT    /api/client/v1/me/playback-preferences              { autoplayNext?, preferredAudioLanguage?, preferredSubtitleLanguage?, defaultPlaybackSpeed? }
GET    /api/client/v1/me/playback-history
DELETE /api/client/v1/me/playback-history
```

The server is the only durable owner of resume position, watched state, autoplay preference and history; clients must not keep a second durable progress store. Clients should resume from `resumePositionMs` (zero means start from the beginning), send bounded checkpoints (for example every 15 seconds while playing plus pause/stop/end) and use `/flow` instead of computing next/previous episodes locally. The semantics are described in the README section *Playback continuity*.

Watchlist endpoint (additive v1, advertised by `watchlist`; #533):

```text
GET /api/client/v1/watchlist
```

Read-only; it reuses `WatchlistStore`/`WatchlistLibraryResolver` (the same profile-scoped follow state the `/Watchlist` Razor page renders) rather than a second data path. Response shape:

```json
{
  "items": [
    {
      "id": "<guid>",
      "mediaType": "anime | tv | movie | manga | light-novel | book",
      "title": "string",
      "artworkUrl": "string | null",
      "availability": "in_library | external",
      "detailsUrl": "string | null",
      "addedAtUtc": "ISO-8601 | null"
    }
  ]
}
```

`availability` is `in_library` when the work is matched to a local library entry (`detailsUrl` then points at that library page) or `external` when it is only known through its provider (`detailsUrl` then points at the provider page, if any). `addedAtUtc` is null for works only included through a followed franchise, never followed individually. Older servers without the `watchlist` flag have no equivalent endpoint; clients must show an explicit "not available" state instead of calling it.

Player controls and preferences (additive v1, advertised by `playbackPreferences` and `embeddedSubtitleCues`):

- `/me/playback-preferences` is the one profile-scoped store for autoplay, preferred audio language, preferred subtitle language (`off` allowed) and default speed. `PUT` is a partial update: omitted/null fields keep their value, an empty language clears it, invalid values return `400` (`invalid_playback_speed`, `invalid_audio_language`, `invalid_subtitle_language`). Older clients that send only `{ autoplayNext }` keep working.
- `/episodes/{id}/player` adds `defaults` (server-resolved `audioTrackId`, `subtitleMode` = `off|learning|embedded`, `subtitleTrackId`, `playbackSpeed`, `preferences`) and `controls` (`playbackSpeeds`, `qualityCaps`). Clients start with these values instead of re-deciding them; later changes are session-only unless the user saves them as defaults.
- Track ids are the canonical `stream:{index}`. `audioTrackId` on `/hls` and `/fallback` keeps the selected audio across a fallback restart; `/subtitle-tracks/{trackId}/cues` returns plain cues of an embedded text subtitle so a playback subtitle survives fallbacks that drop container subtitles. The interactive learning subtitle stays the separate `/cues` model.
- `quality` (`auto`, `1080p`, `720p`, `low`) is a device-local cap. The server applies it only to a stream it must encode anyway or when the media inventory proves the source is taller; direct play and Device-mode remuxes are never converted because of it.
- Playback mode, quality cap and decoder capability are device-local; do not store them on the server. The ownership table is in the README section *Player controls*.

Bounded offline playback endpoints (additive v1, advertised by `offlineDownloads`; see §8.2):

```text
GET  /api/client/v1/episodes/{episodeId}/offline-download   download descriptor
GET  /api/client/v1/offline/media/{mediaFileId}/content      original bytes, HTTP range + strong ETag
POST /api/client/v1/offline/progress                         { items: [{ episodeId, positionMs, durationMs, completed }] }
```

Offline Book/Novel library endpoints (additive v1, advertised by `offlineLibrary`; see §8.3 and [OFFLINE_LIBRARY.md](OFFLINE_LIBRARY.md)):

```text
GET  /api/client/v1/offline-library/works/{workId}/manifest      versioned manifest, per-chapter hash
GET  /api/client/v1/offline-library/chapters/{chapterId}         chapter payload (text/blocks/translations)
GET  /api/client/v1/offline-library/assets/{volumeId}/{asset}    content-addressed cover/illustration bytes
POST /api/client/v1/offline-library/sync                         batched progress + bookmark reconciliation
```

Smart offline prefetch (additive v1, #415; details in [OFFLINE_LIBRARY.md](OFFLINE_LIBRARY.md)). The server owns the per-profile policy and the next-up selection; the client reports its inventory and executes the plan with the download endpoints above:

```text
GET  /api/client/v1/offline/prefetch/policy   { enabled, capBytes, includeEpisodes, includeChapters, episodesAhead, chaptersAhead, allowMetered }
POST /api/client/v1/offline/prefetch/plan     { connection: "unmetered"|"metered", deviceLimitBytes?, inventory: [{ kind: "episode"|"chapter", itemId, sizeBytes, origin: "explicit"|"prefetched", lastUsedUtc, active }] }
                                              -> { reason, policy, budgetBytes, prefetchedBytesAfter, downloads: [{ kind, itemId, containerId, title, sizeBytes, url }], evictions: [{ kind, itemId, sizeBytes }] }
```

Device-code pairing and LAN discovery (additive v1, advertised by `devicePairing`; §9.3, #489):

```text
POST /api/client/v1/pairing/start     anonymous; returns { deviceCode, userCode, expiresInSeconds, intervalSeconds }
POST /api/client/v1/pairing/approve   authenticated; body { userCode }
POST /api/client/v1/pairing/poll      anonymous; body { deviceCode }; signs the connection in once approved
GET  /.well-known/jularr              anonymous; { service, name, version, port, https }
```

The first v1 contract deliberately reports not-yet-implemented facilities through capability flags. HLS fallback, playback sessions, pairing and companion control remain `false` until their later delivery slices are merged. Clients must not infer support from route guesses.

Later session/companion endpoints extend the same v1 boundary:

```text
POST /api/client/v1/playback-sessions
GET  /api/client/v1/playback-sessions/{sessionId}
POST /api/client/v1/playback-sessions/{sessionId}/pair-code
POST /api/client/v1/playback-sessions/{sessionId}/commands
POST /api/client/v1/playback-sessions/{sessionId}/leave
POST /api/client/v1/pair
```

Real-time endpoint:

```text
/hubs/playback
```

HTTP is used for bootstrap, lookup and command requests that need explicit success/failure. SignalR is used for session-state broadcasts and immediate companion updates.

### 4.1 Capabilities

`GET /capabilities` returns at least:

- API version
- minimum supported client API version
- server product version
- feature flags for native playback, HLS fallback, pairing and companion control

A client with an incompatible API version stops with a clear **server/app update required** state. It must not fall back to scraping pages.

### 4.2 Player bootstrap

`GET /episodes/{id}/player` returns one canonical bootstrap model:

- episode/anime identity and display title
- duration
- media/container identity
- video codec/profile/level when known
- audio tracks with stable track IDs, language, title and codec
- subtitle tracks with stable track IDs, language, title, text/image capability and active learning-source flag
- direct-content endpoint
- direct content type and range capability
- compatibility fallback descriptor
- selected/default audio/subtitle preferences
- cue endpoint for the active learning subtitle
- active playback-session information when applicable
- canonical skip-segment markers and the seek-preview (trickplay) descriptor, see [MEDIA_SEGMENTS.md](MEDIA_SEGMENTS.md#client-descriptor)

Never expose host filesystem paths to clients.

### 4.3 Media endpoints

Direct media endpoint must support HTTP range requests and read the original file without modification.

Native compatibility fallback is HLS with fragmented MP4 segments:

```text
GET /api/client/v1/media/{mediaFileId}/content
GET /api/client/v1/media/{mediaFileId}/hls/{playbackId}/master.m3u8
GET /api/client/v1/media/{mediaFileId}/hls/{playbackId}/{segment}
```

The HLS cache is bounded and disposable. It belongs under `/data`, never beside source media.

The current v1 API also exposes the existing on-demand fragmented-MP4 compatibility stream at `/episodes/{episodeId}/fallback`. It can restart at a supplied playback position but is not seekable within the live response. This is explicitly advertised as `liveMp4Fallback=true` and `hlsFallback=false`; native clients must switch to HLS behavior only after the HLS capability becomes true.

The existing browser fragmented-MP4 path may remain while native support is added. Long term, web may consume the same HLS compatibility path, but the Android milestone does not block on that migration.

## 5. Playback selection

### 5.0 Playback plan (canonical, #403)

Advertised by `playbackPlan`. Every client — web, installed PWA, phone, TV — asks the server how to play instead of deciding itself:

```text
POST   /api/client/v1/episodes/{episodeId}/playback-plan
GET    /api/client/v1/stream-sessions/{sessionId}/stream?startSeconds={seconds}   (progressive fMP4)
GET    /api/client/v1/stream-sessions/{sessionId}/hls?startSeconds={seconds}      (redirects to the fMP4 HLS playlist)
PUT    /api/client/v1/stream-sessions/{sessionId}/telemetry                       (ephemeral runtime telemetry, advertised by `playbackTelemetry`)
DELETE /api/client/v1/stream-sessions/{sessionId}
```

Request body (every field optional):

- `capabilities`: what the device measured, each claim `confirmed` (decoder API asked about that exact configuration), `inferred` (weaker signal), `unknown` or `unsupported`: per container (`mp4`, `matroska`, `webm`) the video codecs (`h264`, `hevc`, `av1`, `vp9`, … with `bitDepths`, `maxHeight`, optional `codecTags`) and audio codecs (with `maxChannels`); `hdr` (`display`, `hdr10`, `hdr10Plus`, `hlg`, `dolbyVision`); `delivery` (`progressiveMp4`, `hls`); `subtitles` (`text`, `styledAss`, `image`); `features` (`audioTrackSelection`, `pictureInPicture`, …); `client` (`kind` = `web|pwa|android|android_tv`). Media3 clients report their decoder list; omitting the document makes the server infer a conservative one.
- `audioTrackId`, `subtitleTrackId` (canonical `stream:N`; a bitmap subtitle the client cannot draw is burned in), `quality` (`auto`, `original`, `20mbps` … `1mbps`; legacy `1080p/720p/low` are accepted), `mode` (`auto`, `direct_only`, `always_transcode`), `network` (`throughputKbps`, `bufferSeconds`, `recentStalls`, `saveData`, `connectionType`), `failedModes` (modes that just failed on this device), `replacesSessionId`, `wake` (default `true`: the request is a play intent and wakes sleeping Wake-on-LAN storage; send `false` to only decide, e.g. when a screen opens).

The response carries `sessionId`, `plan` and `delivery`. `plan.mode` is `direct_play` (untouched file, no server processing), `direct_stream` (video copied into fMP4, audio copied or converted), `transcode` (H.264) or `unavailable`. `plan.reasons[]` is machine-readable (`code`, `severity`, `rulesOut`, `values`) and explains every mode that was ruled out — clients show them as "Why not Direct Play?". `delivery.url` is the stream to open; live transports restart at a position with `delivery.startParameter`. When playback fails, send the failed mode in `failedModes` and play the new plan; never pick a fallback locally. Server-side quality decisions (Automatic, home/remote defaults) are part of the plan.

`plan.buffer` (absent when the plan is `unavailable`) is the runtime buffer policy: `preset` (`low`, `normal`, `high`, `max` = 15, 30, 60, 120 s of media ahead of the playhead, set by the Admin), `startupSeconds` (3 s; 6 s when the server transcodes video), `targetAheadSeconds` and `lowWaterSeconds` (40 % of the target). All of it is advisory: a player decides by itself how far ahead it fetches, and a client must not pause playback to wait for a reserve (a paused element loads only a couple of seconds, so such a wait delays the start without loading more). A client shows the policy next to the buffer it actually observes, never as a measured value.

#### Playback telemetry (`playbackTelemetry`)

A playing client reports what it observes about the session every ~5 s with `PUT /stream-sessions/{sessionId}/telemetry` (answer `200` with the quality advice below; `404` `stream_session_not_found` for a session that is not the caller's or expired; `400` `invalid_telemetry` for a missing or impossible value; `429` over the separate per-account limit of 90 reports a minute; body at most 1 KiB):

```json
{ "sequence": 12, "state": "playing", "bufferAheadSeconds": 12.4, "throughputKbps": 24000, "stallCount": 1, "stallTotalMs": 1840, "positionSeconds": 612.5 }
```

- `sequence` is a per-session counter that only grows. The highest sequence wins; a repeated or older report is ignored and answered like a new one, so retries are safe.
- `state` is `playing`, `buffering` or `paused`. A report extends the session's idle lifetime like a stream request only when it shows progress: the state is `playing` or `buffering` and `positionSeconds` advanced at a believable playback speed (at most twice the fastest offered speed) beyond the progress mark, or, while `buffering`, `stallTotalMs` grew no faster than the clock for at most 2 minutes in total since the position last advanced. A larger step is a seek and only moves the mark; a backward seek re-anchors it at most three times per 10 minutes, so a viewer can rewind but alternating positions keep nothing alive. A player that fails reports `paused`. `paused` reports, repeats, older reports and a loop of reports whose position stands still do not, so telemetry never keeps an abandoned session, its slot and its process alive. A client sends one `paused` report when it pauses and then stops reporting until it plays again.
- `bufferAheadSeconds` (0..3600) is the media buffered ahead of the playhead; `positionSeconds` (0..604800) is the absolute playback position. `throughputKbps` (0..10 000 000, optional) is the smoothed rate at which the client received media, over about 15 s; it is a delivery rate, not link capacity, and it is left out when the client could not measure it. `stallCount` and `stallTotalMs` are cumulative for the session. A stall is playback waiting for media after it had started; the initial start and a user seek are not stalls.
- The server keeps the report in memory on the session only. Nothing is written to the database and the report disappears with the session.
- When a client re-plans (`replacesSessionId`) the same title, and the replaced session's last report is not older than 2 minutes, the buffer and the stalls of the last 60 s it reported feed the next plan's Automatic quality (two stalls step it down) and win over the request's `network.bufferSeconds` and `network.recentStalls` hints. `network.throughputKbps` stays the request's own hint.


#### Quality advice and conversion speed (answer of the telemetry report)

The answer to every accepted report is `200` with a small JSON body; a repeated or older report is answered like a new one, because the body is a reading of the session's current state, never of the request:

```json
{ "advice": "step_down", "reason": "transcode_too_slow", "transcodeSpeed": 0.62, "transcodeFps": 12.5 }
```

- `advice` is `none`, `step_down` or `step_up`; `reason` (null when `none`) is `stalls`, `low_buffer`, `transcode_too_slow` or `throughput_headroom`. The server is the only decision owner (one rule, `PlaybackAdaptation`): a step down follows the existing two-stalls-in-60-s rule, a buffer below 4 s while the delivery rate falls, or a server transcode that stayed under real time (below 1.0x for 10 s after 10 s of produced media). A step up needs `quality = auto`, a throughput of at least 1.5x the next tier's bitrate in every report of the last 60 s (healthy buffer, no stall, no gap over 15 s), 120 s since the delivery started (any change of the delivery restarts this cooldown) and no transcode running below 1.15x. It never goes above the source, above a tier the server's encoder already failed to sustain, or while the viewer fixed a tier (`original`, `20mbps` ... `1mbps`); a too-slow transcode is the one exception, because a tier the server cannot encode cannot be played. Apart from a too-slow transcode, no advice is given within the first 20 s of a delivery or while the player reports `paused`.
- A client follows `step_down` / `step_up` by planning again at the same absolute position: `POST ... /playback-plan` with `replacesSessionId` set to the session, the same selections (audio, subtitle, `quality`, `mode`) and the failed modes **unchanged** (a stall is no verdict on a mode and must not enter `failedModes`). The server plans the next tier itself (one ladder step; the reasons `stall_limit`, `bandwidth_limit` or `transcode_too_slow` explain it), keeps the ActiveSession and progress, and starts a new cooldown. A client keeps at least 30 s between two switches and at most 6 per 10 minutes, never switches while paused or while a plan is being replaced, and never treats the advice as a command it must obey: a native client that cannot switch may ignore it.
- A transcode that stays too slow is re-planned in this order: a lower quality tier (down to 1 Mbps), then another healthy encoder backend, then an `unavailable` plan with the blocker `transcode_unsustainable`; a client that can play the original untouched gets it with the warning `limit_ignored_no_transcoder` instead. Slowness never counts against a hardware encoder's circuit breaker.
- `transcodeSpeed` (media seconds produced per wall second, ffmpeg's own cumulative factor) and `transcodeFps` are what the server measured while converting the video; they are null for Direct Play, remux and while no measurement exists. They belong in diagnostics only, next to the values the client observed itself.
- A server whose running conversions of the same kind already stay under real time refuses a new one at once: `503` with the code `transcoder_overloaded` and `Retry-After: 30`. Every admission refusal (`transcoder_busy`, `profile_session_limit` 15 s, `transcoder_overloaded` 30 s, `cache_budget_exhausted`, `cache_free_space_low` 60 s) carries `Retry-After` where asking again can help; `transcoding_disabled` and `cache_folder_not_owned` carry none (only an Admin changes them). The server never queues a refused delivery.


#### Known gaps of the adaptive playback (#403 slice D)

- **No pacing yet.** An HLS transcode still encodes at full speed to the end of the file instead of pausing at a high-water mark and resuming at the low-water mark of the buffer policy. Real pacing needs, at least: (a) a way to suspend and resume the ffmpeg process (`SIGSTOP`/`SIGCONT` on Linux through a small seam on `IHlsEncoderProcess`; `-readrate` only paces to a fixed factor, cannot burst after a seek and `-readrate_initial_burst` needs ffmpeg 6.1 or newer, which the runtime image does not guarantee); (b) the produced-ahead distance from what the server itself knows (newest segment on disk minus the newest segment the player requested), not from the 5 s telemetry; (c) the transcode meter to restart its run on every resume, because ffmpeg's `speed` is a cumulative average and a pause would read as a too-slow encode and trigger a needless re-plan; (d) the hold time to count against the idle expiry, the hardware-session slot and the first-output timeout. Without (c) the safe approach does not exist, so it is not built.
- **ffmpeg's `speed` is a cumulative average since the process started**, ignored for the first 10 s of produced media and required to stay under 1.0x for 10 s. A server that is slow only for a short moment is not re-planned; a slowdown in the middle of a very long run reacts slowly.
- **Only plan-based transcodes are measured and counted for overload.** The legacy `/episodes/{id}/hls` and `/fallback` routes (which are being retired) are admitted and slot-limited but never measured.
- The Android phone and TV players do not follow the advice yet; the contract above is what they implement. The Admin session list does not show the conversion speed (it has no column for it in its mockup); the speed is in the player diagnostics.

### 5.1 Native algorithm

For both phone and TV:

1. Fetch player bootstrap.
2. Inspect the device's Media3 decoder/container capabilities.
3. If supported, open the original media endpoint directly.
4. Prefer hardware decoding when Android/Media3 provides it.
5. Apply requested audio track.
6. Render the learning subtitle through Jularr's cue overlay.
7. If direct playback fails because of codec/container/decoder support, record the current position and track choices.
8. Start the server HLS compatibility stream.
9. Restore position and track choices.
10. Continue automatically; do not ask the user to press Prepare or start again.

A network error is not treated as a codec failure. Network retry and codec fallback are separate states.

### 5.1.1 Media-storage availability and recovery

Before treating a playback failure as a decoder/container problem, clients consume the server's media availability state:

- `available`
- `source_starting`
- `source_offline`
- `source_unreachable`
- `file_missing`
- `unknown`

Temporary source outages are retryable and use the shared bounded recovery cadence: immediate check, then 1s, 2s, 4s, and 5s intervals up to about 60 seconds. The client preserves the requested playback position and whether the user intended playback to continue. Once storage becomes available, it refreshes the player bootstrap and resumes automatically.

`file_missing` is not a Wake-on-LAN or codec-fallback state: the root is readable but the concrete media file is absent. `source_unreachable` is also not blindly retried forever.

The availability also carries the canonical storage `health` (`online`, `starting`, `offline_expected`, `offline_unexpected`, `error`) and a `diagnosticCode`. A root with Wake-on-LAN configured that cannot be reached is `offline_expected` (sleeping on purpose); without Wake-on-LAN it is `offline_unexpected`. `error` with `wake_timeout` or `wake_send_failed` means a start attempt failed: show the problem and offer a retry instead of polling on.

Pressing Play wakes sleeping storage: the media requests (`/content`, `/hls`, `/fallback`, offline downloads) and `/media/{id}/availability?wake=true` start the owning Wake-on-LAN NAS and answer `source_starting` until it is readable; concurrent requests share one bounded start attempt on the server. Plain availability checks, library browsing, artwork and metadata never wake storage. Normal users never receive MAC addresses or mount paths; owners additionally receive the explicit `wakeUrl` of the owning root.

The server keeps library state while a NAS is sleeping/offline and treats an unexpectedly empty previously-populated root as unavailable instead of a mass deletion. Native clients therefore keep library/detail navigation usable while playback storage is down.

### 5.2 Fallback profile

The first compatibility profile is:

- H.264/AVC video
- yuv420p
- AAC audio when audio conversion is required
- fragmented MP4 HLS segments
- seekable after segments exist
- generated on demand
- bounded cache cleanup

Compatible video/audio streams may be copied rather than re-encoded when the resulting stream is valid.

Hardware server transcoding is a later optimization behind the same fallback contract. It must not change the client API.

### 5.3 Native MediaSession

The TV app exposes its player through Android MediaSession so hardware remotes and Android TV system playback controls can control the active episode.

The phone native player should also use MediaSession for normal Android media controls.

## 6. Subtitle and learning model

The video renderer and learning subtitles are separate concerns.

Media3 may expose embedded subtitle tracks for normal playback selection, but Jularr's interactive Japanese subtitle is rendered from the server's normalized cue model so behavior is identical for:

- nearby SRT/ASS
- extracted embedded text subtitles
- manually selected embedded text streams
- generated Japanese audio transcription

Each cue has a stable cue ID, start/end time and normalized learning representation.

The client synchronizes cue display against player position. The server remains authoritative for term IDs, readings, meanings and learning state.

### 6.1 Common actions

Every player supports the same semantic actions:

- `playPause`
- `seekBack10`
- `seekForward10`
- `seekTo`
- `selectAudioTrack`
- `selectSubtitleTrack`
- `repeatCurrentCue`
- `learnCurrentCue`
- `openWord`
- `markKnown`
- `addToLearning`
- `openCompanion`
- `closeOverlay`
- `exitPlayer`

Platform controls invoke these actions. Platforms must not invent alternative meanings for the same action.

The action ids are stable identifiers (companion, session hub and Client API wire values) and carry no duration: `seekBack10` and `seekForward10` keep their names. Every client seeks by the canonical increments of `playback.seekBackSeconds` (10) and `playback.seekForwardSeconds` (30) in `design/player/player-tokens.json`, read from the synced token asset (`PlayerSeekSteps` in `core-design`), never by a number hardcoded in the client or taken from the id.

### 6.2 Learn-current-line behavior

When the user opens learning for the current cue:

1. remember whether playback was playing
2. pause if it was playing
3. freeze the selected cue
4. show tokenized words
5. word selection opens reading, meaning and learning state
6. grammar/context explanation is shown when available
7. Repeat seeks to cue start and plays the cue again
8. closing the learning UI returns to the previous player layer
9. playback resumes only if it was playing before learning opened and the user did not explicitly pause during the learning interaction

This rule is shared by browser, phone and TV.

## 7. Player visual contract

The goal is one Jularr player, not three unrelated players with similar colors.

Canonical design input lives under:

```text
design/player/
├── player-tokens.json
└── player-icons.json
```

`player-tokens.json` owns:

- overlay/background opacity
- text/background contrast values
- radius scale
- spacing scale
- control sizes
- typography scale
- subtitle typography
- subtitle background/padding
- focus/selected/pressed states
- animation durations
- control auto-hide durations
- manual seek increments (`playback.seekBackSeconds`, `playback.seekForwardSeconds`)
- TV scaling/safe-area multipliers

`player-icons.json` owns semantic icon IDs and vector path data. Build tooling generates/validates the web and Android forms from these canonical inputs. Do not hand-maintain separate semantic icon sets.

The generated outputs are implementation artifacts; the JSON inputs are the source of truth.

### 7.1 Shared layout

Visual order is consistent:

- video surface
- centered transport affordance when invoked
- Japanese subtitle above the bottom control area
- progress/timeline
- primary controls
- secondary controls for audio, subtitles, playback mode and learning
- sheets/overlays above the player without navigating away from the scene

The TV version scales controls and safe areas for distance viewing but preserves the same hierarchy and icon/label meanings.

### 7.2 Player status

Playback transport is visible as a small status, not a different UI mode:

- **Direct**
- **Remux** when relevant
- **Server**

Users can inspect it, but normal Auto behavior does not require choosing a mode before pressing Play.

## 8. Browser/phone interaction

Browser and phone native player behavior:

- single tap video: show/hide controls
- double tap left video zone: -10 seconds
- double tap right video zone: +30 seconds
- drag/scrub timeline: seek
- tap Japanese subtitle: Learn current line
- tap word in learning sheet: word details
- Repeat line: seek to cue start and replay
- Back/close: close topmost sheet before leaving player

Controls auto-hide while playing after a short idle period. They remain visible while paused or while a menu/sheet is open.

### 8.1 Hybrid phone shell

The phone application's main surface is a WebView loading only the configured Jularr server origin.

Rules:

- app pages: same-origin WebView
- external links: system browser/custom tab
- TLS errors: never bypassed automatically
- no broad `addJavascriptInterface`
- no arbitrary remote origin inside the app shell
- WebView cookies/session belong to the configured Jularr origin

Normal episode/detail navigation stays inside the WebView so the user can see the episode information and episode list before playback. On the Android phone client, that page exposes an explicit native Play navigation (`/Library/Episode/{id}?native=1`). The WebView shell intercepts only that explicit same-origin Play request and opens the native player using the episode ID. A regular browser continues to use the normal web player and never needs the Android marker.

When native playback closes, the user returns to the same episode/detail WebView history/navigation state.

The Companion screen itself remains a normal responsive Jularr web page and therefore appears identically in a browser or inside the phone app.

### 8.2 Bounded offline playback (phone, #225)

The phone can keep explicitly chosen episodes of the user's own library on the device. This is deliberately not a download platform: no DRM, no license server, no automatic/Smart Downloads, no browser/PWA offline video and no bulk mirroring.

Server contract:

- `GET /episodes/{id}/offline-download` returns the episode/anime titles, audio and subtitle track metadata with defaults, the complete active Japanese learning cue set (tokens include reading, meaning and the learning state at download time), the canonical progress snapshot, and the media identity: canonical `sizeBytes` of the file on disk, a strong `eTag` (length + modification time) and a bounded content `fingerprint` (`sha256-length-head-tail-64k`: SHA-256 over the little-endian length, the first and the last 64 KiB; the same fingerprint the media inventory persists). Unavailable storage answers like the content endpoint (`503`/`404` with the availability JSON).
- `GET /offline/media/{id}/content` serves the same read-only original bytes as `/media/{id}/content`, plus the strong `ETag`, so a resumed request uses `Range` + `If-Range` and can never splice bytes of two file versions. Source media is never modified or copied on the server.
- `POST /offline/progress` replays checkpoints recorded while offline (at most 100 per request) through the canonical `EpisodeProgressService`. Reconciliation is monotonic and idempotent without any server-side per-client state: a checkpoint only moves the resume position forward or marks an unwatched episode watched; watched stays sticky (a stale partial checkpoint neither un-watches an episode nor replaces a newer rewatch position; a replayed completion is `unchanged`); the 30 s accidental-start rule applies unchanged. Every item gets a final outcome: `applied`, `completed`, `unchanged`, `ignored_behind`, `ignored_watched`, `ignored_accidental_start` or `episode_not_found`, plus the resulting canonical progress. A live `PUT /progress` keeps its normal semantics (it may rewind during a rewatch).

Phone implementation (`app-mobile`, package `mobile.offline`; TV is out of scope for now):

- The native player shows an explicit **Download** action when the server advertises `offlineDownloads`. Downloads are managed from the **Downloads** screen (player, download notification, launcher shortcut, or the *Server unavailable* screen): per-episode state (queued, downloading, paused, ready, failed) with pause/resume/cancel/retry/remove, storage used against a configurable device limit (default 10 GB) plus free device space, and a Wi-Fi-only switch (default on). Admission rejects a download that would exceed the limit or eat into a 1 GB device safety margin.
- Transfers are WorkManager jobs (data-sync foreground work) that resume from the partial file length with `Range`/`If-Range`, so they survive process death and device restarts. An episode becomes **ready** only after its size and fingerprint match the descriptor; a changed server file fails the download instead of mixing versions.
- Files live in app-private `noBackupFilesDir/offline/<owner>/<episode>/`. Downloads belong to one account on one server: logging out locks them (hidden, not playable, transfers paused) until the same account signs in again; signing in as another account or changing the server deletes them. The account is re-checked via `/me` whenever the server is reachable and after WebView page loads.
- A ready download is played from the local file with Media3, also while online (the server bootstrap and fresh cues are used when reachable). Offline, the player uses the stored descriptor for titles, audio/subtitle selection and the learning overlay; word details come from the stored cue tokens, and learning-state changes wait until the server is reachable.
- The client keeps only a local copy of the canonical progress plus a sync queue (one entry per episode, completion sticky). Checkpoints that cannot be delivered (offline playback or a lost connection) are queued and replayed by a network-constrained WorkManager job, which first confirms via `/me` that the same account is signed in. Every server outcome removes the queued entry; a successful live checkpoint supersedes a queued partial one.

### 8.3 Offline Book/Novel library (phone, #221)

Bounded offline reading of the user's own Book/Novel library, on the same
canonical `/api/client/v1/offline-library/**` contract the PWA download
manager already uses (part 1, PR #359; full contract in
[OFFLINE_LIBRARY.md](OFFLINE_LIBRARY.md)), advertised by the `offlineLibrary`
capability. Implementation lives in `app-mobile`, package
`mobile.offline.library` — deliberately separate from `mobile.offline` (§8.2)
so the two download engines stay independently reviewable, even though the
library engine reuses `mobile.offline`'s account boundary and download state
machine directly (both are generic, not anime-specific).

- A native **"Save offline"** action appears over the WebView while browsing
  a book/novel detail page (`/Novels/Work/{id}` or `/Books/Library/{id}`),
  detected by URL path exactly like the episode Play route — no JS bridge.
  An **Offline books** screen (reachable next to Downloads: notification,
  launcher shortcut, *Server unavailable* screen) shows per-book aggregate
  status and per-chapter state with pause/resume/retry/remove.
- Manifest/chapter differential download: only a chapter whose hash changed
  (or that is new) is (re)downloaded; a chapter is written to a temporary
  file, read back and byte-compared, then atomically renamed into place
  before being marked ready — a truncated/corrupted write or a version race
  (the fetched payload's hash no longer matches what the manifest asked for)
  is never presented as available offline. Cover/volume-cover assets are
  best-effort, never gating availability.
- Downloads are WorkManager jobs, one per chapter (a chapter is bounded-size
  text, so unlike episode media there is no sub-file Range/resume — the job
  itself, with WorkManager's own retry/backoff, is the resumable unit,
  matching the PWA queue's granularity).
- Storage: app-private `noBackupFilesDir/library/<owner>/**`, a JSON
  snapshot (`AtomicFile`, no Room — this app has no Room/reflection
  serialization dependency anywhere) mirroring the shape of §8.2's own
  store. Account/server isolation reuses the exact same owner-key scheme.
- WebView local interception: `shouldInterceptRequest` answers a same-origin
  `GET` matching the manifest/chapter/asset path shape from local storage
  when available, otherwise falls through to the network — "local source
  first" per the issue's `Reader -> BookRepository` model, without a second
  rendering path or a JS bridge. Its practical effect depends on the reader
  itself fetching through this contract client-side, which is a separate,
  not-yet-landed slice (see OFFLINE_LIBRARY.md's Part 2 TODO).
- Reading-position and bookmark sync-queue plumbing
  (`LibraryDownloads.recordProgress`/`recordBookmark`, drained by a
  WorkManager job against `POST /offline-library/sync`) is implemented and
  tested; like the PWA side, it currently has no reader call site (same
  follow-up slice).

## 9. Android TV interaction

The TV app uses Compose for TV focus semantics and a native Media3 player. It is landscape-only.

The sidebar is exactly **Home, Watchlist, Activity, Profile/Settings** (#522); there is no
Library, Discover or per-media-type destination. Home carries a search field at the very
top (activating it opens the full search/browse screen, which reuses Home's card/grid
components) followed by an All/Anime content filter — Movies/TV are omitted because the
client API has no Movie/TV entity yet (#396) — then content rows: Continue Watching (when
`continueWatching` is advertised) and the library. Watchlist shows `GET /watchlist` as
focusable cards when the server advertises `watchlist` (#533), falling back to an explicit
"not available" state on older servers. Activity shows
`GET /me/playback-history` when the server advertises `playbackHistory`, falling back to
Continue Watching with an on-screen note otherwise. Selecting a title still drills in
**Anime → Episode → Player**: selecting an episode opens its TV detail surface first;
loading Media3/player bootstrap begins only after the user activates **Play/Resume**. Back
restores the previous screen (its sidebar tab, or Search) and Player's Back returns to that
Episode surface, not directly to the season list.

### 9.1 Remote behavior

When no modal/sheet is open:

- Play/Pause media key: toggle playback
- OK with controls hidden: show controls and focus the primary transport control
- OK on a focused control: activate that control
- Left/right with controls hidden: -10s/+10s
- Back with controls visible: hide controls
- Back with controls hidden: leave player after normal navigation behavior
- controls auto-hide after the canonical `timing.controlsAutoHide` token while playback runs; every remote key resets the timer. Paused playback, an open learning overlay or the phone companion overlay keep them visible.
- when the controls hide, remote focus returns to the player surface so the next key still reaches the player

Player control row always contains a **Learn this line** action.

When sentence learning is open:

- playback follows the shared pause/resume rule
- left/right: move focused word
- OK: open focused word
- Back: close word detail, then sentence overlay
- **Repeat line** is focusable
- **Open on phone** is focusable

No pointer/cursor emulation is used.

### 9.2 TV subtitle rendering

Use the same semantic subtitle style as web/phone with TV-specific values from the canonical tokens:

- larger type
- safe-area-aware bottom position
- sufficient opaque/translucent background for readability
- no subtitle behind the focused control bar
- selected cue/word state uses the same selected semantic style

### 9.3 Setup: discovery and device-code pairing (#489)

A fresh TV install never requires typing a server URL or a password with the remote when it can
be avoided:

1. **Setup** (`TvRoute.Setup`) broadcasts a LAN discovery probe and shows any Jularr server that
   answers, with a one-button **Connect**. Manual address entry stays on the same screen as the
   fallback.
2. Once connected, **Login** (`TvRoute.Login`) defaults to device-code pairing when the server
   advertises the `devicePairing` capability: it requests a short user code
   (`POST /api/client/v1/pairing/start`), displays it, and polls
   (`POST /api/client/v1/pairing/poll`) until a signed-in phone/browser session approves it at
   `/Pair` (Razor page, any signed-in account) or it expires, at which point the TV silently
   starts a new one. "Sign in with password instead" stays one click away for accounts/servers
   that need it.
3. Approval signs the TV's own connection in with the *same* cookie mechanism
   `/session/login` uses (`Jularr.Web.Features.Pairing.DevicePairingEndpoints`) — pairing never
   copies the browser's cookie and never mints a separate token type. There is no persistent
   device-pairing table: state lives in an in-memory, short-TTL, single-use store
   (`DevicePairingStore`) so it fits alongside the ongoing PostgreSQL migration (#570) without a
   schema change. A server restart mid-pairing just expires the code; the TV starts over.

**Discovery protocol.** The TV broadcasts the ASCII message `JULARR_DISCOVER_V1` over UDP to port
`37812`; every Jularr server answers unicast with a small JSON payload (service name, version,
port, scheme). The same payload is served over plain HTTP at `/.well-known/jularr` so a manually
typed address can be verified before pairing starts. This is a small dependency-free protocol,
**not** real mDNS/DNS-SD — the `_jularr._tcp.local` service this section originally sketched needs
either a new server-side dependency or a reverse-proxy-level responder (for example Avahi in the
container image), which is deployment-support work and stays out of scope here (any server that
never receives the broadcast — a different subnet, VLAN, or blocked broadcast traffic — falls
back to manual entry; that follow-up is tracked separately from #489).

## 10. TV ↔ phone playback session

A TV player creates one ephemeral `PlaybackSession` when playback starts.

The server is authoritative for the latest accepted state.

### 10.1 Session state

Minimum state:

```text
sessionId
episodeId
positionMs
durationMs
isPlaying
playbackRate
audioTrackId
subtitleTrackId
currentCueId
currentCueText
selectedTermId?
revision
updatedAtUtc
controllerClientId?
```

`revision` is monotonically increasing per session.

The active player publishes immediately on:

- play
- pause
- seek
- audio/subtitle change
- cue change
- selected-word change

While playing, it also publishes a lightweight position heartbeat. Cue changes are never delayed until the next heartbeat.

Target on a normal LAN: the paired phone displays the new cue within 500 ms of the TV cue transition.

### 10.2 Pairing

TV offers **Connect phone**.

It displays:

- QR code with a high-entropy single-use pairing token
- 6-digit numeric fallback code

Pairing token/code:

- expires after 5 minutes
- is single-use for establishing companion membership
- numeric attempts are rate-limited
- repeated invalid attempts are bounded
- is stored/compared safely server-side
- grants only playback-session companion capability
- never grants general server administration or filesystem access

Multiple phones may be paired with one TV playback session.

The TV can select **Disconnect phones**, which invalidates every companion grant for that session immediately.

There is no permanent trusted-device table in v1. A new playback session requires a new pairing.

### 10.3 Session lifetime

- created when the TV starts a playable episode
- active while the player is alive
- short disconnect/reconnect of the TV transport is tolerated
- ends explicitly when the TV player exits or after server-determined stale-session expiry
- ephemeral session loss after Jularr server restart is acceptable
- learning state and review data are unaffected because they use existing durable storage

### 10.4 Companion screen

The paired phone shows, without requiring manual refresh:

- anime + episode
- current Japanese sentence
- tokenized words
- reading
- meaning
- known/learning/new state
- grammar/context explanation when available
- selected word sent by TV
- Repeat line
- -10 seconds
- Play/Pause
- +10 seconds
- Open full episode learning view

If TV selects **Open on phone**, the phone opens/focuses that exact cue and, when supplied, the exact term.

### 10.5 Commands

Companion commands include:

```text
commandId
sessionId
expectedRevision
type
payload
sentAtUtc
```

The server rejects stale/inapplicable commands and broadcasts the resulting authoritative state. Duplicate `commandId` values are idempotent.

The phone never optimistically becomes the source of truth for TV playback position.

## 11. Security boundary

v1 pairing is capability-based and session-scoped.

Requirements:

- pairing tokens use cryptographically secure randomness
- numeric code validation is rate-limited
- tokens/codes are never logged
- session grants cannot call unrelated admin/settings APIs
- source filesystem paths are never returned to clients
- WebView only trusts the configured server origin
- future server authentication, if added, must integrate with this contract rather than introducing a second Android-only account system

No cloud relay is required. Phone and TV both need network access to the same Jularr server endpoint.

## 12. Versioning and compatibility

Server, phone and TV are released from the same repository and use the Jularr product version.

The native client API begins at `v1`.

Rules:

- additive changes may extend v1
- breaking client-contract changes require a new API version
- clients query capabilities at startup
- unsupported combinations fail clearly
- no HTML scraping fallback
- no hidden legacy API compatibility path outside the repository upgrade policy

Contract versions (the number in `GET /capabilities`; the route prefix stays `/api/client/v1`):

- `2` (current, minimum supported `2`): playback checkpoints carry a client-declared `completed` flag and the server no longer infers completion from a position (`PUT /episodes/{id}/progress`, `PUT /video/progress`, `POST /offline/progress`). Clients set `completed` only when playback itself reached 95% of the duration (continuous forward playback from below the threshold) or ended, or when the user marks the item watched. A seek, scrub or resume that lands at or beyond 95% is only a resume point, and after such a seek only `ended` or an explicit watched action completes it. A version 1 client never declared threshold completion, so the server reports minimum supported version 2 and the app shows its existing "update required" state; there is deliberately no server-side position-inference fallback.
- `1`: initial contract.

Additive extensions that keep version `2`: `PUT /stream-sessions/{id}/telemetry` with its answer (`advice`, `reason`, `transcodeSpeed`, `transcodeFps`), the `playbackTelemetry` feature flag, the optional `plan.buffer` object and the `Retry-After` header of playback refusals (see 5.0). A client that ignores them keeps working unchanged.

Additive extensions of contract version 2 (Instant Play, `docs/mockups/instant-play/SPEC.md`; clients read `features.playbackEnabled` and `features.playbackIntents` from `GET /capabilities`):

- `POST /video/playback-intents` with `{ "target": { "workId": "…", "workEpisodeId": "…" | null } }` starts an explicit playback intent. A Movie targets its Work; a Series targets one episode, or no episode for its next required episode (no history: the first aired episode; resumable episode: that one; completed through N: the next after N). The answer is `{ "outcome", "target", "requestId", "acquisition" }` with `outcome` one of `play_now` (open the player for `target`), `acquiring` (an approved request is getting `target`, created or already active), `awaiting_approval` (an equivalent request waits for approval and is not bypassed), `request_required` (the profile may not acquire instantly: show the Request action) , `limit_reached` (the profile already waits for three titles it started; wait for one) or `not_available`. A missing unit is acquired only when the profile may request and is auto-approved, acquisition is ready and the instance plays; it uses the one canonical request with the smallest scope (a Movie, or exactly the target episode; never the whole Series or future monitoring) and an equivalent open request is reused, never duplicated. Idempotent: a repeat or a concurrent intent attaches to the same request. A card tap never sends an intent; there is no server "stop waiting": a client stops polling, and the request is cancelled only through the normal Request action. `404 video_target_not_found` for an unknown or hidden target, an episode that is not one of the Work, or a Work that is not a Movie or Series. Rate limited per account (authentication runs before the limiter, so the partition is the account, not the address); a body over 1 KiB is refused with 413 before it is read. An Admin who unchecked an episode or turned monitoring off is not overridden: such a target is `not_available`.
- `GET /requests/{requestId}?workEpisodeId=…` reads the consumer projection of a request for one unit: `{ "requestId", "target", "acquisition": { "state", "mediaUnit", "progressPercent", "isMonitoring" } }`. `state` is `waiting_for_approval`, `looking_for_media`, `getting_media`, `preparing`, `ready_to_watch`, `available`, `monitoring_future_releases`, `not_available_yet`, `not_available` (the request ended with nothing playable), `needs_attention` or `rejected`; `mediaUnit` (`episode`, `movie`, `media`) names what `getting_media` is getting; `progressPercent` is present only while the transfer reports a trustworthy total, never estimated; `isMonitoring` coexists with `ready_to_watch` and `available`. `starting_playback` is a client-side transient shown after `ready_to_watch` on an instance that plays. The projection never carries provider, release, score, download-client, job, path or error details. `404 request_not_found` unless the profile made the request, manages requests or may request that media type, and for a hidden media type. It stays available on a manager-only instance.
- Manager-only instance (`features.playbackEnabled` false, the Playback instance switch): every route that can serve or locate playable media answers 404 — `/video/*` (player, playback-plan, progress, subtitle-tracks, playback-intents), `/stream-sessions/*`, `/media/*`, `/episodes/*`, `/offline/media/*`, `/offline/prefetch/*`, `/offline/progress` and the video kinds (`episode`, `movie`, `tv`, `series`) of `/offline-media/*`; books, manga and audiobooks (`/offline-library/*`, the other `/offline-media` kinds) are unaffected, as are the companion `/playback-sessions/*` (state only). Clients show Request and `available` states and no player. A test enumerates the real route table so a new route must be classified as streaming (gated) or not.

The Git tag version is used in:

- server package
- phone APK metadata
- TV APK metadata
- GitHub release asset names

## 13. GitHub CI and release artifacts

Android workflow path scope:

```text
clients/android/**
design/player/**
.github/workflows/android.yml
```

Pull request validation:

1. Gradle wrapper validation
2. Kotlin/Android compile
3. lint
4. unit tests
5. phone debug APK
6. TV debug APK

Debug APKs are workflow artifacts only.

Release workflow on Jularr release tag:

1. build server as today
2. build phone release APK
3. build TV release APK
4. sign both with the stable Jularr Android release key
5. verify signatures
6. calculate SHA-256
7. attach APKs/checksums to the same GitHub Release
8. optionally produce AABs for future store distribution

Asset names:

```text
Jularr-Mobile-<version>.apk
Jularr-Mobile-<version>.apk.sha256
Jularr-TV-<version>.apk
Jularr-TV-<version>.apk.sha256
```

Release secrets:

```text
ANDROID_KEYSTORE_BASE64
ANDROID_KEYSTORE_PASSWORD
ANDROID_KEY_ALIAS
ANDROID_KEY_PASSWORD
```

They belong in a protected GitHub release environment/secrets store. Never commit the keystore or decoded key.

Both apps use the same signing identity unless a future store requirement explicitly requires separation.

## 14. Tests required before first APK release

### Server contract

- API compatibility/capabilities
- player bootstrap does not leak filesystem paths
- Range direct content behavior
- HLS fallback creation/cleanup
- cue identity and timing
- pairing token/code expiry
- pairing rate limiting
- companion session authorization
- revision monotonicity
- duplicate command idempotency
- stale command rejection

### Shared Android

- direct-play capability selection
- fallback only on supported failure classes
- fallback restores playback position
- audio/subtitle choice restoration
- cue synchronization
- shared player action semantics

### Phone

- WebView origin restriction
- Play route opens native player
- returning restores WebView navigation state
- subtitle tap opens learning
- external URL leaves WebView

### TV

- D-pad focus path has no traps
- transport remote keys work
- Learn this line works without touch input
- word navigation works with left/right/OK/Back
- QR/manual pairing
- Open on phone sends exact cue/term
- MediaSession state follows player

### End-to-end

At least these real-media scenarios:

1. browser direct-compatible H.264
2. Android direct-compatible MKV/H.264
3. Android device-direct HEVC where hardware reports support
4. unsupported source -> HLS server fallback
5. external Japanese subtitle
6. embedded Japanese text subtitle
7. generated transcription subtitle
8. TV + phone paired while seeking and changing cues

## 15. Delivery slices

Implementation should remain mergeable and testable in these slices:

### Slice A — contract and design foundation

- `/api/client/v1` capabilities/player/cues
- canonical player actions
- `design/player` tokens/icons
- no Android app required yet

### Slice B — Android build foundation

- Gradle multi-module structure
- phone + TV hello/shell
- shared API/model/design modules
- GitHub PR build artifacts

### Slice C — phone native playback

- WebView shell
- episode/detail navigation remains in the WebView
- explicit native Play-route interception
- Media3 direct play
- HLS fallback
- native learning subtitle overlay

### Slice D — web player alignment

- web consumes canonical player design/action contract
- visual/semantic drift removed
- existing browser behavior preserved

### Slice E — TV

- native library/detail shell
- Compose for TV focus behavior
- Media3 + MediaSession
- shared player visual language
- TV subtitle/learning overlay

### Slice F — companion

- PlaybackSession server model
- SignalR hub
- QR/numeric pairing
- phone Companion page
- remote commands
- Open on phone

### Slice G — signed release

- signing environment
- signed APKs/checksums
- device acceptance tests
- release documentation

Do not start with two complete native apps before the shared contract/player foundation exists.

## 16. Explicit non-goals for v1

The first Android/TV milestone does **not** include:

- a second native implementation of phone Library/Learn/Settings
- WebView as the TV shell
- Bluetooth pairing
- Chromecast as the primary playback architecture
- cloud relay outside the configured Jularr server
- permanent trusted-device accounts just for pairing
- offline anime downloads (added afterwards as the bounded, explicit phone feature in §8.2)
- copying source media into app storage outside that explicit download feature
- client-side vocabulary database
- server filesystem paths in APIs
- mandatory server transcoding for Android-capable formats

## 17. Definition of done

The Android/TV milestone is complete only when all are true:

- browser Jularr still works independently
- phone non-player UI is the existing web frontend
- phone player is native Media3
- TV shell/player is native and remote-first
- supported Android media Direct Plays using device decoding
- unsupported media falls back automatically and keeps position
- web/phone/TV player has the same visual/action language
- phone/web subtitle learning is interactive
- TV subtitle learning is usable with D-pad
- TV can send the exact line/word to a paired phone
- paired phone follows the TV's active cue live
- paired phone can issue the defined playback/repeat commands
- pairing is session-scoped and expires
- original media remains read-only
- release workflow publishes signed phone and TV APKs with checksums
- required contract/player/pairing/device tests pass

## 18. Platform references

Architecture choices are aligned with current official Android guidance:

- AndroidX Media3 / ExoPlayer: https://developer.android.com/media/media3/exoplayer/hello-world
- Compose for TV: https://developer.android.com/training/tv/playback/compose
- Android TV app setup: https://developer.android.com/training/tv/get-started/create
- TV playback / MediaSession guidance: https://developer.android.com/training/tv/playback
- WebView application content: https://developer.android.com/develop/ui/views/layout/webapps/embed-web-content-in-app
