# Clean-start completion plan

Owner decisions are in [BACKEND_NEXT](BACKEND_NEXT.md); owners and tests in
[CODEMAP](CODEMAP.md). One branch, bundled commit and PR per package, a module
audit before integration. Each package lists its acceptance; tests stay focused.

| # | Package | Acceptance |
| --- | --- | --- |
| K1a | Subtypes in the media model: Manga/Light Novel as Book subtypes, Anime for Series/Movie, manual classification, subtype libraries, library per Wanted target, subtype profiles, library filter by type/subtype | Subtype Works leave their parent list; Arr+ searches, ranks and places by type and subtype; a Book library never receives Manga when a Manga library exists |
| K1b | Type and subtype switches dependent on their parent, one effective classification used by every User read and Arr+, profile permissions and personal hiding, a library per active type | Subtype off: Works return to the parent list; parent off: hidden; switching creates or deletes nothing; "All" has no duplicates |
| K2 | Canonical Book admin search (Open Library, else internal Work), AniList as mapping only, classification proposals, anime TMDB mapping, review queue, AniList backfill, AniList client for anime and manga with rate limit, backoff, paging and cache | No duplicate Works across providers; uncertain matches create nothing; everything works without AniList |
| K3 | Setup wizard: resumable progress, steps 1-6, 8 and 9 on their Admin owners, backup/restore owner, readiness checks (database, FFmpeg, storage, library per active type) | Fresh database to finished setup without Admin pages; reload resumes; blockers prevent completion |
| K4 | Games: ROM identification (Libretro DAT, ScreenScraper), scan identification, Discover through IGDB, platform in details, retro play through EmulatorJS with per-profile saves and play time, SteamGridDB/PCGamingWiki | A ROM is identified by hash; saves follow the profile across devices |
| K5 | E2E with Playwright against a Compose stack: fresh database, setup, login, NAS (Samba), scan, search, download, import, playback, restarts, failures | All scenarios pass in CI, including restart recovery |
| K6 | Manga reader (CBZ/CBR/CB7 pages, direction, spreads, preloading, progress) | Resumes on the exact page; large volumes stream page by page |
| K7 | Profiles: create/delete, limits, PIN with the agreed lockout | Five failures lock for one minute across restarts |
| K8 | Player: audio/subtitle selection, mini-player navigation, Continue Watching/Reading/Listening | Navigation keeps playback; without script ordinary navigation |
| K9 | NAS probes, reconnect, Synology health and power (DSM admin account) | Outage keeps browsing; reconnect without restart |
| K10 | Provider sync core, Plex progress, Jellyfin, Emby | Two-way progress never regresses; outages catch up |
| K11 | AniList OAuth, list import, sync, discovery feeds | Retries after outages without duplicates |
| K12 | Offline PWA with device check, renditions, local state and sync queue | Offline playback and reading; progress syncs when online |
| K13 | Translation owner (Bergamot, glossary, translation memory, cache) | Same input, same cached output |
| K14 | Backup and restore (`pg_dump`, Setup step 2, scheduled copies) | A restored backup brings back accounts, library and progress |
| K15 | Notifications and events: bell, request approved/available, download failed, per-profile subscriptions | An approved request that becomes available notifies its profile once |
| K16 | Collections (manual, then smart rules) and franchises | A collection keeps its order and hides what the profile may not see |
| K17 | Release calendar and upcoming for Watchlist and Wanted | Upcoming episodes and releases appear by date per profile |
| K18 | Subtitles: provider download, language profiles, completeness | A missing preferred subtitle is found and offered in the player |
| K19 | Intro/credits skip and trickplay thumbnails | Skip appears at the detected segment; seeking shows thumbnails |
| K20 | Statistics for profiles and the Owner, admin health/system/log pages | Numbers come from stored history, never from live provider calls |
| K21 | Library tools: filesystem watch, naming profiles and rename, presentation groups | A rename preview never moves files until confirmed |
| K22 | Reader: annotations, highlights, text search, PDF | Highlights survive re-opening and follow the profile |
| K23 | Music album/artist pages, audiobook chapters and narrators | An album plays in track order with progress per track |
| K24 | Device pairing and client API for native clients, playback-session hub (remote control) | A paired TV plays what the phone sends |
| K25 | Instant play while downloading | Playback starts before the download completes and never plays a broken file |
| K26 | Home layout editor, personal API keys, LAN discovery, Manga upload | Each is a switchable module |
| P2 | Email verification (SMTP), TOTP/WebAuthn/passkeys (library needs approval), branding | |

K14-K26 close the gaps of a parity audit against Alpha-90 (`dev`, 14 October 2026); language
learning, AI and TTS stay out of scope until the owner schedules them.

Done: K1a, K1b, K2, K3, K4a (platform in details and per file), K4b (ROM
identification through libretro DATs), K4c (retro play in self-hosted EmulatorJS with saves and play time per profile; offline play follows with K12), K7 (profile PIN with lockout and Owner reset; up to ten own profiles, deletion with their requests), request rules (limits, approval, allowed media, offered qualities, per-account overrides), K6 (comic reader for CBZ/CBR/CB7 in the shared reading cache), K8 (audio and subtitle choice, mini player, Continue Watching/Listening/Reading shelves and the Continue Reading bar), K9 (connection state from probes, background reconnect with backoff, Synology health and power through DSM), K14 (pg_dump backups with schedule and retention, restore in setup), K15 in-app (request decisions, arrivals, failed downloads, offline storage; push/e-mail delivery waits for P2 SMTP), K16 without franchises (hand-picked and smart collections; smart filters are a fixed set instead of Alpha-90's nested rule groups). ScreenScraper waits for an owner developer account.

Risks: K1 touches most reads; the community anime mapping and EmulatorJS cores need a
license check before use; backup needs `pg_dump` in the container; offline video is
bounded by the browser quota.
