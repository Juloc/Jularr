# Feature matrix

Binding scope of the owner mandate of 14 October 2026 ("Vollständige Basisfunktionen und
Arr+"): full Arr+ management (Radarr, Sonarr, Lidarr, Readarr, Bazarr, Prowlarr and Seerr
features, none of them needed at runtime) and every basic user function. Packages and order
are in the [completion plan](COMPLETION_PLAN.md); owners in [CODEMAP](CODEMAP.md).

Status, strictly: **planned** (documented or absent), **implemented** (code without a focused
test), **tested** (Backend or Frontend tests assert it), **e2e** (a Playwright test drives it
through the UI). A row is done only at e2e. Tests name classes in `tests/Jularr.Backend.Tests`,
`src/Jularr.Frontend/Tools/*.test.mjs` or `tests/Jularr.E2E.Tests`.

## End-to-end path

| Step | Status | Owner | Tests | Missing | Acceptance |
| --- | --- | --- | --- | --- | --- |
| Setup: Owner, sign-in, menu pages | e2e | `Pages/Setup`, Account services | FirstRunTests | Storage, library, provider and download steps not driven | A fresh instance is set up through the UI alone, modules switched on the Instance page |
| Storage/NAS connection, library root | tested | Storage, Library services | StorageTests, LibraryTests | Browser test; Samba stack | A connected share is browsable; an outage keeps browsing |
| Scan and metadata match | tested | Library scan, `WorkMetadata` | LibraryTests, MediaTests | Automatic TMDB identification of unknown folders; E2E | Scanned files appear under the right Work without manual steps where the name is clear |
| Discover, details, request | tested | Discover, Request services | DiscoverTests, RequestTests | Discover for music, books, manga, games, anime (AniList); E2E | Each enabled type can be found and requested by a user |
| Wanted, search, release choice | tested | Acquisition (Wanted, SearchPlanner, judges) | AcquisitionTests | E2E against fake indexer | A request becomes Wanted, finds and grabs the best allowed release |
| Download (SABnzbd, native usenet) | tested | `SabnzbdClient`, Usenet | AcquisitionTests, UsenetTests | E2E fake SABnzbd/NNTP | Failure retries, then blocklists and searches again |
| Import, library | tested | DownloadImport, ImportPlacement | AcquisitionTests | E2E with real files | The file lands in the right library and folder, the request reads available |
| Play / read, progress | tested | Playback, Reading | PlaybackTests, ReadingTests, progress.test.mjs | Media fixtures, FFmpeg on the E2E runner | Playback and reading start, seek and save progress |
| Sign out, sign in, resume | tested | Sessions, Progress | AuthenticationTests, progress.test.mjs | Browser test | Progress resumes on another session and device |
| Failure cases (database restart, NAS offline, download retry, restart mid-operation, missing metadata, ambiguous release, permissions, subtitles/audio, transcoding errors, upgrades, 5xx/JS errors) | tested (parts) | as above | LibraryTests (resume operations), AcquisitionTests, PlaybackTests, StorageTests | Browser scenarios, restart of the E2E host | Each case ends in a visible, recoverable state without data loss |

Needed for these browser tests (Phase B): fake TMDB/Newznab/SABnzbd servers reachable from
the separately started host, configurable provider base addresses, a trigger for worker
cycles, small lawful media fixtures (MP4/MKV with SRT, EPUB, CBZ, FLAC/MP3), FFmpeg on the
runner, a UI-driven setup helper, a restartable `JularrInstance`.

## Arr+ core

| Function | Media | Status | Owner | Tests | Missing | Acceptance |
| --- | --- | --- | --- | --- | --- | --- |
| Wanted target per Work | all | tested | `WantedLogic`, `WantedServices` | RequestTests, AcquisitionTests | Filters by missing, cutoff, paused | Every request and manual add has exactly one target |
| Monitoring of seasons, episodes, specials, volumes, albums, tracks; future only | Series, Anime, Manga, Music | planned | Wanted (new unit monitoring) | – | Schema and UI; specials by setting | Unmonitored units are never searched; new units follow the Work's rule |
| Manual search with reasons, choose release | all | tested | `SearchWantedV1`, `GrabWantedReleaseV1`, Wanted page | AcquisitionTests | Filter and sort of candidates | Every candidate shows score and the reason it is allowed or rejected |
| Automatic search, backoff, upgrade re-search | all | tested | `SearchDueWantedV1`, AcquisitionWorker | AcquisitionTests | Interval test | Due targets are searched once per cycle, failures back off |
| New-episode and new-release detection | Series, Manga, Music | implemented | `RefreshWantedMetadataV1` | – | Test | A newly aired episode becomes wanted within a day |
| Missing and cutoff-unmet lists | all | planned | Wanted page | – | Views | Both lists match `UpgradePolicy` |
| Quality profiles, cutoff, upgrades without downgrade | all | tested | `AcquisitionProfileLogic`, `UpgradePolicy`, `ReplacementLogic` | AcquisitionTests | Direct cutoff test; music upgrades off by default | An upgrade stops at the cutoff and never replaces with worse |
| Release rules, custom formats, scoring | all | tested | `ReleaseScorer`, `ReleaseSelection` | AcquisitionTests | Require/Prefer/Avoid/Reject effects, fallback ladder, identity confidence, delay (#865) | The chosen release and every rejection are explainable |
| Release parsing and identity | all | tested | `ReleaseParser`, per-type judges | AcquisitionTests, BookTests, MangaTests, MusicTests, GameTests | Daily and absolute numbering in matching; parser tests | A release for another Work or unit is never grabbed |
| Search planner per type | all | implemented | `SearchPlanner` | inside AcquisitionTests | Rung tests (#864) | Each type searches its own staged queries within budget |
| Indexers: Newznab, priorities, categories, capabilities | Usenet | tested | `AcquisitionConnectionServices`, `NewznabClient` | AcquisitionTests | Keyless indexers, presets, health with automatic backoff, query/grab limits, media scope (#864) | A failing indexer backs off and the others keep searching |
| SABnzbd and native usenet downloader | Usenet | tested | `SabnzbdClient`, Usenet | AcquisitionTests, UsenetTests | – | Downloads finish or fail visibly; par2/7-Zip present |
| Queue, retry, blocklist, history, recovery | all | tested | `MonitorDownloadsV1`, Activity | AcquisitionTests, LibraryTests | Restart with a download in flight | After a restart every download continues or is retried |
| Import: hardlink, copy, move, remote path mapping | all | tested | `DownloadImport`, `StorageFileTransfer` | AcquisitionTests | Move mode | Import never loses the source before the copy is verified |
| Naming and folders | all | tested | `NamingPattern`, `LibraryRenameLogic` | NamingPatternTests, LibraryTests | Patterns for music, books, games | A rename preview moves nothing until confirmed |
| Protected versions, several releases, recycle modes | all | tested | `ReplacementLogic`, `LibraryRecycleBin` | AcquisitionTests, DeviceTests | – | Delete by default; recycle cleanup touches only registered files |
| Admin visibility of actions, errors, decisions | all | tested | Wanted, Activity, System pages | AcquisitionTests, StatisticsTests | Indexer log and statistics | Every grab, import and failure is visible with its reason |
| One-time takeover | Movie, Series | tested (Radarr) | `TakeoverServices` | TakeoverTests | Sonarr/Prowlarr tests, Lidarr, Readarr, profiles and roots | Takeover is optional; Jularr runs without the old programs |

## Radarr, Sonarr, Lidarr, Readarr, Bazarr

| Function | Status | Owner | Tests | Missing |
| --- | --- | --- | --- | --- |
| Movies: TMDB metadata, search, rename, history, editions, franchises | tested | `TmdbClient`, `WorkMetadata` | AcquisitionTests, DiscoverTests, ReleaseFactsTests | Re-match a Work to another TMDB id; edition choice in the UI |
| Series: seasons, episodes, specials, season packs | tested | `WorkMetadata`, `VideoReleaseJudge` | AcquisitionTests | Monitoring levels; specials search; multi-episode and daily tests; absolute numbering for anime |
| Music: MusicBrainz, Cover Art Archive, albums, tracks, FLAC/ALAC/MP3/AAC, album playback | tested | `MusicBrainzClient`, `MusicReleaseJudge` | MusicTests | Artists, discographies and their monitoring, editions/media, secondary types, multi-format, tag read/write, retag preview, duplicates (#1022) |
| Books, manga, light novels: sources, Newznab, import, reader | tested | Book/Manga judges, sources | BookTests, MangaTests, SourceTests, ReadingTests | Author and series entities and monitoring, editions, format upgrades, chapter releases |
| Audiobooks as Book subtype | planned | Media subtypes | – | Subtype, library/discover view, audio playback with chapters |
| Subtitles: detection, wanted languages, automatic fetch, OpenSubtitles, SubDL | tested | `SubtitleServices` | SubtitleTests, PlaybackTests | Manual search, status and history page, forced flags, quotas, further providers |

## Prowlarr and Seerr

| Function | Status | Missing |
| --- | --- | --- |
| Central indexer management | tested | Presets, health backoff, limits (see Arr+ core) |
| Requests for every type | tested for Movies and Series | User discovery and requests for music, books, manga, games, anime |
| Request rules, approval, limits, quality | tested | – |
| Request → Wanted → acquisition → available, notifications | tested | Push and e-mail wait for SMTP (P2) |

## User pages

| Page | Status | Tests | Missing |
| --- | --- | --- | --- |
| Home/Discover | tested | DiscoverTests, discover.test.mjs, HomeLayoutTests | Other types, genre/year filters |
| Details | tested | PlaybackTests | Complete episode details |
| Library, Watchlist | tested | MediaTests | Filters; numbered paging |
| Player | tested | PlaybackTests, navigation.test.mjs | Real transcoding in CI, casting |
| Reader | tested | ReadingTests, reader.test.mjs, comic.test.mjs | Bookmarks; PDF conversion |
| Login, setup, profiles, sessions | e2e (setup, sign-in, menus) | FirstRunTests, AuthenticationTests | Remaining setup steps in the browser |
| Requests, settings, calendar, collections, notifications, statistics | tested | RequestTests, CalendarTests, CollectionTests, NotificationTests, StatisticsTests | Browser tests |
| Responsive layout | unclear | – | Viewport tests |

## Devices and apps

| Function | Status | Tests | Missing |
| --- | --- | --- | --- |
| Pairing by code and QR, revocable device sessions | e2e | DeviceTests, DevicePairingTests | – |
| App API with device tokens, profile switch, trusted proxies | e2e | DeviceTests, TrustedProxyTests, DevicePairingTests | – |
| Remote control and playback hand-over (SignalR) | planned | – | Stays planned; does not block the core work |
