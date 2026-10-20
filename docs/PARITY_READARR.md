# Readarr parity

Owner decisions of 19 October 2026. Readarr is retired; this records what a switch-over needs. Whatever Readarr
shares with Sonarr follows the [Sonarr decisions](PARITY_SONARR.md) for books too: library views, filters, mass
editor, one management page per author, wanted lists, activity, search, manual import including unmapped files,
naming, webhook, system and access. Editions and languages follow the
[general rule](PARITY_SONARR.md#editions-and-languages-all-media-types). Build state is tracked in the
[feature matrix](FEATURE_MATRIX.md).

## Wanted

| Area | Readarr | Jularr today | Decision |
| --- | --- | --- | --- |
| Authors | Add an author with monitor all, future, missing, existing, first, latest or no books | Authors as people with Open Library ids | Like Readarr |
| Series | – | Book series with Open Library and Hardcover ids | Monitored series: new volumes of a monitored series are added by themselves |
| Rules | Metadata profile: minimum popularity, minimum pages, skip books without release date, without ISBN or ASIN, part books and sets, secondary series books; allowed languages; must not contain | – | Every Readarr rule, held in the Jularr profile (as decided on 15 October) and changeable per author; languages follow the general rule |
| Formats | Quality profile orders formats (for example EPUB before AZW3 before PDF) and upgrades | Book qualities, no format upgrades | Like Readarr: format order and upgrade limit in the profile; an EPUB replaces a PDF; the PDF → EPUB conversion stays for the reader |
| Audiobooks | A second Readarr instance | Planned as a Book subtype | **Their own media type** (replaces the subtype plan and the 15 October rule of one Work across formats): own library and view, playback with chapters, tags like music, narrator as a person |

## Later

| Area | Readarr | Decision |
| --- | --- | --- |
| Calibre | Calibre library, conversion, metadata and covers sent to Calibre | A module later |
| Import lists | GoodReads shelves, lists and series, LazyLibrarian, other Readarr | All later (Hardcover lists among them; the GoodReads API is gone) |

## Not

| Area | Readarr | Reason |
| --- | --- | --- |
| Search | Minimum age, delay profiles | No grab wait times |
| Indexers, download | Torrent and Gazelle indexers, torrent clients | Usenet only |
