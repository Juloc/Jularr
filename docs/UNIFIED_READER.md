# Unified Reader

Jularr exposes one Reader product. Books, Light Novels and Web Novels use the
same shell, settings surface, preference store, theme runtime and interaction
rules. Source-specific pages are adapters that provide content, navigation,
translations and annotations.

Implementation completion is dependency-ordered in
`docs/implementation/unified-reader-engine-completion.md`. That pack coordinates
the remaining #289 engine consolidation with canonical progress from #662 and
PDF Smart Book from #819.

## Document model

`ReaderDocumentDescriptor` describes the content without relying on its URL.
It supplies:

- content type
- layout kind
- genres
- capability flags

The descriptor is not the renderer document itself. Renderer-facing content must
preserve stable source identities, structure/source mapping and semantic content
needed for exact locators, illustrations, annotations, translation and
cross-view restoration.

Current reflowable types are Book, Light Novel and Web Novel. PDF books can
expose Original fixed pages and, through #819, a derived Smart Book view. Manga
and comics are image sequences. Fixed-layout EPUB, magazines and artbooks use
fixed/image layouts when their parsed document structure requires it.

Renderer selection follows parsed document layout and capabilities, never only
the route or file extension. A normal EPUB can be reflowable while a
fixed-layout EPUB is fixed pages; one PDF can expose both Original and Smart
views without becoming two Reader products.

## Capabilities

The settings surface is capability-driven. Reflowable documents expose
typography, continuous and paged reading, text selection, annotations,
auto-scroll and two-page layouts. Future image/fixed documents can omit
typography and expose zoom/page-layout controls instead.

Unsupported controls must not be rendered just because another document type
uses them.

Capabilities are document-instance facts. A digital PDF with a reliable text
layer may expose selection/search/TTS while a scan does not until OCR exists.
Likewise, image documents may gain OCR/search/Learning without changing their
Reader shell.

Keep page-flow direction, text direction and writing mode separate. Manga/comic
LTR/RTL page ordering, Webtoon-style vertical flow and Japanese vertical text
(`vertical-rl`) are different concerns.

## Format and layout routing

The shared Reader supports source formats through document/import adapters:

- normal EPUB/Web/LN text -> reflow renderer;
- fixed-layout/pre-paginated EPUB -> fixed-page renderer;
- original PDF -> fixed-page renderer;
- PDF Smart Book -> semantic/reflow view mapped back to original source regions;
- CBZ/ZIP and, when safely supported by import adapters, CBR/CB7 -> image-sequence renderer;
- magazines/artbooks -> fixed pages or image sequence unless a reliable semantic
  derived view exists;
- scans -> original fixed/image view with optional OCR/Smart derivation.

MOBI/AZW/AZW3/DJVU are not reasons for another Reader. They remain explicitly
unsupported until a safe parser/import adapter can emit the same canonical
document contract. DRM is never bypassed.

Source illustrations, generated chapter artwork and Reader theme artwork are
different assets. Source images participate in document layout; accepted
generated chapter artwork is optional presentation and must never become a
durable progress anchor.

## Preference cascade

Reader settings are stored only in the existing `ReaderPreference` store.
Every field is nullable at a scope so inheritance remains field-level.

Effective order, low to high:

1. system content-type preset
2. profile global scope: `default`
3. profile type scope: `type:<content-type>`
4. matching profile genre scopes: `genre:<priority>:<genre>`
5. profile work scope: `work:<id>`

Matching genre scopes are deterministic: greater numeric priority wins a field;
ties use the normalized genre key. A genre scope affects only fields it
actually overrides.

The UI can reset one field at a scope without removing unrelated overrides.
If the final field is reset, the empty preference row is deleted.

## Built-in presets

System presets provide a useful first experience, not hard limits:

- Book: paged, classic chapter treatment, cream paper, conservative artwork
- Light Novel: paged, Light Novel heading, embedded-image friendly, automatic
  genre artwork
- Web Novel: continuous, auto-scroll capable, modern chapter treatment
- Manga: image-sequence capabilities, page/spread-oriented
- Fixed document: fixed-page capabilities, paging/zoom-oriented

Profiles can override these globally, by type, by genre and per work.

## Shared settings surface

Both `/Novels/Read` and `/Books/Read` render
`Pages/Shared/_ReaderSettingsPanel.cshtml`.

The runtime reorganizes the canonical controls into:

- Lesen
- Text
- Aussehen
- Vorlesen (only when the document supports read-aloud)
- Defaults

Scroll/Pages is a segmented primary choice. Scroll-only and page-only controls
are contextual. Fine visual effects remain secondary. The Defaults tab chooses
where subsequent edits are persisted: this work, this content type, one of the
document genres, or the profile global default.

Each effective setting exposes its source and can be reset to inherit again.

## Chrome behavior

`reader-shell.js` owns chrome visibility for every unified reader:

- visible on initial chapter load
- progress restoration does not count as user scroll
- meaningful downward scroll hides chrome
- upward scroll reveals it
- center tap toggles chrome
- desktop top-edge pointer reveals chrome
- open settings/drawers prevent auto-hide
- mobile uses a bottom action bar with large targets

In paged mode the shared shell routes swipes and left/right edge taps to the
source reader's page-turn handler. Text selection takes precedence.

The thin progress indicator is intentionally independent of chrome.

Shared Reader extensions mount through `root.readerShell` (settings command,
reset/source badges, overflow and mobile actions) instead of patching source
readers. Read-aloud (`reader-tts.js`, see `docs/TTS.md`) is the first one.

## Reader frame

Pages that render the shared frame opt in with `data-reader-frame` on the
reader root. The Books reader is the first; Light Novel and Manga follow the
same structure. The frame keeps Jularr's app sidebar: the reader never builds a
second app navigation, it only lays out its own area.

Structure (markup classes live in `reader-shell.css`, icons in
`Pages/Shared/_ReaderIcon.cshtml`, text in `reader.frame.*`):

- top bar (`.reader-frame-top`, `data-reader-chrome-primary`): back, title and
  subtitle, then the reader's primary actions. On phones back, title and More
  stay in the top bar; contents, read-aloud, appearance and, when more than one
  readable language/view exists, language remain directly reachable in the
  primary mobile reader controls. Secondary actions stay in More.
- contents panel (`[data-reader-contents]`) inside the reader area with tabs
  (`data-reader-contents-tab` / `data-reader-contents-panel`). From 1100 px it is
  a collapsible side panel whose open state is a device convenience in
  localStorage; below that it overlays the reader area as a modal panel with
  focus trap and backdrop.
- bottom bar (`.reader-frame-bottom`): contents, progress slider
  (`[data-reader-progress-slider]`, `[data-reader-progress-text]`), then
  type-specific transport and settings buttons. On phones it becomes the page
  slider with previous/next page and the tool row (`.reader-frame-tools`).
- popover menus: any `[data-reader-menu-toggle="name"]` opens
  `[data-reader-menu="name"]`; one menu at a time, Escape/outside click close,
  arrow keys move between items, activating a `menuitem`/`menuitemradio`
  closes the menu. Phones show menus as bottom sheets over a scrim.
- settings sheet: `[data-reader-settings-open="tab"]` opens the shared
  `_ReaderSettingsPanel` at `reading`, `text`, `appearance`, `tts` or `defaults`.
- quick controls in the frame are preference proxies, not a second settings
  implementation. `data-reader-proxy="field"` mirrors one canonical setting;
  `data-values` can change a small coherent set such as reading mode + chapter
  style. `reader-shell.js` synchronizes those controls with the shared settings
  form and persists through the same ReaderPreferences command path.
- `[data-reader-fullscreen-toggle]`, `[data-reader-share]`,
  `[data-reader-timer="minutes"]` and `[data-reader-page-step="±1"]` are handled
  by the shell. The reading timer stops read-aloud and dispatches
  `jularr:reader-timer-end`.

Source adapter contract:

- the adapter dispatches `jularr:reader-location` with
  `{ value, max, text, valueText }` whenever the position changes; the shell
  updates the slider.
- the shell dispatches `jularr:reader-seek` (`{ value }`) while the slider is
  dragged, `jularr:reader-page-edge` for taps, swipes and page buttons,
  `jularr:reader-contents` (`{ open, tab }`) so lists load lazily, and
  `jularr:reader-layout` when the reader area changes size.
- chrome only auto-hides on phones in Scroll mode; elsewhere the bars are part
  of the layout so the page area never jumps.
- language/view availability is supplied by the source adapter from the shared
  work translation inventory. Book/PDF translated editions are shared content,
  not profile or Learning state. Learning capabilities only control learning
  assistance; missing translation generation follows the canonical AI/runtime
  policy and never requires Learning to be enabled.

### Books adapter

`books-reader.js` pages the chapter into CSS columns inside a paper spread
(`book-reader.css`): two pages from 860 px of reader width when "Two pages" is
on, otherwise one page sized by the text-width setting. Page turns are a
transform on the column box, so long chapters are laid out once per size or
setting change. A paragraph anchor keeps the reading position across layout
changes (font load, window size, contents panel, mode); repeated relayouts
without reader movement reuse the same anchor so the position does not drift.
Scroll mode keeps window scrolling. The chapter opening shows the chapter
number and an ink ornament; the sakura branch and ink mountains are drawn only
in the page margins (two masked bands of one rotated image), never over text.

Chapter ends continue to the adjacent chapter on user page turns (previous
chapter opens at its end). Read-aloud page following never changes chapters.
`?p=<paragraph>` opens a chapter at a paragraph (search hits, highlights).
In-book search is `?handler=Search&q=` (`ReaderTextSearch`, bounded to 40
candidate chapters per source and 60 hits, original text plus current
translations). Bookmarks toggle on the current page and go through the offline
sync queue like progress. A chapter opens at the saved position only when the
saved progress is in that chapter (or at `?pos=`), otherwise at its start.

The progress slider runs from 0 to the chapter's page count in Pages mode and
its value is the last page on screen, the page the percentage in
`{page} / {total} ({percent}%)` is computed from, so the handle always matches
the label (Scroll mode uses the scroll position for both).

### PDF books

A PDF book (`NovelWork.Format` `PDF:<lang>`, one chapter per page, file at
`/Books/File/{workId}` with range requests) opens in the same
`/Books/Read/{chapterId}` frame. `Pages/Books/Read.cshtml` renders the PDF stage
instead of the chapter text and `books-reader-pdf.js` draws the pages with
pdf.js, vendored in `wwwroot/lib/pdfjs` (version and licence in `VERSION` and
`LICENSE`). There is no browser PDF viewer, iframe or separate PDF page.

- Layout: paper sheets in a single page or a book spread (the cover alone,
  then 2–3, 4–5, …) in Pages mode, or a column of pages in Scroll mode. Zoom
  (whole page, page width, 50–300 %) replaces font size and line spacing, which
  fixed pages do not have (`ReaderLayoutKind.FixedPages`); the zoom choice is a
  device convenience in localStorage.
- Rendering: only the pages on screen and the neighbouring spreads render, on
  canvases at the device pixel ratio (bounded per page); a few rendered pages
  are kept, the rest are released.
- Themes: pages are multiplied onto the paper in light paper styles and
  inverted onto it in Midnight/Black. Images are copied above the themed page
  untouched; a page-sized image that looks like paper (a scan) is themed like
  text.
- The pdf.js text layer carries selection, in-book search (page text through
  pdf.js, hits marked in the layer) and read-aloud: each page's layer is one
  read-aloud paragraph, and `reader-tts.js` asks for the next page
  (`jularr:reader-tts-next-page`) when the pages on screen are read.
- Contents come from the PDF outline, or page ranges without one; a number in
  the contents filter jumps to that page. The transport buttons step through
  outline sections (pages without an outline).
- Progress and bookmarks store the page's chapter (position 0) through the
  offline sync queue; the reader resumes on that page and keeps it in the URL.
  Highlights and notes need text anchors and are not offered for PDF pages.

## State ownership

Durable typography, paper, theme, mode and layout settings belong to
`ReaderPreferenceStore`. Layout preferences include hyphenation, page-number
visibility, illustration visibility, paragraph indentation and automatic
chapter continuation. They participate in the same field-level
system → global → content-type → genre → work cascade as the older settings.
Nullable input means "not supplied" during a scope copy/save and must not erase
an existing override.

Legacy Novel localStorage ownership for font size, line height, text width and
theme is removed. Local storage may still hold ephemeral/device conveniences
such as the selected language view or wake-lock preference; those do not
compete with ReaderPreferences.

## Source adapters

For now the existing Books and Novels Razor PageModels remain source adapters
because they own different translation and annotation endpoints. They both
supply the shared descriptor and render the same Reader settings/shell.

Further extraction must move common reflow text rendering/navigation behind a
ReaderCore adapter contract rather than creating another reader UI.

## Novel adapter

The Novel adapter keeps the canonical `NovelWorks`, `NovelChapters`,
`NovelTranslations`, `NovelProgress`, `NovelBookmarks` and `NovelHighlights`
tables (shared with Books). Application services are split by responsibility;
Razor PageModels orchestrate them:

| Responsibility | Owner |
| --- | --- |
| Source/import commands, the only code that calls `INovelSourceProvider` | `NovelImportService` |
| Bounded library, work detail and reader projections, chapter navigation | `NovelCatalogQueries` |
| Per-profile reading progress and resume anchors | `NovelProgressService` |
| Profile-scoped bookmarks and highlights | `NovelAnnotationService` |
| Chapter download, AI translation and AI episode mapping jobs (Operations queue) | `NovelJobs` |
| AI translation cache, AniList metadata, episode mappings | `NovelTranslationService`, `NovelMetadataService`, `NovelMappingService` |

The Light Novel page uses the shared Reader frame: its contents surface combines
chapter navigation, bookmarks and notes; the bottom frame owns progress/page
transport; the appearance sheet uses canonical preference proxies; and the
chapter opening may render the first EPUB illustration as header artwork.
In-work search and read-aloud stay adapters/extensions of the same shell rather
than separate reader chrome.

- Contents rows read like a printed table of contents: "Chapter N" over the
  chapter title, grouped under EPUB volume headings. Titles that are not
  numbered story chapters in the source (prologue, epilogue, interlude, side or
  short story, extra, afterword; Japanese and western spellings) show only their
  name. The data has no other groups; the reader invents none.
- The chapter opening is a quiet chapter label, a large title and a drawn
  ornament. Without an EPUB illustration, an accepted generated chapter artwork
  (`_ChapterArtwork`, #451) is the header.
- A paragraph that only holds a scene-break mark (`◇◇◇`, `＊ ＊ ＊`,
  `NovelChapterDocument.IsSceneBreak`) renders as a section ornament with
  `role="separator"`. Its text stays in the DOM (visually hidden) so paragraph
  indexes and offsets are unchanged; read-aloud skips it.
- The language menu holds Original/German/Both and the translate slot. The
  TranslateGemma source switch (Local/AI/Both) is added there by
  `novel-translation.js` only when the local track is configured or cached.
  The mobile "Language" tool opens and closes the same menu as a bottom sheet
  (#487). Legacy readers without the frame get a generated "Sprache" action
  only when a `[data-reader-language-control]` exists; it toggles
  `.reader-language-expanded` and reports `aria-expanded`.
- Pages mode lets paragraphs flow across pages (orphans/widows 2); headings,
  scene breaks and illustrations stay whole (#501). The paged anchor is the
  first paragraph visible on the page plus the character offset of its first
  character on that page, so progress, bookmarks, resizes, font changes and
  reopening land on the same page even inside a split paragraph.
- Panels, menus and cards mark state with background, weight or an icon; they
  do not use coloured left-edge stripes (`CssAccentStripeTests` checks every
  stylesheet).
- The Sakura effect stays paused on every page with a reader frame
  (`[data-reader-frame]`); navigation bursts only play outside the readers.
- The appearance sheet shows the reading-mode, background, font & layout,
  colour-scheme and more-settings cards in one row from 1400 px; the background
  row scrolls sideways through the real `wwwroot/reader-backgrounds` themes.
  "Comic" is not offered because no licensed comic typeface is bundled.

Anchors and annotations resolve against the paragraph layout the reader
renders: Japanese source text, or the selected German translation variant with
the matching source/version identity.

### Bounded reader load

`/Novels/Read/{chapterId}` loads a constant number of queries regardless of
work size: one chapter projection (text, current translation, adjacent
chapter ids), reader preferences, the chapter's anime mappings, progress, the
current chapter's bookmarks/highlights and aggregate counts of notes in other
chapters. It does not embed the chapter index or work-wide notes.

- Chapter drawer: `?handler=Chapters` returns at most 100 chapters around the
  current chapter, pages with `after`/`before` (chapter number) and searches by
  number or title in the database.
- Notes panel: the current chapter's notes render with the page; notes from
  other chapters load on demand with `?handler=WorkNotes&kind=bookmarks|highlights&offset=n`
  in pages of 40. Every note (current chapter or other) carries the chapter
  number/title and, for EPUB works, the volume.
- Notes search: `?handler=SearchNotes&kind=bookmarks|highlights&q=...&offset=n`
  is a bounded, paged, profile-scoped `LIKE` search across the whole work
  (current chapter included), same page size as the notes panel.
- Previous/next bookmark: `?handler=AdjacentBookmark&forward=true|false&positionPermille=n`
  returns the nearest bookmark before/after a chapter+position across the
  whole work (wrapping at either end), bounded to a single row. The reader
  binds this to `[`/`]` and to buttons in the notes panel.
- Library counts (chapters, cached text, current German translations) are
  database aggregates.

### Annotation editing

A bookmark's name (`NovelBookmark.Label`) and a highlight's note
(`NovelHighlight.Note`) can be changed after creation through
`?handler=BookmarkLabel` / `?handler=HighlightNote`, without a full page
reload. Selecting text also offers "Bookmark selection" next to "Highlight",
which saves a bookmark anchored at the selection's start through the same
`AddBookmarkAsync` path as any other bookmark.

### Desktop keyboard shortcuts

`novel-annotations.js` defines its shortcuts in one `READER_SHORTCUTS`
constant so they can be checked against the rest of the reader: `B` saves a
bookmark, `N` toggles the notes panel, `[`/`]` jump to the previous/next
bookmark. They are chosen to not collide with `reader-shell.js` (`Escape`) or
`reader-personalization.js`'s paged-mode navigation
(`ArrowLeft`/`ArrowRight`/`PageUp`/`PageDown`). Shortcuts are ignored while
typing in an input, textarea, select or contenteditable element.

### Chapters without cached text

A reader GET never contacts the source provider. When a chapter's text is not
cached, the page shows a preparation state. Its action (`PrepareChapter`)
queues a `novel-chapter-download` operation and redirects back to the same
reader URL (including `bookmark`/`highlight` jump targets) with the operation
id; the page polls `ChapterStatus` and opens the reader once the text exists.

### Highlights

Overlapping highlights are valid. An exact duplicate range returns the existing
highlight. The client renders each paragraph as flat segments split at every
highlight boundary; overlapping segments carry all highlight ids and a depth
for stronger tinting, so no highlight is dropped by nested DOM ranges.
Highlight marks wrap the text nodes of a pristine copy of the paragraph, so
inline EPUB markup (emphasis, ruby) survives highlighting.

### Series → volume → chapter

Every `NovelWork` is a series, every chapter belongs to exactly one
`NovelVolume` (`NovelChapters.VolumeId`, required, cascade). There is no
chapter without a volume and no second runtime path:

| Source | Volumes |
| --- | --- |
| Narou/Ncode web novel | one implicit `web` volume (the chapter index) |
| Books catalog import | one implicit `book` volume |
| EPUB light novel | one `epub` volume per imported EPUB file |

Migration `AddNovelVolumes` moved every existing work into one implicit volume
once (NovelChapters is rebuilt with foreign keys disabled so no translation,
progress, bookmark or highlight is touched). Chapter `Number` stays the
series-wide reading order (volume order, then spine order) and is renumbered
when a volume is inserted between existing ones; progress and annotations
reference chapter ids and survive renumbering. Manual segment and anime
mappings are keyed by local chapter numbers and may need review after a volume
is inserted in the middle of a series; automatic segments are reconciled on
every import.

**Decision: `NovelVolume`, not Book Edition/File.** A Book Edition (#291) is an
alternative manifestation of the *same* content (language/ISBN, one primary
edition per work); a volume is a *sequential part* of a series. Reusing
Editions for volumes would break the primary-edition semantics. What is shared
is the single EPUB path: `EpubBookParser` (Features/Books) is the only EPUB
parser, and `NovelVolumeContent.SyncChaptersAsync` is the only writer of
volume chapters — used by the Books import (implicit volume) and by
`NovelEpubImportService` (EPUB volumes).

### EPUB light-novel import

`NovelEpubImportService` is the one import path for uploads (Novels → Add
novel, or "Add or replace volumes" on an EPUB series), for completed Light
Novel downloads and for the Light Novel inbox folder (Settings → Acquisition →
Media folders, see [READING_ACQUISITION.md](READING_ACQUISITION.md#inbox-folders)).
EPUBs directly in the inbox folder resolve their series from metadata; EPUBs in
`<inbox>/<Series>/` belong to that series.

- Series: explicit target series, else inbox folder name, else calibre
  `series` / EPUB 3 `belongs-to-collection`, else the title without its volume
  marker. Series identity is a hash of the normalized name.
- Volume number: calibre `series_index` / `group-position`, else parsed from the
  title (`第3巻`, `Vol. 3`, `（3）` …), else next free number.
- Volume identity: package unique identifier, else ISBN, else title+author.
  A file with an unknown identity but an existing EPUB volume number replaces
  that volume. An unchanged file (same SHA-256) is a no-op.
- Chapter identity inside a volume: spine document path, then identical text,
  then unique title. Changed chapters are updated in place (translations are
  invalidated by source hash), unchanged ones are not touched, removed ones are
  deleted with their notes.
- Every file succeeds or fails on its own with a diagnostic (not a ZIP,
  missing container/package, malformed XML, no readable text, too large,
  DRM-encrypted). Encrypted EPUBs are rejected; DRM is never removed.
- Source files are only opened for reading. Normalized chapter text and block
  structure live in SQLite; covers and illustrations are cached
  content-addressed under `/data/novels/volumes/<volume>/` and served by
  `/Novels/Asset/{volume}/{file}` (raster images only, `nosniff`).
- After import the series goes through the canonical AniList matching
  (`NovelMetadataService.AutoMatchAsync`). EPUB volume numbers feed the existing
  reading-segment planner/resolver, so a segment with a volume range maps local
  volumes to AniList `progressVolumes`; implicit web/book volumes never do.

### EPUB content rendering

`EpubBookParser` converts chapter XHTML by whitelist into paragraphs plus
`NovelContentBlock`s (paragraph, heading, image) with inline runs (text,
ruby reading, emphasis, strong). Scripts, styles, event handlers, links,
iframes, SVG documents and external/`data:` resources never survive; only
internal JPEG/PNG/GIF/WebP images are kept. The text blocks equal the
paragraphs of `OriginalText`, so anchors, highlights and translations keep
their paragraph indexes and offsets. The reader renders runs with encoded text,
`<em>` (sesame emphasis dots in Japanese), `<strong>` and `<ruby>`; readings
are drawn from `rt[data-rt]` by CSS so the paragraph DOM text stays the plain
text. Illustration-only pages open the next text chapter.

### Front-end modules

`novel-reader.js` is the single bootstrap: it owns the shared reader context,
language view state and the restore lifecycle, then starts
`novel-position.js`, `novel-annotations.js`, `novel-chapter-drawer.js`,
`novel-translation.js` and `novel-learning.js`, which only register factories.
Chrome, settings and paged mode stay in `reader-shell.js` and
`reader-personalization.js`.
Styles: `novels.css` (reader surface), `novel-reader-panels.css` (drawer,
notes, highlights, selection) and `novel-library.css` (library, work detail,
chapter preparation).

### Learning (issue #147)

`Read.cshtml` hosts the shared language inspector
(`LanguageInspectorHost.ForNovelChapter`, see docs/LEARNING_V2.md) without a
`selectionSurface`, so the inspector never binds its own floating "Look up"
button. Instead, `novel-learning.js` adds a "Nachschlagen" action to the
existing selection menu (`novel-annotations.js`'s `data-selection-menu`) that
opens the inspector for the current selection, and taps a Japanese word
through `Intl.Segmenter` where the platform supports it. Saving a word from
the reader records a `LearningContext` with `chapter:{id}` +
`paragraph:{index}`, the same anchor shape Learn/Review resolves back into a
"Zurück zum Kapitel" link.

Optional furigana is computed from the same Japanese analysis pipeline as the
inspector and rendered as `<ruby>`/`data-rt` (identical to native EPUB
readings), so it survives highlighting without another selection/offset path.
It is off by default, stored as `ReaderPreferences.FuriganaEnabled` (the
existing preference cascade, saved at the global default scope), and only
offered when the toolkit supports readings.
