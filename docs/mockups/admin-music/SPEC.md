# Admin Music — artists, albums and manual search

Status: binding for the Lidarr-replacement MVP (`docs/DOMAIN.md`, "Music (manager MVP)"). There is no mockup image: the pages reuse the Admin table, tag and button language of Requests and Wanted and add nothing new to the design system.

Route family `/Admin/Music` (Admin media policy, Music and Acquisition modules). Navigation: "Music" in the "Media & downloads" group of the Admin menu. A disabled Music module hides the entry and returns 404 for the routes.

## `/Admin/Music` — artists

- **Add artist**: a search field queries the metadata provider; each result shows name, type, country and disambiguation with a monitor choice (All albums / Future albums / Nothing) and an Add button. A provider failure is an explicit error, never an empty result.
- **Artists** table: artist (link), monitor mode, albums, monitored, available, last updated. Empty state: "No artist yet. Search one above."

## `/Admin/Music/Artist/{id}` — one artist

- Monitor mode with Save, and **Refresh discography**.
- **Albums** table, newest first: album (link) with release date, type, status tag, tracks with files over tracks, and a per-album monitoring toggle. Statuses: Not monitored, Missing, Requested, Downloading, Needs attention, Partly available, Available. They are derived from files, monitoring and the album's request; the page stores no state of its own.

## `/Admin/Music/Album/{workId}` — one album

- Facts (status, type, year, tracks), the request message, **Monitored** toggle and **Search now** (not offered while Downloading or Available).
- **Tracks** table: disc, number, title and the library file name when the track has one; before the first search the track list is empty and says so.
- **Manual search**: Search releases (Normal) and Deep search; Refresh asks the indexers again. The result shows how many raw results became how many distinct releases, one message per indexer that failed, why the best release wins, and a table of releases: title, indexers (all sources of a merged release) and how it was found, quality, score, size, age, verdict (Eligible / Warning / Rejected / Already tried) with every reason, and **Grab** where the owner may take it. A release whose identity is only ambiguous is a Warning and can be taken; a rejected one never can. Grab posts only an opaque release identity; the server searches again and re-validates.

## Rules

- No second state machine: monitoring only creates the album's request; Wanted, Requests and Operations show and run it.
- Every visible string is a catalog key. No host paths in responses except the file name of a track.
- Home, Discover and the consumer Library do not show Music in this slice.
