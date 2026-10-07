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
- Home and Discover do not show Music in this slice; the consumer Library does (below).

## Consumer Library: `/Music`

Music is a Library type behind the Music module and the profile's Music browse permission, reachable as the Library tab "Music". It is a read surface over the same canonical records as the Admin pages (`MusicQuery`); it shows only what is true and offers no Play action until Jularr plays music.

- **`/Music`**: a grid of the artists Jularr knows: a letter tile (MusicBrainz provides no artwork; a cover is shown only once a provider supplies one), the name and "N albums, M in the library". Empty state: "No music in the library yet."
- **`/Music/Artist/{id}`**: the artist's albums, newest first, as tiles with year, type and the derived album status.
- **`/Music/Album/{workId}`**: artist link, year, type, status, "x of y tracks in the library", the track list with an in-library mark per track. An account that may request Music sees **Request album** for an album that is not in the library and has no open request; it goes through the shared request flow, and the server decides. An Admin also sees **Manage in Admin**.
- Search and discovery of new artists stay in Admin Music for now; the consumer surface never changes monitoring.
