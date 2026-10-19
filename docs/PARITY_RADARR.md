# Radarr parity

Owner decisions of 19 October 2026. Whatever Radarr shares with Sonarr follows the
[Sonarr decisions](PARITY_SONARR.md) for films too: library views, filters, mass editor, one management page
per film, wanted lists, activity, search, test parsing, extra files, naming, webhook, system and access.
Only what Radarr adds or does otherwise is listed here. Build state is tracked in the [feature matrix](FEATURE_MATRIX.md).

## Wanted

| Area | Radarr | Jularr today | Decision |
| --- | --- | --- | --- |
| Library | Filter fields of films | – | Sonarr's filters plus film fields: TMDB, IMDb and Rotten Tomatoes ratings, studio, collection, runtime, release dates |
| Collections | A monitored collection adds its new films; per collection profile, folder, minimum availability, search on add; mass actions | Franchises from TMDB to browse and request | Like Radarr |
| Availability | Cinema, digital and physical release dates; minimum availability announced, in cinemas or released | One release date | Like Radarr: three dates from TMDB, minimum availability per film and profile (default released), all three in the calendar; the search starts at the date, without waiting time |
| Import | Propers and repacks: prefer and upgrade, do not upgrade, do not prefer | Upgrades to propers and repacks | A setting like Radarr; **default: no automatic upgrade, only through the custom format score** |
| Import | Change file date to the cinema or physical date | – | A setting: off, cinema date, physical date (series: air date) |

## Differently

| Area | Radarr | Jularr |
| --- | --- | --- |
| Editions | One file per film; editions preferred through custom formats | The [general rule](PARITY_SONARR.md#editions-and-languages-all-media-types): one edition per needed language by default, further editions requestable side by side and chosen when playing; edition as a naming token and custom format condition |

## Later

| Area | Radarr | Decision |
| --- | --- | --- |
| Import lists | 20 list types: TMDb list/person/company/keyword/popular/user/collection, IMDb lists, Plex watchlist, Trakt, Simkl, StevenLu, RSS, other Radarr, custom | All later |

## Not

| Area | Radarr | Reason |
| --- | --- | --- |
| Discover | Recommendations from the library and lists with an exclusion list | Jularr's Discover is enough |
| Search | Minimum age, delay profiles, availability delay | No grab wait times |
| Indexers, download | Torrent indexers and clients | Usenet only |
