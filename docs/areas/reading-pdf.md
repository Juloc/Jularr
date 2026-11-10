# Reading copies: PDF, DjVu, conversions and OCR

Reading copies of PDF, DjVu and other book formats, page views and OCR.
Index: [CODEMAP](../CODEMAP.md).

## Backend

- Reading copy of the other book formats: [BookConversion.cs](../../src/Jularr.Backend/Logic/Reading/BookConversion.cs) makes an EPUB in memory, which
  `ReadingContent` prepares like any EPUB (the file in the library is never changed; source at most 256 MiB, text 64 MiB, pictures 5000 and 256 MiB
  (one at most 32 MiB, read once however often referred to, at most 100,000 references), XML depth 256 (`EpubReader.LoadXml`, also for EPUB packages),
  sections within the EPUB entry limit, ZIP entries bounded, patterns bounded with a timeout; a malformed file reads as `reading_invalid_file`, also
  for any unforeseen converter failure): [Fb2Book.cs](../../src/Jularr.Backend/Logic/Reading/Fb2Book.cs) (FB2 and `.fb2.zip`: sections as chapters,
  notes, binaries as pictures), [TextBook.cs](../../src/Jularr.Backend/Logic/Reading/TextBook.cs) (TXT: paragraphs, chapter lines,
  UTF-8/UTF-16/Windows-1252), [RtfBook.cs](../../src/Jularr.Backend/Logic/Reading/RtfBook.cs) (paragraphs, bold, italic, heading styles and outline
  levels, PNG/JPEG pictures), [DocxBook.cs](../../src/Jularr.Backend/Logic/Reading/DocxBook.cs) (heading styles, emphasis, tables, embedded pictures),
  [MobiBook.cs](../../src/Jularr.Backend/Logic/Reading/MobiBook.cs) (PalmDOC text, MOBI/AZW HTML split at page breaks with `filepos` links and image
  records, KF8/AZW3 skeletons and fragments from their INDX tables, combined files read their KF8 part; an encrypted book is `reading_drm`, HUFF/CDIC
  compression `reading_kindle_compression`).
  The reader opens an own EPUB or comic first, then a PDF's ready copy, then such a book, then the PDF, then a DjVu.
  Tests `BookFormatTests` (fixtures built in code, `BookFormatFixtures`), E2E `ReadingPathTests.LocalFb2_*`.
- DjVu books (`.djvu`, `.djv`; content check `AT&TFORM` with `DJVU`/`DJVM`, reason `content_djvu`):
  [DjvuTools.cs](../../src/Jularr.Integrations/Logic/Djvu/DjvuTools.cs) runs DjVuLibre's `djvused` (page count and sizes) and `ddjvu` (one page as
  PNM, turned into PNG for black and white, else JPEG) without a shell, by full path, with a time limit and bounded output; a page is drawn at most
  1800×2400 and never enlarged (`Reading:DdjvuPath`, `Reading:DjvusedPath`, else found on the `PATH`; without them the reader says
  `reading_djvu_unavailable`).
  `ReadingContent` opens a DjVu as a comic document of its counted pages (`ReadingStructure.RendersPages`); the Reader's page handler
  (`/Read/{id}?handler=Page`) asks [RenderReadingPageV1](../../src/Jularr.Backend/Service/User/Reading/V1/ReadingServices.cs), which serves a page
  from the disk (`ReadingFiles`) or draws it with the next three (one batch on the server at a time, a book on a direct share copied aside first) and
  keeps them there.
  Tests `DjvuTests` (books made with `c44`, `cjb2` and `djvm`; `JULARR_TEST_DJVULIBRE` names the tools' folder, CI installs `djvulibre-bin`), E2E
  `ReadingPathTests.LocalDjvu_*`.
- Reading copy of a PDF (module `SmartPdf`, off by default): [DerivationWorker.cs](../../src/Jularr.Workers/DerivationWorker.cs) runs
  [DeriveReadingEbooksV1](../../src/Jularr.Backend/Service/System/Reading/V1/DerivationServices.cs) one PDF per call (never while a person is opening
  a book, `ReadingActivity`); [ReadingDerivation.cs](../../src/Jularr.Backend/Service/User/Reading/V1/ReadingDerivation.cs) converts it with `PdfBook`
  and, only when the stricter ebook verdict passes (digital text, every page shown, no scan, no page left to the original), writes `<name>.smart.epub`
  beside the PDF through [LibraryFileWriter.cs](../../src/Jularr.Backend/Service/Core/LibraryFileWriter.cs) (`.partial`, atomic, never over a foreign
  file) and assigns it to the same media asset.
  `ReadingDerivations` (source size, modified time, SHA-256, analyzer version, status Preparing/Ready/Declined/Failed with reason, counts) and the
  view `ReadableStoredFiles` in [Playback.sql](../../database/Playback.sql) make a copy count only while it is Ready and its PDF unchanged; a book
  with its own EPUB is never converted and the original PDF is never touched.
  [ReadingServices.cs](../../src/Jularr.Backend/Service/User/Reading/V1/ReadingServices.cs) prefers an own EPUB, then a current copy, then the PDF and
  reports the state the reader shows.
  Failure classes are measured with the opt-in [PdfCorpusTests.cs](../../tests/Jularr.Backend.Tests/PdfCorpusTests.cs) (`JULARR_TEST_BOOKS` names a
  folder of books, `JULARR_TEST_REPORT` the report file: pages by kind, characters, chapters, pictures found/kept/unreadable, figures, verdicts; not
  run without the variable).
  Tests `PdfBookTests` (PDFs generated in code), `ReadingDerivationTests`,
  `ReadingPathTests.LocalPdf_PageOfItsOwnPicture_ReadsAsFigureAndOpensFullScreenWithZoom`.
- Views of a PDF book, page anchors and page notes: `ReadingPreferences` ([Playback.sql](../../database/Playback.sql)) keeps per profile and Work the
  language and the view (`ReadingView`: Smart Book, Original pages or Smart Pages; none is automatic) on every device
  ([SaveReadingViewV1](../../src/Jularr.Backend/Service/User/Reading/V1/ReadingViewServices.cs),
  [ReadingLogic.cs](../../src/Jularr.Backend/Logic/Reading/ReadingLogic.cs)).
  The reader opens a book in the profile's choice, else the Smart Book while the prepared text passed its gate (`Recommended` in the document's
  structure, from `PdfQuality.Judge`), else Smart Pages where the scan's pages were set anew, else the original pages, which need no analysis and open
  from the file alone; a choice always wins, and a better view is only offered quietly
  ([ReadReadingStateV1](../../src/Jularr.Backend/Service/User/Reading/V1/ReadingViewServices.cs)).
  The page gate of a scan's recognition layer or recognizer ([PdfQuality.Unreliable](../../src/Jularr.Backend/Logic/Reading/PdfQuality.cs),
  [PdfLayout.IsInterleaved](../../src/Jularr.Backend/Logic/Reading/PdfLayout.cs)) shows text only where words look like words and lines run in one
  order; else the page stays a whole page picture, or a stand-in for the original page, which the reader draws in its place with pdf.js (an EPUB
  download leaves such pages out and says how many).
  Every paragraph carries `data-pdf-page` (and `data-pdf-end` where it runs on) and every section its page range;
  [ReadReadingPositionV1](../../src/Jularr.Backend/Service/User/Reading/V1/ReadingViewServices.cs) maps a page to its section and block and back,
  search hits name their page, and page notes (`ReadingPageNotes`: page and the first words of it) are kept per profile and book.
  Tests `ReadingTests.Pdf_ReadsInTheViewTheProfileChose_*`, `ReadingTests.ReadingView_*`, `PdfBookTests`, E2E `ReadingPathTests.LocalPdf_Scan_*`.
- OCR (module `Ocr`, off by default; models in `DataPath/ocr`): a PDF without a text layer, or with scanned pages that carry only a hidden recognition
  layer (Internet Archive scans; they read from that layer meanwhile, `PdfBookContent.WantsRecognition`), waits in `ReadingRecognitions`
  ([Playback.sql](../../database/Playback.sql)), queued by a person opening it or by the reading-copy job, and opens as its original pages meanwhile.
  [DerivationWorker.cs](../../src/Jularr.Workers/DerivationWorker.cs) (the one background job of reading text) also runs
  [RecognizeReadingV1](../../src/Jularr.Backend/Service/System/Reading/V1/RecognitionServices.cs): a bounded slice of a scan's pages per call (8 pages
  or 20 seconds), the page a reader has open first (`WantedPage`, set by the progress save `p<page>`), then a rest as long as it worked (four times as
  long within 30 seconds of any use, [ReadingRecognitionPlan.cs](../../src/Jularr.Backend/Logic/Reading/ReadingRecognitionPlan.cs)); never while a
  person is opening a book.
  Each scanned page goes through [PdfScanPages.cs](../../src/Jularr.Backend/Logic/Reading/PdfScanPages.cs): drawn upright at about 300 dpi by PDFium
  ([PdfRendering.cs](../../src/Jularr.Integrations/Logic/Pdf/PdfRendering.cs), PDFiumCore; JPEG 2000 and JBIG2 layers too, one call at a time), read
  by [OcrEngine.cs](../../src/Jularr.Integrations/Logic/Ocr/OcrEngine.cs) (PP-OCRv5 mobile through ONNX Runtime on a quarter of the cores; pieces of
  large type joined into their line before reading, a region run over a column gutter parted; uncertain lines left out) and analysed by
  [PdfScanAnalysis.cs](../../src/Jularr.Backend/Logic/Reading/PdfScanAnalysis.cs): an unsure word takes the scan's own layer word at the same place,
  quotes the recognizer dropped come back from it, the text is erased from a small copy of the page (each line box filled from the paper above and
  below) and large connected areas left are pictures (parts of one illustration joined), short text in a picture's rows is its label.
  The page is kept in `ReadingRecognizedPages` as lines in fractions of the page (`k`: `label` or `picture` for those), so an interrupted scan goes on
  where it stopped and a changed file starts over.
  The last slice resets the prepared text of the PDF and prepares it again: [PdfBook.cs](../../src/Jularr.Backend/Logic/Reading/PdfBook.cs) reads the
  stored pages like a recognition layer (labels stay out of the running text) and cuts each picture from the page PDFium draws as a JPEG with the
  scan's paper tint taken out, the short text right below it as its caption (a picture over most of the page shows as a page of its own); a page whose
  text does not hold together keeps its pictures, else stays with the original.
  Smart Pages ([PdfSmartPages.cs](../../src/Jularr.Backend/Logic/Reading/PdfSmartPages.cs)): every scanned page read plausibly (not a cover) is drawn
  again with each line it read filled with the paper around it (a JPEG of 1600 pixels, `pdf/smart/NNNNN.jpg`), and its text becomes blocks at the
  places of the original (lines joined by their baselines' pitch, indent, size and colour, column by column, a line read across two columns parted
  again; each block keeps its lines and where they broke; labels in series; lettering in a photograph stays in it; text flowing around a picture one
  block in several boxes, so it translates as one sentence; a lead-in in its own look a block of its own inside its block; long rules put back after
  erasing), each run in the look measured on the scan from the word's place the recognizer kept (`w` in `ReadingRecognizedPages`): serif or sans,
  weight, slant, width, size and colour (the paper the commonest colour in and around the line).
  The pages are reading units of their own (`Structure.SmartPages`, `:smartpage/NNNNN`, read and translated as section 100000 and on by the same
  services and the `bookTranslations` job, the page being read first).
  Files over 512 MiB are not drawn and keep their layer and the original pages.
  [OcrFiles.cs](../../src/Jularr.Backend/Service/Core/OcrFiles.cs) fetches the detection model and the recognition model of the book's script once
  from RapidOCR's ModelScope files (`Providers:OcrModels`, pinned by size and SHA-256 in
  [OcrModels.cs](../../src/Jularr.Integrations/Logic/Ocr/OcrModels.cs)).
  Tests `OcrTests` (slices, open page first, resuming, noise of an illustration, picture and labels, layer words where unsure, a scanned page with its
  picture and caption and its Smart Page; the real models only with `JULARR_OCR_MODELS`).

## Frontend

- [Original pages](../../src/Jularr.Frontend/Modules/Reader/OriginalPages.ts) inside the same frame: one page of the PDF at a time, drawn by Mozilla's
  pdf.js (script, worker and decoders bundled or copied beside the build by [pdf-assets.mjs](../../src/Jularr.Frontend/Tools/pdf-assets.mjs);
  [PdfEngine.ts](../../src/Jularr.Frontend/Modules/Reader/PdfEngine.ts) loads it lazily), fit to the page or its width, zoom by buttons, wheel with
  Control, pinch and double tap, turns by buttons, keys, swipe and the seek range, the same progress store (`p<page>`).
  Smart Pages ([SmartPages.ts](../../src/Jularr.Frontend/Modules/Reader/SmartPages.ts), styles and the two bundled variable fonts Source Serif 4 and
  Roboto Flex in [SmartPages.scss](../../src/Jularr.Frontend/Modules/Reader/SmartPages.scss) and `Modules/Reader/Fonts`, OFL) show a scan's prepared
  page in place of the drawn one: the text-free page with the text set line by line into the original's lines (`data-rows`: left edge, baseline,
  width) at the measured size, the original's own line breaks kept, a line made smaller only where our face runs wider
  ([SmartPageFit.ts](../../src/Jularr.Frontend/Modules/Reader/SmartPageFit.ts)), labels of a series in one size, a short label and a running head in
  capitals spread like the print; in another language the server's TranslateGemma translation (cached, else block by block while the page is in view)
  in the look of each sentence's main text (a lead-in apart), flowing from line to line through a block's lines and, around a picture, through its
  boxes, all of it smaller where it does not fit, the language chosen in the bar's language panel; Mozilla's emergency as in the Reader, through the
  same `browserTranslator`: the page in view and the next three Smart Pages (`pagesAhead`), moving along with each page shown.
  The view switch ([ViewChoice.ts](../../src/Jularr.Frontend/Modules/Reader/ViewChoice.ts), group `View` of the settings: Automatic, Smart Book, Smart
  Pages, Original pages) and the quiet offer of a better view ([ViewSwitch.ts](../../src/Jularr.Frontend/Modules/Reader/ViewSwitch.ts)); the
  arithmetic is in [PageView.ts](../../src/Jularr.Frontend/Modules/Reader/PageView.ts).
  [Figures](../../src/Jularr.Frontend/Modules/Reader/Figures.ts) draws the stand-ins for original pages in the Smart Book from the same PDF.
  Tests `Tools/page-view.test.mjs`, `ReadingPathTests.LocalPdf_Scan_*`.
