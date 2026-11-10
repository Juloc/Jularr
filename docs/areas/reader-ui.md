# Reader UI

The Reader page: sections, bars, original pages and the book view.
Index: [CODEMAP](../CODEMAP.md).
Specs: [READER_DESIGN](../READER_DESIGN.md).

## Decisions

Owner decisions; change them only with the owner.

- The Reader's translation notices share one look (`reader__notice`, tokens only): the progress and its failure float as a centered pill above the
  bottom bar (a working ring, the danger tint for a failure), the Codex bar and the page notes stand under the reader as one card with the mood's icon
  and tint (`data-tone`: working/rest, done, warning, problem), and a notice inside a panel is plain text.
  No unstyled lines, no box inside a box.

## Frontend

- [Reader](../../src/Jularr.Frontend/Pages/User/Reader/Reader.cshtml.cs), [client](../../src/Jularr.Frontend/Modules/Reader/reader.entry.ts):
  sanitized sections, device settings (including the four page margins as a share of the page width, applied as the paged content's padding and the
  gap between the two pages, and the book view's binding: paperback or hardcover; the bar's reading-mode button cycles flat pages, book and scroll;
  arrows beside the page in [ReaderFrame](../../src/Jularr.Frontend/Framework/Components/ReaderFrame.cs), chapter ways in a scrolled
  [ReaderSection](../../src/Jularr.Frontend/Framework/Components/ReaderSection.cs)), chapters of the same book opened in place ([chapter
  navigation](../../src/Jularr.Frontend/Modules/Reader/ChapterNavigation.ts): the next reader is built hidden and swapped in, the old one ends through
  its [lifetime](../../src/Jularr.Frontend/Modules/Reader/Lifetime.ts)), semantic locators and end-of-content completion.
  [bookmarks](../../src/Jularr.Frontend/Modules/Reader/bookmarks.ts) (header button marks or clears the current place, the list sits in the notes
  panel, in the page panel of an image book), [ReaderFrame](../../src/Jularr.Frontend/Framework/Components/ReaderFrame.cs), [view/settings
  controls](../../src/Jularr.Frontend/Modules/Reader/Settings.ts) and [paper paging](../../src/Jularr.Frontend/Modules/Reader/PaperPages.ts) share the
  approved [Reader design](../READER_DESIGN.md): normal/theater/fullscreen chrome and grouped typed appearance fields.
  [Translation](../../src/Jularr.Frontend/Modules/Reader/Translation/translation.ts) picks the book's language (tag, else kana/kanji or common words)
  and shows the server's TranslateGemma translation, asking the server for the blocks still missing;
  [Retranslate.ts](../../src/Jularr.Frontend/Modules/Reader/Translation/Retranslate.ts) is the Reader's one re-translation, in the language panel: a
  chosen model (`/api/v1/translation/local/models`, Codex only with `instance.settings.update`), then the paragraph at the top of the page, the
  chapter or the whole book through `translator().retranslate`, which keeps the choice first and each block's earlier text until its new one arrives;
  a whole-passage model (Codex) re-translates the chapter or the book chapter by chapter with progress and Stop through
  [codex.ts](../../src/Jularr.Frontend/Modules/Reader/Translation/codex.ts); the panel names the paragraph's model; a block from another model carries
  `data-translation-model` (a faint dotted underline); the path through [a
  worker](../../src/Jularr.Frontend/Modules/Reader/Translation/translation.worker.ts) with Mozilla's [Bergamot
  loader](../../src/Jularr.Frontend/Modules/Reader/Translation/bergamot-translator.js) (MPL-2.0, Firefox revision 2c8de06acae0 for engine 3.0; local
  changes: default export, `globalThis` for strict mode) runs only as an emergency, when the Mozilla module is on and the server's TranslateGemma
  failed or is too slow (`serverIsSlow`; a slow server goes on and its text replaces Mozilla's as it arrives), and keeps its results only in the
  browser (IndexedDB, a week, 300 MB), never on the server; it translates only its window (`windowOf`, `AheadPages`): the page or spread in view and
  the three pages after it, flat or as a book (the same CSS-column pages, `ahead()` of [paper
  paging](../../src/Jularr.Frontend/Modules/Reader/PaperPages.ts)), in a scrolled text the screen in view and three screens below it; each page turn
  or scroll moves the window along (`moved`), and no block goes to Mozilla twice; marks pause while a translation shows.
  [Figures](../../src/Jularr.Frontend/Modules/Reader/Figures.ts) ([styles](../../src/Jularr.Frontend/Modules/Reader/Figures.scss)) open page and wide
  figures in the full-screen view with zoom and pan, fitted to the screen: by a tap in scrolled text, by a double click or double tap on pages (flat
  or book, the printed figure pages found by position), where a single tap and the page arrows never open a picture (owner 2026-10-28); and give the
  stand-in for a page only the original PDF shows its link; the view is the `FigureViewer` dialog of the [Reader
  page](../../src/Jularr.Frontend/Pages/User/Reader/Reader.cshtml.cs), which also shows the state of a book's reading copy (`reader.smart.*`).
  Image content uses the same frame with the [image adapter](../../src/Jularr.Frontend/Modules/Reader/ComicPages.ts): one page or a spread (cover and
  wide pages alone), reading direction (Manga right to left), three pages preloaded, `p<page>` as locator; without script, page links.
  A DjVu book's pages come through the page's `OnGetPageAsync` (`?handler=Page`), which has the server draw a page when it is first shown.
  The Back arrow ([BackTarget.ts](../../src/Jularr.Frontend/Modules/Reader/BackTarget.ts), `reader-back.test.mjs`, E2E
  `BackArrow_LeavesTheBookInOneStep...`) leaves the book one step to the page it was opened from (the link to the title's details only where no page
  of this app came before); another chapter opened in place replaces the book's history entry instead of adding one.
  Without a local model the Owner's Reader keeps the original text and offers Codex by button in the bar under it
  ([CodexBar](../../src/Jularr.Frontend/Modules/Reader/Translation/CodexBar.ts): Translate with Codex, the next chapter, Stop, Try again, time left;
  [CodexChapter](../../src/Jularr.Frontend/Modules/Reader/Translation/CodexChapter.ts) loops the chunk requests, tests `Tools/codex-chapter.test.mjs`,
  `ReaderCodexTests`).
- Reader bars ([READER_DESIGN](../READER_DESIGN.md)): the header and the bottom bar are one line each;
  [Overflow.ts](../../src/Jularr.Frontend/Modules/Reader/Overflow.ts) measures them and moves the tools that find no room, in the order of their
  `data-overflow-rank`, into the More menu, where a stand-in with `data-overflow-for` appears (arithmetic in
  [OverflowFit.ts](../../src/Jularr.Frontend/Modules/Reader/OverflowFit.ts)); the settings button of the bottom bar never leaves.
  The progress slider on the bar's upper edge ([ProgressBar.ts](../../src/Jularr.Frontend/Modules/Reader/ProgressBar.ts),
  [ProgressMath.ts](../../src/Jularr.Frontend/Modules/Reader/ProgressMath.ts)) runs over the whole book with a tick at each chapter, previews chapter
  and share while dragged and seeks by page inside the chapter or opens another with `?s=&f=`.
  Reading aloud marks the word and, softer, its sentence, follows a scrolled text and has a sleep timer
  ([Speech.ts](../../src/Jularr.Frontend/Modules/Reader/Speech.ts), [SpeechFollow.ts](../../src/Jularr.Frontend/Modules/Reader/SpeechFollow.ts)).
  The text settings of a profile are saved with `/api/v1/reading/settings` ([Settings.ts](../../src/Jularr.Frontend/Modules/Reader/Settings.ts));
  tests `Tools/reader-v3.test.mjs`, `ReaderV3Tests`.
- [Reader book](../../src/Jularr.Frontend/Modules/Reader/BookRenderer.ts), [scene](../../src/Jularr.Frontend/Framework/Components/ReaderBook.cs),
  [leaf](../../src/Jularr.Frontend/Modules/Reader/BookLeaf.ts), [solid](../../src/Jularr.Frontend/Modules/Reader/BookSolid.ts), [geometry
  tests](../../src/Jularr.Frontend/Tools/book-geometry.test.mjs): one bounded decorative engine over canonical prose/image pagination; replaces
  StPageFlip.
  [Page prints](../../src/Jularr.Frontend/Modules/Reader/PagePrint.ts) retain visible runs, not chapters.
  `ReaderV3Tests.DoublePage_*` checks paper-centred numbers and matching text insets in both reading directions.
  `ReadingPathTests.LocalEpub_ReaderOpening_ReturnsContent` and `LocalComic_BookRenderer_CoverSpreadAndMobile` cover actual pages and gestures;
  current integration gaps are in [Reader design](../READER_DESIGN.md).
