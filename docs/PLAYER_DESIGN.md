# Player design transfer

The owner approved transferring the UiDesigner Player appearance with existing
production functions first, keeping scoped preference persistence separate.
The full interview remains the target, not a capability claim:
[`62a2e1db:PLAYER_DESIGN.md`](https://github.com/Juloc/Jularr/blob/62a2e1db/docs/PLAYER_DESIGN.md).
The design source is UiDesigner's `Projects/Jularr/Player/PlayerDesign.cs` and
`PlayerDesign.entry.scss`; sample transport and in-memory profile scopes are not copied.

## This package

- One canonical `UiPlayer` and video/session. Normal, device-local remembered Wide
  and native Fullscreen; the existing minimized bar retains the same video.
- Existing services own plans, direct play/HLS, authorized tracks, trickplay and
  acknowledged progress. Back minimizes, Stop remains separate. Previous/Next
  retain their supplied targets and the current autoplay behavior.
- Volume left, existing transport in the middle, subtitle toggle/settings/view
  controls right. The filled rounded Play icon and accent timeline use the shared
  theme. Dragging previews the range position; only release commits seeking.
- A native settings popover is 400px wide and at most 460px high, lightly translucent, CSS-anchored to the timeline's right edge 8px above it (a full screen on small viewports) and
  never scrolls: fixed header, one pane of compact form fields, bottom tabs. Basic holds Audio, Subtitles, Speed and Quality; Audio and Subtitles repeat their field; Playback has
  Quality, Speed (slider 0.25 to 4 with the usual speeds as dots and a number), Fit/Fill/Original, autoplay and the intro/credits skip choice; Info lists delivery, picture size, audio and quality.
  Everything except the speed applies to the current playback only; the profile keeps speeds from 0.25 to 4. Existing WebVTT subtitles remain supported.
- Episodes (where the list is not beside the picture: Wide, Fullscreen, narrow) is the first button of the bottom bar's right group; a file with chapters adds a Chapters button beside it
  that opens the chapter list, and ticks on the timeline seek to them. A Skip button (Skip intro, Skip credits and so on) sits above the timeline's right edge while playback is inside a marked stretch;
  where the profile chose Automatic the player jumps on arrival and shows a short note with Undo, and credits that run to the end of the file open the next episode after that note when autoplay is on.
- Hovering or dragging the timeline always names the target time and the chapter there; thumbnails are added where the server rendered them. The chapter playing is named under the
  timeline (not on a phone, where the picture is low: there the preview names it while dragging). A mouse leaving the picture hides the controls at once. The loading ring ends as soon as frames are ready, and a dropped connection or broken segment of a conversion is retried in place up to five times before the player reports a failure.
- Touch: a single tap shows or hides the controls, a double tap on the left or right third seeks back 10 or forward 30 seconds (more taps add up) and lights up the tapped half with a rounded inner edge, arrows and the total, a swipe up or down on the right half changes the volume and on the left half the picture's brightness (a filter on the video, not the device; the bottom is Auto, no filter), both shown as a pill with icon, bar and level at the top centre, and holding plays at 2x until release (pill "2x" at the top centre). A double tap never toggles fullscreen; only a double click of the mouse does.
- The playback is published as media (Media Session): title, context and poster, play and pause, seek back and forward, previous and next episode and the position, so the Android notification, the lock screen and headset keys control it; leaving the app with the video running may enter Picture in Picture where the browser allows it.
- Chromecast: a Cast button (Chrome on a secure page) loads Google's sender library only when pressed and plays to Google's default receiver (no receiver of our own, no registration). The device cannot sign in, so every item
  has an HLS address of its own playback session (H.264 and AAC), signed and valid up to 12 hours, readable by that device from another origin and nowhere else; the subtitles of a session hang below it (`/api/v1/cast/{token}/subtitles/{key}`, WebVTT, same signature).
  A loopback address is refused, a device cannot reach it.
  - Queue: the device gets the episode and the next three of the player's own next rule (a movie is a queue of one) as a Cast queue, each with a session made up front (a session costs no conversion before its first segment,
    and its time counts from the moment its item starts: `UpdatePlaybackV1` with `isStarted`). While the page is open the queue is filled up to three items after the one that plays, whenever the device moves on, and a
    session the server has no room for leaves the item out until the next change. The device goes on by itself, also with the page closed (then no place is saved: every position goes through the page).
    Previous and Next of the page, of the TV remote and of Google Home move along the queue; the page's names, buttons, episode list and track fields follow the item that plays. Where the profile turned autoplay off, every item after the first
    waits at its start on the device. The device advances at once at the end of an item: the receiver has no countdown, the ten seconds exist only locally.
  - Progress: each item saves its place on its own session; an item that ends within 15 seconds of its end counts as watched and ends its session, one left earlier keeps its place and its session; the next item starts at its saved place.
    Going back to an item that finished makes it again on a new session. When the device lets go every session ends and the page goes on locally with the item and place the device had reached (another episode opens its player page).
  - Extras: the volume slider, the mute button, the keys and the touch gesture drive the device's volume (`RemotePlayerController`). A subtitle chosen in the settings is shown on the device as a side-loaded WebVTT text track; the next episodes take the language
    (a forced track only where nothing else has it). Another audio track is another conversion: the item is made again at the same place and the next episodes follow the language. Translated subtitles, ASS styling and the subtitle and audio delay are not carried over.
    What the default receiver really does with the text tracks, the queue's `autoplay` and the end of the queue is unverified on a device; see the PR's checklist. Books and music are not cast. `CastQueueTests` and `cast.test.mjs` cover it with a fake sender.
- Info (for the curious, still one fixed pane without scrolling): the delivery, the picture, container and file bit rate, the video (codec, size, bit depth, dynamic range) and audio (codec, channels, language) of the file as probed, what the server does with each (copy or convert, encoder, target height, bit rates), and values measured every second while the pane is open: frames decoded and dropped with the measured rate, buffer ahead, stream codecs, bandwidth estimate and the last segment's load time. A button copies it all as text. The server gives the file's facts through the session's `/facts` address.
- Audio delay and subtitle delay (milliseconds, later with a plus, earlier with a minus, 10 seconds either way, number field and Reset in the Audio and Subtitles tabs; for this playback only). Subtitles shift in the browser by adding the offset to every cue's own times, so moving it again never adds up. The audio is moved by the server: a playback with an audio delay always runs through a conversion, the audio is encoded as AAC with a silent start (later) or without its first moments (earlier), counted from where the conversion starts so a seek keeps the shift; changing it asks for a new conversion that plays on from the same place.
- Fullscreen belongs to the page's main element: choosing the next episode (button, countdown or credits) replaces the player inside it, so fullscreen and the page stay; outside fullscreen the page changes as before. On a phone fullscreen also turns to landscape and leaving releases it, where the browser allows it (Android; iOS does not).
- The speaker shows three, two, one or no wave; the timeline is white-thumbed, played in the accent, buffered more opaque.
- The page composes a sidebar using authorized Work details and the current
  season's existing episode service. Compact rows reuse `EpisodeCard`; the current
  episode stays accent-colored. Wide/fullscreen/narrow views use the same sidebar
  in a native popover, not a second list or another media session.
- Popovers retain light/dark tokens; only their panel surface has the approved
  slight transparency. No blurred/glass controls, new dependency or database change.

## Explicitly separate

Series/media-type/admin persistence, expanded settings/preview transfer,
dual subtitles and advanced timing/styling, version selection, audio waveform/processing, touch
gestures and physical TV/Safari validation are not implemented by this appearance
package. The profile's audio and subtitle choice, speed and autoplay of the next
episode (with a countdown) and each device's quality are stored by their own
package and read by the player. The full interview and #1228 retain that target; no inactive placeholder
buttons imply those features exist.

## Focused verification

TypeScript/assets, UI resources and focused progress/navigation tests; browser
`FilmPathTests` covers settings, seek release, views, responsive bounds and the
same video through mini/expand. `TracksTests` covers real audio switching,
WebVTT on/off, chapters and skip, the fit of every settings pane (no scrolling, 1280x720 and 390x844, light and dark, Wide and Fullscreen), Episodes first and a broken file with isolated databases and local FFmpeg fixtures.
