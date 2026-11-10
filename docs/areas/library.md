# Library: roots, scans, identification and naming

Library roots, scans, file identification, probing, naming and the user Library page.
Index: [CODEMAP](../CODEMAP.md).
Specs: [LIBRARY_PERFORMANCE](../LIBRARY_PERFORMANCE.md).

## Decisions

Owner decisions; change them only with the owner.

- The Library defaults to actual present files (no metadata-only, Plex-only or Wanted-only titles).
  Optional availability choices union Available files, My requests and Other requests; active requests of all profiles are shared for now, under the
  existing profile/media visibility rules.
  Genre, audio/subtitle language, progress and minimum-rating filters combine with media type/year; choices within a multi-select combine with OR,
  different filters with AND.
  A separate request-sharing model is deferred.

## Backend

- [Library root services](../../src/Jularr.Backend/Service/Admin/Library/V1/LibraryRootServices.cs) (also `ReadLibraryOverviewV1`: every library with
  titles, files, size, missing, unidentified and last scan), [LibraryRootLogic.cs](../../src/Jularr.Backend/Logic/Library/LibraryRootLogic.cs): Owner
  library paths per connection without overlaps; [Library.sql](../../database/Library.sql) holds roots, inventory, tracks and Operations, and the
  `ProfileWorkStates` view: the one rule for here, requested or not requested that Discover, Library and Watchlist read.
- [StorageFileSystem.cs](../../src/Jularr.Integrations/Logic/Storage/StorageFileSystem.cs),
  [MediaProbeClient.cs](../../src/Jularr.Integrations/Logic/Media/MediaProbeClient.cs): bounded folder/file enumeration without links or traversal and
  `ffprobe` analysis.
  [Storage runtime](../STORAGE_RUNTIME.md) owns deployment/limits; [schema alignment](../SCHEMA_SNAPSHOT_ALIGNMENT.md) records snapshot coverage.
- Media naming: [NamingPattern.cs](../../src/Jularr.Backend/Logic/Library/NamingPattern.cs) is shared by automatic/manual imports, Manga uploads and
  rename previews.
  [NamingPatternLogic.cs](../../src/Jularr.Backend/Logic/Library/NamingPatternLogic.cs) owns versioned patterns and rules for films, series/Anime,
  books/Manga/light novels, music and games; migration `0010_media_naming` adds the non-video defaults with catalog IDs while preserving existing
  video patterns.
  Daily formats follow dated releases; multi-episode naming retains unit coverage.
  Tests: `NamingPatternTests`, `AllMediaNamingTests`, `VolumeUploadTests`, `NamingSettingsTests` (browser).
- Identifying library files: the scan ([OperationServices.cs](../../src/Jularr.Backend/Service/System/Operation/V1/OperationServices.cs),
  `ExecuteLibraryScanV1`) links films and series by the ids of a folder (path, nfo, .plexmatch; ids a catalog cannot check are ignored; a series is
  TheTVDB's: a folder's TheTVDB id is the series' own and TMDB's possibly stale link gives way, else IMDb's id through TheTVDB's `search/remoteid` and
  TMDB's through TMDB's link must name the same series, else title and year as one TheTVDB search result; films through TMDB; files of several
  episodes take the first as their unit and cover the others), falls back to title and year, and links book, manga and light novel folders to a Work
  here or to the most relevant catalog entry; the log of a scan names why a folder stays open.
  A file the scan leaves is identified by hand on `/Admin/Library/Unmatched`: as an existing Work or a new title (`AssignLibraryFileV1`) or, through
  *Find the work*, as a catalog entry of its library's type (`SearchMetadataV1`, then `IdentifyLibraryFileV1` imports the entry and joins the file to
  it; `BookTests.Unmatched_FileIsIdentifiedAsACatalogEntry_*`).
  Tests `LibraryTests.Scan_*`, `BookTests.Scan_BookFolders_*`.

## Frontend

- [Library](../../src/Jularr.Frontend/Pages/User/Library/Library.cshtml.cs): present files by default, optional own/other active requests; genre,
  track-language, progress and rating filters alongside type/year.
  [InfiniteList](../../src/Jularr.Frontend/Framework/Components/InfiniteList.entry.ts) appends bounded SSR pages of 48 over the shared [paging
  model](../../src/Jularr.Frontend/Framework/Components/Paging.cs), with retry, native links and Back restoration.
  Query: `type`, `year`, `sort`, `q`, `p`, `availability`, `progress`, `genre`, `audio`, `subtitle`, `rating`; `MediaTests.Library_*`,
  `LibraryFiltersTests`.

## Tests

- Library paths, scans, missing/offline handling, identification, versions, restart recovery, local paths, wake, browsing, parser, probe parsing and
  Razor forms: `LibraryTests.cs`.
- [Library read benchmark](../LIBRARY_PERFORMANCE.md): opt-in `LibraryPerformanceTests`, authenticated service/HTTP p95, SQL plans and card snapshots
  on isolated PostgreSQL.
- Library visibility and filter options: [LibraryServices.cs](../../src/Jularr.Backend/Service/User/Library/V1/LibraryServices.cs), shared read
  boundary for actual files and own/other active requests (the profile's view is read for those Work ids only, never for the catalog), real track
  languages, manual-first genres and the card's canonical rating/progress.
  Contracts: [Library.cs](../../src/Jularr.Backend/Data/User/Library/V1/Library.cs); focused tests `MediaTests.Library_*`, `LibraryFiltersTests`.
