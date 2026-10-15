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
| Setup: Owner, sign-in, menu pages | e2e | `Pages/Setup`, Account services | FirstRunTests, FilmPathTests | Setup wizard steps after the Owner; modules switched in the browser | A fresh instance is set up through the UI alone, modules switched on the Instance page |
| Storage/NAS connection, library root | e2e (local path) | Storage, Library services | StorageTests, LibraryTests, FilmPathTests, MusicPathTests, ReadingPathTests | SMB/NFS in the browser (Samba stack) | A connected share is browsable; an outage keeps browsing |
| Scan and metadata match | tested | Library scan, `WorkMetadata` | LibraryTests, MediaTests | Automatic TMDB identification of unknown folders; E2E | Scanned files appear under the right Work without manual steps where the name is clear |
| Discover, details, request | e2e (films, series); tested (books, light novels, manga, music and games found in their catalogs and requested) | Discover, Request services, `MetadataCatalog` | DiscoverTests, RequestTests, BookTests, FilmPathTests, MusicPathTests, ReadingPathTests | Anime tab (needs an AniList to TMDB mapping), catalog types in the All search, rankings for music and games | Each enabled type can be found and requested by a user |
| Wanted, search, release choice | e2e (films, episodes, albums, books, manga, light novels) | Acquisition (Wanted, SearchPlanner, judges) | AcquisitionTests, MangaTests, path tests | Games and anime in the browser | A request becomes Wanted, finds and grabs the best allowed release |
| Download (SABnzbd, native usenet) | e2e (SABnzbd) | `SabnzbdClient`, Usenet | AcquisitionTests, UsenetTests, path tests | Native downloader in the browser (NNTP fake) | Failure retries, then blocklists and searches again |
| Import, library | e2e | DownloadImport, ImportPlacement | AcquisitionTests, path tests | – | The file lands in the right library and folder, the request reads available |
| Play / read, progress | e2e (film, episode, album track with FFmpeg in CI; EPUB, comic) | Playback, Reading | PlaybackTests, ReadingTests, progress.test.mjs, path tests, TracksTests | Transcoding in the browser | Playback and reading start, seek and save progress |
| Sign out, sign in, resume | e2e | Sessions, Progress | AuthenticationTests, path tests | Another device | Progress resumes on another session and device |
| Failure cases: restart during a download, lost database connections, failed download and Retry, offline library, 5xx and script errors | e2e | as above | FailureTests, every path test watches 5xx and script errors | – | Each case ends in a visible, recoverable state without data loss |
| Failure cases: ambiguous release, profiles and permissions, unavailable media, upgrades with the recycle bin | e2e | as above | LibraryChangesTests, ProfilesTests | – | Each case ends in a visible, recoverable state without data loss |
| Failure cases: subtitles and audio tracks, a file the browser cannot play | e2e | as above | TracksTests | – | Each case ends in a visible, recoverable state without data loss |
| Failure case: missing metadata | tested | as above | LibraryTests, AcquisitionTests | Browser scenario | Each case ends in a visible, recoverable state without data loss |

The path tests run against [fake services](../tests/Jularr.E2E.Tests/FakeServices.cs) (TMDB, MusicBrainz, Open Library, AniList, Newznab, SABnzbd) the started host reaches through the `Providers` addresses; files are generated (WebM and FLAC by FFmpeg, EPUB and CBZ in code). Still needed: a NNTP fake for the native downloader and a browser scenario for missing metadata.

## Arr+ core

| Function | Media | Status | Owner | Tests | Missing | Acceptance |
| --- | --- | --- | --- | --- | --- | --- |
| Wanted target per Work | all | tested | `WantedLogic`, `WantedServices` | RequestTests, AcquisitionTests | Filter by paused | Every request and manual add has exactly one target |
| Monitoring of seasons, episodes, specials and volumes; future units | Series, Anime, Manga | tested | `WorkUnits.IsMonitored`, `WantedItems.MonitorsNewUnits`, `WantedLogic.SetUnitMonitoringAsync`, Wanted Monitoring tab | AcquisitionTests | Albums and tracks (C5) | Unmonitored units are never searched; new units follow the Work's rule |
| Manual search with reasons, choose release | all | tested | `SearchWantedV1`, `GrabWantedReleaseV1`, Wanted page | AcquisitionTests | Filter and sort of candidates | Every candidate shows score and the reason it is allowed or rejected |
| Automatic search, backoff, upgrade re-search | all | tested | `SearchDueWantedV1`, AcquisitionWorker | AcquisitionTests | Interval test | Due targets are searched once per cycle, failures back off |
| New-episode and new-release detection | Series, Manga | tested | `RefreshWantedMetadataV1` | AcquisitionTests | Music (C5) | A newly aired episode becomes wanted within a day |
| Missing and cutoff-unmet lists | all | tested | `ReadWantedV1` (`WantedViewType`), Wanted page | AcquisitionTests | Automatic upgrade searches below the cutoff; until then a manual search per target | Both lists match `UpgradePolicy` |
| Quality profiles, cutoff, upgrades without downgrade | all | tested | `AcquisitionProfileLogic`, `UpgradePolicy`, `ReplacementLogic` | AcquisitionTests | Direct cutoff test; music upgrades off by default | An upgrade stops at the cutoff and never replaces with worse |
| Release rules, custom formats, scoring | all | tested | `ReleaseScorer`, `ReleaseSelection` | AcquisitionTests | Several fallback tiers (one tier of fallback qualities after a set number of hours today) | The chosen release and every rejection are explainable |
| Release parsing and identity | all | tested | `ReleaseParser`, per-type judges | AcquisitionTests, BookTests, MangaTests, MusicTests, GameTests | Daily and absolute numbering in matching; parser tests | A release for another Work or unit is never grabbed |
| Search planner per type | all | implemented | `SearchPlanner` | inside AcquisitionTests | Rung tests (#864) | Each type searches its own staged queries within budget |
| Indexers: Newznab (also keyless), presets, priorities, categories (media type ranges by default), capabilities, media scope, manual-only, daily limits, failure backoff | Usenet | tested | `AcquisitionConnectionServices`, `NewznabClient` | AcquisitionTests, SourceTests | – | A failing indexer backs off and the others keep searching |
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
| Movies: TMDB metadata, search, rename, history, editions, franchises | tested | `TmdbClient`, `WorkMetadata` | AcquisitionTests, DiscoverTests, ReleaseFactsTests | Edition choice in the UI |
| Series: seasons, episodes, specials, season packs | tested | `WorkMetadata`, `VideoReleaseJudge` | AcquisitionTests | Specials search; multi-episode and daily tests; absolute numbering for anime |
| Music: MusicBrainz, Cover Art Archive, albums, tracks, FLAC/ALAC/MP3/AAC, album playback | tested | `MusicBrainzClient`, `MusicReleaseJudge` | MusicTests | Artists, discographies and their monitoring, editions/media, secondary types, multi-format, tag read/write, retag preview, duplicates (#1022) |
| Books, manga, light novels: sources, Newznab, import, reader | tested | Book/Manga judges, sources | BookTests, MangaTests, SourceTests, ReadingTests | Author and series entities and monitoring, editions, format upgrades, chapter releases |
| Audiobooks as Book subtype | planned | Media subtypes | – | Subtype, library/discover view, audio playback with chapters |
| Subtitles: detection, wanted languages, automatic fetch, manual search and choice, OpenSubtitles, SubDL | tested | `SubtitleServices` | SubtitleTests, PlaybackTests | Status and history page, forced flags, quotas, further providers; a browser test needs an HTTPS subtitle fake |

## Prowlarr and Seerr

| Function | Status | Missing |
| --- | --- | --- |
| Central indexer management | tested | – |
| Requests for every type | tested | Anime through AniList (C4b) |
| Request rules, approval, limits, quality | tested | – |
| Request → Wanted → acquisition → available, notifications | tested | Push and e-mail wait for SMTP (P2) |

## User pages

| Page | Status | Tests | Missing |
| --- | --- | --- | --- |
| Home/Discover | tested | DiscoverTests, BookTests, MangaTests, discover.test.mjs, HomeLayoutTests | Genre/year filters |
| Details | tested | PlaybackTests | Complete episode details; the browser tab names "Media details" instead of the work |
| Library, Watchlist | tested | MediaTests | Filters; numbered paging |
| Player | tested | PlaybackTests, navigation.test.mjs | Real transcoding in CI, casting |
| Reader | tested | ReadingTests, reader.test.mjs, comic.test.mjs | Bookmarks; PDF conversion |
| Login, setup, registration, profiles, sessions | e2e (setup, sign-in, registration with Owner approval, menus) | FirstRunTests, ProfilesTests, AuthenticationTests | Remaining setup steps in the browser; the sign-in page's appearance note "Instance defaults and profile policies are not connected yet" |
| Requests, settings, calendar, collections, notifications, statistics | tested | RequestTests, CalendarTests, CollectionTests, NotificationTests, StatisticsTests | Browser tests |
| Responsive layout | unclear | – | Viewport tests |

## Devices and apps

| Function | Status | Tests | Missing |
| --- | --- | --- | --- |
| Pairing by code and QR, revocable device sessions | e2e | DeviceTests, DevicePairingTests | – |
| App API with device tokens, profile switch, trusted proxies | e2e | DeviceTests, TrustedProxyTests, DevicePairingTests | – |
| Remote control and playback hand-over (SignalR) | planned | – | Stays planned; does not block the core work |
