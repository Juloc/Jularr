# Books

A Book is one canonical `Work` (`WorkMediaType.Book`) with one or more `BookEdition` rows and their `BookFile`s; the reading unit is the legacy `NovelWork` (chapters, covers, progress). They meet through `WorkSourceLinks` (`NovelWork` and `BookEdition`), kept by `LegacyWorkBridge`. The audiobook is a separate target of the same Work.

## Lifecycle owners

- Request: `Pages/Books/Index` add dialog -> `AcquisitionRequestService` (`BookRequestPayload`: catalog id, title, author, language, ISBN, other provider ids). `RequestWorkBinder` resolves the one Work (any provider id or the ISBN of the book finds it), links the other ids and monitors a requested Book unless the owner decided.
- Search: `BookAcquisitionExecutor` -> `AcquisitionCore` (direct/free + OPDS + the shared indexer pipeline). `BookReleaseSelector` judges identity (title words, author, language the release names, ISBN) and numbers `Rank`; Manual Search (`BookManualSearchService`, `Pages/Admin/BookManualSearch`) uses the same plan.
- Grab/import: shared download client + `BookCompletedDownloadImportAdapter`; a direct source's import is bound to the Work by `BookAcquisitionExecutor.BindDirectImportAsync`. The importer adds a file to the Work's existing library entry, so a better format never replaces a readable copy.
- Wanted/upgrade: `BookInstalledQuality` + `BookUpgradeAssessor` read the files of the Work's editions; the shared `UpgradeWantedSource` reopens the completed request while the profile wants a better format and the Book is monitored.
- Admin: `BookWorkAdminQuery` (read model) and `Pages/Admin/Books/Work` (monitoring, Search now, Manual Search, Retry, profile, editions and files, audiobook). Wanted rows link here.

## Tests

`BooksLifecycleTests` (request to reader, PDF fallback and upgrade, wrong books and editions, one Work, failures, restart, offline storage, free edition, monitoring), `BooksAdminPageTests` (+ `BooksAdminPageHost`), `BookPdfAcquisitionTests` (shared environment), `BookCompletedDownloadImportTests`, `BookWorkAdminTests`.
