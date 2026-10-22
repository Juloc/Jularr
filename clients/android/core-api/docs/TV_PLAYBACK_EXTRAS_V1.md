# Original TV playback extras – clean-start V1 (parallel-safe)

Branch: `feat/android-tv-playback-extras-v1`, based on PR #1468's
`feat/android-tv-v1-ui-integration`. All additions live in a **new**
`core-api/.../playbackextras/` package. No files owned by the main TV agent,
other existing files, Phone code or backend endpoints were modified.

## Verified service contracts (clean-start backend)

Source-of-truth:
- `src/Jularr.Backend/Service/User/Playback/V1/{PlaybackMarkerServices,TrickplayServices,PlaybackServices,PlaybackPreferenceServices}.cs`
- `src/Jularr.Backend/Data/User/Playback/V1/{PlaybackMarkers,Playback,PlaybackPreferences}.cs`
- `src/Jularr.Backend/Data/Enums/Playback/{MediaMarkerKind,SkipMode,SubtitleMode}.cs`
- `src/Jularr.Backend/Logic/Playback/PlaybackTracks.cs`
- `src/Jularr.Backend/Data/Fields/Playback/{TrickplayFields,PlaybackPreferenceFields}.cs`

All routes require the **Library + Playback** modules and `profile.playback` permission.
Trickplay routes require the **Trickplay** module additionally.

| Feature | Actual endpoint | Response |
| --- | --- | --- |
| Skip intro/credits/recap/preview | GET `/api/v1/playback/assets/{mediaAssetId}/markers` | Bare array of `{mediaMarkerKindTypeId,startMs,endMs}` |
| Chapters | GET `/api/v1/playback/assets/{mediaAssetId}/chapters` | Bare array of `{chapterIndex,startMs,endMs,title?}` |
| Trickplay geometry | GET `/api/v1/playback/assets/{mediaAssetId}/trickplay` | `{intervalSeconds,frames,columns,rows,tileWidth,tileHeight}` |
| Sprite sheet | GET `/api/v1/playback/assets/{mediaAssetId}/trickplay/{sheet}` | **JPEG bytes** (`image/jpeg`), sheet number is **1-based** |
| Audio/Subtitle track inventory | GET `/api/v1/playback/assets/{mediaAssetId}/tracks` | Bare array of track DTOs |
| Embedded / external subtitles | GET `/api/v1/playback/assets/{mediaAssetId}/subtitles/{key}` | **WebVTT text**, not JSON |
| Profile preferences | GET `/api/v1/playback/preferences` | Preference object |
| Save preferences | PUT `/api/v1/playback/preferences` | `{"success":true}` |
| Change speed only | PUT `/api/v1/playback/preferences/speed` | `{"success":true}` |

An external subtitle is a real sidecar `.srt/.vtt/.ass/.ssa` stored with the
media. No provider search or arbitrary subtitle URL fetch API exists in this contract.

Marker kinds: `1=Intro`, `2=Credits`, `3=Recap`, `4=Preview`.
Skip modes: `1=Off`, `2=Button`, `3=Automatic`.
Subtitle modes: `1=Off`, `2=ForcedOnly`, `3=Always`, `4=WhenForeign`.
Tracks: `2=Audio`, `3=Subtitle`. Keys are `sN` (media stream index)
or `fN` (sidecar StoredFileId); neither is a MediaAssetId.
Response fields include `key,mediaTrackTypeId,language,title,channels,
isDefault,isForced,isSupported,isSelected`. Some fields are nullable;
unsupported tracks must stay unsupported.

Trickplay sheets have `columns*rows` tiles, row-major. The helper
`TvV1Trickplay.previewAt(positionMs)` returns a **1-based sheetIndex** and
pixel crop rectangle. The JPEG is fetched independently with bearer auth and
must be decoded/cropped by the UI/Android bitmap code. If metadata/sheets are
missing, the backend returns a failure (often 404); do not synthesize images.

Profile preferences include nullable `audioLanguage/subtitleLanguage`,
`subtitleModeTypeId`, `speed` (0.25..4.00, two decimals),
`autoplayNext`, intro/outro skip modes, `autoplayPreviews`,
and **read-only** `profileLanguage`/`deviceQuality`. The save DTO omits
those two read-only fields. The server chooses defaults and language fallbacks;
the client must not fabricate profile preferences.

## Wiring to original TV player (owner: main TV integration agent)

```kotlin
import de.juloc.jularr.core.api.playbackextras.*

val extras: TvV1PlaybackExtrasClient = HttpTvV1PlaybackExtrasClient(
    origin = serverOrigin,
    tokens = sharedDeviceTokenStore, // exact same object as TV pairing/playback client
)
val markers = extras.markers(realMediaAssetId)
val chapterMenu = extras.chapters(realMediaAssetId)
val tracks = extras.tracks(realMediaAssetId)
val prefs = extras.preferences()
// To choose an alternate, supported audio track, start a NEW real session:
// val track = tracks.single { it.key == selectedAudioKey && it.isAudio && it.isSupported }
// val newPlan = extras.startWithAudioTrack(mediaAssetId, deviceCapabilities, track)
// Main agent closes old session, then passes newPlan to its existing Media3 host.
```

- For the existing Skip button, map the server's start/end milliseconds and
  marker kind to the existing TV player's `ClientMediaSegment` equivalent.
  Do not override the user's Off/Button/Automatic preferences. A skip action is
  still a Media3 seek to the real marker's `endMs`; automatic skipping needs
  client-side UI logic (not a server push).
- For the seek-preview panel, request `trickplay(assetId)`, then
  `trickplaySheet(assetId, tile.sheetIndex)`, decode the returned JPEG and
  crop `x,y,width,height`. Cache actual sheet bytes, **not fabricated frames**.
  If the Trickplay module or the file's extracted images are missing, disable
  the preview while retaining ordinary seeking.
- For Audio selection, `tracks(assetId)` supplies real `sN` audio stream
  keys. The backend's existing POST `/api/v1/playback/assets/{mediaAssetId}/sessions`
  accepts `audioStreamIndex` (not the track key). This PR now provides the
  independent `startWithAudioTrack(mediaAssetId, capabilities, audioTrack, quality?)`
  operation. It only accepts a **supported embedded audio track from the server**,
  sends the actual stream index, and validates the returned session URL, mode,
  and chosen index before handing it to the existing player.
  **The main agent must end/report progress for the previous session and
  switch Media3 to the returned session plan.** A non-default audio track may
  require FFmpeg conversion; the server can legitimately reject an unavailable
  conversion. This PR does not modify the shared `HttpTvPlaybackV1Client` or
  create an automatic mid-stream switch.
- For subtitle selection, use the server-reported `isSupported` and
  `isSelected` flags. For keys `sN` or `fN`, fetch
  `subtitleVtt(assetId, key)` and attach the real `text/vtt` document
  as a Media3 sidecar media item/track using the same authenticated session.
  The server can convert certain textual formats using FFmpeg; no API supports
  image subtitle bitmap overlays/burn-in today.
- For settings, `preferences()` reads current profile defaults;
  `savePreferences(prefs.editable().copy(...))` updates all editable fields,
  and `saveSpeed(speed)` changes only speed. Reading preferences must not
  automatically POST/PUT defaults.

## Supported and missing functionality

| Original TV player feature | Backend status | Integration notes |
| --- | --- | --- |
| Intro / recap / credits / preview skip intervals | Supported if stored markers exist | `GET /markers`; only those real intervals, no AI detection added |
| Chapters | Supported if file chapters exist | `GET /chapters`; null titles retained |
| Seek previews and sprite thumbnails | Supported if Trickplay module enabled and sprites generated | Metadata + authenticated JPEG sheets |
| Audio choices | Fully represented by tracks plus explicit audioStreamIndex session start | Server may require conversion; live in-place switch is **not** supported. Main agent must end old session, start new and replace Media3 media item |
| Embedded and sidecar text subtitles | Supported subject to codec, FFmpeg and file availability | WebVTT conversion endpoint, real `isSupported` flag |
| Image subtitles such as PGS | **Not supported** by current playback plan | Server lists tracks as unsupported; no image-subtitle rendering/burn-in |
| Preferred audio/subtitle languages & subtitle modes | Supported | Profile preferences and server track selection |
| Speed, autoplay-next, intro/outro mode, autoplay-previews | Supported as stored preferences | Actual UI automation/player action is client responsibility |
| Fetch subtitle files from remote Internet subtitle providers | **Not supported** in this API | Only sidecars already stored with the media |
| Original TV learning-word subtitle cues/interactive vocabulary | **Not covered** by playback extras APIs | Do not infer cue IDs/tokens from plain WebVTT; discuss any needed API separately |

## Safety and tests

The adapter rejects invalid asset IDs, subtitle path traversal, redirects
and unexpected binary/text media types. JPEG and VTT responses have bounded
buffers. No bearer is exposed in a URL. Requests use the **shared**
`DeviceTokenStore` and compare the token sent with the current one before
applying rotation/401. A late unauthorized response retries once only with a
new valid token. No separate persistence or session state.

Added JSON-contract, route, authenticated HTTP, 401 rotation/concurrency and
redirect-leak prevention tests. Android GitHub Actions must pass on the final
head before claiming verified build/test success; no TV-device or live media
playback testing is claimed by this independent adapter PR.
