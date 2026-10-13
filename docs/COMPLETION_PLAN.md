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
| K9 | NAS probes, reconnect, Synology API | Outage keeps browsing; reconnect without restart |
| K10 | Provider sync core, Plex progress, Jellyfin, Emby | Two-way progress never regresses; outages catch up |
| K11 | AniList OAuth, list import, sync, discovery feeds | Retries after outages without duplicates |
| K12 | Offline PWA with device check, renditions, local state and sync queue | Offline playback and reading; progress syncs when online |
| K13 | Translation owner (Bergamot, glossary, translation memory, cache) | Same input, same cached output |
| P2 | Email verification (SMTP), TOTP/WebAuthn/passkeys (library needs approval), branding | |

Done: K1a, K1b, K2, K3 (without backup/restore), K4a (platform in details and per file), K4b (ROM
identification through libretro DATs), K7a (profile PIN with lockout and Owner reset). ScreenScraper waits for an owner developer account.

Risks: K1 touches most reads; the community anime mapping and EmulatorJS cores need a
license check before use; backup needs `pg_dump` in the container; offline video is
bounded by the browser quota.
