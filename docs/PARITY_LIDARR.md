# Lidarr parity

Owner decisions of 19 October 2026. Whatever Lidarr shares with Sonarr follows the
[Sonarr decisions](PARITY_SONARR.md) for music too: library views, filters, mass editor (plus writing tags), one
management page per artist, wanted lists, activity, search, test parsing, extra files, naming, webhook, system and
access. Editions follow the [general rule](PARITY_SONARR.md#editions-and-languages-all-media-types).
Earlier music decisions (15 October): tags through TagLibSharp, AcoustID as an optional module, transcoding only
what a device cannot play, Lidarr metadata profiles taken over as monitoring rules of the Jularr profile.
Build state is tracked in the [feature matrix](FEATURE_MATRIX.md).

## Wanted

| Area | Lidarr | Jularr today | Decision |
| --- | --- | --- | --- |
| Artists | Add an artist with monitor all, future, missing, existing, first, latest or no albums; new releases all, new or none; albums on/off one by one | Albums requested one by one; artists as people with MusicBrainz ids | Like Lidarr |
| Release types | Metadata profiles: primary types (album, EP, single, broadcast, other), secondary types (live, compilation, soundtrack, remix …), release status | – | A default in the Jularr profile (album and EP, official only), changeable per artist; any other release requestable on its own |
| Album editions | One chosen release per album (CD, vinyl, deluxe, remaster) | Editions table | The general rule: one edition per album by default, the one fitting the files best, changeable; further editions requestable |
| Tags | Write MusicBrainz tags: always in sync, initial import only, new downloads only; embed cover art; scrub existing tags | Reads tags | Like Lidarr, plus a preview of old → new before writing |
| Lyrics | `.lrc` files as extras | – | Imported with the album and shown in sync in the player; fetching them (for example LRCLIB) later as a module |
| Library | Watch the library for file changes | Scheduled scans | For every media type: local folders at once; NAS shares where watching is unreliable through the scheduled scan; an offline NAS never counts as deleted |

## Later

| Area | Lidarr | Decision |
| --- | --- | --- |
| Import lists | Spotify, Last.fm, MusicBrainz series, Headphones, other Lidarr, custom | All later |

## Not

| Area | Lidarr | Reason |
| --- | --- | --- |
| Search | Minimum age, delay profiles | No grab wait times |
| Indexers, download | Torrent and Gazelle indexers, torrent clients | Usenet only |
