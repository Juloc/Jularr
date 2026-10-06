# Discover / Search — Clean Design

Status: **approved clean UX direction**.

## One Home/Discover surface (binding product decision, 2026-10-07)

This decision supersedes every conflicting rule in this specification and in `docs/mockups/home/SPEC.md`. It was extended on 2026-10-07 (Discover completion): provider ownership, browse views, filters, paging, live search, Hero pool and the card action.

### One surface, three concepts
- Home and Discover are one consumer experience at `/`. An empty global Search is the normal Home/Discover surface. `/Discover` is only a permanent redirect to `/` that keeps the query string; there is one render implementation.
- Three concepts stay separate and are never mixed in one control: **media type** (what am I browsing), **browse view** (which ranking/feed) and **filters** (how the feed is narrowed).
- Content header (shell): left the context "Discover", centre the one wide global Search, right the Filter action (with the active count) and Notifications.

### Media-type bar
One wide rounded segmented control directly under the header (page content, not shell navigation): `All | Anime | Series | Movies | Light novels | Books | Manga`, later Audiobooks and Games when real discovery exists. Only types that are enabled for the instance and profile are rendered. Books and Light Novels are separate scopes and never share results.

### All landing
With no query, scope All and no narrowing filter the page is the landing: Hero, Continue Watching / Reading / Listening / Playing, then discovery shelves **for every enabled media type**, in the media-type bar order. Per type the shelves are Trending, Top, New and, where the source truthfully has the semantic, Upcoming. A shelf that a source cannot truthfully provide is omitted or uses the nearest honestly named semantic; a shelf is never faked to make types symmetrical. The page is intentionally long: vertical browsing across types, horizontal browsing inside a shelf. Shelves are previews (hidden native scrollbar, chevrons, bounded prefetch near the horizontal end); **See all** opens the browse view of that type and ranking.

### Selected type: browse views
With one type selected, a compact secondary **browse navigation** sits directly under the media-type bar (not inside Filters). It starts with **All**, the default: the overview of that type, its shelves (Trending, Top, New, Upcoming) one below the other exactly like the All landing but for this type only, with the Hero and Continue of that type above. The other entries open one ranking as a full view with infinite scrolling. It lists only the views that have real semantics for that type's provider: Trending, Top, New, Upcoming, All-time popular, Top rated, and My List when an applicable AniList account is connected (consumer label "My List"; hidden when no account is connected). My List is never a filter.

### Provider ownership
- **AniList** owns Anime, Manga and Light Novels. Manga and Light Novels use the same AniList MANGA family but are separated by format on the provider side: Manga excludes the NOVEL format, Light Novels require it. A Light Novel is never a generic Book and a Manga never a Light Novel. Provider records stay transient discovery evidence until a durable action resolves them to canonical Jularr identity.
- **TMDB** owns Movies and Series.
- **Books** use a capability boundary: a book provider may be a **primary discovery** source (browse feeds, search, paging, locale/region and rating capability), an **enrichment** source (metadata, editions, covers, ratings) or an **acquisition** source. These roles are separate. Open Library is the primary catalog/search/paging source and owns the global feeds; Google Books enriches (metadata, editions, covers, locale-aware lookups); Wikisource, Gutenberg and OPDS contribute search/acquisition evidence and never own consumer Trending; Usenet is acquisition availability only and never a discovery identity owner; Hardcover keeps its enrichment role (community rating) unless its real API proves it can own a feed.

### Ranking scope and regions
Every ranking declares its scope: **Regional**, **Locale-aware** or **Global**, and is only labelled for the scope it really has. The effective discovery region comes from the canonical instance/profile locale policy (de-DE is Germany, en-GB the United Kingdom, en-US the United States); it is never inferred from an IP address. A regional label ("in Germany") requires a defensible regional signal from the source. Open Library trending is global: it is shown as global trending or omitted, never as regional, and nothing is fabricated from language, publication country or search order. Search may be locale- and language-aware independently of ranking. A region-capable Book discovery provider is a tracked gap, not an assumption.

### Hero
The Hero is a rotating pool of useful candidates, in priority order: a useful Continue/Resume item; newly available local media; newly imported or acquired local media; a personalized recommendation; a New discovery item; a Trending discovery item. A strong Continue item comes first but never monopolizes the Hero; locally available discovery items are preferred over remote ones. The primary action is the canonical state (Play, Continue, Read, Listen, or the Request/Start intent); there is no Hero-specific acquisition model.

### Paging and infinite scrolling
Shelves are bounded previews. **See all** (the browse view) and Search load provider pages progressively with infinite scrolling until the source is exhausted. A first page has a bounded size; nothing fetches the catalogue at once. The paging contract is generic (category, mode, query, filters, locale, region, page/cursor in; items, next page, has-more out) and each provider maps it to its native paging (AniList page/pageInfo, TMDB pages, Open Library offset/page). Results are de-duplicated by canonical identity across pages, the end of a source stops loading, and a failed later page is retried without removing pages already loaded.

### Live search
Typing in the global search is debounced (no request per keystroke), obsolete requests are cancelled, and an older response never overwrites a newer query. Search mode shows local, persisted and cached results together with concurrent provider results and then pages progressively; late results follow the staged-commit rules of this specification without moving pointer, focus or scroll anchors.

### Filters
The Filter drawer contains narrowing constraints only (never Trending, Top or My List): **Genres** (searchable multi-select with selected tokens), **Year** (from/to range with presets 2020s, 2010s, 2000s, Before 2000; no year dropdown), **Status** (multi-select where the source has trustworthy semantics), **Availability** (multi-select: In library, Requested, Not requested) and **Language** (searchable multi-select only with reliable evidence). No filter is offered for a field the source cannot reliably filter. The Filters button shows the active count; active filters are also shown as removable tokens (with Clear all) below the browse navigation. Filters that the provider supports are pushed into the provider request so paging stays truthful; fields that can only be applied locally continue loading pages until enough matches exist, the source is exhausted or a bounded safety limit is reached, and a first page that happens not to match never claims that nothing exists.

### Card action
A card shows a fixed status area at its bottom (for example "Not requested"), never a separate metadata line beneath the card. On hover or focus the area becomes the canonical action: Request where approval is required, Play / Start watching where an instant start is permitted, Read for reading media, Listen for audio; a local title shows Play / Continue / Read / Continue reading / Listen / Continue listening. The action reuses the Request, Instant Play and Detail contracts; no second state machine exists. A card click never acquires.

### Continue and artwork
Continue uses clean poster-oriented cards. A vertical poster is never shown inside a horizontal card over a blurred copy of itself; only the Hero uses landscape/backdrop artwork.

### Future Admin-owned Discovery cache policy (requirement, not yet built)
Discovery caching and retention are not hardcoded policy. A future Admin area will configure a Discovery cache policy (for example Minimal, Balanced/default, Extended) and/or bounded settings for browse TTL, search TTL, transient metadata retention and artwork-cache retention and size, because self-hosters differ in provider tolerance, storage and privacy. Invariants: canonical, local and requested media metadata stays durable as the Library requires; transient Discover candidates never become permanent Works by being cached; lowering Discover retention never deletes canonical Library metadata; cache settings never disable request de-duplication or rate limiting; no provider is hammered because retention is low.

## Purpose

Discover and Search are one coherent surface.

- **Home** = personal continuation and recommendations from the user's existing context.
- **Discover** = actively finding new media, including titles not yet in the local library.
- Empty search = Discover.
- Typing = live search.
- Submit = full result view.

## Global search bar

The shell header carries the one global Search on every page, Discover included (Ctrl/Cmd+K focuses it); Discover has no search field of its own.

- very wide, rounded search field in the content header
- searches Anime, Series, Movies, Light Novels, Books, Manga, Audiobooks and Games
- the Filter action sits in the header to the right of it, before Notifications

## Media-type switch

Directly below the header, as page content (see the decision above): All, Anime, Series, Movies, Light Novels, Books, Manga, Audiobooks, Games, restricted to the types enabled for the instance and profile. This is the primary quick scope control.

## Discover content

Use horizontal rows such as:

- Trending
- New Releases
- For You
- Hidden Gems
- Because You Watched / Read / Listened To …
- Popular
- dynamic genre/theme rows when useful

Rows can open a full filtered result surface.

Do **not** add a permanent second chip wall of genres/tags such as Action / Adventure / Fantasy / Sci-Fi / Romance underneath the media-type row. Keep the layout as clean as the approved Light mockup. Genre/theme browsing belongs inside rows or the Filter surface.

## Filter

Filter opens as:

- Desktop: popover / side panel
- Mobile: bottom sheet / fullscreen sheet
- Tablet: adaptive panel
- TV: remote-friendly panel

Possible filter groups:

- media type
- genre
- year
- status
- language availability
- local/request availability
- sort

Do not permanently expose all filter values as chips.

## Discover cards

Discover cards are intentionally simpler than Library cards.

Show:

1. artwork
2. title
3. compact year/type line
4. one compact **language/request status indicator**

Do not show Library-style progress unless the title is already in the user's Library.

## Language / request indicator

The card should answer only the important question: **what is available for me?**

Use one small icon/badge-style state, not verbose text.

Required semantic states:

- **Preferred language available**
- **Only other language(s) available**
- **Requested in preferred language**
- **Requested in another language**
- **Not available / not requested**

The visual system may use icon + short code/color, but must remain understandable in both Light and Dark and must not rely on color alone.

Detailed audio/subtitle/edition language information belongs in the preview/detail surface.

## Media Preview / Quick View

Binding shared specification: `docs/mockups/media-preview/SPEC.md`.

Binding missing-media playback intent: `docs/mockups/instant-play/SPEC.md`.

Desktop:
- ordinary hover may highlight the card and reveal a Quick View affordance;
- the card itself does **not** expand/reflow on hover;
- normal card click opens the canonical Detail page;
- Quick View opens the cinematic Preview overlay;
- external trailer players are not instantiated by casual hover.

Mobile:
- no hover behavior;
- normal card tap opens Detail;
- secondary Quick View action opens the large Preview sheet.

Tablet:
- touch-first adaptive Preview sheet/dialog.

TV:
- stable card focus may populate the larger Preview/hero area;
- trailer begins only after a deliberate stable-focus delay;
- rapid D-pad movement must not continuously instantiate/start trailers;
- Back restores the prior row/focus position.

Trailer/artwork source and autoplay rules are owned by the Media Preview spec. Preview reuses canonical Play/Read/Listen/Request state and never creates a second playback/request model.

## Light / Dark

Both are first-class:

- same hierarchy and behavior
- separately tuned contrast and surfaces
- no simple color inversion
- approved Light mockup structure is the baseline
- Dark must follow the same clean structure and **must not add the extra genre/tag-chip row shown in the earlier Dark concept**

## States

Discover/Search must cover:

- loading
- no search query / Discover
- live search
- no results
- provider unavailable
- partial provider results
- local title
- discover-only title
- requested title
- downloading/preparing title
- trailer unavailable

## Staged late results and zero-shift ghost hints

Late provider/cache results must never make Discover/Search feel unstable while the user is actively navigating.

### Core rule

A visible result generation is stable while the user is interacting with it.

Newly arrived results are first **staged**, not immediately inserted into the card layout. The UI may show a restrained ghost hint in the existing inter-card spacing to communicate that another result is ready nearby.

Ghost hints:
- live entirely inside already-reserved card gap/padding space;
- have zero layout footprint and cause **no card movement, reflow or scroll-position change**;
- are not full cards and are not independently focusable/clickable;
- use subtle neutral geometry rather than provider-specific branding;
- respect reduced-motion preferences;
- disappear when the staged result is committed or discarded.

A real card is committed only at a safe interaction boundary. The insertion/rerank uses the next deterministic result generation; it must never be based on provider completion order.

### TV

TV has the strictest stability rule.

Do not insert/rerank cards while:
- D-pad/remote navigation is active or recently active;
- a key/button is held or repeated;
- focus is moving between cards/rows;
- the focused card/Preview is still in its stable-focus interaction window.

A short idle period after navigation settles may allow staged results to commit. The exact delay is an implementation tuning value, but it must be long enough that ordinary rapid remote navigation never triggers mid-navigation rearrangement.

Before commit, a zero-shift ghost hint may appear in the adjacent card gap. Current focus identity and its visual screen position must remain unchanged. If the next generation cannot preserve the focused item's stable context, defer the commit until the user leaves/re-enters the row or another natural navigation boundary occurs.

### Mobile / touch

Do not insert/rerank while:
- a touch/pointer contact is active;
- the user is swiping/dragging;
- inertial scrolling is still moving;
- a sheet/Preview gesture is active.

After scrolling and touch interaction have fully settled for a short idle period, staged results may commit. Preserve the visible anchor/scroll offset so the content under the user's finger/eyes does not jump.

A ghost hint may appear in existing card spacing while results are staged, but it must not widen the row/grid or consume a new layout slot.

### Desktop / mouse + keyboard

Desktop must avoid moving a target underneath a pointer that may be about to click.

Do not insert/rerank a result group while:
- a pointer button is down;
- wheel/trackpad scrolling is active or recently active;
- keyboard navigation is active;
- the pointer is over an actionable card, Quick View affordance or result-group control that would move because of the update.

Pointer movement alone should not freeze the page indefinitely. After a short inactivity window, a commit is allowed only when it cannot move an actionable target currently under the pointer. Otherwise defer until the pointer leaves the affected target/group or another safe boundary occurs.

Ghost hints may occupy the visual gap between covers while staging and must use `pointer-events: none`.

### Safe commit behavior

When staged results are committed:
- preserve the current canonical item, focus/selection and scroll anchor;
- avoid inserting before the active/focused/hovered item when that would visually move it;
- prefer changes outside the currently visible interaction neighborhood;
- never steal focus;
- never auto-open Preview/Detail because a new result arrived;
- keep accessibility order coherent after commit and announce new result availability only when useful, without repetitive live-region noise.

If no safe in-place commit exists, keep the current generation and apply the new generation on explicit refresh, navigation away/back, filter/query change, or another natural surface reload.

## Mockup reference naming

Recommended files:

- `discover-clean-light-approved.png`
- `discover-clean-dark-approved.png`

Platform-specific exports may later use:

- `discover-desktop-light.png`
- `discover-desktop-dark.png`
- `discover-mobile-light.png`
- `discover-mobile-dark.png`
- `discover-tablet-*.png`
- `discover-tv-*.png`

## Implementation rule

This specification is binding for Discover/Search UX. Coding agents must not turn Discover into another Home page, add a permanent genre-chip wall, or overload cards with Library/Admin metadata without updating the approved spec.

## Canonical media identity and Anime search mode

Search and Discover must use the owning domain's canonical identity rather than exposing provider records as separate product identities.

For watch/read/listen media, the canonical identity is Work. The Work/Season/Episode rules below apply to those media. Games is the explicit isolated-domain exception defined later in this spec and resolves to canonical Game identity instead of Work.

- The default model is **Work -> Season -> Episode** for Series/Anime. AniList is a metadata/presentation provider layered on top of that model, not a second library model.
- One canonical Anime work may map to multiple AniList media entries (for example separate seasons, parts/cours, specials or sequels where the provider splits them differently).
- **Default/global search** groups results by media type and deduplicates to one canonical Jularr work wherever identity resolution can prove the match. It must not normally show Season 1/2/3 as unrelated Anime cards just because AniList exposes separate entries.
- Result groups are independently collapsible/expandable: Anime, Series, Movies, Light Novels, Books, Manga and Audiobooks. “Show all” keeps the active type/filter context.
- When the **Anime** media-type filter is active, expose a compact result-view switch: **Jularr** / **AniList**.
  - **Jularr** (default): canonical work cards with seasons underneath/on the detail page.
  - **AniList**: provider-native entries may be shown individually for users/admins who intentionally want the AniList split.
- A sufficiently specific query such as “<title> Season 3”, a provider part title, or an exact AniList title may surface the matching season/provider entry directly. Opening it still resolves to the same canonical Jularr work and deep-links/highlights the matching season/presentation group.
- Selecting an AniList result must never create a duplicate Work solely because the provider split differs. Identity resolution produces a canonical Work plus an optional season/presentation/provider target.
- The same resolution rules apply to Discover shelves, Home recommendations, Requests, Calendar deep links and media details so cards do not disagree about identity.
- Provider-specific IDs, mapping conflicts and corrective mapping controls stay in Admin. Consumer search only exposes the simple Anime view switch when Anime is selected.



## Metadata localization and persistence boundary (#820)

Discover/Search is a provider-driven, transient surface. It must not become a second permanent multilingual media catalog.

For normal media:
- request provider candidates in the effective profile metadata locale where the provider supports it;
- cache candidate metadata and artwork with bounded TTL;
- keep locale in the candidate/cache identity when provider output is locale-dependent;
- do not backfill every transient provider candidate into every language used by the instance;
- do not create a durable Work merely because a result was displayed;
- resolve/dedupe to canonical Jularr identity before Request, monitoring, import or another durable action.

When a result already resolves to a durable Work, consumer presentation should prefer locally persisted Work metadata for the effective locale where available and may use the provider candidate only as transient supplemental evidence.

Discover artwork follows the same boundary:
- temporary provider cover/poster/backdrop/logo may be cached for Discover/Preview;
- language-specific artwork may be requested for the effective locale;
- durable local Work artwork is created/retained only once the Work has durable Jularr context.

Provider failure may degrade affected remote rows but must not make already-persisted Work metadata/artwork unreadable.

The fixed-vs-free instance language policy and background Library metadata spool are owned by #820. Discover never bulk-enrolls its transient results into that spool.

## Games in Discover

Games is an additional media type inside the existing Discover/Search surface.

There is no separate Games Discover page, Games search engine or Games request catalogue.

When the `Games` media-type filter is active, the existing Discover surface adapts its rows, filters and card metadata to Games.

### Games rows

Useful examples:
- Trending Games
- New Releases
- For You
- Popular
- platform-focused rows such as `Beliebt auf Game Boy Advance` or `PlayStation Klassiker`
- dynamic genre/theme rows where useful

Platform rows are dynamic. Do not create a permanent row for every known console.

### Games filters

When Games is active, the Filter surface may include:
- platform
- year
- genre
- region/language where useful
- local/request availability
- sort

Do not add a permanent chip wall for every platform.

### Games cards

Games Discover cards remain compact.

Show:
1. artwork/cover
2. canonical Game title
3. compact platform + year line
4. local/request/acquisition state

Examples:
- In Bibliothek
- Anfragen
- requested/downloading/preparing progress where the shared request pipeline exposes it

Do not show on Discover cards:
- emulator/runtime names
- BIOS/Firmware state
- ROM filenames
- raw regions/revisions unless they are genuinely required to distinguish the canonical result
- controller capability badges

### Games identity

One card represents one canonical Jularr Game.

Provider-specific records, regional releases or ROM variants must not become duplicate canonical Games in Discover merely because providers split them differently.

Opening a local Game goes to Game Detail.

Opening a non-local Game uses the same canonical Game Detail / shared Request behavior as the rest of Jularr.

For watchable media, opening a non-local title never acquires it merely from card activation. Detail/Preview resolves the effective action: Play/Continue when local, Start watching/Watch now when Playback + instant acquisition are permitted, or Request when explicit acquisition/approval is required. Manager-only instances never expose the playback-intent actions.

Media Preview / Quick View is not required for Games in V1; a Game card may open Game Detail directly unless a future Games-specific preview is explicitly approved.

### Shared request/acquisition

Games Discover uses the existing shared Request flow:

```text
Request
 -> Wanted
 -> Search
 -> Candidate
 -> Download
 -> Games Import
 -> Ready
```

No Games-specific Request, Wanted, Downloader or Activity stack is introduced.

### Mobile / Tablet / TV

The same existing responsive Discover behavior applies.

Mobile:
- Games appears in the existing media-type selector;
- Games filters open in the existing Filter sheet;
- no separate Games discovery navigation.

Tablet:
- same adaptive Discover layout.

TV:
- Games may appear as a media-type filter and in horizontal Discover rows;
- use normal remote/focus behavior;
- no emulator/controller configuration inside Discover.

### Games status vs language status

The general Discover card language/request indicator remains appropriate for watch/read/listen media.

For Games, platform plus local/request state is usually more useful than a language-first badge.

The card component may therefore adapt its compact secondary status by media type while preserving the same overall Discover card grammar.
