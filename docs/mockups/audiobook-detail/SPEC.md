# Audiobook Detail — Clean Design

Status: **approved Light-mode UX direction; binding planning specification**.

This is the binding consumer detail-page specification for Audiobooks. It follows the shared Jularr media-detail skeleton but keeps audio-specific progress, narration and chapter behavior explicit.

The approved Light-mode mockup establishes the current visual direction: shared Jularr detail-page modules, wide media Hero, compact fact strip, optional Parts rail, chapter list, Continue/Listen card, Editions & Languages card, About/Details, Related Works and More Like This.

The uploaded image in this mockup folder is the visual reference once present. This text remains authoritative for canonical data semantics, interaction, responsive behavior and edge states.

## Purpose

Canonical consumer detail page for an audiobook presentation.

The page must answer:

1. What audiobook is this?
2. Who narrates this edition?
3. Which language/narration is available?
4. Where did I stop listening?
5. Which chapter should I continue with?
6. How much listening time remains?
7. Are other editions/narrations/languages available?
8. Which written/related works belong to the same canonical media graph?

The screen is a consumer surface. Acquisition providers, files, codecs, release candidates, paths and import diagnostics belong to Admin.

## Canonical data contract

The target UI consumes the universal media model:

```text
Work
├─ Structure / chapter targets where known
├─ Edition
│  └─ Version
│     └─ Asset (Audio)
│        └─ File
│           └─ Track
└─ WorkRelation
```

Audiobook-specific publication metadata such as narrator, spoken language, abridged/unabridged state and total duration belongs to the applicable edition/presentation metadata. It does not justify a parallel Audiobook/AudiobookFile core.

Multiple narrators, languages or releases must not become duplicate canonical Works merely because their audio presentation differs.

Where an audiobook is related to a written Book/Light Novel Work, preserve that relation explicitly. Do not duplicate book identity or silently collapse distinct works.

User state is separate:

- canonical `MediaProgress`
- playback history
- playback preferences
- personal Watchlist/Favorite/Collection state

No permanent `AudiobookProgress` store is allowed.

## Progress semantics

Exact resume and completed progress are different.

Example:

```text
Current chapter: 7
Resume position: 18:42
Completed through: chapter 6
Total remaining: 4h 31m
```

Opening or seeking inside chapter 7 does not mark chapter 7 complete.

Continue Listening restores the exact canonical time position in the active audio Asset/structural target.

If a replacement Version/File becomes preferred later, progress survives because it targets canonical content identity rather than the physical file.

## Page structure

Single scroll page. No major tab bar.

Recommended order:

1. Hero
2. compact Hero fact strip
3. optional Parts / presentation-group rail
4. Continue Listening
5. Chapters for the selected Part/group
6. Editions / Narrations & Languages
7. About + compact Details
8. Related Written Work / Related Works
9. More Like This

Sections without meaningful data disappear.

## 1. Hero

### Content

- cover artwork
- optional backdrop/banner
- canonical title
- optional original/native title
- media type: Audiobook
- publication/release year
- compact genre/theme set
- short synopsis
- primary action is state-dependent: `Listen` / `Continue Listening` when a playable Edition is available; `Request` when unavailable and requestable; otherwise show the existing live Request state
- personal Watchlist/Favorite action where supported
- optional Collection action
- overflow for secondary consumer actions

### Request / acquisition behavior

When no playable Audiobook Edition is available and the active profile may request it:

- the primary consumer action is `Request`;
- it opens/uses the shared Request contract rather than an Audiobook-specific downloader UI;
- after approval or instant policy approval, Jularr searches enabled audiobook providers automatically;
- direct/free acquisition and shared Usenet/download-client acquisition converge on the same import path;
- while acquisition runs, the primary action remains in place and reflects the persisted live Request/Operation state;
- consumer-visible progress is limited to Pending/Search/Download/Import/Completed plus a compact percentage when meaningful;
- on completion the primary action becomes `Listen` / `Continue Listening`;
- provider/indexer/release/file details stay in Admin/Wanted/Manual Search surfaces.

If another profile already caused a compatible acquisition for the same canonical audiobook edition, show the shared request/acquisition state rather than offering a duplicate Request.

### Compact information strip

High-value audio facts:

- author / original creator
- narrator or primary narrator
- spoken language
- total duration
- chapter count where meaningful
- publisher/imprint where useful
- abridged/unabridged when known
- available narration/language count only when multiple exist

Do not show codec/bitrate/container in the normal Hero.

### Artwork fallback

Priority:

1. canonical/provider backdrop;
2. derived cover blur/gradient;
3. clean neutral gradient.

Do not stretch low-resolution cover art without treatment.

## 2. Parts / presentation groups

Audiobooks may expose larger listening groups such as **Part 1, Part 2, Part 3** when the source edition or canonical presentation has meaningful grouping.

This uses the shared presentation-group concept or canonical structure where appropriate. It must not create a separate AudiobookPart identity model.

Each Part item may show:

- title/order;
- optional artwork/backdrop crop;
- duration;
- compact progress;
- selected state.

Desktop uses a horizontal rail. Mobile uses a swipeable rail.

If the audiobook has no meaningful Parts, omit the rail and show Chapters directly.

## 3. Continue Listening

Primary personal-state card directly below the Hero.

When progress exists, show:

- current chapter
- chapter title
- current time in chapter or whole edition
- total chapter duration
- compact progress bar
- total time remaining for the audiobook when available
- primary `Continue Listening`

Example:

```text
Chapter 7 · The Northern Gate
18:42 / 42:10
4h 31m remaining
```

The action opens the shared Player in audio-only mode at the exact resume point.

When there is no progress:

- omit the empty progress card;
- Hero primary action becomes `Listen` or `Start Listening`.

Optional secondary action:
- `Start from beginning` in overflow, not as a second dominant button.

## 4. Chapters

Chapters are the primary structural navigation when reliable chapter structure exists.

Each row/card may show:

- chapter number/order
- title
- duration
- read/listened state
- current/resume marker
- progress for the active chapter
- availability state
- open/play action

### Chapter source

Chapter structure may come from:

- canonical mapped chapter structure;
- embedded M4B chapters;
- multi-file chapter/track organization;
- provider/import metadata resolved into canonical structure.

The UI must not expose which physical source produced the chapter list unless troubleshooting is explicitly requested in Admin.

### Desktop

Use a compact chapter list.

Suggested row density:

- number
- title
- duration
- progress/status
- play action

Current chapter can be highlighted subtly.

### Mobile

Touch-sized stacked rows.

Do not squeeze desktop columns onto the phone.

### Very long works

For very large chapter counts:

- virtualize/paginate as needed;
- retain current chapter position;
- provide jump/search only if it solves real navigation needs.

### No reliable chapter structure

Do not invent chapters from arbitrary file names in the UI.

Show a single continuous listening experience or the best canonical track structure available.

## 5. Editions / Narrations & Languages

This section is central to Audiobooks and uses canonical Edition/Version semantics.

Each compact edition row may show:

- spoken language
- narrator(s)
- publisher/source edition label
- release year
- unabridged/abridged where known
- duration
- complete/partial availability
- preferred marker
- local/requested/downloading state

Examples:

- German · Anna Keller · Unabridged · 12h 42m
- English · James Reed · Unabridged · 11h 58m
- German · Ensemble Cast · Dramatized · 9h 34m

### Narrator semantics

Narrator is presentation metadata, not a tag attached to the Work globally.

Different narrators or dramatized productions may represent distinct Editions/Versions while still referring to the same canonical underlying work where identity is known.

Do not merge clearly distinct narration editions just because title + author match.

### Preferred language

Preferred spoken language is visually clear but restrained.

The page should answer quickly:

- Is my preferred language available?
- Which narrator/edition will play?
- Is it complete?
- Are alternatives available?

### Actions

When permitted:

- switch edition/narration
- switch spoken language
- request missing edition/language
- view additional narrations

Switching editions should preserve equivalent canonical listening position when structural mapping is known. If mapping is unsafe, do not guess; start/ask explicitly rather than silently jumping to the wrong chapter.

## 6. About

Show:

- synopsis/description
- compact genres/themes
- author/creator credits where useful

Long text uses `Read More`.

Avoid repeating all Hero metadata.

## 7. Related Written Work / Related Works

Use canonical WorkRelation.

Priority relationships:

- original Book / Light Novel
- ebook/print counterpart where modeled as related canonical work
- adaptation/source relation
- sequel/prequel
- series entry
- dramatization where it is a distinct canonical work
- related audio production

When a written counterpart exists, make it easy to open its Reading Detail page.

Do not create a second duplicate book record merely because the audiobook exists.

## 8. More Like This

Simple Discover-style recommendation row.

Cards should remain lightweight:

- artwork
- title
- compact media type/year
- optional preferred spoken-language availability

Do not use admin or file-quality details.

## 9. Details

Only useful secondary facts:

- release/publication date
- publisher
- narrator credits
- abridged/unabridged
- original publication language
- edition identifiers only if consumer-useful
- series/order information

Raw file paths, hashes, codecs, bitrates, provider IDs and download history are not consumer details.

## Player integration

Audiobook playback uses the shared Player/ActiveSession/MediaProgress contracts.

Audio-only Player presentation may use:

- cover/backdrop
- large Play/Pause
- timeline
- chapter navigation
- +/- seek
- speed
- audio output controls where supported

Playback speed is reached from the Player, not permanently exposed as a large Detail-page control.

The Detail page must not create a second embedded player implementation.

### Future/optional controls

Features such as sleep timer may be added to the audio Player later. They are not required to clutter the Detail page.

## User actions

Primary:

- Listen
- Continue Listening
- open/play chapter
- switch narration/edition/language

Secondary:

- Watchlist/Favorite where supported
- Collection
- Request missing content
- open related written work
- open recommendation

A visible Back control must use shared context-aware back behavior and preserve originating Library/Discover context where practical.

## Availability and acquisition summaries

Consumer states may include:

- Available
- Partial
- Requested
- Looking for media
- Downloading
- Preparing
- Unavailable
- Storage offline

These summarize canonical acquisition/library state.

Do not expose:

- Newznab/Torznab result rows
- SABnzbd job details
- provider-specific download payloads
- release score
- file-match confidence diagnostics
- raw import paths

Those belong to Admin Manual Search / Imports / Media Detail.

## Light / Dark

Both are first-class.

### Light

- white/soft-gray surfaces
- artwork-driven Hero
- restrained purple Jularr accent
- subtle selected/current states
- calm narration/language rows

### Dark

- deep neutral surfaces rather than pure-black card walls
- maintain separation between Hero, chapter list and edition cards
- preserve metadata contrast
- artwork gradient must keep title/actions readable

Information hierarchy remains identical between themes.

## Desktop

Approved Light composition:

- persistent Jularr sidebar/top search shell consistent with the other consumer mockups;
- wide artwork Hero with one integrated cover, title, author, summary and primary actions;
- Hero fact strip directly below with rating/community only when globally supported, runtime, narrator, publisher, primary spoken language and available-language summary;
- optional horizontal **Parts** rail below the Hero;
- two-column content region:
  - large left column = `Chapters — <selected Part>`;
  - compact right column = `Listen`/Continue card followed by `Editions & Languages`;
- About and compact Details below the chapter area;
- Related Works and More Like This as compact artwork rows near the bottom;
- chapter rows remain calm and list-like rather than large media cards;
- compact hover secondary actions allowed, never required

Do not imitate a music-library table full of file metadata.

## Mobile

Approved Light composition:

- artwork-led compact Hero with integrated cover and title;
- rating/runtime/narrator as a short fact row;
- prominent Continue Listening and personal Watchlist/Favorite action where supported;
- optional compact **section jump bar** such as Parts / Chapters / Details / More;
- the jump bar scrolls to sections on the same page; it is not a separate tabbed information architecture and must not duplicate page state;
- swipeable Parts rail when meaningful;
- Chapters as touch-sized list rows with play action and duration;
- Editions card below chapters with active edition clearly selected;
- About/Details and Related/Similar continue on the same scroll page;
- entering playback opens the dedicated audio Player

No dense metadata table.

Entering playback opens the dedicated audio Player.

## Tablet

Touch-first adaptive layout.

Portrait:
- close to Mobile

Landscape:
- may use two-pane chapter + edition/narration layout
- no hover assumptions

## TV

Audiobook browsing and playback are valid TV workflows.

Detail page:

- large cover/title
- narrator/language/duration
- Continue Listening
- chapter rail/list
- alternate narration/language where useful
- Related/Similar

Focus state:

- strong ring/glow
- modest scale/lift
- predictable remote path

Selecting Listen opens the shared TV audio Player.

No tiny metadata columns or dense chapter table.

## Loading state

Use skeletons preserving final geometry:

- Hero
- Continue Listening
- short chapter list
- narration/language card

Optional provider/recommendation data must not block the core page.

## Empty states

### No local audio

Still show canonical metadata and relations.

Primary action becomes:

- `Request` when permitted;
- concise unavailable state otherwise.

Never show a separate consumer Add acquisition action.

Do not render an empty file table.

### No chapter data

Show continuous playback state rather than fake chapters.

### No alternate narrations/languages

Hide the editions chooser or show only the active edition compactly; do not create an empty selector.

### No related/recommendation data

Hide those sections.

## Partial states

Must support independently:

- metadata available while artwork is missing
- audio available while chapter metadata is incomplete
- some chapters/files missing
- preferred language unavailable but another edition exists
- requested/downloading/preparing alternate narration
- storage offline while cached metadata/artwork/progress remains visible
- recommendation provider unavailable
- external/provider progress unavailable while local resume remains valid

Optional failures must not collapse the whole page.

## Error states

### Page-level

Only when the canonical Work/detail target itself cannot resolve.

Show:

- concise error
- Retry
- context-aware Back

### Section-level

For recommendations, alternate editions or optional metadata:

- preserve the rest of the page;
- show compact Retry only inside the affected section.

Never replace valid local progress with provider failure.

## Accessibility / input

- semantic heading order
- keyboard reachable Desktop actions
- visible focus states
- touch-sized Mobile/Tablet targets
- strong TV remote focus
- progress not color-only
- narration/language state has textual labels
- artwork is decorative unless it conveys unique information

## Current architecture migration notes

Existing legacy entities such as `Audiobook`, `AudiobookFile` and `AudiobookProgress` are migration sources, not the target UI contract.

Issue #440's product requirements remain useful for:

- M4B/MP3 support
- narration/language preservation
- chapter handling
- local/import/provider acquisition
- duplicate prevention
- cross-device resume

But the target consumer/detail model is the canonical:

```text
Work -> Edition -> Version -> Asset -> File -> Track
```

with shared acquisition and unified MediaProgress.

Do not implement a separate audiobook acquisition engine or permanent release hierarchy beside the canonical model.

## Dependencies before implementation

Required:

- canonical Work/detail queries
- Edition/Version/Asset/File availability
- narrator/spoken-language edition metadata
- canonical chapter/track mapping
- unified MediaProgress exact resume/completion
- shared Player audio-only mode
- WorkRelation
- request/capability policy

Optional:

- external/provider progress
- recommendation service
- multiple narration comparison
- future sleep timer

The basic Detail page must work without optional integrations.

## Must not implement

- no permanent `Audiobook` / `AudiobookFile` parallel core
- no `AudiobookProgress` store
- no narrator/language variant as a duplicate Work by default
- no raw file/release/import/indexer diagnostics
- no codec/bitrate/container clutter on normal consumer UI
- no embedded second audio player on the Detail page
- no marking chapters complete merely because they were opened
- no provider state replacing exact local resume
- no invented chapters from unreliable filenames
- no desktop table squeezed onto Mobile
- no empty placeholder sections
- no desktop tab-heavy layout hiding Chapters/Continue
- no Mobile section jump control that becomes separate duplicated page state
- no different acquisition workflow just for Audiobooks

## Mockup deliverables

The current approved Light mockup covers the required initial **Desktop + Mobile ready state** and is the baseline for implementation planning.

It establishes:

- shared modules with other Jularr media-detail pages;
- wide Hero + compact facts;
- Parts rail;
- selected-Part chapter list;
- dedicated Continue/Listen card;
- compact Editions & Languages selector;
- About + Details;
- Related Works + More Like This;
- corresponding Mobile stacking/order.

Still needed later only where useful:

- Dark-mode derivation using the exact same hierarchy;
- no-local-audio / Request state;
- alternate narration or preferred-language-unavailable state;
- TV focus state because audiobook playback is a valid TV workflow.

Do not produce duplicate mockups merely to restate the same modules.

Text specification wins over images on conflict.
