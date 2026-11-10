# Playback

Playback plans, streaming, tracks, progress and the Player.
Index: [CODEMAP](../CODEMAP.md).
Specs: [PLAYER_DESIGN](../PLAYER_DESIGN.md).

## Decisions

Owner decisions; change them only with the owner.

- Progress writes are serialized, retain only the latest pending state and become acknowledged only after a successful response.
  Retry transient failures finitely and display unsaved state.
  Unload delivery is best-effort, not guaranteed durable storage.
  Reader completion requires reaching the actual content end.
- Full Player and minimized Now Playing are presentations of the same playback session; keep the media element outside exchanged page content.

## Backend

- [PlaybackServices.cs](../../src/Jularr.Backend/Service/User/Playback/V1/PlaybackServices.cs),
  [Playback.cs](../../src/Jularr.Backend/Data/User/Playback/V1/Playback.cs): canonical playback item, capability-based PlaybackPlan with on-demand
  wake, authorized range streaming through `IFileOutput` in [ServiceApi.cs](../../src/Jularr.Backend/Service/Core/ServiceApi.cs) and progress reports;
  conversions serve a VOD playlist and on-demand segments.
  [PlaybackTranscodes.cs](../../src/Jularr.Backend/Service/User/Playback/V1/PlaybackTranscodes.cs) runs one paced FFmpeg job per session (restart on
  seek, a requested segment waits while FFmpeg runs, at most two minutes, and the player retries and keeps a minute ahead, process stop when idle or
  far ahead, cache expiry after six idle hours or at session end, job budget, software fallback) through
  [MediaTranscoder.cs](../../src/Jularr.Integrations/Logic/Media/MediaTranscoder.cs);
  [MediaOptions.cs](../../src/Jularr.Backend/Service/Core/MediaOptions.cs) holds tool paths and limits.
  [PlaybackLogic.cs](../../src/Jularr.Backend/Logic/Playback/PlaybackLogic.cs) owns direct-play and conversion decisions (another audio track always
  through a conversion; the quality of the playing device, set on `/Account/Sessions` and per playback in the player (`StartPlaybackDataV1.Quality`),
  caps what is converted: `PlaybackLogic.QualityTarget` gives Automatic and Original no cap, High 1080p / 8 Mbit/s video and 256 kbit/s music, Data
  saver 480p / 1.5 Mbit/s and 128 kbit/s, a picture or file above it is converted with `-vf scale` and `-maxrate`, and an instance limit can only
  lower the target (`PlaybackQualityTarget.LoweredBy`, used by the Transcoding ladder below); the quality is `AccountSessions.PlaybackQuality`, the
  device being its session; a browser keeps a random `jularr.device` cookie that sign-out leaves alone
  ([BrowserDevice.cs](../../src/Jularr.Backend/Service/Core/BrowserDevice.cs), stored as `AccountSessions.DeviceId`), and its next session of the same
  account starts with the quality of the latest earlier one), sessions and MediaProgress; audio and subtitle tracks (`ReadPlaybackTracksV1`: streams
  of the played file, subtitle files beside it with language and forced flag from their names, image subtitles listed as unsupported) a subtitle
  translated by the local model ([SubtitleTranslations.cs](../../src/Jularr.Backend/Service/User/Playback/V1/SubtitleTranslations.cs): background job
  per subtitle, numbered batches of cues through `OllamaLocalModels` and the shared `LocalTranslationGate`, a WebVTT file in the playback cache, track
  key `x-<language>-<source>`; rules in [SubtitleTranslationLogic.cs](../../src/Jularr.Backend/Logic/Playback/SubtitleTranslationLogic.cs); module
  `Translation` is the switch; `SubtitleTranslationTests`) and WebVTT subtitles (`ReadPlaybackSubtitleV1`, with `?styled=true` an ASS/SSA track as ASS
  text for the player's libass renderer, and the fonts a Matroska file carries (`/fonts` lists them, `/fonts/{key}` serves one;
  [PlaybackFonts.cs](../../src/Jularr.Backend/Service/User/Playback/V1/PlaybackFonts.cs) lists the attachments with ffprobe and takes each one out by
  its stream number with `-dump_attachment` into the playback cache, never by the name inside the file; at most 40 fonts of 16 MB); SubRip/WebVTT
  files directly, other text subtitles once through FFmpeg into the playback cache,
  [PlaybackSubtitles.cs](../../src/Jularr.Backend/Service/User/Playback/V1/PlaybackSubtitles.cs), rules in
  [PlaybackTracks.cs](../../src/Jularr.Backend/Logic/Playback/PlaybackTracks.cs)); Playback end-of-session cache cleanup runs only after the progress
  transaction commits; FFmpeg input for subtitles and trickplay accepts Jularr's loopback SMB streaming bridge without allowing remote HTTP sources.
  Per-profile playback choices (`ProfilePlaybackPreferences`: audio language, subtitle language and mode, speed, autoplay of the next episode,
  automatic preview of a lingering card on touch devices, keeping the screen on while playing or reading (`KeepScreenOn`, migration `0014`), a second
  subtitle language (`SecondSubtitleLanguage`, migration `0015`), which `ReadProfileV1` also reports for the shell) are read and saved by
  [PlaybackPreferenceServices.cs](../../src/Jularr.Backend/Service/User/Playback/V1/PlaybackPreferenceServices.cs) through
  [PlaybackPreferenceLogic.cs](../../src/Jularr.Backend/Logic/Playback/PlaybackPreferenceLogic.cs); the player saves its speed alone
  (`SavePlaybackSpeedV1`) and `ReadPlaybackPreferencesV1` also names the asking device's quality.
  `PlaybackTracks.ChooseAudio` / `ChooseSubtitle` pick the tracks by those choices (a subtitle by mode Off, forced only, always, or only while the
  audio is not in the subtitle language, which without a choice is the profile's interface language): `ReadPlaybackTracksV1` marks them `IsSelected`
  for the player and `StartPlaybackV1` plays the preferred audio when the client names none (plan: `AudioStreamIndex`, `Quality`).
  Tests `PlaybackPreferenceTests`, `ProgressTests`.
  [Playback.sql](../../database/Playback.sql) is the fresh baseline.
  Seek thumbnails (Trickplay module, off by default): [GenerateTrickplayV1](../../src/Jularr.Backend/Service/System/Media/V1/TrickplayServices.cs)
  renders 10-by-10 JPEG sheets of keyframes every 10 seconds into `Media:TrickplayPath` file after file
  ([TrickplayWorker.cs](../../src/Jularr.Workers/TrickplayWorker.cs); a failed or unreachable file is tried again after a day or once it changes),
  [TrickplayServices.cs](../../src/Jularr.Backend/Service/User/Playback/V1/TrickplayServices.cs) serves the layout and sheets of the played file.
  Skip markers and chapters ([Library.sql](../../database/Library.sql) `MediaChapters`, `MediaMarkers`):
  [MediaMarkers.cs](../../src/Jularr.Backend/Logic/Playback/MediaMarkers.cs) holds the rules (chapter titles such as intro, opening, OP, recap,
  credits, ending, ED, outro and preview name a kind; a sidecar `<video>.edl` of `start end action` lines in seconds, actions 4 intro, 5 credits, 6
  recap, 7 preview as Jellyfin's EDL provider writes them, at most 64 KB and 200 lines; other actions are ignored),
  [MediaMarkerLogic.cs](../../src/Jularr.Backend/Logic/Playback/MediaMarkerLogic.cs) stores them.
  The scan probe (`ffprobe -show_chapters`, [MediaProbeClient.cs](../../src/Jularr.Integrations/Logic/Media/MediaProbeClient.cs)) keeps all chapters
  and replaces the chapter and sidecar markers of a file it reads again, never the manual ones; a sidecar is read when its video is probed, so a later
  change of the `.edl` alone waits for a rescan of the video.
  A chapter stretch that cannot be one is no marker (intro or recap over five minutes; credits or preview at the start of the file or over half of it,
  as one chapter "Credits" tagged over a whole episode).
  The player also leaves out, when it reads them, markers from chapters that cannot be right and chapters of such a file
  (`MediaMarkers.UsableChapters`, `IsUsable`; one chapter alone is no list).
  One marker per kind and source; the lowest source wins (manual, sidecar, chapter, detected, which nothing writes yet).
  [PlaybackMarkerServices.cs](../../src/Jularr.Backend/Service/User/Playback/V1/PlaybackMarkerServices.cs) reads markers and chapters of the played
  file for the player; [WorkMarkerServices.cs](../../src/Jularr.Backend/Service/Admin/Media/V1/WorkMarkerServices.cs) (`media.manual.update`) sets,
  deletes (manual only) and copies markers of an episode onto every available file, or over the season with an offset.
  The profile's `IntroSkipModeTypeId` (intro and recap) and `OutroSkipModeTypeId` (credits and preview) are Off, Button or Automatic beside the other
  playback preferences.
  Tests `MediaMarkerTests`, `PlaybackPreferenceTests`, E2E `TracksTests`.
  [Playback.sql](../../database/Playback.sql) is the fresh baseline.
  Seek thumbnails (Trickplay module, off by default): [GenerateTrickplayV1](../../src/Jularr.Backend/Service/System/Media/V1/TrickplayServices.cs)
  renders 10-by-10 JPEG sheets of keyframes every 10 seconds into `Media:TrickplayPath` file after file
  ([TrickplayWorker.cs](../../src/Jularr.Workers/TrickplayWorker.cs); a failed or unreachable file is tried again after a day or once it changes),
  [TrickplayServices.cs](../../src/Jularr.Backend/Service/User/Playback/V1/TrickplayServices.cs) serves the layout and sheets of the played file.

## Frontend

- [Player](../../src/Jularr.Frontend/Pages/User/Player/Player.cshtml.cs), [chrome](../../src/Jularr.Frontend/Framework/Components/Player.cs),
  [client](../../src/Jularr.Frontend/Modules/Player/player.entry.ts): PlaybackPlan, transport, HLS and progress; the [approved appearance
  transfer](../PLAYER_DESIGN.md) has Normal/Wide/Fullscreen, current-playback settings tabs, subtitle toggle and a shared sidebar with real
  current-season episodes/Details.
  Audio changes replan at the same position, withholding transient pause progress until the new source has metadata; subtitles remain WebVTT tracks.
  Minimize/Back keeps the same video/session while the in-place navigation swaps app content; Expand returns, Stop ends it.
  Previous/Next retain their targets; the next episode follows after a ten-second countdown (Play now, Cancel) unless the profile turned autoplay off,
  an album's next track at once.
  The profile's audio, subtitle and remembered speed come with the page, the settings are native form fields (Audio and Subtitles selects, speed
  slider 0.25 to 4 with presets and a number, Quality, Fit, autoplay and the two skip choices for this playback; the profile keeps the speed from 0.5
  to 2); Quality is this playback's own (the device's choice is current; another one plans the same video again at the same place, nothing is saved).
  `FilmPathTests` and `TracksTests` cover playback, settings, seek release, views, tracks and mini/expand.
  Chromecast: [Cast.ts](../../src/Jularr.Frontend/Modules/Player/Cast.ts) (Google's sender, queue items, text tracks, volume) and
  [CastQueue.ts](../../src/Jularr.Frontend/Modules/Player/CastQueue.ts) (one session per queued episode, refill, progress per item, language of
  tracks), the queue's items come from `?handler=Queue` of the player page
  ([Player.cshtml.cs](../../src/Jularr.Frontend/Pages/User/Player/Player.cshtml.cs)), the signed addresses from
  [PlaybackCast.cs](../../src/Jularr.Backend/Service/User/Playback/V1/PlaybackCast.cs); `Tools/cast.test.mjs` and E2E `CastQueueTests` (fake sender).
  Skip markers and chapters: the page carries the played file's markers (`data-markers`) and the profile's two skip choices,
  [Skip.ts](../../src/Jularr.Frontend/Modules/Player/Skip.ts) holds the rules (`skip.test.mjs`): a Skip button while playback is inside a stretch (it
  stays six seconds, then returns only while the controls are shown and the stretch lasts), Automatic jumps once on arrival with an Undo note, credits
  to the end of the file open the next episode when autoplay is on; the chapters have ticks on the timeline and their own list (the button beside
  Episodes, anchored like the settings) that marks the current one and seeks.
  The settings, chapters, skip offers and the countdown are CSS-anchored above the timeline's right edge; `TracksTests` proves that no settings pane
  scrolls (light/dark, normal/wide/fullscreen, phone).
  A stopped playback repairs itself: HLS faults are retried in place, then the same video is planned again at the same position as a fresh session
  after a growing pause ([Recovery.ts](../../src/Jularr.Frontend/Modules/Player/Recovery.ts), `recovery.test.mjs`), and never gives up (a picture that
  stands still without an error is nudged after twenty seconds and planned again after sixty): after six tries in a row the failure message shows as a
  note while the tries go on, and a session that plays again takes it away.
  ASS and SSA subtitles (tracks marked `IsStyled`, fetched as `?styled=true`) are drawn by libass through JASSUB (the Subtitles tab has a device-local
  switch, `libassWanted` in Device.ts: on by default for a pointer device, off for a touch device)
  ([StyledSubtitles.ts](../../src/Jularr.Frontend/Modules/Player/StyledSubtitles.ts), loaded only for such a track; WebVTT remains the fallback and
  for every other format; the fonts the file carries are asked for once (`/fonts`) and given to libass, the default font only stands in for the rest).
  Device arrangement ([Device.ts](../../src/Jularr.Frontend/Modules/Player/Device.ts), `device.test.mjs`): a touch screen with a short side under 600
  px is a phone, whatever the picture's width, so fullscreen keeps it (classes `player--touch`, `player--phone`): the transport floats over the middle
  with room between its five buttons, no volume and no Theater (Theater is also hidden in fullscreen everywhere), Episodes and Chapters stand where
  the volume was, Picture in Picture sits at the top beside Cast; a message (small, at the top, never clickable on touch) goes away after 8 s; a jump
  by button, double tap, timeline or key moves the position at once, playing or paused, and shows the loading ring.
  On a touch device the player offers no Matroska direct play (HEVC in MKV gave sound without a picture on a phone), takes HEVC only where the media
  source does, and keeps ASS subtitles as plain WebVTT lines (libass is for pointer devices until it is proven steady on phones).
  A second subtitle language (the profile's `SecondSubtitleLanguage`, or the Second subtitle choice in the settings; `Subtitles.ts`,
  `subtitles.test.mjs`) runs as a second native WebVTT track four lines above the first, only while the first subtitle is on; tracks carry their
  two-letter `LanguageCode`.
  Subtitle translation: the Second subtitle select offers "Translate to <language> (AI)" when the profile's language has no track and the Translation
  module is on; the player starts it (`POST /api/v1/playback/assets/{id}/subtitle-translations`), follows its status and then shows the result as the
  second subtitle.
  Automatic quality ([AutoQuality.ts](../../src/Jularr.Frontend/Modules/Player/AutoQuality.ts), `auto-quality.test.mjs`): on a device that plays in
  Automatic, two stalls of 1.5 s within a minute ask for a new plan one step lower (High, or the saver when the loader's measured rate is under 2.5
  Mbit/s; High then the saver) with the notice `playback_slow`; Original, the saver and a quality chosen by hand are never changed.
  The AirPlay button shows only while Safari reports a target (`webkitplaybacktargetavailabilitychanged`) and opens Safari's picker; Chromecast stands
  beside it in the header at the top right (Chrome only; on an address that is not https it stays visible and says that casting needs https).
  The Audio and the Subtitles tab each hold both delays and the speed (the fields of one value stay equal).
  Advanced controls remain separate.
  [MediaFocus](../../src/Jularr.Frontend/Modules/Playback/MediaFocus.ts) (`media-focus.test.mjs`): the screen wake lock while a video plays, a book is
  open or read aloud runs (the profile's `KeepScreenOn`, read from `data-keep-screen-on` on `#app-content`; a hidden page releases it and gets it back
  when shown) and one medium at a time in this browser: starting the player or read aloud pauses the other, also in another tab; no cross-device
  focus.
- [Progress transport](../../src/Jularr.Frontend/Modules/Tracking/Progress.ts): serialized acknowledged writes, finite transient retries and visible
  unsaved state for Reader/Player.

## Tests

- Playback plans, range streaming, resume/completion, foreign sessions, module gate, sleeping storage, delivery/conversion decisions, FFmpeg
  arguments, playlists and HLS segment serving: `PlaybackTests.cs`; set `JULARR_TEST_FFMPEG` to an FFmpeg 7+ executable to also run the real
  transcoding/seek/budget test.
