# Player design decisions

Owner interview, 15 October 2026. These decisions supersede conflicting historical
mockups, including centered over-video Desktop transport. This is a target contract,
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

## Settings and defaults

- Settings popover: Audio, Subtitles, Playback, Info. Opening it preserves Play/Pause.
- Explicit user changes are remembered for the Work. Fallback priority is Work,
  media-type preferences, general profile preferences. A changed-defaults button
  saves only conscious changes across tabs for that media type; automatic selection
  and runtime recovery never rewrite preferences. Track IDs/offsets need valid file/track scope.
- Audio uses ordered preferred languages, then original language, then file default;
  a saved Work choice wins. Quality offers Automatic, Original and valid server
  presets labeled with resolution/bitrate. PlaybackPlan still owns delivery decisions.
- Multiple cuts/versions are selectable before start and under Playback; changes
  preserve logical position and compatible tracks where possible.
- Speed has common presets and a fine slider; preserve pitch where supported.
- Audio and each subtitle offset have earlier/later millisecond steps, numeric input
  and reset to zero. Seeking must not accumulate an offset again.
- Autoplay defaults On with a cancellable 10-second countdown; immediate or Off are
  options. Intro/recap/outro default to a Skip button; automatic skipping is optional
  per category and depends on authoritative segment markers.
- Sleep timer defaults Off: 15/30/60 minutes, custom duration or end of item/chapter.
- Info contains concise actual playback facts and expandable reasons/diagnostics,
  not permanent technical overlays or user-facing forced-transcode switches.

## Subtitles

- Two independent tracks, selected by ordered profile language preferences; Work
  changes override them. Default behavior is Forced matching audio language, otherwise Off;
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
shared controls, four settings tabs, sample caption styling, local changed-defaults,
sidebar and seeking. All sample values are clearly labeled. No playback/profile/
translation/casting API is called and no browser-stored preference is claimed as
a persisted profile preference. Controls are local presentation examples, not service forms.

Actual preference persistence, cue/ASS rendering and synchronization, translation,
handoff, waveform generation, runtime recovery, automatic background/PiP and the
complete Wide/Fullscreen/Mobile/TV/mini-player designs remain implementation work.
