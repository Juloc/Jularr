# Playback

Canonical operational documentation for Jularr video playback. UI planning and future-state design remain in `docs/mockups/player/SPEC.md`; this file documents current implemented behavior.

## Playback

Episode pages include an integrated HTML5 player. Jularr serves media with HTTP range support, synchronizes the imported Japanese cue track, and exposes local reading, meaning and learning state when a highlighted subtitle word is clicked. The lookup path is deterministic and does not call AI.

Playback is **device-first and instant**. Each browser stores its own preference in local storage:

- **Auto** (default): prefer direct play or a live video-copy remux that the device can decode; use the live server H.264 fallback when the browser does not report the required codec support or device playback fails.
- **Device only**: never video-transcode on the server. Compatible H.264 is remuxed to fragmented MP4 as it is watched, and HEVC/H.265 can be remuxed with `hvc1` tagging while keeping the video stream unchanged.
- **Server**: stream a broadly compatible H.264 `yuv420p` MP4 directly from ffmpeg when video conversion is required. Compatible H.264 is still copied rather than wastefully encoded again.

There is no prepare-playback step. Direct-play files retain HTTP range support; remux/transcode paths emit fragmented MP4 to the browser as ffmpeg produces it, so the whole episode is never encoded before playback starts. The NAS media stays read-only. The server encodes with the first working hardware encoder (NVENC, QSV, VAAPI, AMF; see `ADMIN_OPERATIONS.md`) and otherwise with software `libx264`.

Players show **Skip intro / recap / outro** only for canonical per-episode segment markers above the configured confidence (never automatically), and timeline seek previews once they have been generated in the background into a disposable `/data` cache. Markers come from owner corrections (**Edit skip segments** on the Episode page) or `*.segments.json` / `segments.json` sidecars read during library scans; see [MEDIA_SEGMENTS.md](MEDIA_SEGMENTS.md).

## Player controls

Below the video the player offers **−10 s / Repeat line / +10 s**, **Speed** (0.5×–2.0×), **Audio** (shown when the file has more than one audio track), **Subtitles** and **Quality**. Tracks are addressed by the stable canonical id `stream:{index}` from the [media inventory](#media-inventory) on every client.

- **Speed** changes only the playback rate. Both subtitle layers follow the media clock, so cue timing stays exact at any speed.
- **Audio**: the file's default track keeps direct play; another track is delivered as a live video-copy remux (video is never re-encoded just to switch audio). The selection survives a device → server fallback and every stream restart.
- **Subtitles**: **Off**, **Japanese · learning** (the interactive Jularr cue overlay) or any embedded text track. When the chosen playback subtitle differs from the learning text, both are shown: the plain playback line below the interactive Japanese line. Choosing the embedded stream that already is the learning source simply shows the learning overlay. Image subtitles (PGS/VobSub) are listed but cannot be rendered.
- **Quality** is an optional remote-bandwidth cap: **Auto · original**, **1080p**, **720p** or **Lower bandwidth** (≤480p). It never transcodes when direct play already satisfies it: the server compares the cap with the source height from the media inventory, and only a source that is proven taller is converted (Server mode, or Auto when the server fallback can honour the cap). **Device only** never converts video, so a cap it cannot honour is explained instead of silently transcoding.
- Fullscreen, Picture-in-Picture, Wake Lock and system media controls (Media Session, including ±10 s) are used when the browser supports them and are simply absent otherwise.

**Save as my defaults** stores the current audio language, subtitle choice and speed for the profile.

| Preference | Owner | Where it lives |
| --- | --- | --- |
| Preferred audio language | Profile | `ProfilePlaybackPreferences` (server), `/api/client/v1/me/playback-preferences` |
| Preferred subtitle language or Off | Profile | `ProfilePlaybackPreferences` |
| Default playback speed | Profile | `ProfilePlaybackPreferences` (default 1.0×) |
| Autoplay next episode | Profile | `ProfilePlaybackPreferences` |
| Playback mode (Auto / Device only / Server) | Device | Browser local storage per profile; native device settings |
| Quality cap | Device | Browser local storage per profile; native device settings |
| Decoder support (for example HEVC) | Device | Detected at runtime, never stored |
| Current audio/subtitle track, speed in this playback | Session | Player memory only; kept across fallback/restart, dropped when the page closes |

The server resolves the initial selection once (file default, overridden by a matching profile language) for both the web page and `/episodes/{id}/player`, so clients do not re-implement track or codec rules.

## Playback continuity

Each signed-in profile has its own playback state on the server; web/PWA, Android and Android TV use the same state through `/api/client/v1`. One canonical owner (`EpisodeProgressService`) holds every fact:

- **Resume position** — players send bounded checkpoints (web: at most one every 15 seconds while playing, plus pause, end, restart and page close). A first start below 30 seconds is treated as accidental and stores nothing. **Restart from beginning** clears the resume position.
- **Watched** — reaching 95% of the duration (or the end) marks an episode watched and clears its resume position. Watched is sticky: rewatching a watched episode updates the resume position but never flips it back to unwatched. **Mark watched** / **Mark unwatched** on the Episode and Anime pages are the only way to change it manually; both also clear the resume position.
- **Previous / next** — resolved from local season/episode numbers of episodes with a media file. Jularr only moves to the directly adjacent number, crosses from the last local episode of season N to S(N+1)E01 (and back), never crosses into or out of specials (season 0), and shows no neighbor when numbering is duplicated or has a local gap.
- **Continue Watching** (Home) — at most one card per anime, anchored on that anime's most recently updated resumable or watched episode, newest first with the episode id as tie-break. An unfinished anchor resumes that episode; a watched anchor shows the next local episode as **Up next** unless it is already watched. Finished items disappear automatically.
- **End of episode** — the player shows **Replay**, **Next episode** and **Back to episodes**. **Autoplay next episode** is an optional per-profile preference (default off); when enabled a cancellable 10-second countdown starts the next episode.
- **Recent playback history** — the last 50 playback sessions of the own profile, shown collapsed on Home and clearable by the user. Clearing history keeps watched state and resume positions. History is never shown to the owner or other users.

AniList sync stays a separate, explicit integration and never becomes the local source of truth.

## Media inventory

Every library scan also maintains a persisted technical analysis per media file (PostgreSQL tables `MediaAnalyses` and `MediaAnalysisStreams`): container, duration, video codec/profile, resolution, bit depth, dynamic range (SDR, HDR10, HLG, Dolby Vision), audio tracks (codec, language, title, channels, default/forced) and subtitle streams (codec, language, title, default/forced, text or image). Playback decisions, the client API player metadata, the episode page's embedded subtitle list and embedded subtitle/Whisper stream selection all read this one inventory; there is no second probe cache.

`ffprobe` only runs when a file is new, its size or modification time changed, or the analysis logic changed (a code-level probe version). A file whose modification time changed but whose length and first/last 64 KiB are identical (a touched or re-copied file) keeps its analysis. An unchanged library therefore performs no `ffprobe` work on reconciliation. Media that `ffprobe` rejects is recorded as a failed analysis with a diagnostic, stays in the library and does not fail the scan; it is analysed again only once the file changes. If `ffprobe` cannot run at all (missing binary or timeout), the analysis stays pending and is retried by the next scan or playback once five minutes have passed. The first scan of a large library probes every file once, one file at a time.

The learning-text fallback order is **nearby text subtitle → embedded text subtitle → optional Jimaku lookup → local Whisper transcription**. Configure Jimaku under **Settings → Subtitles** with an API key generated by the Jimaku account. Jularr tests the key before saving it and protects it with ASP.NET Core Data Protection under `/data/integrations`. AniList episode mappings are reused for Jimaku matching when available; otherwise Jularr falls back to the local anime title and episode number.

If no usable subtitle is found, Jularr transcribes the preferred Japanese audio stream through `whisper.cpp`. The quantized multilingual small model is downloaded once into `/data/whisper`, verified, and reused. Generated SRT lives under `/data/transcription-cache` and is imported through the same subtitle/vocabulary pipeline; source media is never modified. ASS/SSA, SubRip and WebVTT text are supported by the learning parser. Image subtitle formats such as PGS/DVD/DVB do not require OCR for learning text because Whisper remains the final fallback.

**Settings → Subtitles** shows ready/queued/processing/failed coverage, can prepare every episode that is still missing learning text, and can retry individual failures. Jimaku is optional: removing or never configuring the key does not disable the local Whisper fallback.
