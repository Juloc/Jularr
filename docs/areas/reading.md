# Reading: books and progress

EPUB and comic reading copies, reader settings and reading progress.
Index: [CODEMAP](../CODEMAP.md).
Specs: [READER_DESIGN](../READER_DESIGN.md).

## Backend

- Reading: [EpubReader.cs](../../src/Jularr.Backend/Logic/Reading/EpubReader.cs) (bounded, path-safe EPUB 2/3 packages),
  [ReadingHtml.cs](../../src/Jularr.Backend/Logic/Reading/ReadingHtml.cs) (HtmlSanitizer, package-only images, reader links, numbered blocks as
  locator, `AnalyzerVersion`), [ReadingLogic.cs](../../src/Jularr.Backend/Logic/Reading/ReadingLogic.cs) (shared documents by content hash, the
  reading text, structure and translations as resources, reading progress; a prepared book stays while its file is current, documents of changed or
  vanished files leave by LRU beyond the budget with 14-day protection),
  [ReadingContent.cs](../../src/Jularr.Backend/Service/User/Reading/V1/ReadingContent.cs) (storage I/O and batched preparation; comics: CBZ in place,
  CBR/CB7 unpacked once by the approved 7-Zip, pages counted as `:page/NNNNN` on first open, at most 5000 pages and 4 GiB per volume; the reader then
  loads page by page; a mislabelled CBZ goes through 7-Zip).
  Pictures and pages never enter the database (migration [0051](../../database/migrations/0051_reading_files_on_disk.sql) removed them): an EPUB's
  pictures and a CBZ's pages are read from the book file when first shown (`ReadingContent.ReadFileAsync`), what the server draws (a PDF's pictures
  and Smart Page images, a book read through a copy, CBR/CB7 pages, DjVu pages) is written while preparing; both are kept by
  [ReadingFiles.cs](../../src/Jularr.Backend/Service/Core/ReadingFiles.cs) under `DataPath/reading/<document>` (`Media:ReadingCachePath`), bounded by
  the Owner's reading cache (`ReadingCacheBytes`, 2 GiB): the books used longest ago leave first, a whole book at a time, one used in the last five
  minutes stays; a book missing on disk is drawn again on opening (`.complete` marks a finished set) or when a picture is asked for.
  `ReadReadingResourceV1` answers with an entity tag of the content, for good (`immutable`) except a PDF's (an hour, its pictures change when its
  recognition is read anew); deleting a title (`DeleteWorkV1`, the mass editor) and database eviction take the books' folders along.
  Tests `ReadingFilesTests`, `ReadingTests`, `BookFormatTests.Reader_OpensConvertedBooksAndNamesAProtectedOne`,
  `DatabaseMigrationTests.ReadingFilesOnDisk_*`, [ComicPages.cs](../../src/Jularr.Backend/Logic/Reading/ComicPages.cs) (natural page order, type by
  image signature, page limits), [PdfBook.cs](../../src/Jularr.Backend/Logic/Reading/PdfBook.cs) (a PDF's text layer through PdfPig as reflowable
  chapters: running headers and footers drop, paragraphs join across lines, pages and columns, hyphens rejoin, numbered or named lines (`Chapter`,
  `Capítulo`, `Kapitel`, ...) with their centred title start chapters, pictures stay where they stood as resources of the book (PNG where PdfPig
  decodes them, JPEG as stored); a page that is mainly a picture, a vector drawing, a table or an irregular multi-column layout becomes a whole-page
  or full-width figure (`data-figure=page|wide`, caption and text alternative kept), a page only the original can show (a picture PdfPig cannot
  decode, such as JPEG 2000/JBIG2 scans until the Ocr module has read them, or a drawing too large) stays in the text as a marked stand-in
  (`data-figure=original`) linking to the PDF page; a PDF without text or one PdfPig cannot read opens as its original pages (the original stays
  downloadable)).
  Its helpers: [PdfPageScan.cs](../../src/Jularr.Backend/Logic/Reading/PdfPageScan.cs) (words, pictures and vector paths of one page),
  [PdfLayout.cs](../../src/Jularr.Backend/Logic/Reading/PdfLayout.cs) (lines, columns, tables, captions),
  [PdfSvg.cs](../../src/Jularr.Backend/Logic/Reading/PdfSvg.cs) (a drawing region as a self-contained SVG with its text as labels, without a
  rasteriser), [PdfQuality.cs](../../src/Jularr.Backend/Logic/Reading/PdfQuality.cs) (per-page report and the verdicts that gate reading and a stored
  copy), [PdfBookHtml.cs](../../src/Jularr.Backend/Logic/Reading/PdfBookHtml.cs) (chapters as reader sections, the same figure markup for the reader
  and the EPUB), [ImageSize.cs](../../src/Jularr.Backend/Logic/Reading/ImageSize.cs) (picture size from the header: large pictures of an EPUB become
  figures too) and [reading services](../../src/Jularr.Backend/Service/User/Reading/V1/ReadingServices.cs) (also text search over the cached sections,
  the book's format and PDF files streamed for the browser's own viewer or as the original beside a reflowed PDF).
  [ReadingEbook.cs](../../src/Jularr.Backend/Logic/Reading/ReadingEbook.cs) builds an EPUB 3 from the prepared text and the pictures its sections show
  for download (`ReadReadingEbookV1`, `IDownloadOutput`): a reflowed PDF beside its original, and any book in a language once every block with text
  has a server translation (`TranslatedLanguages`; earlier versions of a model count, no single model needs to cover the book): per block the model
  chosen in `ReadingTranslationChoices`, else the first model of the default order with a text (the Owner's model, the model the book job translates
  with, the TranslateGemma models from the largest, then any other), never Mozilla text; paragraphs that kept their original stay as they are, and the
  metadata (`dc:contributor`, role `trl`) and a last page (`colophon.xhtml`) name the models and count the kept paragraphs; an incomplete translation
  is 404.
  Highlights and notes per profile and book file (block and character offsets, at most 1000 per book):
  [ReadingAnnotationServices.cs](../../src/Jularr.Backend/Service/User/Reading/V1/ReadingAnnotationServices.cs),
  [ReadingAnnotationLogic.cs](../../src/Jularr.Backend/Logic/Reading/ReadingAnnotationLogic.cs), drawn by
  [highlights.ts](../../src/Jularr.Frontend/Modules/Reader/highlights.ts).
  `ReadingDocuments`/`ReadingResources` live in [Playback.sql](../../database/Playback.sql); the budget is in `InstanceSettings`.
  Books are prepared before anyone opens them: [PrepareReadingBooksV1](../../src/Jularr.Backend/Service/System/Reading/V1/PreparationServices.cs) (job
  `readingPreparation` in [DerivationWorker.cs](../../src/Jularr.Workers/DerivationWorker.cs), one book per call, newest first, never while a person
  opens a book) prepares each book's EPUB or PDF exactly as opening it does, a scan through the Ocr module's recognizer, so the server's translation
  can take it; files that cannot be prepared are not tried again until Jularr restarts or they change.
  A PDF, and an EPUB without a language tag, take their language from their text
  ([ReadingLanguage.cs](../../src/Jularr.Backend/Logic/Reading/ReadingLanguage.cs), the Reader's common-word lists).
  Tests `OcrTests.Books_ArePreparedWithoutOpening_ScansThroughTheRecognizer`, `OcrTests.Language_IsGuessedFromCommonWords`,
  `ReadingTests.Cache_KeepsCurrentBooks_*`.
  The `Reading` instance module switches all book processing and the reader off (books stay plain files; capability `CanRead`).
- Reader text settings per profile: `ProfileReaderSettings` ([Playback.sql](../../database/Playback.sql)) keeps typeface, size, line and paragraph
  spacing, text width and hyphenation on every device of a profile
  ([ReaderSettingServices.cs](../../src/Jularr.Backend/Service/User/Reading/V1/ReaderSettingServices.cs),
  [ReaderSettings.cs](../../src/Jularr.Backend/Data/User/Reading/V1/ReaderSettings.cs),
  [ReaderSettingsLogic.cs](../../src/Jularr.Backend/Logic/Reading/ReaderSettingsLogic.cs); a profile without a row has the defaults and its devices
  keep their own).
  The same file serves where each section of a book starts (`/api/v1/reading/documents/{id}/offsets`) for the Reader's progress slider.
  Tests: `ReaderSettingsTests`, `ReadingTests.Epub_OpensFromTheCacheOfflineSharesContentAndKeepsProgress`.

## Frontend

- [Continue Reading bar](../../src/Jularr.Frontend/Framework/Components/ContinueReadingBar.cs),
  [fragment](../../src/Jularr.Frontend/Pages/User/ContinueReading/ContinueReading.cshtml.cs),
  [client](../../src/Jularr.Frontend/Framework/Components/ContinueReadingBar.ts), [tests](../../src/Jularr.Frontend/Tools/continue-reading.test.mjs):
  the most recent unfinished book or comic at the bottom of app pages; one Continue action and Dismiss, which hides it on this device until the
  position changes.
  Playback owns the slot while the mini player is open.
- [Book page count](../../src/Jularr.Frontend/Modules/Reader/BookPages.ts), [completed-count
  cache](../../src/Jularr.Frontend/Modules/Reader/BookPageCache.ts), [shared measurement](../../src/Jularr.Frontend/Modules/Reader/PaperMeasure.ts),
  [tests](../../src/Jularr.Frontend/Tools/book-pages.test.mjs): one cancellable background chapter ruler, current-layout totals only when complete;
  `ReadingPathTests.LocalEpub_BackgroundPages_PauseRestartAndFailWithoutBlockingReading` checks pause, reflow and unavailable chapters.

## Tests

- EPUB preparation, sanitizing, offline cache, shared documents, eviction, progress and unsafe files: `ReadingTests.cs`; book metadata, judging,
  Gutenberg download and content check: `BookTests.cs`; source catalog, OPDS login and signed links, Anna's Archive key handling, slow download,
  manual file links and simulated gates: `SourceTests.cs`; Narō search, chapters, resumed download and the web novel from Discover to the Reader:
  `NarouClientTests.cs`, `NarouAcquisitionTests.cs`; the browser that reads a page behind a check: `SourceBrowserTests.cs` (only with
  `JULARR_BROWSER`).
