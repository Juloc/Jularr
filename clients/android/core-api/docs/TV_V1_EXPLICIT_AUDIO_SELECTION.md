# Android TV V1: choosing a different audio stream

This follow-up is deliberately separate from PR #1474 because the main TV agent
already copied its original playback-extras files into the integration branch.
This change adds **only new files** under `core-api`; it never modifies
`HttpTvPlaybackV1Client`, `TvV1PlaybackExtrasClient` or any TV UI code.

## Verified clean-start contract

`src/Jularr.Backend/Service/User/Playback/V1/PlaybackServices.cs`,
`src/Jularr.Backend/Data/User/Playback/V1/Playback.cs`,
and `src/Jularr.Backend/Logic/Playback/PlaybackTracks.cs` define:

- `GET /api/v1/playback/assets/{mediaAssetId}/tracks` returns actual
  `PlaybackTrackV1` values, including `key=s{streamIndex}`,
  `mediaTrackTypeId=2`, `isSupported`, `isSelected` and `language`.
- `POST /api/v1/playback/assets/{mediaAssetId}/sessions` accepts
  `containers`, `videoCodecs`, `audioCodecs`, optional
  `audioStreamIndex` and optional `quality`. The response is
  `PlaybackPlanV1` (session UUID, playback mode, *same-origin relative*
  `streamUrl`, resume/duration, selected audio stream, quality and reduction).
- Selecting a nondefault stream can require FFmpeg/HLS conversion. The
  backend may return an error if it cannot convert that track.

## Integration: main TV agent only

```kotlin
val tracks = extras.tracks(realMediaAssetId)
val selected = tracks.singleOrNull { it.key == requestedTrackKey && it.isAudio && it.isSupported }
    ?: error("That audio track is not supported")

val audioSessions: TvV1AudioSelectionSessionClient = HttpTvV1AudioSelectionSessionClient(
    serverOrigin,
    sharedDeviceTokenStore, // EXACT same store used for pairing and playback
)
val next = audioSessions.startWithAudioTrack(realMediaAssetId, deviceCapabilities, selected)

// The main agent must end/update the previous session, switch Media3 to
// next.streamUrl with the current bearer, and resume at the intended position.
```

This is **not** an in-place track-switch endpoint. The new adapter does not
change old player state, invoke `TvV1PlaybackHost`, or end any existing session.
The main agent owns session lifecycle, player UX, and preserving the position.

Validation prevents fake asset ids, sidecar-subtitle keys passed as audio,
unsupported tracks, invalid capabilities/quality, off-server redirects and
untrusted playback plans. Authorization uses the same shared DeviceTokenStore;
a late 401 cannot clear a later rotation, and a stale 401 retries once with
the new credential. Only real server-returned metadata and session plans
are exposed. Additional JVM contract tests exercise these cases.

This does not implement Internet subtitle search, image-subtitle burn-in,
interactive learning subtitle cues or any new backend endpoints.
