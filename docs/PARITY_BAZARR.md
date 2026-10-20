# Bazarr parity

Owner decisions of 19 October 2026. Subtitles are Jularr's `Subtitles` module; series and films come from the
library itself, so Bazarr's Sonarr/Radarr sync falls away. Notifications, system and access as decided for
[Sonarr](PARITY_SONARR.md). Build state is tracked in the [feature matrix](FEATURE_MATRIX.md).

## Same

OpenSubtitles.com and SubDL; automatic search that skips languages already embedded or beside the video, prefers the
same release and looks again after a day; manual search and choice per video; subtitle events in the media history.

## Wanted

| Area | Bazarr | Jularr today | Decision |
| --- | --- | --- | --- |
| Languages | Language profiles per series or film with forced, hearing impaired, audio exclusion and cutoff | One ordered list for the instance | The [general language rule](PARITY_SONARR.md#editions-and-languages-all-media-types): the languages of the profiles that watch or request the title, as the Owner allows; per language forced and hearing impaired (yes, no, either); not needed when an audio track already has the language; changeable per title |
| Upgrade | Minimum score per episode and film; a better match replaces the old one for 1–30 days; manual or translated ones optionally kept | – | Like Bazarr |
| Views | Missing subtitles, history, blocklist, statistics per provider | Events in the media history | Like Bazarr, as a part of Activity: missing (search), history with provider and score, blocklist (a bad subtitle is never taken again), statistics per provider |
| Sync | Audio synchronisation with ffsubsync | – | Automatic when the subtitle is not from the same release; threshold setting; can be started by hand |
| Processing | UTF-8, remove hearing-impaired notes, tags and colours, OCR and common fixes, uppercase fix, reverse RTL | – | Like Bazarr, each fix a setting; UTF-8 always |
| Translation | Google, Gemini or Lingarr translate a subtitle of another language | Book translation | Jularr's own translation turns a subtitle of another language into a needed one, marked as machine-translated |

## Later

| Area | Bazarr | Decision |
| --- | --- | --- |
| Providers | About 60 providers | All later, after a review: which are still active, really work and are free, grouped by region, so that a user adding providers finds the good ones as presets |
| Whisper | Subtitles generated from the audio by a self-hosted Whisper | A module later |
| Captcha | Anti-Captcha, Death by Captcha, CaptchaAI for provider sites | Later; tests only against simulated sources |

## Not

| Area | Bazarr | Reason |
| --- | --- | --- |
| Library sync | Sonarr and Radarr connections, webhooks, path mappings | Jularr owns the library |
| Integrations | Refreshing Plex or Jellyfin after a download | Jularr is the player |
