# Library — Cross-media Consumer Library

Status: **binding planning specification**.

Visual baseline:
- `library-clean-approved.png` is the approved Clean Library reference.
- newer/alternate images in this folder are non-binding unless explicitly approved.
- this text wins where an older image conflicts with later decisions.

Global UX: `docs/UX.md`  
Collections: `docs/mockups/collection-detail-edit/SPEC.md`  
Media Preview: `docs/mockups/media-preview/SPEC.md`  
Request: `docs/mockups/add-request-flow/SPEC.md`

## 1. Purpose

Library is the profile-facing catalog of media that already has durable Jularr library/monitoring context.

It is not:
- a second Discover page;
- an Admin media-management dashboard;
- an import surface;
- a statistics dashboard;
- a place where every media type gets its own navigation tree.

Discover/Search is for finding new media. Library is for browsing and consuming media Jularr already knows as part of the profile/library context.

## 2. Primary structure

Inside the single Library destination use:

`Library | Collections`

Collections is a Library subview.

Do not add Collections as a permanent top-level sidebar/bottom-nav/TV-nav destination.

When `Library` is selected, expose media-type scope:

- All
- Anime
- Series
- Movies
- Manga
- Light Novels
- Books
- Audiobooks

A future display preference may combine related types, but canonical media identity remains unchanged.

## 3. What appears in Library

Normal Library membership may include:

- locally available Works;
- partially available Works;
- Works with durable library/monitoring context;
- a Work whose approved/requested acquisition has already entered the durable Library/monitoring context.

A transient Discover/provider candidate with no durable Jularr relationship does **not** appear in Library merely because it was searched/fetched.

Library does not create a second membership truth from provider lists or search results.

## 4. Request / acquisition state

Consumer Library only projects the canonical Request/availability state.

Examples:
- Requested;
- Looking for media;
- Downloading;
- Preparing;
- Partially available;
- Available.

Do not create a Library-specific request state machine.

If the Work is unavailable and has no durable Library/request/monitoring context, normal discovery starts in Discover rather than exposing an Add button in Library.

There is no general consumer `Add` menu in Library.

Manual file/folder import and repair belong to Admin flows.

## 5. Desktop layout

Order:

1. page title `Library`;
2. `Library | Collections` switch;
3. media-type scope;
4. compact toolbar;
5. media grid/list.

Toolbar:

- Search within Library;
- Filter;
- Sort;
- Grid/List toggle where useful.

Do not show:
- stats sidebar;
- active-genre dashboard;
- large Add button;
- media-type-specific Add menu;
- permanent status/genre chip wall;
- duplicated Continue Watching shelf that belongs on Home.

The normal state should be visually quiet and media-first.

## 6. Search within Library

Library search searches the current local/durable Library context only.

It does not silently expand into external provider discovery.

For new media discovery, use global Search/Discover.

Search respects current media type and filters.

## 7. Filter model

One `Filter` control with active-count indicator, e.g. `Filter 3`.

Desktop:
- popover/side panel.

Mobile:
- bottom/fullscreen sheet.

Tablet:
- adaptive sheet/panel.

TV:
- remote-friendly panel.

Useful groups:

### Progress
- Not started
- In progress
- Completed

### Availability
- Fully available
- Partially available
- Missing units
- Requested/downloading where the Work is already in durable Library context

### Language
Video:
- preferred audio available
- audio language
- subtitle language

Written:
- edition/reading language

Audiobook:
- spoken language

### Personal state
- Watchlist / Reading List
- Favorite where implemented

### Metadata
- Genre
- Year
- status
- format only where relevant

Do not duplicate the current top-level media-type scope inside Filter unless a platform-specific combined filter UI genuinely requires it.

Only expose filters that make sense for the selected media type.

## 8. Sort

Single Sort menu.

Supported baseline:
- Recently used
- Recently added
- Title
- Release date/year
- Progress
- User rating where useful

Manual Collection custom order belongs to Collections, not normal Library.

## 8a. Personal lists

Watchlist/Reading List is profile state over canonical Works, not a separate Library, Collection or acquisition model.

Expose it through the existing Library surface:
- a Personal state filter such as Watchlist / Reading List / Favorite where those states exist;
- deep links may open Library with that filter already applied;
- written media may label the canonical list state `Reading List`;
- Home/Profile may link to the filtered view;
- removing an item changes only that personal-list state.

Do not create a separate top-level Watchlist app or duplicate Watchlist as a Manual Collection.

## 9. Canonical MediaCard

Library reuses one shared MediaCard/ListRow grammar.

Always:
1. poster/cover;
2. title;
3. one useful media-specific status/progress line;
4. language availability line;
5. optional progress bar;
6. restrained overflow/context action.

Do not overload cards with:
- provider IDs;
- release groups;
- codec/container details;
- download-client details;
- acquisition scoring;
- many badges.

In `All`, media type may appear compactly when needed.

Inside a specific media-type scope, do not repeat that media type on every card.

## 10. Media-specific status examples

### Anime / Series
Prefer the next useful consumption state:
- `S1 E8 · 22 min left`;
- `Up next: S2 E4`;
- `18 / 24 available` when partial availability is the important state.

### Movie
- `2024 · 2h 04m`;
- or resume state such as `48 min left`.

### Book / Light Novel
- `Vol. 4 · 63%`;
- `Ch. 18`;
- useful next/resume context.

### Manga
- volume/chapter/progress.

### Audiobook
- chapter;
- time remaining/progress.

Show the next useful fact, not a technical inventory summary.

## 11. Language availability

Cards answer: **Can this profile consume it in the preferred language?**

### Video
Audio and subtitles remain distinct.

Examples:
- `Audio DE · Subs DE`
- `Audio JP · Subs DE`
- `Audio JP · Subs JP, EN`

Preferred languages appear first.

When preferred language is unavailable, show the actually available fallback instead of falsely implying availability.

### Written media
Show available Edition/read languages.

Examples:
- `DE`
- `JP · EN`
- `DE · generated`

Official/generated must remain distinguishable without turning the card into a metadata table.

### Audiobook
Show spoken language.

Detailed Edition/Track information belongs on Detail/selector surfaces.

## 12. Partial / missing media

A Work may remain useful while incomplete.

Card-level examples:
- `18 / 24 available`;
- one compact partial/missing state;
- request/download progress where active.

Do not put a warning badge on every missing Episode/Chapter at Library-card level.

Exact missing units belong to the canonical Detail/Admin surfaces.

## 13. Card interaction

Normal card activation:
- opens the canonical Detail page.

Do not expand/reflow the card on ordinary Desktop hover.

Pointer hover may:
- highlight the card;
- expose a restrained direct Play/Read/Listen affordance when useful;
- expose the approved Quick View action.

Quick View behavior is owned by `media-preview/SPEC.md`.

Play/Read/Listen starts/resumes canonical Player/Reader flow, never a Library-specific session.

## 14. Collections integration

When `Collections` is selected:
- replace Library-media browsing with the Collections landing;
- do not keep normal media-type controls as though Collections were another media type.

Collection behavior is fully owned by `collection-detail-edit/SPEC.md`.

Library and Collections share visual primitives but not membership semantics.

## 15. Mobile

Structure:

1. Library title;
2. `Library | Collections`;
3. horizontally scrollable media-type scope;
4. compact Library search;
5. Filter + Sort + optional view action;
6. two-column **poster/cover** grid where width permits.

Do not replace normal poster cards with wide landscape thumbnails.

Each card keeps:
- title;
- one compact status/progress line;
- language line;
- optional progress bar.

Tap:
- Detail.

Secondary action/long-press:
- context actions / Quick View where useful.

Do not squeeze Desktop filter panels/tables onto phone.

## 16. Tablet

Portrait:
- touch-first, close to Mobile with more grid width.

Landscape:
- close to Desktop.

Same state/data semantics on both.

## 17. TV

TV is remote-first, not an enlarged Desktop grid.

Primary Library screen:
- `Library | Collections`;
- media-type scope when Library is selected;
- large poster cards;
- Filter/Sort;
- predictable remote focus.

Focused card:
- strong accessible focus ring/border;
- slight scale/lift;
- full title;
- useful progress/status;
- language availability.

After stable focus, Media Preview may populate according to `media-preview/SPEC.md`.

Do not:
- add Collections as separate TV top-level navigation;
- expose tiny desktop controls;
- expose management/import UI.

Back restores previous focus/scroll context.

## 18. Clean / Original Jularr

Same structure and behavior.

### Clean
- neutral surfaces;
- purple default accent;
- no decorative Japanese/anime background;
- media artwork supplies most visual color.

### Original Jularr
- red/pink default accent;
- restrained ink/watercolor/sakura treatment may frame the page;
- media remains primary;
- decoration must not reduce card/filter readability.

Theme/accent hue shifting uses shared semantic tokens.

## 19. Light / Dark

Both are first-class.

Do not implement Dark as simple inversion.

Card hierarchy, progress, language labels, focus and filter state must remain accessible in both modes.

## 20. States

Support:
- loading/skeleton;
- empty Library;
- no filter/search results;
- missing artwork;
- storage temporarily offline;
- partial availability;
- preferred language unavailable;
- request/search/download/preparing;
- permission/module-unavailable state;
- generic error state through shared error contracts.

Empty Library should direct the user toward Discover/Search, not expose a media-type Add menu.

## 21. Canonical identity

Library identity is canonical:

`Work -> Structure -> Edition -> Version -> Asset/File -> Track`

Anime/Series:
- one canonical Work with Season/Episode structure;
- provider entries such as AniList season/part records are mappings/presentation evidence;
- provider splits do not duplicate Library cards when they resolve to the same Work.

Provider-specific deep links may select/highlight a relevant season/presentation target while remaining on the same Work identity.

## 21a. Localized Work metadata and artwork (#820)

Library/Detail are local-first consumers of durable Jularr state.

Normal Library/Detail reads:
- use persisted canonical Work metadata and local artwork derivatives;
- do not require a live AniList/TMDB/Open Library/other provider request;
- keep last-known metadata usable during provider outages;
- may enqueue or reprioritize background metadata refresh, but must not block rendering on it.

The effective metadata locale comes from the canonical instance language policy:
- Fixed mode -> the single admin-selected instance language;
- Free mode -> the profile metadata language, with deterministic local fallback.

When the preferred locale is unavailable:
1. render the best locally persisted fallback immediately;
2. if the locale is already in the metadata spool, promote that `(Work, locale)` job when the Work is opened;
3. otherwise enqueue it at interactive priority when policy permits;
4. replace the fallback naturally on later reads after the localized data is persisted.

One Work remains one Work across all locales. Localized titles/descriptions and other localized fields are variants/evidence on the same canonical identity, not separate Library entries.

When Admin enabled “Keep Library metadata for all active profile languages”, durable Library/monitoring Works are gradually populated for every effective profile metadata language. A newly introduced profile language therefore causes background coverage work without making the first page request wait for a whole-Library backfill.

Artwork:
- canonical slots are media-independent: Poster/Cover, Backdrop/Banner, Logo and other approved slots;
- prefer artwork matching the effective locale where the provider exposes language-specific art;
- then prefer neutral artwork;
- then another locally available variant;
- compact derivatives remain Jularr-owned local cache data (for example under `/data`) so Library cards remain available while NAS/provider sources are offline.

Transient Discover candidate metadata/artwork is not Library truth and does not enter Library solely because it was viewed.

## 22. Approved visual baseline

`library-clean-approved.png` remains the approved Clean baseline.

Later textual decisions override visual elements that predate:
- Request-only consumer acquisition;
- `Library | Collections` nesting;
- Media Preview;
- removal of consumer Add/import controls;
- removal of stats/genre dashboard clutter;
- refined language/progress card semantics.

Do not treat other unapproved images in this folder as implementation authority.

## 23. Must not implement

- no Collections top-level navigation;
- no media-type-specific sidebar duplication;
- no consumer Add/import menu;
- no Library statistics sidebar;
- no active-genres dashboard;
- no Home-style large Continue shelf duplicated in Library;
- no permanent genre/status chip wall;
- no hover card expansion/reflow;
- no separate Library request/progress model;
- no provider-native identity as Library identity;
- no live provider dependency for normal Library/Detail rendering;
- no duplicate Work per metadata language;
- no technical release/download metadata on normal cards;
- no wide landscape-card substitution on Mobile;
- no Desktop grid simply scaled up for TV.

Text specification wins over imagery on conflict.
