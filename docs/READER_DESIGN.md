# Reader design integration

## Approved scope

The owner approved the complete UiDesigner Reader, not just its frame. The book
reference is `bookstudy` release-v4, frozen in UiDesigner `227051b`; the frame
reference is `9cd7fd3`. Later explicit decisions override the earlier mockups:

- One Reader for prose/light novels/images, one canonical app navigation; no
  Reading destination or permanent book-specific sidebar. Cover/title/chapter
  belong in the header, with an icon fallback for missing artwork.
- Normal shows app navigation and both control strips. Theater hides app
  navigation but keeps controls. Native fullscreen shows the book and small reveal
  arrows; revealed controls overlay the unchanged canvas.
- Contents, search, notes and settings are overlays. Opening them never reserves a
  second column or reflows the book. Escape/Close returns focus to the opener.
- Paged or vertical scroll, automatic one/two pages with manual override and RTL.
  Mobile shows one cropped right-hand leaf and advances one page; its two halves
  select backward/forward gestures. The shared tool strip moves below the book by
  CSS Grid, not duplicate markup or JavaScript panel placement.
- Text selection is explicitly enabled from the header. Canonical accessible HTML,
  semantic locators, annotations and existing service/progress owners remain.
- Book thickness follows calculated Reader pages, including typography and page
  width changes, not guessed printed-edition metadata. While whole-book counting
  is incomplete/unavailable, no whole-book total or paper-block thickness is made up.
- The stacks are asymmetric by reading position (the original read effect): the stack of read
  pages on the left grows, the unread one on the right shrinks, and a thicker stack lifts its
  sheet higher and narrows it at the outer edge. The text box and the page number follow their
  own paper (`printPlacement` in `BookGeometry.ts`): the print rises with the paper's edge at its
  middle and sits centred on the paper's width, so the padding above, below, at the spine and at
  the outer edge keeps the same ratio on both pages whatever has been read.
  `LocalEpub_BookSpread_StacksFollowReadingAndTextFollowsEachPaper` and `Tools/book-geometry.test.mjs` check it.
- The page print takes each line from where the browser puts it, up to where the next line of the
  same text begins (never by the line's box height), so tight line spacing cannot print the end of a
  broken word twice; `LocalEpub_PageBreakInsideAWord_NeverRepeatsTheFirstLine` checks it with a generated EPUB.

This package starts from clean-start and carries forward the approved frame work.
The overlapping #1228 remains untouched; #289 is roadmap context, not permission
to invent unavailable TTS/OCR/processing actions. No database/schema changes or
active installation data are part of this work.

## Mockup-aligned controls

The supplied mockup remains the reference for composition and buttons, not just
the book surface. Its incorrect book-specific sidebar and excluded landscape
decoration are not imported. The normal Jularr sidebar remains unchanged.

The header is one line: back, cover, title and chapter (each one line with an ellipsis and a tip), the chapter switch and the tools
(language, appearance, paper, bookmark, notes, read aloud, search, text selection, More). The bottom bar is one line, too, with the progress
slider on its upper edge: contents, the chapter and share read, the page controls with "Pages a-b of n" (the book's pages once they are
counted, else the chapter's), theater, fullscreen and settings. Both bars separate with the app's own 1px rule. Which tools stay is
decided by width, not by breakpoints: `Overflow.ts` measures the bar and moves what does not fit, the highest rank first, into the More
menu, where an entry of the same name takes its place; a menu entry does what its tool does. The settings button of the bottom bar never
leaves, so the settings open with one press at every width. The language button is there whenever the Translation module is on; with no
reading language set up its panel says so and leads to the profile settings. The shared Range owns the slider: a thin track across the
bar with a notch at each chapter, the thumb showing while the bar is in use. It runs over the whole book (0 to 1000); dragging shows the
chapter and share under the thumb, releasing turns to that page inside the chapter or opens another chapter there.

A header button marks or clears a bookmark at the current place (per profile and book file, at most 200); the list with chapter title and position is in the notes panel, in the page panel of an image book. A prose page print draws a hyphenated line break with its hyphen and the remainder at the line start.

Read-aloud is required. The [Speech integration](SPEECH.md) connects browser device voices and the neural runtime with the canonical
server profile preferences, selection/visible-text reading and Pause/Resume/Stop. The spoken word is marked strongly and its sentence
softly, on the book's printed pages as on the canonical text; a scrolled text follows the voice (a page turn follows it in the paged
view) unless the reader scrolls by hand. A floating bar over the lower edge of the pages has pause, stop, the sleep timer (it stops
reading aloud after 10 to 60 minutes and shows the minutes left) and the voice settings. Neural-browser runtime/cache and cross-chapter
continuation remain tracked by [Speech #1299](https://github.com/Juloc/Jularr/issues/1299).

On a phone the same two bars hold what fits their width; the rest is in More. There is no second toolbar, grid dock or duplicate form.

Typography and palette buttons expose focused groups of the existing settings
form; All restores the user's disclosure state. Popovers overlay the unchanged
canvas and Escape/Close restores focus to a visible opener. Mobile fullscreen
reveal keeps the same page geometry and dock, rather than adding another toolbar.
Immersive hiding targets the actual mobile grid controls, not only their
`display: contents` wrappers; hidden controls stay inert without changing size.

## Shared controls and document ownership

`Form<T>.Range` uses the existing DataField bounds, numeric precision, indexed
names, labels/errors and local/server/read-only guards. It rejects optional or
non-numeric fields and non-increasing ranges. Native input owns interaction;
fractional output is localized without rounding away canonical precision.

Reader settings retain the approved local Form mode and device preference key.
Typography includes serif/sans, size 14–40px (20 default), line height 120–220%
(165 default), paragraph spacing 0–200% (100 default), width 40–90ch (68 default), indentation, hyphenation and original
illustrations. Typeface, size, both spacings, width and hyphenation belong to the profile (`ProfileReaderSettings`, shown on every
device; a profile that has chosen nothing keeps what each device remembers), the rest stays with the device. Image adapters omit prose-only fields. Appearance currently exposes
clean/paper and colour; the full accepted material workshop is still pending below.

The same typed Range supplies transient navigation, not another progress store:
chapter pages for prose, source page indices for images. Input previews; native
change/release commits through the canonical adapter. Integer page bounds preserve
the final End-key position. Its transport presentation removes the outer form
frame, retains an accessible label/focus indication and does not copy the control.
Native chapter/image links remain, with compact icon presentation.
Pending view/font reflow retains the latest explicit seek/turn rather than
restoring an older scheduled semantic anchor.

Prose retains authorized server chapter routes and `s<section>/b<block>` progress.
Images retain `p<page>`, solitary covers/wide pages, bounded preloading and a
12-image vertical window. Completion in scroll mode requires the loaded final
image's bottom, not merely entering that image. Scanned PDFs without prepared text
open as original pages (below), with real page progress.

## Figures

A picture worth its own room is a figure, in the reader and in the EPUB download alike
(`<figure data-figure>`, `epub:type="figure"`, the caption as `figcaption` and the
alternative text): `page` fills a page of its own (paged views break before and after
it; scroll views show it up to 90 % of the screen height), `wide` takes the width of
the text; small decoration stays in the line. A PDF page that is mainly a large picture,
a vector drawing (as an SVG that keeps its labels), a table or an irregular multi-column
layout becomes such a figure instead of a tiny inline picture; EPUB pictures become
figures by their declared kind or their size. A tap or click, Enter or Space on a figure
opens the full-screen view (`dialog.reader-figure`): the picture fits the screen; wheel,
pinch, double tap, the zoom buttons and the 100–800% range adjust magnification.
Dragging pans within the visible picture's actual fitted bounds, not the full viewport.
The close button, Escape or a tap on the empty letterboxed stage closes the modal;
pointer events in the modal cannot reopen the printed picture behind it. On the printed pages of the book view the figure is
found by position, because those pages take no pointer events. A page only the original
PDF can show stands in the text as a one-line note with a link to its page and is left
out of the EPUB. The figure's look follows the global tokens; the cursor is `zoom-in`.

## Original pages and views

A book with a PDF can be read in two views, Automatic, Smart Book or Original pages, chosen per book and
profile in the `View` group of the Reader settings and kept on every device (`ReadingPreferences`). Automatic
shows the Smart Book while its text passed the quality gate and the original pages otherwise; a choice always
wins, and a better view that becomes ready while a book is open is only offered quietly, never switched to.
The original pages are drawn by the browser (Mozilla's pdf.js, scans with JPEG 2000 and JBIG2 included) in the
same frame: one page at a time on the theme's surface, fitting the page or its width, zoom by buttons, wheel with
Control and pinch, turns by buttons, keys, swipe and the seek range, the position `p<page>` in the one progress
store. They open from the file alone, without any analysis. A scan's text, from its own recognition layer or from
the local recognizer, is shown only on pages where it holds together; any other page stays whole as a picture or
as a stand-in that the reader draws from the PDF, and links to its page in the original pages. Paragraphs carry
the page they start on, so the other view opens at the same page, search names pages and page notes keep their
page and the first words of it. An EPUB download leaves the pages out that only the original shows and says how
many. Deskew and perspective correction are not done; the page's own rotation is applied before recognition.

## Bounded whole-book measurement

`BookPages.ts` reuses the current chapter and existing authorized section reads.
One additional chapter/request/ruler is allowed at a time. `PaperMeasure.ts` is
shared with foreground CSS-column pagination and accounts for current scroll
offsets; padded blank spread faces are not counted as content pages.

Work waits for idle time, pauses on interaction/held pointers/hidden pages and
cancels stale generations. Typography, geometry, direction, illustration visibility,
hyphenation and font loading invalidate counts. A zero-size clipped inert wrapper
cannot enlarge the canvas or document. Extra HTML/images are released after each
chapter; only numeric counts remain in this Reader document. There is no second
storage/cache or automatic bulk translation.

Per-job limits: 2 MiB response, 5,000 elements, 32 illustrations, 15 seconds.
Failures/oversized/unauthorized chapters leave totals unavailable without blocking
reading or endless retry. Translated views without all translated chapter HTML
do not substitute original text counts. Scroll mode does not run this count.
Only complete counts publish the total, preceding pages and current global page;
image adapters already know their source count. Until that happens the footer explicitly
labels local chapter page numbers with a short localized chapter prefix, rather than
presenting them as whole-book page numbers. Completed numeric counts are cached
in the browser's bounded IndexedDB layout store, keyed by immutable reading document
and exact geometry/typography settings. Opening another chapter restores the whole-book
count without fetching every section again; the currently rendered chapter must still
match the saved count. Resizes, font changes and layout changes invalidate the key.
A failed/private-mode cache falls back to the existing bounded background ruler.
Ordinary page turns defer new ruler work without aborting a request already in flight.

Paged spreads reserve a responsive central CSS column gap in the canonical text
layout (mirrored inside margins, including RTL); one-page mode uses wider symmetric
text margins. The decorative book samples the same column gap, so neither text nor
page numbers intrude into the pronounced paper crown at the binding.

## Single book renderer

`BookRenderer.ts`, `BookLeaf.ts`, `BookSolid.ts` and `BookGeometry.ts` connect the
accepted geometry to both real adapters. No workshop prose, page-65 position,
320-page fixture, secondary progress store or second parser is imported.

The resting cover, cut edges, paper-tinted gutter/cast shadows are static SVG/CSS.
The accepted near-overhead paper geometry is retained. Paper thickness uses the
completed layout count, never the old character-proportion stack. The canonical
page dimensions remain independent of decorative thickness, avoiding a count/
thickness/resize feedback loop.

`PagePrint.ts` freezes only visible text paint runs, list markers, table/rule paint
and images, with measured baselines and subpixel column bounds. Prints are inert,
aria-hidden and do not retain chapter DOM or duplicate IDs. A maximum of 512 runs
per print, 12 cached prints and three concurrent leaves bounds memory regardless
of jump length. Oversized source/failure retains canonical instant paging with
the existing localized unavailable status.

Manual middle grips initially push; corner grips fold. Release starts from the
last rendered pose and retains the same substrate/shadow geometry. Rapid input
moves concurrent leaves, reverses pending reservations and compresses long jumps
into a bounded paper packet. Only actual commits update the canonical adapter.
Stationary held paper and settled paper request no animation frames. Reduced
motion/off use instant navigation while retaining the static book.

The old `PaperCurl.ts`, PageFlip declarations/license, Vite alias and `page-flip`
dependency/lock entry are removed. There is one active decorative renderer, not
an unused new engine alongside a legacy path. Reader routes deliberately remain
full document navigation; app-shell navigation was not expanded in this package.

## Verification checkpoint — 20 October 2026

Passed: TypeScript/Vite build without StPageFlip; 27 affected script, Range,
background-count and book geometry tests; six typed Form.Range binding cases
passed on the updated clean-start base. The latter verify invariant bounds/value,
precision, indexed fields, error output and server/local/read-only guards.
The current local-EPUB browser case passes, including distinct first leaves, final
paragraph, bounded printing of fonts/lists/tables/images, stationary held paper,
return without progress, three concurrent leaves, reversal, a 12-turn compressed
packet and no pending frame after settling.

The separate local-comic case passed with real distinct coloured PNG fixtures:
single cover, distinct spread, mobile one-page forward/back and viewport bounds.
The bounded background count case previously passed its reflow/pause/failure and
mobile overflow checks. Captures were visually inspected.

The mockup-control correction additionally passed its individual local EPUB and
comic browser cases. EPUB covers focused typography/paper popovers and focus
return, controls above/below the canvas, mobile dock/menu, native fullscreen
entry/exit and unchanged canvas bounds when revealing controls. An initial test
attempt incorrectly tried resizing the still-fullscreen browser; the corrected
test exits through the actual mobile More command before resizing. TypeScript/
Vite compilation passed. No backend or test-host repair was included.

After the owner's request to merge this checkpoint, the final local EPUB case
passed with the borderless strips, custom accent Range, mobile circular buttons,
normal prose/mobile captures and fullscreen menu controls. It verifies the
absence of footer borders/shadows and outer seek focus frames. The actual track
was visually checked; Chromium does not expose its native track pseudo-element's
computed background through the ordinary style API. Dedicated mobile PDF/RTL
interaction checks remain outside this final local EPUB case.

The first complete CI run passed Backend, frontend/assets, authorship, container
startup and two browser groups. Its path group exposed an older PDF Download
selector and a pending-layout/seek race after changing views. Both were corrected;
the two failed cases plus the local EPUB view/seek regression passed together
(three cases, 1m54s). The new commit must still pass the complete PR checks.
The next CI run passed the previous failures and exposed a later mobile
fullscreen visibility assertion on Linux. The actual grid controls are now hidden
explicitly, and visibility assertions wait for the browser transition. The
affected full Reader path and local control case passed together (1m23s);
cross-platform CI confirmation is still required for this correction.

The earlier combined renderer run is **not green**: EPUB passed, but the comic test's server
became unreachable during Owner sign-in and the background-count test's server
during Library setup, both before entering Reader (`ERR_CONNECTION_REFUSED`).
Existing logs contain no established exit cause. The later individual UI checks
do not establish that this separate host problem has been resolved. There were
no unrelated test-host/backend repairs, commit, push or merge.
The run is recorded in `reader-book-renderer.trx` and the local test logs.

Remaining before claiming the complete Reader accepted:
- Resolve the isolated test-server failure and finish current integration checks,
  including RTL, reduced motion, selection, lifecycle and unchanged chrome bounds.
- Transfer the remaining accepted material presets/fine controls and automatic
  light/dark paper rule; existing colour/surface controls are not that full UI.
- Visually check more complex typography (automatic hyphens, bidi and nested
  formatting), physical Safari/touch/TV and constrained-device performance.
- Seamless cross-chapter vertical loading and unavailable TTS/OCR/processing
  capabilities remain separate work, not fabricated actions.

A passing build or individual test is not acceptance of the complete Reader.
The owner has explicitly requested merging this partial checkpoint, then the
full Speech #1299 foundation. Remaining Reader capabilities above are not declared
Done by that integration; merge still requires the PR checks to pass.
