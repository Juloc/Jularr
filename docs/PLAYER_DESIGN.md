# Player design decisions

Owner interviews, 15–16 October 2026. Only choices confirmed by the owner are
decisions; the supplied external audit is inspiration, not an approved specification.
These decisions supersede conflicting historical mockups, centered over-video
Desktop transport and the earlier changed-defaults Save button. This is a target contract,
**not a statement that the features below are implemented**. Older functional context:
[Player spec](https://github.com/Juloc/Jularr/blob/dev/docs/mockups/player/SPEC.md),
[subtitles #899](https://github.com/Juloc/Jularr/issues/899),
[lifecycle #855](https://github.com/Juloc/Jularr/issues/855),
[Connect #856](https://github.com/Juloc/Jularr/issues/856).

## Layout and navigation

- Desktop has Normal, Wide/Theater and Fullscreen modes. Normal shows a right
  sidebar with Episodes/Details tabs and season selection; the other modes expose
  this content as a popover. Remember Normal/Wide per device, never force fullscreen.
- Desktop bottom: mute/volume left; Previous, -10, Play/Pause, +30, Next centered;
  subtitle On/Off, Settings, Normal/Theater and Fullscreen right. Cast is top-right.
- The subtitle button switches the configured tracks together. Their settings
  live under Subtitles, not in another track-selection popup.
- Hide controls after three seconds without interaction; keep them visible during
  interaction, keyboard focus or an open settings popover.
- Episode rows remain compact: slightly larger, consistent preview images on the
  left, episode number/title/duration beside them and a thin progress bar. Keep the
  current episode-selection treatment; its Play icon becomes filled with rounded corners.
- Chapters have a collapsible thumbnail strip below video, a panel in Wide/Fullscreen,
  and timeline dots. Real scene markers may add dots; preview frames are not scenes.
- The seek handle moves continuously across the full duration. Hover and drag show
  a small target-time/image/chapter preview at the cursor. Playback continues at
  the old position until release commits the seek; it does not pause or seek per tick.
- Back returns to the previous page with playback in the mini-player. Its bar and
  floating-window presentations share the same media element/session; remember
  the chosen presentation per device. Stop is separate from minimize and PiP.
- Mobile portrait: video above Episodes/Chapters/Details tabs. Touch transport is
  Previous, Play/Pause, Next; double-tap left/right seeks -10/+30. Vertical swipes
  right adjust volume, left brightness, only where genuinely supported. Brief
  feedback, no permanent gesture UI. Settings offers a control lock with visible unlock.
- TV: Previous, Play/Pause, Next; seeking through a focusable timeline with previews,
  not duplicate seek buttons. Settings remain accessible by remote.

## Compact settings popover

- Basic opens first; the tabs are Basic, Audio, Subtitles, Playback and Info.
  The scope selector sits at the top, the content in the middle, tabs at the bottom.
  Tabs meet the rounded popover frame directly, with a subtle background and no
  separately bordered capsule. No repeated Player settings heading.
- Keep a compact, stable outer size across tabs, including Info. The header and
  tab bar remain fixed; only content may scroll, never nested content and outer scrollbars.
  Keep useful Audio options without routine scrolling. If space is insufficient,
  switch to a screen-filling settings view instead of shrinking controls; playback
  continues behind it and closing returns directly to the same running Player.
- Close and Expand sit together; Close matches the tab height. Expand opens the
  complete settings page, not browser/video fullscreen. The bottom-right Player
  controls retain their white circular surfaces. Opening settings preserves Play/Pause.

| Tab | Compact content |
| --- | --- |
| Basic | Three compact selects: audio, subtitle 1, subtitle 2 (subtitles include Off); speed and quality. |
| Audio | Track, offset, language mode (preferred or original), audio-description preference. Edit the language order and advanced processing on the full settings page. |
| Subtitles | Both tracks with independent offsets; shared font size and bottom distance. Detailed styling and drift correction stay on the full page. |
| Playback | Quality, speed, next-episode behavior, intro/recap/outro skipping; a segmented picture-format control. Sleep and other advanced options stay on the full page. |
| Info | Actual resolution, video/audio codecs, FPS, HDR, bitrate and Direct Play/Remux/Transcode, with a concise conversion reason when applicable. Full diagnostics stay on the full page. |

- Speed uses a fine slider from 0.25× to 4× in 0.05 increments, clickable preset
  points at 0.25/0.5/0.75/1/1.25/1.5/2/3/4×, and an editable numeric value beside it.
- Quality remains one select: Automatic, Original and actual available presets
  labeled with resolution and bitrate.
- Picture format is three direct segments: Fit (whole image, bars when needed),
  Fill (crop edges when needed), Original size (downscale to fit but never upscale
  beyond source dimensions). Advanced Zoom is separate.

## Scope, inheritance and saving

- Scope choices: Current playback, This series (for a series/anime), This media
  type (labels such as Movies/Series/Anime), My profile, and authorized Admin file
  corrections. Final UI labels must use semantic localized resources.
- Changes apply immediately and save automatically in the explicitly selected
  scope; Current playback is temporary. No Save button. Automatic track selection
  and recovery never rewrite preferences. Report save failures, not successful saves.
- Remember the chosen personal scope while staying on the Player page, including
  closing/reopening its popover. Leaving and reopening the page resets to Current
  playback. Never automatically restore Admin scope.
- Switching scope loads its values; it does not copy or save the previous scope's
  values. Subsequent user edits affect the selected scope only.
- Personal fallback: Current playback → This series where applicable → Media type
  → Profile. Show inherited versus overridden values unobtrusively. Reset per field
  removes that override, so later upstream changes are inherited; it is not a copy.
- The popover has per-field Reset but no copy/import menu. The full settings page
  offers explicit Take values from another scope for the current tab, all personal
  settings, or selected groups. Never copy incompatible file-bound track IDs or
  Admin corrections as personal preferences. Track IDs/offsets need valid file/track scope.
- Admin is limited to permanent corrections for the specific file shared by its
  users, for example audio/subtitle timing. It is not a personal-preference scope,
  an instance-wide default or a way to overwrite everyone's playback preferences.

## Complete Player settings page

- Give settings the wider main column, with a smaller preview on the right that
  stays visible while scrolling. On narrow screens put the preview above settings;
  do not squeeze controls into two unusably narrow columns.
- One tab bar: Audio, Subtitles, Playback, Picture, Info. Within a tab use collapsible
  coherent groups, such as Appearance and Synchronization, not nested tab bars.
- Use the current playback as the embedded preview: the same media element/session
  moves from the mini-player into this surface without restarting. Normal navigation
  keeps playback alive in the mini-player. With no active playback, use a clearly
  labeled example instead. No duplicate stream/decoder just for the settings preview.
- Advanced controls belong here, not in extra popover submenus: detailed subtitle
  styling/drift correction, audio processing, frame stepping, A–B repeat, zoom,
  picture adjustment, sleep timer and detailed diagnostics.
- Frame stepping and A–B repeat live under Playback → Advanced.
- Audio output is automatic by default, with optional volume amplification and
  downmix under Audio → Advanced.
- Subtitle synchronization offers fixed offset and speed/drift correction
  independently for each track. The compact popover retains fixed offset only.
- Picture adjustment offers brightness, contrast and saturation, neutral by default
  and resettable. It does not mean changing system display brightness.

## Selection and playback behavior

- Audio uses ordered preferred languages, then original language, then file default;
  an applicable explicit saved choice wins. Quality offers Automatic, Original and valid server
  presets labeled with resolution/bitrate. PlaybackPlan still owns delivery decisions.
- Multiple cuts/versions are selectable before start and on the full settings page; changes
  preserve logical position and compatible tracks where possible.
- Preserve pitch when adjusting speed where supported.
- Audio and each subtitle offset have earlier/later millisecond steps, numeric input
  and reset to zero. Seeking must not accumulate an offset again.
- Autoplay defaults On with a cancellable 10-second countdown; immediate or Off are
  options. Intro/recap/outro default to a Skip button; automatic skipping is optional
  per category and depends on authoritative segment markers.
- Sleep timer defaults Off: 15/30/60 minutes, custom duration or end of item/chapter.
- Do not add permanent technical overlays or user-facing forced-transcode switches.

## Subtitles

- Two independent tracks, selected by ordered profile language preferences; applicable
  explicit scoped choices override them. Default behavior is Forced matching audio language, otherwise Off;
  full subtitles can be saved as the media-type default.
- Both tracks stay grouped, primary above secondary at the bottom by default.
  Authored positions/signs remain positioned; background dialogue can use the top.
- Bottom distance, maximum width, font, size, color/opacity, background, outline and
  shadow are adjustable. Shared style by default; optional second-track overrides.
- Respect ASS/SSA authorship. Personal styles normally affect dialogue; applying
  them to positioned signs is an explicit option. Real cue timing and collisions matter.
- Missing languages do not block playback. Offer authorized translation in Subtitles
  only on explicit request, prefer existing tracks and reuse derived results. No AI
  on normal playback. Dual subtitles do not require Learning; Learning stays out of scope.

## Devices, audio and recovery

- Play on opens a compact target popover. Transfer at the same position with
  compatible settings; pause local playback only once transfer succeeds. If it fails,
  local playback continues. Show only real supported/authorized targets.
- Default background behavior is Continue, automatically entering PiP when the
  platform permits it; Pause is an option. No fake PiP/brightness/volume workaround.
- Music/audiobooks reuse the Player with cover, track/chapter list and an actual
  audio-derived waveform timeline. Music supports editable queue, Play next,
  shuffle and repeat Off/Track/Queue; do not derive a waveform from random bars.
- Recovery is automatic but bounded, retains position/settings and offers Retry
  after failure. A delivery restart is not a new viewing intention or completion.

## Current prototype boundary

`/Design?pane=player` on the isolated design branch demonstrates Desktop Normal,
shared controls, Basic/Audio/Subtitles/Playback/Info, a compact bottom-tab popover,
viewport-filling fallback and an expanded settings surface with the same video element.
The compact panel uses a fixed 480 × 540 surface; small viewports fill the screen
and lock background scrolling. Expanded groups and speed controls remain compact,
and subtitle sizing scales with the smaller preview while retaining track proportions.
Scope inheritance, per-field Reset, copy operations and mirrored fields are local,
in-memory presentation examples. The expanded surface is a Development hash view,
not a connected production settings route. No playback/profile/
translation/casting API is called and no browser-stored preference is claimed as
a persisted profile preference. Controls are local presentation examples, not service forms.

Actual preference persistence, cue/ASS rendering and synchronization, translation,
handoff, waveform generation, runtime recovery, automatic background/PiP and the
complete Wide/Fullscreen/Mobile/TV/mini-player designs remain implementation work.
Real scoped persistence/authorization, production page navigation/preview transfer,
cue timing/drift correction, audio processing, frame stepping and A–B execution
remain unimplemented. Unsupported frame actions are inactive with an explanatory
notice; other advanced values demonstrate configuration, not processing.

## Remaining precision before implementation

- Verify the technical limits of speed, stepping,
  drift correction, downmix and amplification against actual playback capabilities.
  Unsupported actions must not be presented as functional or patched with workarounds.
- Scope/track compatibility and storage lifetime need real typed service contracts;
  this interview defines product behavior, not new schema or database authorization.
