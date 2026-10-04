# Unified Reader engine completion — implementation pack

Status: implementation handoff for the remaining Unified Reader work.

Related:
- #289 — Unified Reader: one engine, capability adapters and granular profile presets
- #662 — canonical Reader/Watch progress and exact auto-resume
- #819 — PDF Smart Book
- #221 — offline Reader sync
- #317 — TTS
- #407 — generated chapter artwork
- `docs/UNIFIED_READER.md`
- `docs/mockups/reader/SPEC.md`
- `docs/DOMAIN.md`
- `docs/DOMAIN-AUDIT.md`
- `docs/ARCHITECTURE.md`

This pack does not redefine the Reader UX. It turns the already approved architecture and UX into a dependency-ordered implementation plan.

## 1. Problem statement

Jularr already has a shared Reader frame, `ReaderDocumentDescriptor`, `ReaderPreference` and shared Reader UI, but the runtime is still split across Books, Novels, Manga and PDF-specific controllers.

The end state required by #289 is not yet reached while any source-specific controller independently owns:
- Reader chrome;
- settings persistence;
- page/scroll gestures;
- page turns;
- keyboard navigation;
- layout mode state;
- exact-location capture/restore;
- progress autosave;
- shared Reader menus/sheets;
- generic pagination/reflow behavior.

The fix is not another Reader rewrite. Keep the working shared shell and source capabilities, move generic behavior behind one Reader runtime, and reduce source code to content/data adapters plus real renderer-specific behavior.

## 2. Binding ownership

There is one Reader product.

Canonical ownership:

```text
MediaCore / Library
        |
        v
Reader source adapter
        |
        v
ReaderDocument + capabilities + variants + source mapping
        |
        +--> ReflowTextRenderer
        +--> FixedPageRenderer
        +--> ImageSequenceRenderer
        |
        v
Shared Reader Shell
        |
        +--> ReaderPreference
        +--> MediaProgress / exact ReaderLocator
        +--> Bookmark / Highlight
        +--> Translation / Edition
        +--> TTS
        +--> Learning
        +--> Offline repository/sync
```

The source adapter owns source-specific loading and commands. The renderer owns only layout/render behavior required by its document layout. The shell owns shared interaction and presentation. Progress owns durable consumption state.

No source adapter or renderer may recreate another shell, settings owner or progress store.

## 3. Renderer selection is based on document layout, not file extension

Never decide the Reader engine only from `.epub`, `.pdf`, `.cbz` or another extension.

Examples:

| Source | Document result | Renderer |
| --- | --- | --- |
| normal EPUB | reflowable semantic document | ReflowTextRenderer |
| fixed-layout EPUB | fixed/pre-paginated document | FixedPageRenderer |
| image-only comic EPUB | image/fixed document according to package metadata | ImageSequenceRenderer or FixedPageRenderer |
| Web/Light Novel | reflowable semantic document | ReflowTextRenderer |
| original PDF | fixed physical pages | FixedPageRenderer |
| PDF Smart Book derived view | semantic blocks mapped to source regions | ReflowTextRenderer with mapped fixed regions where needed |
| CBZ/ZIP manga/comic | ordered image pages | ImageSequenceRenderer |
| CBR/CB7 comic | ordered image pages after a supported safe archive adapter exists | ImageSequenceRenderer |
| scanned book | original fixed pages; optional derived OCR/Smart view | FixedPageRenderer and optional Smart view |
| magazine/artbook | normally fixed pages or image sequence; never forced to reflow | FixedPageRenderer or ImageSequenceRenderer |

A source may expose more than one readable view. Example: one PDF can expose both Original Pages and Smart Book while retaining one canonical reading position.

Reader layout/profile terms such as Comic, Magazine, Scan or Artbook do not by
themselves create a second canonical media identity. Whether a future product
decision adds a distinct WorkMediaType is a MediaCore/domain question; the
Reader must already support those documents without inventing parallel Works.

## 4. ReaderDocument contract

`ReaderDocumentDescriptor` remains the lightweight identity/capability description. Add a renderer-facing document contract instead of making renderers inspect page routes or source-specific DOM.

The contract must carry:
- canonical Work identity;
- optional Volume/Chapter or other structural target;
- selected Edition/Version and readable Asset/File identity where available;
- layout kind;
- source language and available readable variants;
- capability flags;
- stable document/section/item identifiers;
- source mapping needed for cross-view/cross-edition position restoration;
- document direction/writing-mode metadata where meaningful.

### Reflow content

The reflow document must preserve semantic content instead of flattening everything to paragraphs.

Required semantic kinds as actual source content requires them:
- heading;
- paragraph/text;
- scene break;
- illustration/image;
- figure + caption;
- list/quote/preformatted block;
- table;
- footnote/endnote reference and body;
- formula/math;
- fixed/original-layout region for content that cannot be reflowed safely.

Do not create one class/file for every theoretical block kind merely to satisfy this list. Use the smallest clear representation that preserves the source semantics currently needed.

Every content item used by progress/annotations must have a stable source-derived identity or deterministic locator.

### Images

Distinguish:
1. source content images/illustrations — part of the readable document;
2. generated/accepted chapter artwork — optional Reader presentation;
3. Reader theme/background artwork — presentation only;
4. Manga/comic pages — primary document content.

Generated chapter artwork must never become part of the text anchor identity. Enabling/disabling it may change rendered layout but must not invalidate the logical resume locator.

## 5. Capability model

Capabilities are document-instance facts, not a fixed guess from only media type or renderer.

Examples:
- a digital PDF with reliable text may support search/selection/TTS;
- a scanned PDF may not until OCR is available;
- a Manga may gain OCR/search/Learning later;
- a fixed-layout EPUB may support text selection even though it is not reflowable;
- a generated translation may support dual-language mapping while another Edition may not.

Refine the current capability model so source adapters can supply or constrain capabilities after parsing/analysis.

Do not use one overloaded “vertical” capability for unrelated concepts.

Keep separate:
- image/page flow direction: LTR / RTL;
- continuous vertical image flow / webtoon mode;
- text direction: LTR / RTL / auto;
- text writing mode: horizontal-tb / vertical-rl / vertical-lr.

Japanese vertical text and Manga/Webtoon vertical page flow are different features.

## 6. Renderer responsibilities

### 6.1 ReflowTextRenderer

One implementation for Book, Light Novel and Web Novel reflowable content.

Owns:
- Scroll/Pages projection;
- one/two-page projection;
- pagination/reflow;
- generic page turns;
- resize/font/settings relayout;
- stable anchor capture and restoration;
- rendered location calculation;
- source illustrations/figures inside pagination;
- semantic text selection hooks;
- page-mode keyboard/touch navigation delegated from the shell.

Must preserve:
- headings;
- illustrations;
- ruby/furigana;
- inline emphasis;
- scene breaks;
- supported footnotes/internal links;
- tables/formulas without producing misleading flattened text.

Book/LN/Web Novel differences come from content, capabilities, presets, variants and source commands, not separate pagination engines.

### 6.2 FixedPageRenderer

Used for faithful fixed/pre-paginated content:
- original PDF;
- fixed-layout EPUB;
- page-faithful magazines/artbooks;
- other supported fixed documents.

Owns:
- single/spread/continuous projection;
- page width / whole page / zoom;
- page rotation/orientation;
- bounded page rendering/cache;
- text layer only when the source exposes reliable text;
- outline/internal navigation when available;
- fixed-page location reporting.

It must not expose reflow typography controls.

Fixed-layout EPUB content must be sanitized and isolated like all imported document content. Never execute embedded script or remote actions merely because the package contains XHTML/SVG.

### 6.3 ImageSequenceRenderer

Used for Manga, comics and image-sequence documents.

Owns:
- single page;
- spread;
- continuous/webtoon projection;
- fit width/page/height;
- zoom;
- LTR/RTL page order;
- cover/single-page spread rules;
- very wide spread/panorama handling;
- bounded image decoding/rendering;
- image-page location reporting.

Archive type is not a Reader concern. CBZ/ZIP/CBR/CB7 import adapters must all normalize to the same ordered image-sequence contract when supported.

## 7. Special-format rules

### EPUB

Support based on package/layout metadata:
- normal reflowable EPUB;
- fixed-layout/pre-paginated EPUB;
- illustrations and full-page images;
- SVG where safely renderable;
- ruby/furigana;
- footnotes/endnotes and internal links;
- tables;
- MathML/formulas where supported;
- embedded fonts only through a deliberate safe asset policy;
- text direction and writing mode.

Unsupported embedded active content must be removed/ignored safely.

Media overlays/audio/video embedded in EPUB are optional capabilities, not a reason to create another Reader. Unsupported media must degrade without breaking the text.

### PDF / scans

#819 remains authoritative for Smart PDF analysis.

Required Reader integration:
- Original PDF stays canonical and untouched;
- physical page and logical page are distinct;
- scan spread splitting can create two logical pages mapped to one physical page;
- Smart Book semantic blocks retain source-region mapping;
- Smart and Original views share one canonical progress identity;
- low-confidence layout/OCR falls back to fixed/original regions rather than inventing bad reflow;
- tables, figures, formulas, footnotes, CJK/RTL/vertical writing and mixed-language regions remain representable;
- page rotation/crop/orientation and internal links retain stable source mapping;
- hostile PDFs remain subject to #819 parser/resource/security limits.

### Manga / comics

Manga and western comics use the same image renderer but not the same defaults.

Manga may default to RTL when metadata says so. Comics must support LTR. Auto direction must come from metadata/profile policy, not title guessing.

Handle:
- cover/single first page;
- intentional two-page spreads;
- wide panorama/foldout pages;
- mixed portrait/landscape pages;
- different page dimensions;
- missing/corrupt page assets;
- nested archive paths and deterministic page order at import;
- optional OCR text as an overlay/capability without replacing page identity;
- optional panel detection/guided view as a derived navigation projection; the
  canonical durable locator remains the page/source position rather than a
  detector-specific panel id;
- ultra-tall Webtoon/long-strip images must use bounded/tiled rendering rather
  than decoding an unbounded surface at once.

`ComicInfo.xml` or equivalent metadata may be consumed by the import layer when supported. The Reader consumes normalized page/structure metadata.

### Magazines / artbooks / illustrated books

Do not force them into text reflow.

A magazine/artbook may be:
- fixed page;
- image sequence;
- Smart/semantic derived view only when analysis can preserve layout.

Sections/articles are navigation metadata when available. A document does not need fake chapters merely to be readable.

### CBR / CB7

The acquisition/storage layer already recognizes comic archive concepts, but current Manga import is CBZ/ZIP-oriented.

Reader requirement:
- no archive-specific UI or renderer;
- once a safe archive adapter exposes ordered images, it uses ImageSequenceRenderer.

Implementation prerequisite:
- dependency/licensing/security review for RAR/7z handling;
- archive bomb/path traversal/resource limits;
- no shell execution of untrusted archive commands.

### MOBI / AZW / AZW3 / DJVU

These formats are currently not normal readable Book imports.

Policy:
- do not silently convert or claim support;
- no DRM bypass;
- if a future parser safely emits the canonical ReaderDocument contract, reuse the same Reader;
- DRM-free MOBI/AZW can map to reflow/fixed according to actual content;
- DJVU can map to fixed pages and optional derived OCR/semantic view.

Format support belongs to import/document adapters, not a fourth Reader shell.

### DRM / encryption

- no DRM circumvention;
- encrypted/password PDF follows #819 secure password handling;
- unsupported DRM-protected EPUB/Kindle content gets an explicit unsupported/encrypted state;
- credentials are never stored through an ad-hoc Reader setting.

## 8. Variants, Editions and translation

Readable language/translation choices are content variants over the same Reader, not Reader engines.

Canonical target:
`Work -> Edition -> Version -> Asset/File -> ReaderDocument`.

Support:
- Original;
- official translated Edition;
- generated/machine translated Edition/Version;
- current temporary legacy translation tracks while migration is in progress;
- Both/parallel only when the source mapping supports it.

Rules:
- generated translation never overwrites the source or masquerades as official;
- cached translations remain readable even if generation/Learning capability is currently off;
- switching variant preserves the nearest stable logical location;
- the last readable variant may be remembered as presentation/session state without becoming a second progress owner;
- annotations tied to text must retain the variant/source identity needed to avoid attaching to the wrong text.

For existing Light Novel AI/TranslateGemma tracks, adapters may bridge them temporarily. The canonical end state is explicit Translation provenance + derived Edition/Version.

## 9. Chapter artwork and illustrations

Existing generated chapter artwork remains compatible.

Rules:
- source EPUB illustration wins as source content when the design calls for it;
- accepted generated chapter artwork is optional header/presentation artwork;
- generated artwork provenance/storage stays with the existing ChapterArtwork capability until canonical Asset migration reaches it;
- artwork loading/failure never blocks chapter text;
- no chapter artwork is used as a progress anchor;
- toggling generated artwork does not create or reset Reader progress;
- spoiler-safe generation remains owned by #407/shared story context, not the Reader.

## 10. Exact ReaderLocator and MediaProgress

#662 is the canonical progress dependency.

The Reader requires one layout-independent logical locator contract that can project into rendered pages.

Minimum locator semantics:

### Reflow text
- canonical structural target;
- selected source/Edition identity when needed;
- stable block/paragraph/content anchor;
- character/text offset where needed;
- fallback percentage.

### Image sequence
- canonical structural target;
- logical page/index;
- optional intra-page viewport only when meaningful;
- fallback percentage.

### Fixed document
- logical page;
- physical/source page reference when different;
- optional source-region/viewport offset;
- fallback document percentage.

### Smart PDF
- semantic block/logical page plus mapped physical source page/region;
- enough mapping to switch to Original and back without forking progress.

The rendered page number is never durable identity for reflowable text.

When an Edition/File/document is replaced or reanalysed, progress keeps the
canonical Work/structure identity and attempts deterministic source-anchor
mapping. If the exact locator no longer resolves, fall back explicitly to the
best valid mapped anchor/percentage; never silently mark content completed or
attach an annotation to unrelated text.

Keep separate:
- CurrentItem;
- ResumePosition / ReaderLocator;
- CompletedThrough;
- external ProviderProgress.

Opening an item never means completed.

### Persistence direction

Use one canonical `MediaProgress` identity envelope and a typed Reader/document position owned by Progress. Do not extend the current video millisecond fields into a giant nullable catch-all and do not create `BookProgress`, `MangaProgressV2` or another parallel store.

The exact persistence shape must preserve typed validation and be versionable for locator evolution.

## 11. Progress migration

Dependency order:

1. Finalize ReaderLocator application contract.
2. Add canonical Reader position persistence under MediaProgress.
3. Add migration/backfill for existing NovelProgress and MangaProgress.
4. Validate profile isolation, counts, newest timestamps and exact anchors/pages.
5. Switch Reader writes to canonical progress.
6. Switch Reader reads/restore to canonical progress.
7. Switch Continue Reading/Home/detail projections to canonical progress.
8. Switch offline progress queue/reconciliation to canonical progress IDs/locator payload.
9. Switch AniList reading write-back to CompletedThrough only.
10. Remove legacy NovelProgress/MangaProgress runtime paths after the supported migration gate passes.

No indefinite dual-read/dual-write fallback.

## 12. Preferences

`ReaderPreference` is the sole durable Reader preference owner.

Normalize all Reader types to:
- `default`;
- `type:<content-type>`;
- `genre:<priority>:<genre>`;
- `work:<canonical-work-id>`.

Manga-specific preference stores/scopes must become adapters/migrations into this owner rather than a second preference system.

LocalStorage is allowed only for clearly device/ephemeral convenience state that does not compete with profile preferences, for example transient panel open state. Durable mode, direction, layout, typography and other profile/work settings belong to ReaderPreference.

## 13. Shared interaction ownership

The shell owns:
- chrome visibility;
- settings/menu/sheet lifecycle;
- common keyboard routing;
- gesture arbitration;
- page-edge/swipe dispatch;
- progress UI;
- contents/search panel integration;
- fullscreen/focus/wake-lock integration where shared;
- common accessibility/focus behavior.

Renderer owns the actual requested page/layout movement.

Source adapter must not independently listen for the same generic arrows/swipes/taps if the shell already owns them.

Text selection/annotation always wins over navigation gestures.

## 14. Offline

#221 semantics remain:
- same Reader and renderers online/offline;
- same ReaderLocator;
- same ReaderPreference semantics;
- same bookmark semantics;
- local-first progress update followed by reconciliation;
- packages contain the actual assets needed by the selected readable variant;
- partial availability is explicit.

Do not create offline-only pagination or progress identities.

## 15. Source adapters after consolidation

Allowed source-specific behavior:
- bounded content loading;
- source/import preparation commands;
- translation availability/generation commands;
- annotations until canonical annotation migration completes;
- structure/contents queries;
- source-to-canonical mapping;
- document construction.

Not allowed:
- duplicate Reader chrome;
- duplicate preferences;
- duplicate generic page/scroll engine;
- duplicate generic gesture system;
- duplicate progress owner.

Books and Novels should become thin reflow document adapters around one ReflowTextRenderer.

Manga should become an image-sequence document adapter around one ImageSequenceRenderer.

PDF should become a fixed/Smart document adapter around the shared fixed/reflow renderer contracts.

## 16. Implementation phases

### Phase 0 — regression and ownership guards
- keep current Reader functional while refactoring;
- add behavior tests for chrome restore, page movement and exact-location preservation;
- add source/architecture guards for forbidden new Reader preference/progress owners where deterministic;
- document current legacy owners slated for deletion.

### Phase 1 — document + locator contracts
- strengthen ReaderDocumentDescriptor;
- introduce the renderer-facing ReaderDocument contract;
- introduce layout-independent ReaderLocator;
- distinguish page flow direction, text direction and writing mode;
- make capabilities document-instance driven.

Exit: source adapters can describe every current Book/LN/Manga/PDF document without renderer route guessing.

### Phase 2 — shared interaction runtime
- make reader-shell the sole generic interaction owner;
- normalize location/seek/page/layout events;
- remove double keyboard/swipe/tap ownership;
- one shared data-reading-mode state.

Exit: one gesture produces one action and chrome/settings state cannot diverge by source.

### Phase 3 — ReflowTextRenderer
- move generic Book + Novel pagination/scroll/layout/anchor code into one renderer;
- preserve EPUB images, ruby, annotations, translations, search, TTS and Learning hooks;
- source adapters keep only source-specific commands/data.

Exit: Book and Light/Web Novel use the same reflow engine.

### Phase 4 — ImageSequenceRenderer
- move Manga page/spread/webtoon behavior behind the image renderer;
- migrate Manga settings into ReaderPreference;
- normalize direction/spread/fit/zoom capabilities;
- preserve current page progress/bookmarks.

Exit: Manga uses the same shell and preference hierarchy and owns no generic Reader chrome/gestures.

### Phase 5 — FixedPageRenderer
- move PDF original-page behavior behind the fixed renderer;
- add fixed-layout EPUB path when parser metadata identifies it;
- keep text layer/search/TTS capability-driven;
- preserve page/spread/zoom behavior.

Exit: fixed documents share the same shell without typography controls.

### Phase 6 — canonical MediaProgress
- implement ReaderLocator persistence under canonical MediaProgress;
- backfill NovelProgress/MangaProgress;
- switch autosave/restore/Continue Reading/offline sync;
- remove legacy runtime progress owners after validation.

Exit: Book/LN/Manga/PDF continuation comes from one canonical Progress owner.

### Phase 7 — PDF Smart Book integration
- consume #819 derived semantic document;
- render Smart Book through the shared semantic/reflow path;
- retain mapped fixed regions;
- switch Original ↔ Smart through source mapping without progress fork;
- preserve figures/tables/formulas/OCR provenance.

Exit: PDF has two views, not two Reader products.

### Phase 8 — variants and canonical translation
- route official/generated Editions/Versions through one Reader variant contract;
- migrate legacy translation tracks as canonical Translation/Edition work lands;
- preserve Original/Official/Generated/Both UX and stable location mapping.

### Phase 9 — annotations/bookmarks canonicalization
- move legacy Novel/Manga bookmark/highlight targets to canonical content locators as the domain migration permits;
- keep text highlights variant-safe;
- avoid fragile fixed-page highlights when no reliable text/source mapping exists.

### Phase 10 — format adapters and hardening
- fixed-layout EPUB;
- safe CBR/CB7 adapter when dependencies are accepted;
- magazine/artbook profiles;
- explicit unsupported states for MOBI/AZW/DJVU until supported;
- future text/document adapters such as HTML/TXT/FB2, if ever added, emit the
  same reflow/fixed document contract rather than another Reader;
- import/render regression corpus for malformed/large/mixed documents.

### Phase 11 — legacy deletion
Delete obsolete runtime owners after parity tests pass:
- duplicate Book/Novel generic pagination/navigation logic;
- source-specific shared chrome code;
- legacy Reader localStorage preference ownership;
- Manga preference owner/scopes after migration;
- NovelProgress/MangaProgress runtime paths after progress migration;
- compatibility branches no longer inside the supported upgrade window.

Do not rename dead code to Legacy/V2 and leave it active.

## 17. Test matrix

Behavior tests must cover at least:

### Shared shell
- initial chrome visible;
- programmatic restore does not hide chrome;
- center tap/scroll/reveal behavior;
- settings/menu prevents hide;
- one key/swipe causes exactly one navigation action;
- selection prevents accidental page turn.

### Reflow
- Book and LN produce identical pagination behavior for equivalent documents;
- anchor survives width/font/theme/mode changes;
- long paragraph split restores exact offset;
- source illustration stays with correct logical content;
- vertical/RTL text is not reordered incorrectly where supported.

### Image sequence
- LTR and RTL;
- cover single-page rule;
- spread pairing;
- wide spread/panorama;
- portrait/landscape mix;
- continuous/webtoon;
- page restore after restart.

### Fixed
- PDF original page resume;
- fixed-layout EPUB;
- page rotation;
- spread/single/continuous;
- reliable text layer capability vs scan without text.

### Smart PDF
- logical != physical page count;
- scanned-spread split mapping;
- Smart ↔ Original round trip;
- fallback to fixed region when reflow confidence is insufficient.

### Progress
- exact autosave/flush;
- no completion on open;
- profile isolation;
- restart;
- migration preserves Novel/Manga positions;
- Continue Reading uses the same locator;
- offline reconciliation is idempotent;
- provider progress advances only from CompletedThrough.

### Variants
- Original -> official translation -> generated translation keeps equivalent location;
- Both only appears when supported;
- generated translation is labeled;
- annotation never jumps to the wrong translation variant.

### Content edge cases
- footnote/internal link round trip;
- ruby/furigana;
- tables/formulas remain readable;
- missing/corrupt image;
- huge image bounded rendering;
- encrypted/DRM unsupported state;
- malicious archive/PDF inputs fail safely.

## 18. Completion criteria

#289 is complete only when:
- there is one shared Reader shell/runtime;
- Book/LN/Web Novel use one ReflowTextRenderer;
- Manga uses ImageSequenceRenderer under the same shell;
- PDF/fixed content uses FixedPageRenderer under the same shell;
- renderer selection follows parsed layout/capabilities, not route or extension;
- one durable ReaderPreference owner remains;
- Reader progress uses canonical MediaProgress/ReaderLocator;
- Smart PDF integrates without a second progress or shell;
- source illustrations, generated chapter artwork and translation variants work without changing ownership;
- no duplicate generic gesture/chrome/pagination/progress paths remain active;
- regression tests protect the shared contracts;
- old runtime owners are removed after migration/parity gates.

#662 remains the progress authority and #819 remains the PDF-derived-document authority. This pack coordinates them; it does not duplicate their bounded responsibilities.
