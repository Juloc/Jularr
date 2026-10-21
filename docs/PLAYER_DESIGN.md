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
  Everything except the speed applies to the current playback only; the profile keeps speeds from 0.5 to 2. Existing WebVTT subtitles remain supported.
- Episodes (where the list is not beside the picture: Wide, Fullscreen, narrow) is the first button of the bottom bar's right group; a file with chapters adds a Chapters button beside it
  that opens the chapter list, and ticks on the timeline seek to them. A Skip button (Skip intro, Skip credits and so on) sits above the timeline's right edge while playback is inside a marked stretch;
  where the profile chose Automatic the player jumps on arrival and shows a short note with Undo, and credits that run to the end of the file open the next episode after that note when autoplay is on.
- The speaker shows three, two, one or no wave; the timeline is white-thumbed, played in the accent, buffered more opaque.
- The page composes a sidebar using authorized Work details and the current
  season's existing episode service. Compact rows reuse `EpisodeCard`; the current
  episode stays accent-colored. Wide/fullscreen/narrow views use the same sidebar
  in a native popover, not a second list or another media session.
- Popovers retain light/dark tokens; only their panel surface has the approved
  slight transparency. No blurred/glass controls, new dependency or database change.

## Explicitly separate

Series/media-type/admin persistence, expanded settings/preview transfer,
dual subtitles and advanced timing/styling, version selection, casting, audio waveform/processing, touch
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
