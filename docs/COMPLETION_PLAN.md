# Historical clean-start completion packages

Status: **historical** acceptance-criteria inventory, not a current delivery schedule, implementation status or new-work approval. Preserve requirements until reconciled with `main` and tracked issues. See [documentation authority](DOCUMENTATION_POLICY.md).

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

Packages of the mandate of 14 October, after K24b, from the gaps in the feature matrix:

| # | Package | Acceptance |
| --- | --- | --- |
| B1 | E2E base: fake TMDB, Newznab and SABnzbd servers the separately started host can reach, configurable provider addresses, a worker trigger, small lawful media fixtures, FFmpeg on the runner, setup driven through the UI, a restartable instance | A test sets up a fresh instance only through the browser and reaches a configured library |
| B2 | E2E film and series path: scan, match, discover, request, Wanted, search, release, download, import, play, progress, sign out and in, resume | Passes in CI without live services |
| B3 | E2E music, books, light novels and manga to play and read | Passes in CI without live services |
| B4 | E2E failure cases: database restart, NAS outage, download retry, restart mid-operation, missing metadata, ambiguous release, permissions, tracks, transcoding error, upgrade, HTTP 5xx and script errors | Each ends visible and recoverable; failures keep screenshots and logs |
| C1 | Monitoring of seasons, episodes, specials, volumes, albums and tracks; missing and cutoff-unmet lists; new-release detection tested | Unmonitored units are never searched |
| C2 | Indexers (#864): keyless Newznab, presets, health with backoff, limits, media scope | A failing indexer backs off while others search |
| C3 | Release rules (#865): custom formats, quality groups, score thresholds, identity confidence, immediate grabs and upgrades | Every decision names its rule |
| C4 | Discover and requests for music, books, manga, light novels, games and anime | A user finds and requests each enabled type |
| C5 | Music (#1022): artists, discographies, editions and media, multi-format, tags, retag preview, duplicates | An artist's new album is found and imported with correct tags |
| C6 | Books: authors, series, editions, format upgrades; audiobooks as Book subtype | A series' new volume is found; an audiobook plays by chapter |
| C7 | Subtitles: manual search, status and history page, forced flags, quotas | A missing subtitle can be searched and chosen by hand |
| C8 | Takeover tests for Sonarr and Prowlarr, Lidarr and Readarr sources, profiles and roots | The old programs can be switched off after one takeover |
| C9 | Re-match a Work to another provider id; daily and absolute episode numbering | A wrongly matched film is corrected without losing files |
| D1 | Every user page against its spec with real media: Discover, Details, Library, Watchlist, Player, Reader (bookmarks, PDF conversion), Requests, Settings, Calendar, Collections, responsive layouts | No empty error pages or actions without effect |

K14-K26 close the gaps of a parity audit against Alpha-90 (`dev`, 14 October 2026); language
learning, AI and TTS stay out of scope until the owner schedules them.

Done: K1a, K1b, K2, K3, K4a (platform in details and per file), K4b (ROM
identification through libretro DATs), K4c (retro play in self-hosted EmulatorJS with saves and play time per profile; offline play follows with K12), K7 (profile PIN with lockout and Owner reset; up to ten own profiles, deletion with their requests), request rules (limits, approval, allowed media, offered qualities, per-account overrides), K6 (comic reader for CBZ/CBR/CB7 in the shared reading cache), K8 (audio and subtitle choice, mini player, Continue Watching/Listening/Reading shelves and the Continue Reading bar), a better imported release replaces the earlier one (deleted, or into a recycle folder inside its library or an own one, cleaned only of what Jularr put there; manual versions protected; worse ones replace only on the administrator's confirmation; several releases optional), K24a (device pairing by code or QR code, paired devices as revocable sessions per account and for the Owner; the app API and remote control follow), K5a (Playwright browser tests: a fresh instance from setup through every menu page, in CI), K10a (Jellyfin and Emby: name proposals confirmed by an administrator, two-way progress for films and episodes, catch-up after outages; Plex progress and linking one's own account follow), K9 (connection state from probes, background reconnect with backoff, Synology health and power through DSM), K14 (pg_dump backups with schedule and retention, restore in setup), K15 in-app (request decisions, arrivals, failed downloads, offline storage; push/e-mail delivery waits for P2 SMTP), K16 (TMDB film series as franchises; hand-picked and smart collections up to an Admin limit per profile (default 100); smart filters are a fixed set instead of Alpha-90's nested rule groups), K17 (episodes, volumes and tracks by their date, movies and other Works without units by TMDB's first release date), K20 (profile statistics, Owner system overview with recent warnings and errors, a processor and memory graph of the last six hours and service timings of the last hour, both kept in memory), K18 core (ordered wanted languages, OpenSubtitles.com and SubDL as subtitle-role connections, automatic fetch beside the video; further providers follow), K19 trickplay (seek thumbnails; intro and credits detection needs an audio-fingerprint tool the owner has to approve), K26 home layout (shelf order and visibility per profile) and manga volume upload by administrators (Uploads module); LAN discovery open (personal API keys dropped), K23 for music (albums continue track by track with progress per track, track titles, author and artist pages; audiobooks follow as a Book subtype), K22 without a PDF page reader (highlights and notes per profile, text search in the book; PDFs open in the browser's own viewer; pdf.js is declined, reader conversion of PDFs follows), K21 (confirmed rename into the import layout; periodic rescans instead of a filesystem watch, which network shares do not report; named sections over unit ranges in the UnitGroups module; Admin naming patterns for movie and episode files with Sonarr-like placeholders). ScreenScraper is deferred; IGDB and libretro thumbnails cover games. Order after the running Arr+ packages: Playwright, K24, core features, optional modules ([decisions of 14 October](BACKEND_NEXT.md)).

Risks: K1 touches most reads; the community anime mapping and EmulatorJS cores need a
license check before use; backup needs `pg_dump` in the container; offline video is
bounded by the browser quota.
