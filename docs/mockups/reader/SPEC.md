# Reader — Cross-platform Clean Design

Status: **binding planning specification for Reader mockups**. This document refines `UX.md`, `UNIFIED_READER.md`, the canonical Media Core and issue #662. Existing Reader behavior that already matches this contract should be preserved rather than redesigned.

Architecture source of truth:
- `Work -> Structure -> Edition -> Version -> Asset -> File`
- written structure uses canonical Volume/Chapter where applicable;
- canonical `MediaProgress` owns current item, exact resume locator and completed-through state;
- `ReaderPreference` remains the one durable reader preference store;
- Translation produces a derived Edition/Version and never overwrites the source;
- offline reading reuses the same document/progress/bookmark semantics through repository/sync adapters;
- Learning and TTS are optional capability layers over Reader content, not separate Reader identities.

## 1. Purpose

Provide one distraction-minimized reading product for Books, Light Novels, Web Novels, Manga and fixed documents while adapting controls to the content type.

The Reader must:
- open at the exact canonical resume position;
- save progress automatically without a manual Save action;
- keep reading content visually dominant;
- expose TOC, search, language/edition, appearance and annotations without permanent clutter;
- support paged and continuous reading where the document capability allows it;
- support fixed-page/image content without showing irrelevant typography controls;
- distinguish official editions from generated/machine-translated derivatives;
- continue to function with verified offline content;
- reuse the same shell, preference model and progress semantics across media types.

The Reader is one product with source/render adapters, not separate Book, Novel and Manga applications.

## 2. Entry / route contract

Reader entry can come from:
- Continue Reading;
- Book / Light Novel / Manga detail;
- chapter selection;
- bookmark/highlight/search result;
- offline library;
- compact Continue Reading surface;
- deep link to a canonical chapter/page/locator.

The Reader resolves:
1. canonical Work;
2. selected Edition and language;
3. Volume/Chapter or fixed-page target;
4. readable Asset/File/document descriptor;
5. profile Reader preferences;
6. exact canonical resume locator;
7. annotations/bookmarks relevant to the target;
8. translation and offline availability.

A URL may carry a jump target, but canonical progress identity must not depend on URL structure.

## 3. Shared Reader frame

All supported content types use the same conceptual frame.

### Top bar

Desktop/Tablet baseline:
- context-aware Back/Close on the far left;
- compact Work thumbnail/cover where useful;
- Work title;
- current volume/chapter/section context below or beside the title;
- **Contents**;
- **Search** where supported;
- **Language/Edition** when more than one readable view exists;
- **Appearance**;
- **More**.

The top bar is a compact Reader control strip, not a second application header. It may disappear completely in clean-reading mode.

Mobile:
- Back;
- compact Work/Chapter context;
- Search where supported;
- Bookmark/More as space permits;
- lower-frequency controls move into sheets/More.

Do not squeeze the full Desktop action row into Mobile.

### Content surface

The reading document occupies the dominant visual area.

The shell must not force every source into the same rendering model:
- reflowable text uses readable column/page layout;
- Manga uses page/image layout;
- PDF/fixed documents use fixed-page rendering;
- unsupported controls disappear through capabilities.

### Bottom frame / progress

Desktop:
- slim progress slider across the lower frame;
- current overall/section progress such as `42%`;
- **Previous Chapter** on the left when a canonical previous target exists;
- **Next Chapter** on the right when a canonical next target exists;
- page/chapter position text where meaningful;
- no manual Save button.

Tablet:
- same progress semantics;
- page/position indicator may sit centered between previous/next navigation;
- in paged/two-page mode the visible page/spread indicator is derived from the rendered document state.

Mobile:
- compact progress slider;
- current percentage/page position;
- Previous / Next below or beside the slider with large touch targets;
- lower-frequency Reader tools stay in top actions or sheets rather than forming a dense permanent bottom toolbar.

TTS transport appears only while TTS is active/relevant.

When Reader chrome hides, the full control frame disappears. A minimal non-interactive progress affordance may remain only if the selected Reader preference asks for it.

## 4. Content capability model

The UI is driven by document capabilities, not by media-type conditionals spread across pages.

### Reflowable text

Typical for Book, Light Novel and Web Novel.

May expose:
- continuous Scroll;
- paged reading;
- one/two-page layout;
- font family;
- font size;
- line height;
- paragraph spacing/indent;
- text width;
- hyphenation;
- chapter styling;
- illustrations;
- selection;
- highlights/bookmarks/notes;
- search;
- TTS;
- Learning interaction.

### Manga / comic image sequence

Manga and western comics share the image-sequence renderer but not necessarily
the same defaults. Reading direction comes from document metadata/profile
preference rather than title guessing.

Expose:
- single page;
- spread / two-page where meaningful;
- right-to-left / left-to-right direction;
- fit width / fit page;
- zoom;
- continuous vertical mode where supported;
- page number;
- chapter navigation;
- bookmarks;
- optional OCR/Learning only when an explicit capability exists.

Do **not** show text font/line-height controls for image pages.

### Fixed document / PDF

This includes original PDF, fixed-layout/pre-paginated EPUB and other
page-faithful documents such as magazines/artbooks when reflow would destroy
the intended layout.

Expose:
- page navigation;
- single/spread/continuous layout;
- zoom / page width / whole page;
- outline/contents where present;
- text search/selection/TTS only when the document exposes reliable text;
- bookmarks.

Do not emulate reflowable typography for fixed pages.

### Format/layout resolution

Renderer choice follows the parsed document layout/capabilities, not only the
file extension:

- normal EPUB -> reflowable text;
- fixed-layout EPUB -> fixed pages;
- original PDF -> fixed pages;
- PDF Smart Book -> semantic/reflow view mapped to original pages/regions;
- CBZ/ZIP/CBR/CB7 after safe import normalization -> image sequence;
- scans -> fixed/image source view with optional OCR/Smart derived view;
- magazine/artbook -> fixed/image unless a reliable semantic derived view exists.

One source may expose more than one view without creating a second Reader or
progress store. Smart PDF and Original PDF are the primary example.

Keep these concepts separate:
- image/page order: LTR / RTL;
- continuous vertical image flow / Webtoon;
- text direction: LTR / RTL / auto;
- text writing mode: horizontal / vertical-rl / vertical-lr.

Japanese vertical writing is not the same capability as vertical Manga/Webtoon
scrolling.

Source illustrations, Manga/comic pages, generated chapter artwork and Reader
theme artwork are different concepts. Generated chapter artwork is optional
presentation and must not become a progress/annotation anchor.

Unsupported DRM-protected content must fail explicitly; Reader planning never
implies DRM circumvention. MOBI/AZW/AZW3/DJVU remain format-adapter work and
must reuse this same Reader contract if supported later.

## 5. Exact progress and completion semantics

Reader progress follows the canonical split from issue #662.

Keep separate:
- `CurrentItem`;
- `ResumePosition`;
- `CompletedThrough`;
- external `ProviderProgress`.

Opening Chapter 12 does not automatically mean Chapter 12 is completed.

### Exact locator by content type

EPUB/eBook:
- chapter/content identity;
- stable CFI/paragraph/block anchor or equivalent;
- character/offset where useful;
- fallback percentage.

Light Novel / Web text:
- chapter;
- stable block/paragraph identity or index;
- character offset/anchor text;
- fallback percentage.

Manga:
- chapter;
- page/index;
- optional intra-page position/zoom only when needed for resume;
- fallback percentage.

PDF/fixed:
- page;
- optional vertical/viewport offset;
- fallback document percentage.

### Auto-save rules

Progress saves automatically:
- debounce/throttle during normal reading;
- flush on page turn;
- flush on chapter change;
- flush before route navigation;
- flush on visibility/background transition;
- flush on Reader close;
- queue locally first when offline support is active.

The Reader must restore the exact stable locator first and use fallback percentage only when the exact anchor can no longer be resolved.

There is no manual Save Progress requirement.

## 6. Desktop composition

Desktop is reading-first, pointer/keyboard friendly.

### Layout

Approved Desktop Light baseline:
- Reader occupies the main content surface without a heavy surrounding dashboard;
- compact top Reader bar;
- centered readable text column on a light/neutral paper surface;
- generous horizontal margins;
- chapter number/eyebrow above a clear chapter title;
- illustrations may appear inline where the document provides them;
- slim bottom progress/navigation frame;
- Contents can open as a left side panel on wide layouts;
- Learning/annotation details can open as a right side panel without destroying the readable text width;
- side panels and document content may coexist only while the remaining text column stays comfortable.

Wide-screen reflowable text may use one or two pages depending on Reader preferences and document capability.

### Clean-reading / UI-hidden state

While actively reading:
- top and bottom Reader chrome can disappear entirely;
- application navigation must not remain visually dominant;
- content stays at the same logical reading position when chrome hides/shows;
- moving pointer toward the top edge, scrolling upward or using the approved reveal action restores chrome;
- text selection never accidentally toggles chrome.

The approved visual mockup may use chapter artwork/scenery as an illustrative backdrop. This is **not permission to reduce text readability**:
- default Clean Design reading uses an opaque/controlled paper surface;
- optional illustration/immersive presentation may exist only where the document/theme explicitly supports it;
- readable text must retain sufficient contrast through an opaque/translucent reading surface;
- decorative artwork never becomes required for normal Reader operation.

### Desktop interactions

- center/content-safe click may toggle transient chrome only where it does not interfere with text selection;
- text selection always wins over chrome gestures;
- pointer near the top edge may reveal hidden chrome;
- keyboard page navigation works only outside text input/editing;
- Escape closes the deepest menu/sheet first;
- search result/bookmark/highlight jumps preserve the active Reader session.

### Required Desktop mockup states

1. **Light reflowable Book/LN reading — chrome visible**;
2. **Clean reading — chrome hidden**;
3. Contents side panel open;
4. Appearance/settings panel;
5. language/edition menu;
6. Learning/annotation detail side panel;
7. translation/preparing state;
8. offline/degraded state.

## 7. Mobile composition

Mobile is touch-first and content-dominant.

### Chrome

Approved Mobile baseline:
- compact top row with Back, Work/Chapter context and only high-frequency actions;
- slim progress + Previous/Next at the bottom while chrome is visible;
- chrome hides while reading when appropriate;
- center tap reveals/hides chrome when it cannot conflict with selection;
- downward scroll may hide chrome;
- upward scroll reveals it;
- open menus/sheets prevent auto-hide;
- no desktop action strip squeezed onto phone.

The content column uses comfortable side margins and remains visually primary.

### Paged mode

- edge tap/swipe turns page;
- selection/annotation gesture takes priority over page turn;
- page transition should be fast and restrained, not decorative;
- chapter boundary can continue to next chapter only according to the configured auto-continue behavior.

### Scroll mode

- normal platform scrolling;
- progress follows stable anchors rather than only raw scroll percentage;
- restoring after font/layout changes must remain anchored to the same logical text.

### Mobile sheets

Use bottom sheets/fullscreen sheets for:
- Contents;
- Search;
- Appearance;
- Language/Edition;
- Bookmarks/Highlights;
- **word/sentence Learning details**;
- TTS settings.

Approved Learning-detail sheet behavior:
- opens from selected text only when Reader Learning mode is On;
- starts as a compact/medium bottom sheet;
- can expand for more examples/details;
- shows term/reading/meaning as available;
- actions may include **Save, Learn, Known, Ignore, Listen** according to resolved capabilities;
- closing the sheet restores the exact reading position;
- the sheet must not turn normal scrolling/tap gestures into Learning interactions outside the selected text.

Avoid tiny Desktop popovers on phones.

### Required Mobile mockup states

1. clean reading with chrome hidden;
2. chrome visible;
3. Contents sheet;
4. Appearance sheet;
5. text selection + Highlight/Bookmark action;
6. language/edition sheet;
7. offline unavailable chapter;
8. Manga single-page state.

## 8. Tablet composition

Tablet remains touch-first but can use more width.

Portrait:
- similar to Mobile;
- wider readable margins;
- larger sheets.

Landscape:
- optional **two-page/spread** view for reflowable content where layout supports it;
- Contents can use a persistent/collapsible **left side panel**;
- the current chapter is clearly highlighted in Contents;
- page/spread position remains visible in the lower progress frame;
- document illustrations may occupy a dedicated page/column when the source layout contains them;
- Appearance, Translation and Learning settings may open as a right side sheet;
- annotation/Learning details may use a right side sheet;
- do not adopt hover-only Desktop behavior.

The approved Tablet composition is:
`Contents side panel | two-page/spread Reader | optional right settings/detail sheet`.

Panels must never reduce the actual reading area below a comfortable width; when space becomes insufficient, fall back to modal/sheet behavior.

iPadOS/WebKit follows the same Reader semantics and uses platform-safe selection, fullscreen and storage behavior.

## 9. TV

TV is **not** a primary full reading target in the current planning baseline.

Allowed:
- browse Work/Volume/Chapter;
- show reading progress;
- Continue on phone/tablet/desktop;
- optional large-page Manga/fixed-image viewing only if separately approved.

Do not create a dense TV text Reader simply for platform parity.

## 10. Reading modes

### Scroll

- continuous content;
- exact anchor follows the visible reading position;
- thin progress remains available;
- chapter boundary behavior follows user preference.

### Pages

- content flows into stable pages;
- layout changes must preserve the logical anchor;
- one/two-page choice is capability- and width-aware;
- page number reflects the rendered document state, not a fake fixed page count before layout completes.

### Manga page/spread

- single page;
- spread;
- continuous vertical where supported;
- reading direction explicit and persistent through Reader preferences;
- cover/single first page must not be incorrectly paired in spread mode.

### Fixed/PDF

- whole page;
- page width;
- explicit zoom;
- single/spread/continuous modes as supported.

## 11. Contents and navigation

Contents can include:
- volumes;
- chapters;
- prologue/epilogue/side stories exactly as source/canonical metadata provides;
- PDF outline/sections;
- Manga chapters/pages where applicable.

Rules:
- current item clearly selected;
- large works load lists lazily/paged;
- search/filter within Contents is allowed;
- do not load an entire 100k-chapter structure into the Reader page;
- previous/next resolves canonical structure;
- browser/system Back remains navigation history, not a hard-coded Library link.

## 12. Search

Where source text supports it:
- bounded in-work search;
- search original and currently available readable translations where appropriate;
- result shows chapter/section + concise context;
- selecting a result jumps to a stable locator;
- search panel does not replace the whole Reader route.

For Manga/image-only content, Search appears only if OCR/text capability actually exists.

## 13. Language, Edition and Translation

Language and Edition selection are user-facing reading concepts, not provider/debug data.

Approved control model:
- compact language/edition selector in Reader chrome where multiple readable variants exist;
- Appearance/Reader Settings may expose the current reading source more explicitly.

The selector/settings may show:
- **Original**;
- official localized edition(s), clearly labeled as translated/official;
- generated/machine-translated derivative(s), clearly labeled as generated;
- **Both / side-by-side** only where the renderer explicitly supports it.

Switching between these views preserves the closest stable logical reading position rather than restarting at the chapter beginning.

The UI must clearly distinguish:
- **Official translation**
- **Machine/generated translation**

Generated translation must never masquerade as an official edition.

### Missing translation

If no target edition exists and policy allows generation:
- expose `Translate` / `Prepare translation`;
- show queued/running state;
- keep source content readable while generation runs where possible;
- cache/store the result as the derived canonical Edition/Version;
- switching language later reuses the stored derivative.

Do not translate on every page render.


### Progressive / live translation

When generation can produce validated blocks before the whole requested unit is
finished, the Reader exposes them immediately instead of waiting for the whole
chapter/book. The UX is provider-neutral and follows #834.

Required behavior:
- keep original/source content readable for blocks that are not translated yet;
- show a compact first-text/preparing state until the first validated translated
  block is available;
- render completed translated blocks in their canonical positions immediately;
- an optional in-progress current block may visibly fill from ephemeral provider
  deltas, but it is not yet durable/searchable/annotatable/offline content;
- previously completed blocks do not change underneath the reader during the
  same translation version;
- translation completion must not move the current logical Reader locator or
  cause layout/scroll jumps;
- show useful progress such as validated blocks / known total plus current
  translating/finalizing state; never show 100% before final validation;
- reconnect/reload restores validated blocks and reconnects to the shared active
  run without duplicating text or work;
- closing/navigating away from Reader does not implicitly cancel shared
  translation;
- authorized controls may expose Pause, Resume, Cancel and Retry without turning
  them into permanent primary chrome;
- partial failure/rate limiting keeps already validated blocks readable and
  Retry resumes missing/failed blocks rather than starting over;
- if source/model/prompt/context changes, present/use the resulting translation
  as another version rather than mixing it into the currently displayed run;
- never show raw provider JSON, markup/script or provider-specific protocol
  events.

For providers without token streaming, Jularr still delivers progressive reading
by translating bounded semantic blocks sequentially. PDF Smart Book uses #819
semantic blocks/source mapping. Manga/comics/fixed image content can use the same
states only when a stable OCR/text-region model such as #846 exists; otherwise a
translation action is not offered for guessed whole-page text.

Only validated completed blocks feed search, canonical TTS text, durable
translation-based annotations and offline packages.

## 14. Reader appearance and preferences

Reader settings use the existing preference cascade rather than per-page localStorage copies.

Scopes:
1. system content-type preset;
2. profile global;
3. profile media/content type;
4. matching genre scope;
5. Work-specific override.

The UI may allow the user to choose where a change is saved.

### Reflowable settings

Approved Appearance/Reader Settings layout groups:
- text size controls;
- supported font family;
- paper/background surface;
- line spacing;
- reading mode: **Scroll / Pages**;
- one/two pages where supported;
- text width;
- paragraph spacing/indent;
- hyphenation;
- chapter presentation;
- illustration visibility;
- page number visibility;
- auto-continue chapter.

Translation/Edition controls may appear in the same Reader Settings sheet as a separate group when this keeps the flow compact.

Learning controls may also appear as a separate capability-gated group; they are not part of typography itself.

### Manga settings

May include:
- reading direction;
- single/spread/continuous;
- fit width/page;
- page gap;
- background;
- auto-advance only if explicitly approved later.

### Fixed/PDF settings

May include:
- single/spread/continuous;
- zoom;
- page width/whole page;
- background.

Unsupported controls must not render.

## 15. Reader themes / Light and Dark

Reader has deliberate reading surfaces independent from application chrome.

Required base modes:
- System;
- Light;
- Dark.

Reader-specific surfaces may include:
- white;
- warm/cream paper;
- sepia-like neutral;
- dark gray;
- near-black.

Rules:
- changing Reader paper does not change global Jularr theme;
- application chrome still respects global Light/Dark tokens;
- no low-contrast decorative backgrounds behind text;
- artwork/background themes stay in page margins and never reduce text readability;
- Dark mode is designed, not an inversion filter.

## 16. Bookmarks, highlights and notes

### Bookmark

Can target:
- current reading position;
- selected text start;
- Manga/PDF page.

May include a user label.

### Highlight

Only where reliable text selection/anchors exist.

Supports:
- selected range;
- optional note;
- overlapping highlights where the renderer supports it.

### Notes/annotation panel

- current chapter annotations readily available;
- work-wide annotations load on demand;
- selecting one jumps to the saved stable anchor;
- large annotation sets are paged/searchable;
- offline support follows the explicit sync contract; do not pretend unsynced capabilities exist for types that are not offline-enabled.

Do not require annotations to mutate source HTML/content.

## 17. Learning interaction

Learning is an **optional Reader mode**, not a permanent change to normal reading.

The Reader Learning control exists only when:
- the instance Learning module is enabled;
- the current profile has Learning enabled for itself;
- authorization/capabilities permit Reader learning tools;
- the current document exposes selectable/OCR-backed Learning content.

### Learning Off

This is normal reading:
- no Learning-specific word highlights/hit targets;
- normal text selection remains normal selection;
- scrolling, page turns and tap zones behave exactly as ordinary Reader interaction;
- no Learning sheet/panel is reserved or shown;
- Learning must not change typography, page layout or resume behavior.

### Learning On

For readable/selectable text:
- tap/select term;
- show reading/meaning/explanation;
- Save / Learn / Known / Ignore where authorized;
- Listen where TTS/speech capability exists;
- optional known-word highlighting can be enabled separately;
- preserve Reader position when the Learning sheet/panel opens/closes.

The mode can be switched On/Off **without reloading the document or changing the canonical Reader position**.

Turning Learning Off:
- closes open Learning details;
- removes Learning-specific hit targets/highlights;
- returns immediately to normal Reader interaction.

### Placement

- Desktop: Learning toggle/action may live in Reader Settings/More; active term details can use a right side panel.
- Tablet: Learning toggle in the right settings sheet; details may use a right side sheet.
- Mobile: Learning toggle in Reader Settings/More; term details use a bottom sheet.

The toggle is shown only after instance + profile + permission/capability + content eligibility all resolve true.

Learning interaction must not create another copy of chapter identity or reading progress.

For Manga, Learning/OCR is absent unless a real OCR/text capability exists.

## 18. TTS / Read aloud

TTS is an optional Reader extension.

Normal controls:
- Read aloud / Play;
- Pause/Resume;
- Stop;
- voice/speed settings.

Rules:
- current selection can be read;
- otherwise continue from visible/current text;
- Reader may follow spoken content without redefining reading progress;
- TTS cursor is ephemeral session state;
- switching visible language stops/restarts according to explicit TTS behavior;
- auto-continue into next chapter is a Reader preference and defaults conservatively.

TTS controls must not become a second permanent toolbar when inactive.

## 19. Offline reading

Offline content uses the same Reader UI and source semantics.

Requirements:
- verified local content opens without network;
- chapter navigation works for downloaded adjacent content;
- exact progress writes locally first and synchronizes later;
- bookmarks use the same canonical semantics;
- Reader preferences remain available;
- downloaded translation/edition identity remains explicit;
- stale/corrupt/incomplete local content is not presented as valid.

### Partial offline state

If current chapter exists locally but next chapter does not:
- current chapter remains fully readable;
- Next shows a clear unavailable/offline message;
- do not navigate to a browser/network error page.

If the whole requested Work is not cached:
- show a Reader-specific offline unavailable state with Back and downloaded-content choices when available.

## 20. Preparing / loading / partial states

### Document loading

Show:
- restrained skeleton/progress;
- title/chapter context;
- no fake page count before layout is known.

### Exact resume restore

Avoid visible jump where possible:
1. render enough document structure;
2. resolve exact locator;
3. position reader;
4. then settle chrome/progress.

### Missing chapter content

When canonical chapter metadata exists but content is not ready:
- show `Preparing chapter`;
- offer Prepare/Retry when permitted;
- poll/refresh through normal Job state;
- do not call providers synchronously from the Reader GET.

### Translation running

Show source content plus compact translation state where possible.

### Partial edition

Reader remains usable for available chapters and clearly marks unavailable next/previous targets.

## 21. Error states

Explicit categories:
- content unavailable;
- storage offline;
- unsupported/corrupt format;
- failed translation;
- failed document render;
- permission revoked;
- offline chapter missing;
- stale/corrupt offline package.

Recovery actions depend on the error:
- Retry;
- Prepare;
- switch Edition/language;
- use original;
- remove/re-download offline content;
- Back.

Never show raw provider/server exceptions.

## 22. Empty states

Reader itself normally opens a specific target, so classic empty pages are rare.

Valid empty/unavailable cases:
- Work has no readable Edition;
- selected Edition has no readable chapter/file;
- no downloaded chapter while offline;
- search has no results;
- annotations list has no items.

Use small contextual empty states inside the relevant sheet/panel rather than replacing the full Reader where possible.

## 23. Continuation surface

Binding shared specification: `docs/mockups/continuation-surfaces/SPEC.md`.

Leaving the full Reader may expose a compact **Continue Reading** surface derived from canonical `MediaProgress` and the exact Reader locator.

Continue Reading is not an active-player toolbar.

### Desktop / wide Tablet

Show:

- thin informational reading-progress line at the top;
- subtle chapter/section markers where meaningful;
- cover;
- bold Work title;
- current structure, e.g. `Vol. 4 · Ch. 28 — The Ruined Shrine`;
- exact useful position, e.g. `Page 5 / 29 · 17%`;
- one primary `Continue Reading` action;
- explicit Dismiss `×`.

Hover/focus over the cover may expose **Open Reader**.

Cover, title, Open Reader and Continue Reading all restore the same exact canonical locator.

Do **not** add:
- Previous/Next chapter controls;
- page backward/forward;
- typography controls;
- bookmarks;
- TOC;
- Reader settings;
- a generic `...` overflow.

The progress line is informational here and is not directly scrubbable.

### Mobile

Use one compact row above bottom navigation:

- cover;
- title;
- concise chapter/section;
- page/position + percentage where useful;
- compact Continue Reading action/icon;
- Dismiss `×`.

At narrow width the Continue action may be icon-only with an accessible `Continue Reading` label.

Do not create a second row of Reader controls.

### Dismiss behavior

Dismiss hides only this continuation surface.

It must not:
- reset/delete `MediaProgress`;
- remove bookmarks/history;
- mark the content complete;
- forget the exact resume locator.

### TTS

When TTS is actively playing, it is an active audio session and uses **Now Playing** instead. Do not stack Continue Reading below/above active Now Playing.

TV may expose Continue Reading as a normal browse card rather than a persistent bottom surface.

## 24. Accessibility

Required:
- keyboard operation on Desktop;
- visible focus;
- minimum touch targets;
- screen-reader labels for icons;
- semantic headings for reflowable text;
- selectable text remains selectable;
- user text scaling remains usable;
- reduced motion disables decorative transitions;
- themes meet contrast requirements;
- focus trapping for modal panels/sheets;
- Reader controls do not steal standard browser/system selection behavior unnecessarily.

## 25. Required mockup set

Create in this order:

1. **Desktop Light — reflowable Book/LN reading, chrome visible**  
   Approved baseline: compact top actions, centered paper/text, bottom progress + Previous/Next.

2. **Desktop Light — clean reading / chrome hidden**  
   Content-first reference; no permanent application chrome.

3. **Desktop Light — Learning detail side panel**  
   Word/sentence detail while exact reading position remains stable.

4. **Mobile Light — reading with chrome visible**  
   Compact top row + bottom progress/Previous/Next.

5. **Mobile Light — Learning word-detail bottom sheet**  
   Approved compact/expandable sheet.

6. **Tablet Light — landscape two-page + Contents side panel**  
   Approved wide-layout reference.

7. **Tablet Light — Reader Settings side sheet**  
   Appearance + Translation/Edition + capability-gated Learning controls.

8. **Mobile Light — Appearance/Reader Settings sheet**  
   Validate touch settings density.

9. **Mobile Light — annotation/text selection state**  
   Highlight / Bookmark / optional Learning action.

10. **Desktop/Mobile — language & translation state**  
    Clearly distinguish Original / Official / Generated.

11. **Manga Mobile — primary single-page state**  
    Validate shared Reader frame with image-specific controls.

12. **Dark validation**  
    At minimum Desktop reflowable + Mobile Manga/reading.

13. **Offline/Preparing/Error reference**  
    One shared state reference plus responsive notes.

TV Reader mockup is not required in this phase.

## 26. Data / information contract

Reader consumes view data derived from:
- canonical Work;
- Volume/Chapter or fixed-page structure;
- Edition/Version;
- readable Asset/File/document descriptor;
- content/layout capability flags;
- exact canonical progress locator;
- completed-through state;
- ReaderPreference effective values + inheritance source;
- available official/generated languages/editions;
- Translation state;
- bookmarks/highlights/notes;
- Learning capability/state;
- TTS capability/preferences;
- offline availability/package verification state.

The new Reader UI must not permanently query legacy `NovelWork`, `NovelProgress`, `MangaProgress` etc. as separate domain truths once canonical migration is complete. Existing services are migration/source adapters until moved behind canonical contracts.

## 27. Must not implement

- No separate Book, Novel, Manga, Comic, PDF or Magazine Reader shells.
- No renderer selection based only on route or file extension.
- No second progress database/store per media type.
- No manual Save Progress requirement.
- No progress represented only as percentage when a stable locator is available.
- No automatic completion merely because a chapter was opened.
- No provider write-back from CurrentItem/ResumePosition.
- No destructive overwrite of original content during translation.
- No machine translation presented as official.
- No translation request on every render.
- No whole-work chapter list embedded into every Reader page for large works.
- No typography controls on Manga/PDF when they are meaningless.
- No giant persistent toolbar around the reading surface.
- No Learning-specific hit targets/highlights while Reader Learning mode is Off.
- No document reload or position reset merely to toggle Reader Learning On/Off.
- No Reader Learning control when instance/profile/policy/content eligibility does not allow it.
- No TV text Reader purely for parity.
- No unsanitized imported HTML.
- No execution of embedded document scripts/actions or DRM bypass.
- No annotation implementation that mutates canonical source text.
- No duplicate online/offline Reader rendering paths.
- No separate offline progress semantics.
- No page-local preference system competing with `ReaderPreference`.
- No decorative theme/background behind text that reduces readability.
- No source-provider network call during normal Reader GET solely to make missing chapter content appear synchronously.

## 28. Mockup acceptance checklist

A Reader mockup is acceptable only when:
- content is visually dominant;
- Book/LN/Manga can share the frame without sharing inappropriate controls;
- exact resume/progress semantics are representable;
- Desktop, Mobile and Tablet behaviors match the approved Reader hierarchy;
- clean-reading chrome can hide without changing the logical reading position;
- Tablet two-page + Contents composition degrades safely when width is insufficient;
- Learning Off remains ordinary reading and Learning can be toggled without document/session reset;
- Light/Dark reading surfaces are deliberate;
- Contents works for very large structures;
- language/edition clearly distinguishes official and generated content;
- annotations do not overwhelm the reading surface;
- Loading/Preparing/Offline/Error states are defined;
- no manual progress-save control is required;
- no legacy parallel core model is implied;
- TV is not forced into an unsupported reading UX.
