# Media list import

A one-time import of many titles at once for the Owner and Media Managers (permission
`media.manual.update`, modules Library and Requests): page `/Admin/Import` in the Media
menu. No profile access, no collections; it is not the file import of a library and not a
recurring import list. Nothing is placed on disk; the only download is what the normal
search of a monitored title starts.

## Format, version 1

```json
{
  "version": 1,
  "items": [
    { "type": "movie", "ids": { "tmdb": "603", "imdb": "tt0133093" }, "title": "The Matrix", "year": 1999 },
    { "type": "book", "ids": { "isbn": "978-0-14-143951-8", "openlibrary": "OL66554W" }, "title": "Pride and Prejudice", "author": "Jane Austen" },
    { "type": "music", "title": "The Dark Side of the Moon", "artist": "Pink Floyd" }
  ]
}
```

The example file (all eight types) is offered on the page and in
[MediaImportExample](../src/Jularr.Backend/Data/Admin/Media/V1/MediaImport.cs).

- Top level: only `version` (must be `1`) and `items` (1 to 2000 entries). Anything else rejects the whole list.
- Entry keys: `type`, `ids`, `title`, `year` (1800 to 2200), `author` or `artist` (same meaning). Another key marks that entry invalid; the rest stays usable.
- `type`: `movie`, `series`, `anime`, `book`, `manga`, `lightnovel`, `music`, `game`.
- `ids` per type: `tmdb` (movie, series, anime), `tvdb` (series, anime), `imdb` (movie, series, anime), `isbn` and `openlibrary` (book, manga, lightnovel), `anilist` (anime, manga, lightnovel), `musicbrainz` (music release group), `igdb` (game). Values are checked and normalized (numbers without leading zeros, `tt` ids lower case, `OL...W` upper case, ISBN-10 or -13 with check digit stored as ISBN-13, MusicBrainz id as lower-case GUID).
- An entry needs an id or a title. Texts are plain text, trimmed, one space between words, at most 500 characters, no control characters; markup is never interpreted.
- At most 1 MiB (bytes), depth 8, no comments or trailing commas, strict UTF-8 for files. Nothing is fetched from a URL.

## Flow

1. Upload a `.json` file or paste the list, choose how titles are added (add only, monitor, monitor and search).
2. Preview, nothing changes: each row is New (a provider entry would be added), In library, Duplicate (same title as an earlier row), Open or Invalid with its reason. Per row: include, the candidate for an open row (or another search by title), the action, quality profile and library. Defaults per kind of title are offered where there is a choice.
3. Import selected: rows are written one by one, each its own step. A report lists every row's outcome. At most 40 titles that are new to the library are read from their providers per run; the rest is reported as waiting and offered as a list of its own (Continue).

## Matching

Identities come first: the file's ids are looked up in `WorkExternalIdentities` (including a book's edition ISBNs and an anime's seasons). Without a local hit a provider id is used as it is; an IMDb or TVDB id finds its TMDB entry. Title, year and author only search the provider to propose candidates: one candidate that carries the whole title and the year or author is preselected, never taken; the person ticks the row. An id that names a title of another type, ids that name different titles, titles deleted with an exclusion and ids no provider knows are not imported.

A title is never imported twice: repeating a list changes nothing that exists; an existing target stays as it is. Monitoring creates the title's target like the Work page does (the quality profile and library as chosen, otherwise the defaults of its type); games take their platform on the title page and are only added.

Tests: `MediaImportTests` (backend: parsing, matching, idempotence, options, permissions, failures; page) and E2E `MediaImportTests`.
