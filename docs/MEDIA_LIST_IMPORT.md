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
- Entry keys: `type`, `ids`, `title`, `year` (1800 to 2200), `author` or `artist` (same meaning). `author` may also be an explicit array of 1–50 names, with a combined display length of at most 500 characters. A string remains one credited name; commas in names are never guessed as separators. Another key marks that entry invalid; the rest stays usable.
- `type`: `movie`, `series`, `anime`, `book`, `manga`, `lightnovel`, `music`, `game`.
- `ids` per type: `tmdb` (movie, series, anime), `tvdb` (series, anime), `imdb` (movie, series, anime), `isbn` and `openlibrary` (book, manga, lightnovel), `anilist` (anime, manga, lightnovel), `musicbrainz` (music release group), `igdb` (game). Values are checked and normalized (numbers without leading zeros, `tt` ids lower case, `OL...W` upper case, ISBN-10 or -13 with check digit stored as ISBN-13, MusicBrainz id as lower-case GUID).
- An entry needs an id or a title. Texts are plain text, trimmed, one space between words, at most 500 characters, no control characters; markup is never interpreted.
- At most 1 MiB (bytes), depth 8, no comments or trailing commas, strict UTF-8 for files. Nothing is fetched from a URL.

## Book versus audiobook matches

The book catalog uses Open Library Works, which can include both print/ebook and audiobook editions. Normal books with mixed print/audio editions remain searchable. Results explicitly labelled as audio editions (for example `- Audiobook`, `(Hörbuch)`) and Open Library records with **only** audio formats are not offered as regular book candidates. An ISBN resolving to an explicitly audio-only edition is not automatically bound to a book Work; the item remains subject to normal manual matching. Missing/incomplete provider formats cannot be reliably classified and may still require manual review. Audiobooks are not deleted from the Work's legitimate other editions.

The editable preview also offers a **server-rendered on-demand edition comparison** for ambiguous Open Library books: real publisher, language, year, physical format and available ISBNs for the selected Work (up to 30), read-only and permission-gated. A Work is not duplicated when another edition is displayed. Missing publisher metadata stays blank instead of being guessed.

## Flow

1. Choose a `.json` file in the file input (drop zone, name, size, remove) or paste the list, and choose how titles are added. **Default is Add only**: adding catalog entries does not schedule a search or download; Monitor and Monitor+Search require explicit selection. The preview summarizes how many selected titles would be monitored and searched, including per-row overrides. The page offers the example as a download and as a sample to copy.
2. Preview, nothing changes: each row is New (a provider entry would be added), In library, Duplicate (same title as an earlier row), Open or Invalid with its reason. Per row: include (a switch; Select all switches every row), the candidate for an open row (or another search by title), the action, quality profile and library. Defaults per kind of title are offered where there is a choice.
3. Import selected: with JavaScript, the page queues a durable, owner-scoped server job and shows saved per-row stages and outcomes. Closing the tab or restarting the server does not lose committed progress; returning to Import reconnects to the job. Each row is re-planned on resume and permissions are rechecked. Unresolved rows remain reviewable rather than silently guessing a match. Creating a Work or scheduling its search does not prove that its file has downloaded or opens in the Reader.
   Without JavaScript, the ordinary HTML apply path remains available, bounded to 40 new catalog imports per request and offering explicit continuation for deferred/unavailable rows. Preview remains a request-scoped read; its streaming progress does not make an unfinished preview durable.

## Matching

Identities come first: the file's ids are looked up in `WorkExternalIdentities` (including a book's edition ISBNs and an anime's seasons) of Works with details or a library tie; a catalog title only a Discover list named counts as new. If a book ISBN is not in Jularr yet, the importer asks Open Library for the **edition's canonical Work**, verifies its title and author against the input where supplied, and uses that Work identity for the ordinary import. A conflicting explicitly supplied Open Library work ID is rejected rather than overridden. A missing/ambiguous ISBN or a disagreement in the title/author remains open for manual selection. No fuzzy hit is imported automatically. Without a local hit a provider id is used as it is: a series comes from TheTVDB by its TVDB id, a TMDB id through TMDB's link and an IMDb id through TheTVDB; a film's IMDb id finds its TMDB entry. Title, year and author only search the provider to propose candidates: one candidate that carries the whole title and the year or author is preselected, never taken; the person ticks the row. An id that names a title of another type, ids that name different titles, titles deleted with an exclusion and ids no provider knows are not imported.

For ordinary books, an exact local title/alias and creator credit are read in one batch before catalog searches. Local matches are offered as explicit `local:<WorkId>` choices; multiple matches are never selected automatically. Conflicting known identities and exclusions still reject the row. This does not merge or delete existing duplicates.

When a book has a title and author but no importable catalog ID, **Use the list's details** (`list:book`) is an explicit alternative. It adds the supplied details through the same planner/processor and durable job, without a catalog call or invented provider identity. Missing covers, language, format and other metadata remain unknown. A supplied ISBN is retained on an edition with unknown language/format, separately from catalog editions. Each credited name must fit the canonical 300-character person name limit. The short creation transaction rechecks local matches and ISBN conflicts while serializing concurrent list creation; repeat/recovery reuses one exact match and leaves ambiguity for review. Existing targets, files, profiles and roots are preserved.

Repeating a resolved entry reuses its Work; an explicit list choice also rechecks exact local matches before creation; an existing target stays as it is. Monitoring creates the title's target like the Work page does (the quality profile and library as chosen, otherwise the defaults of its type); games take their platform on the title page and are only added.

Tests: `MediaImportTests` (backend: parsing, matching, idempotence, options, permissions, failures; page) and E2E `MediaImportTests`.
