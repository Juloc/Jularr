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
- A native settings popover has a stable size, fixed header/bottom tabs and one
  scrolling body. Basic links to Audio, Subtitles and Playback; Info uses actual
  delivery and decoded picture size. Current playback is the only settings scope.
  Existing speed presets and one WebVTT subtitle track remain supported.
- The page composes a sidebar using authorized Work details and the current
  season's existing episode service. Compact rows reuse `EpisodeCard`; the current
  episode stays accent-colored. Wide/fullscreen/narrow views use the same sidebar
  in a native popover, not a second list or another media session.
- Popovers retain light/dark tokens; only their panel surface has the approved
  slight transparency. No blurred/glass controls, new dependency or database change.

## Explicitly separate

Profile/series/media-type/admin persistence, expanded settings/preview transfer,
dual subtitles and advanced timing/styling, quality/version selection, chapter and
skip markers, configurable autoplay, casting, audio waveform/processing, touch
gestures and physical TV/Safari validation are not implemented by this appearance
package. The full interview and #1228 retain that target; no inactive placeholder
buttons imply those features exist.

## Focused verification

TypeScript/assets, UI resources and focused progress/navigation tests; browser
`FilmPathTests` covers settings, seek release, views, responsive bounds and the
same video through mini/expand. `TracksTests` covers real audio switching,
WebVTT on/off and a broken file with isolated databases and local FFmpeg fixtures.
