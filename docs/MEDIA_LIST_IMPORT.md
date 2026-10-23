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

## Book versus audiobook matches

The book catalog uses Open Library Works, which can include both print/ebook and audiobook editions. Normal books with mixed print/audio editions remain searchable. Results explicitly labelled as audio editions (for example `- Audiobook`, `(Hörbuch)`) and Open Library records with **only** audio formats are not offered as regular book candidates. An ISBN resolving to an explicitly audio-only edition is not automatically bound to a book Work; the item remains subject to normal manual matching. Missing/incomplete provider formats cannot be reliably classified and may still require manual review. Audiobooks are not deleted from the Work's legitimate other editions.

The editable preview also offers an **on-demand edition comparison** for ambiguous Open Library books: real publisher, language, year, physical format and available ISBNs for the selected Work (up to 30), read-only and permission-gated. A Work is not duplicated when another edition is displayed. Missing publisher metadata stays blank instead of being guessed.

## Flow

1. Choose a `.json` file in the file input (drop zone, name, size, remove) or paste the list, and choose how titles are added. **Default is Add only**: adding catalog entries does not schedule a search or download; Monitor and Monitor+Search require explicit selection. The preview summarizes how many selected titles would be monitored and searched, including per-row overrides. The page offers the example as a download and as a sample to copy.
2. Preview, nothing changes: each row is New (a provider entry would be added), In library, Duplicate (same title as an earlier row), Open or Invalid with its reason. Per row: include (a switch; Select all switches every row), the candidate for an open row (or another search by title), the action, quality profile and library. Defaults per kind of title are offered where there is a choice.
3. Import selected: rows are written one by one, each its own step. At most **40 new provider imports per server request** remain allowed. With JavaScript, the page automatically continues into bounded sequential waves as long as each wave imported new titles, preserving per-row decisions and reporting every row live. On an unreachable provider, lack of forward progress or exceeded safety cap, Jularr pauses and offers **Continue with the rest** for explicit retry. Without JavaScript, the previous manual continuation flow still works.
   Finished runs expose a client-side JSON report of each title and outcome; failed/not-found rows can be reopened as their own preview with original per-row choices (never silently retried). With JavaScript, importing streams real per-item outcomes back to the page as they commit, with a live processed counter and a continuation action for deferred/unavailable items. Provider matching runs first, so the visible count starts advancing when writes begin. Without JavaScript, the existing standard HTML POST/result page still works. With JavaScript, **initial preview, refresh and Search more** all report the live state of each row (ISBN lookup, provider search, candidates and final matching status); the same editable HTML table appears after the stream finishes using cached provider results, preserving choices. Active requests disable conflicting submit actions and block accidental in-app navigation; full browser navigation/reloads prompt a warning. **This is not yet a durable server-side import job:** closing a tab, forcing a reload, losing the connection or restarting the server can still interrupt an import. A background-job queue with authenticated ownership, durable progress/recovery and a separate status endpoint remains follow-up work.

## Matching

Identities come first: the file's ids are looked up in `WorkExternalIdentities` (including a book's edition ISBNs and an anime's seasons). If a book ISBN is not in Jularr yet, the importer asks Open Library for the **edition's canonical Work**, verifies its title and author against the input where supplied, and uses that Work identity for the ordinary import. A conflicting explicitly supplied Open Library work ID is rejected rather than overridden. A missing/ambiguous ISBN or a disagreement in the title/author remains open for manual selection. No fuzzy hit is imported automatically. Without a local hit a provider id is used as it is; an IMDb or TVDB id finds its TMDB entry. Title, year and author only search the provider to propose candidates: one candidate that carries the whole title and the year or author is preselected, never taken; the person ticks the row. An id that names a title of another type, ids that name different titles, titles deleted with an exclusion and ids no provider knows are not imported.

A title is never imported twice: repeating a list changes nothing that exists; an existing target stays as it is. Monitoring creates the title's target like the Work page does (the quality profile and library as chosen, otherwise the defaults of its type); games take their platform on the title page and are only added.

Tests: `MediaImportTests` (backend: parsing, matching, idempotence, options, permissions, failures; page) and E2E `MediaImportTests`.
