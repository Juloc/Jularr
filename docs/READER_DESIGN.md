# Reader design integration

Owner approved the UiDesigner paper Reader on 17 October 2026 and requested its
transfer to clean-start. This overrides older separate Book/Comic chrome, landscape
decoration and generic click-to-turn over selectable text.

- One `ReaderFrame`, one Reader entry, shared device preferences and controls.
  Text and image adapters retain authorized reading services and semantic
  `s<section>/b<block>` / `p<page>` progress. PDFs without prepared text retain the
  browser viewer inside that frame; no fabricated PDF progress.
- Pages or vertical scrolling; automatic single/spread layout with manual override.
  Text has a 24px inner margin on each page. Clean/paper and paper colour are editable.
  Three presentation presets are values, never separate Readers or content parsers.
- Header selection restores canonical selectable text and annotation tools.
  Normally dragging holds, reverses or completes a paper fold; fast swipes, keyboard
  and edge controls remain available. System animation respects reduced motion;
  explicit On/Off overrides only this Reader preference, never the OS.
- Text sections retain server routes at chapter boundaries. Page numbers are
  measured for the current section; stack thickness uses supplied whole-book
  character proportions, not an invented total reflowed page count.
  Image scrolling retains a bounded sliding window and releases distant images.
  Scroll progress records the current page's start until the loaded final image's
  bottom is reached; merely entering that image does not complete the book.
- Translation, notes, search, downloads, capability checks and acknowledged progress
  retain their canonical owners. No database/schema changes.
- Device preferences opt into the approved `Form<T>` local field group. It has no
  form submission or server action; ordinary unbound forms retain their disabled guard.

## Paper dependency

`page-flip` **2.0.7**, [StPageFlip](https://github.com/Nodlik/StPageFlip), MIT;
[license](../src/Jularr.Frontend/Modules/Reader/PageFlip.LICENSE.txt). No runtime
dependencies. Vite uses its pinned source entry to extract CSS, not runtime style
injection or a CDN. The optional chunk is approximately 47 KB (12 KB gzip), loaded
only for animated page mode. At most six decorative inert, aria-hidden faces exist.
Text sections over 100,000 characters retain canonical instant paging instead of
cloning unbounded text into animation faces.

One library instance is retained for the document lifetime, including settings and
selection changes. Reader routes use full-page navigation, not app-shell fragment
replacement: upstream exposes no render-loop stop API, and recreating it on each
turn would leak animation loops. Revisit disposal before enabling Reader fragment
navigation. No alternate progress store or automatic UiDesigner approval is added.

## Focused checks

Frontend TypeScript/assets, UI resources, settings/image grouping, progress and
translation tests; isolated `ReadingPathTests` exercise actual EPUB, Manga, light
novel and PDF paths plus paper gestures, settings and narrow layouts.
Physical Safari/TV validation and seamless cross-section vertical loading remain
separate work; do not claim those from Chromium tests.
