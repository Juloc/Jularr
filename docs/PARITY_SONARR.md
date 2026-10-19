# Sonarr parity

Owner decisions of 19 October 2026, row by row. **Same**: Jularr already does it.
**Differently**: Jularr solves it its own way. **Wanted**: to be built. **Later**: wanted after the first release.
**Not**: deliberately left out. Build state is tracked in the [feature matrix](FEATURE_MATRIX.md).

## Wanted

| Area | Sonarr | Jularr today | Decision |
| --- | --- | --- | --- |
| Library | Poster, overview and table views; selectable columns and card details | Cards, type tabs, sort by added/title/year | Like Sonarr: three views, columns and card details selectable |
| Library | Built-in and custom filters | Type tabs | Fixed filters (monitored, continuing/ended, missing episodes) plus a filter builder; sort by any column |
| Library | Mass editor: profile, folder, tags, monitoring, rename, delete | – | Like Sonarr |
| Library | Season pass | Monitoring per episode on the wanted page | An action of the mass editor (seasons: all, only new, first, last, none; specials on/off), no page of its own |
| Series page | Seasons and episodes with files, monitoring, automatic and interactive search, history, edit, move | Split over details, Work and wanted pages | One management page per series; the details page for watching stays |
| Adding | Monitor option: all, future, missing, existing, recent, pilot, first/last season, none | A request takes the whole series and new episodes | All Sonarr options for administrators; a user's request stays simple (whole series or chosen seasons) |
| Adding | Series type standard, daily, anime | Anime subtype; dated releases are recognised | Like Sonarr: a daily type searches, matches and names by air date |
| Adding | Specials (season 0) | Monitored like other episodes | Not monitored by default; per episode or through the mass editor |
| Monitoring | Scene numbering (TheXEM) | Episode offset for AniList only | Automatic from TheXEM, correctable by hand |
| Monitoring | Unmonitor deleted episodes | – | A setting, off by default; only when the library is reachable and the file is really gone (an offline NAS never counts as deleted) |
| Calendar | iCal feed | – | A link per profile with a token, last 7 and next 28 days |
| Wanted | Missing, cutoff unmet | One wanted list | Like Sonarr: two lists, search selected/all, toggle monitoring, filters |
| Activity | History | All media events | Like Sonarr: filter by event, details (indexer, age, size), mark as failed (remove, block, search again) |
| Activity | Blocklist | Blocked releases per target | Its own list over everything (reason, date, title; unblock one or all); per target stays |
| Activity | Manual import | Waiting imports, assigning unmatched library files | Like Sonarr: interactive import from any folder and from doubtful queue items, prefilled; series, season, episode, quality, language; move or hardlink |
| Search | RSS sync | Scheduled search per target | In addition: interval setting (15 minutes), per indexer, grabs at once |
| Search | Interactive search | Manual search and grab per target | Like Sonarr: per episode, season or film; quality, size, age, indexer, score and the rejection reason; filters; grab anyway; override the match |
| Tools | Test parsing | – | A release name shows series/episode, quality, language, group, custom formats and score: the decision the import would make |
| Import | Extra files | Subtitles | Subtitles plus selectable extensions (srt, ass, nfo …), renamed with the file |
| Naming | Every token, separate anime format, season folders, multi-episode style, colon replacement | Films and series, a subset | **Before the first release**: every token of every *arr, per media type and subtype |
| Connect | Notifications, scripts, webhooks | The in-app bell | **First release**: a webhook; every other target and scripts later |
| Download | Further Usenet clients | SABnzbd and the native downloader | SABnzbd now; NZBGet soon; NZBVortex, Pneumatic and blackhole later |
| System | Tasks, log files, updates | – | Scheduled tasks (last/next run, run now), log files to view and download, an update notice with the change list (updating stays with the image) |
| Access | Login, API key | Accounts, passkeys, device tokens | Differently; plus an API key per account for scripts and the webhook if needed |

## Differently

| Area | Sonarr | Jularr |
| --- | --- | --- |
| Activity | Queue that often asks for manual review | Downloads that are clearly the wanted episode import by themselves; only real doubts are shown, with the reason; actions remove, remove and block, assign by hand |
| Library | Library import of organised folders | Takeover and scan |
| Download | SABnzbd with remote path mappings | SABnzbd with path detection and the native downloader |

## Later

| Area | Sonarr | Decision |
| --- | --- | --- |
| Calendar | Day, week, month, forecast, agenda | The calendar page from the Owner's mockup |
| Metadata | Kodi/Emby/Plex NFO and images written beside the files | A module later |
| Import lists | AniList, Trakt, Plex watchlist and others | AniList as planned in #1257; Plex watchlist and Trakt later |
| Tags | Auto-tagging | Later |
| API | Sonarr API v3 for apps and dashboards | A read-only part (series, calendar, queue, history) later; Jularr replaces Seerr and Bazarr itself |

## Not

| Area | Sonarr | Reason |
| --- | --- | --- |
| Search | Minimum age, delay profiles | No grab wait times |
| Indexers, download | Torrent indexers and clients | Usenet only |
| Import lists | MyAnimeList, Simkl, other Sonarr, custom lists | Not needed |

## Same

Quality profiles, custom formats and quality sizes (with takeover; the Owner's six profiles and seven formats
are tested at the switch-over), release profiles, Newznab indexers with Prowlarr as import only, rename,
hardlinks and recycle bin, automatic search, episode monitoring and monitoring of new episodes, tags,
system overview with health, warnings and backups.
