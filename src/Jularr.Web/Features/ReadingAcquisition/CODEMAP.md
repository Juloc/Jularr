# Manga and Light Novel acquisition

A Manga is one canonical `Work` (`WorkMediaType.Manga`); the library's own reading unit is the legacy `MangaSeries` with its `MangaChapters` (raw tables, string ids). They meet through `WorkSourceLinks` (`MangaSeries`). Light Novels share the engine, parser, judge, Manual Search and import skeleton but not the unit model below.

## Lifecycle owners

- Request: Discover (`Pages/Manga/Index`) -> `AcquisitionRequestService` (AniList id, title, native title). `RequestWorkBinder` resolves the one Work and monitors a requested Manga unless the owner decided.
- Structure: `ReadingStructureService.RefreshAsync` asks AniList (`MangaAniListService.GetAsync`) and creates `WorkVolume` rows (else `WorkChapter` rows) with provider identities through `ReadingUnits`; it also adds the native and English titles as aliases and ties library files that name their volume or chapters. First search and the Admin page's "Update from AniList" call it.
- Coverage: `ReadingCoverageService` is the one calculation of installed / partial / missing / wanted per volume and chapter (a volume is installed when a library file is tied to it or all its chapters are; a chapter inside a monitored volume is covered by the volume). `WantedSql` (`intended_units`) applies the same rules; `MangaUnitCoverageTests.AssertWantedMatchesCoverageAsync` keeps them equal.
- Search: `MangaAcquisitionRequestExecutor` -> `ReadingAcquisitionEngine.TargetAsync` (aliases + `ReadingWant`: missing and held units) -> `AcquisitionCore` with `ReadingReleaseJudge`. Identity is judged before quality; releases covering more missing units win, held units cost, a release naming no volume or chapter is Ambiguous (manual review only). Manual Search (`ReadingManualSearchService`, `Pages/Admin/ReadingManualSearch`) builds the same target, so Rank 1 is what automatic acquisition grabs.
- Import: `MangaCompletedDownloadImportAdapter` imports the files into the series, then `ReadingImportTies` ties every file the release brought to the volume or chapters it holds (`WorkUnitBindings`; one local file may cover several chapters or volumes) and reconciles Wanted.
- Wanted: a Work without a structure, or with library files nothing is tied to, is wanted as a whole title; otherwise per unit. `WantedRequestSource` opens the request, the same request reopens while units are missing.
- Admin: `MangaWorkAdminQuery` + `Pages/Admin/Manga/Work` (monitoring per Work, volume and chapter, Search now for the title or one unit, Manual Search, Retry, profile, history, files). Wanted rows and the library series page link here.

## Not covered

Manga quality upgrades (a CBZ replacing a ZIP) are not implemented: the importer keeps both files and the reader would list the volume twice.

## Tests

`MangaReleaseCoverageTests` (parser and judge), `MangaLifecycleTests`, `MangaUnitCoverageTests` (volumes, chapters, ranges, overrides, existing library), `MangaManualSearchTests`, `MangaRecoveryTests`, `MangaAdminPageTests` (+ `MangaAdminPageHost`); the Manga half of `BookPdfAcquisitionTests.BookAcquisitionEnvironment` (`aniList:` argument) is their shared wiring.
