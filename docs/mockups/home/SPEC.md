# Home — Clean Design

Status: approved UX direction for the Jularr Home page.

## One Home/Discover surface (binding product decision, 2026-10-07)

This decision supersedes every conflicting rule in this specification and in `docs/mockups/home/SPEC.md`. It was extended on 2026-10-07 (Discover completion): provider ownership, browse views, filters, paging, live search, Hero pool and the card action.

### One surface, three concepts
- Home and Discover are one consumer experience at `/`. An empty global Search is the normal Home/Discover surface. `/Discover` is only a permanent redirect to `/` that keeps the query string; there is one render implementation.
- Three concepts stay separate and are never mixed in one control: **media type** (what am I browsing), **browse view** (which ranking/feed) and **filters** (how the feed is narrowed).
- Content header (shell): left the context "Discover", centre the one wide global Search, right the Filter action (with the active count) and Notifications.

### Media-type bar
One wide rounded segmented control directly under the header (page content, not shell navigation): `All | Anime | Series | Movies | Light novels | Books | Manga`, later Audiobooks and Games when real discovery exists. Only types that are enabled for the instance and profile are rendered. Books and Light Novels are separate scopes and never share results.

### Personal layout (#877)
The order of the media types, which of them are shown, the landing page (Home/Discover or Library) and whether Continue comes first are one preference with one owner, `HomeLayoutStore` (tables `InstanceHomeDefaults`, one row, and `ProfileHomeLayouts`). Media types are stored as stable identifiers, never labels. The bar and the media-type groups of the All landing follow it; inside a type the rows stay Trending, Top, New, Upcoming, and the Hero stays first. Hiding a type removes its tab, its group and its recommendations from this viewer's Home, and nothing else: the module stays on, Library and detail access stay, and `?category=` still browses it. With Continue first off, the Continue row follows the media rows.

Instance defaults apply to every profile without an override. They are edited in Admin → Instance and in the instance step of first-run Setup (the same editor and `HomeLayoutStore`, writing only `InstanceHomeDefaults`; Setup shows a media type as soon as its module switch is on and never writes a profile's layout; leaving the section alone or skipping Setup keeps the built-in default). saving in Settings → Home (or in the one-time, skippable onboarding offered on Home, `/Settings/Home?welcome=true`) creates the override, and "Use the instance default" removes it, so later changes of the default apply again. Skipping leaves the instance default in effect and the offer does not return. A module that is switched off is neither offered nor shown, but its place and hidden flag stay stored, so switching it on again restores them; a media type the stored order does not know yet is appended in the built-in order and a stored hidden type is never shown again by itself. Quick presets (Balanced, Movies & Series, Anime / Manga, Books / Light Novels, Everything) only fill the editor. Cards reorder by dragging the handle, by the arrow buttons, or from the keyboard and a remote (Enter on the handle picks the card up, Up/Down move it, Enter or Escape drops it); a live preview shows the resulting bar. Not covered: density, text size, Reduced Motion and High Contrast preferences (no owner exists for them), Audiobooks and Music (no discovery shelves yet).

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

### Local-first discovery snapshot (built)
The last successful answer of every browse source (not searches) is kept in the local database (table DiscoverySnapshots: at most 256 answers, at most 14 days old) and loaded once after a restart. A revisit is stale-while-revalidate: an aged answer (fresh for 3 minutes) is shown at once while one background call renews it; a failed renewal keeps what was usable, so a provider outage never empties the page. The first response of / carries the titles when any source has something to show (fresh, stale or from the snapshot), without a skeleton and without a second request; the browser only asks for what was renewed since and applies it through the staged-commit rules. Only a page with nothing yet (the very first load, a type that was never browsed) shows its placeholder. The snapshot holds provider facts only: per-viewer state (requests, follows, library overlay) is applied per response, and nothing cached here becomes a Work. The freshness, age and size bounds are constants until the Admin policy below exists.

### Future Admin-owned Discovery cache policy (requirement, not yet built)
Discovery caching and retention are not hardcoded policy. A future Admin area will configure a Discovery cache policy (for example Minimal, Balanced/default, Extended) and/or bounded settings for browse TTL, search TTL, transient metadata retention and artwork-cache retention and size, because self-hosters differ in provider tolerance, storage and privacy. Invariants: canonical, local and requested media metadata stays durable as the Library requires; transient Discover candidates never become permanent Works by being cached; lowering Discover retention never deletes canonical Library metadata; cache settings never disable request de-duplication or rate limiting; no provider is hammered because retention is low.

## Mockup files

Expected references in this folder:

- `desktop.png`
- `mobile.png`
- `tablet.png`
- `tv.png`

Each platform mockup must cover both **Light and Dark** variants. A comparison sheet may show both themes side-by-side inside the same platform image.

## Purpose

Home is a personalized media surface, not an admin/dashboard page. It should feel closer to Netflix/Plex than to a monitoring dashboard.

The Clean design is the canonical UX/layout baseline. Original Jularr is the supported Japanese-inspired visual skin over the same layout/components and must not change navigation, information architecture or interaction behavior.

## Theme requirement

- Light and Dark are both first-class.
- Both themes must be designed and reviewed separately on every relevant platform.
- Dark mode is not generated by simple inversion.
- Layout, hierarchy and interactions stay consistent between themes.
- Visual style is Clean or Original Jularr; both use the same core UX contract.
- Accent changes use shared semantic tokens and may hue-shift permitted branded/decorative elements.
- Third-party/provider logos and semantic state colors are not arbitrarily recolored.

## Navigation

### Desktop / wide tablet
- Home
- Library
- Games
- Calendar
- Learning, once it is no longer Unfinished (#870)
- global Search in the top header, not in the sidebar
- Profile and Settings at the bottom; the account menu (Profile, Settings, sign out) and theme sit in the header
- Unfinished: one subordinate collapsible section closing the sidebar
- Admin is a separate explicit mode entry for authorized users

Do not expose Downloads, Imports, Wanted/Missing or other admin/operations destinations in normal user navigation.

### Mobile
Bottom navigation is fixed to:
- Home
- Library
- Calendar
- Learning, once it is no longer Unfinished (#870; until then it is listed under Profile -> Unfinished)
- Profile

Games does **not** occupy a permanent Mobile bottom-navigation slot.

When Games is available, Mobile reaches the dedicated Games destination contextually through:
- Games/Continue Playing content on Home;
- Games rows/filter in global Search/Discover;
- Game Detail/back-navigation/deep links.

If Games is available but the profile has no recent Game activity, Home should still expose a lightweight Games entry/row so the installed Games library is reachable without replacing a bottom-nav item.

Search remains globally accessible in the top/app bar.

### TV
Leanback/focus-first navigation with the same core destinations. TV controls and spacing are intentionally different from desktop/mobile.

## Home content hierarchy

Recommended order:

1. Hero / featured media
2. Continue Watching / Reading / Listening
3. Continue Playing, when the current profile has resumable/recent Games activity
4. Up Next
5. optional Watchlist / Reading List shelf when it adds value
6. For You
7. Because you watched/read/listened to/played …
8. New episodes / newly relevant library items
9. Upcoming releases
10. Trending
10. dynamic genre/media-type rows

Rows are horizontal and can lead to a filtered Discover surface.

The exact set of rows is personalized; not every row must appear for every user.

### Personal list shelf

When shown, Watchlist / Reading List uses normal media cards and opens the normal Library with the corresponding personal-state filter. It is not a separate Home-only list store and not a Collection.

## Hero / banner system

The Hero must work reliably even when a perfect landscape banner is unavailable.

### Artwork source priority

1. real provider backdrop/banner when available and suitable
2. poster/cover composited over an automatically derived blurred/gradient background
3. clean generated fallback background using artwork-derived colors plus poster/cover

Do not require AI-generated artwork for normal operation.

### Hero candidate priority

Superseded by the Hero pool of the "One Home/Discover surface" decision above: Continue/Resume, newly available local media, newly imported or acquired local media, personalized recommendation, New discovery, Trending discovery.

### Hero information

Keep it compact:
- title
- useful media metadata
- short description/subtitle
- progress when applicable;
- for Games, do not invent completion percentage: use last-played/save context instead
- primary action: Continue / Play / Read / Listen when available; Request when the surfaced item is unavailable and requestable
- small secondary action such as Details or a personal-state action

Do not use a generic `Add` acquisition action. Request/acquisition and Watchlist/Favorite/Collection state are separate concerns.

Do not show library counts, server stats, download state or technical media diagnostics in the normal Home Hero.

## Card behavior

Cards across Home share one visual grammar:
- artwork is dominant
- title
- progress where relevant
- only a small amount of useful secondary state
- no tag/badge clutter

The card component may adapt its secondary information by media type while remaining visually consistent.

## Responsive behavior

### Desktop
- persistent compact sidebar
- wide Hero
- multiple visible items per horizontal row
- mouse/keyboard affordances allowed

### Tablet
- touch-first
- Hero remains prominent
- adaptive row/card sizing
- sidebar/bottom navigation chosen by available width

### Mobile
- compact Hero
- horizontal swipe rows
- large touch targets
- avoid dense metadata
- no hover-only controls

### TV
- Hero can occupy more of the screen
- large focus states
- horizontal media rows
- safe-area spacing
- remote-first behavior

## States

Home must support:
- loading/skeleton
- empty new-user state
- ready/populated
- degraded provider state
- storage unavailable while cached metadata remains browsable
- offline/PWA state where applicable

## Explicit exclusions

Home must not become a dashboard with:
- media-count statistic tiles
- storage charts
- provider health
- jobs
- download/import queues
- server CPU/RAM
- admin warnings except a minimal user-relevant notice when absolutely necessary

Those belong to Admin.

## Implementation rule

The mockups are binding visual references for layout/hierarchy. Agents may adapt exact spacing to responsive constraints but must not redesign Home without updating this specification and receiving new approved mockups.

## Canonical identity on Home rows

Recommendation rows use the same typed canonical identity resolution as Discover/Search. Watch/read/listen media resolve to canonical Work; Games resolve to canonical Game through the isolated Games module. For Anime/Series, one canonical Work is shown once by default even when AniList or another provider returns separate season/part entries. Opening a provider-derived recommendation resolves to the owning domain's canonical target and optional presentation target. Home must not create provider-specific duplicate cards that disagree with Discover or Library/Games.



## Games on Home

Games is integrated into the existing Home experience. There is no separate Games Home page beyond the dedicated `Games` library destination.

### Continue Playing

Show a dedicated `Continue Playing` row only when the current profile has recent/resumable Game activity.

A Game card may show:
- artwork;
- title;
- platform;
- last played;
- concise save-state availability/context;
- `Continue` when a valid resumable launch path exists.

Do not show a fake percentage-complete value for Games.

`Continue` launches directly when the Games application layer resolves one valid LaunchPlan. If a genuine release/runtime ambiguity remains, use the approved Play Options flow.

Opening the card itself goes to Game Detail.

### Games in mixed Home rows

Games may appear in existing personalized/mixed rows such as:
- For You;
- Newly Added / newly relevant items;
- Trending;
- Because you played …;
- dynamic genre/theme rows.

Do not force Games into every mixed row. The Home personalization logic decides whether a Game is relevant.

### Games Hero

A Game may become the Home Hero under the normal Hero candidate rules.

Useful Game Hero information:
- title;
- platform;
- year;
- short description/tagline;
- last-played/save context when applicable;
- primary `Continue` or `Play`;
- secondary `Details`.

Do not show:
- runtime name;
- BIOS/Firmware state;
- ROM filename;
- release-region/revision details;
- controller setup;
- downloader/import diagnostics.

### Games cards

Home Game cards follow the same clean card grammar as other media.

Compact secondary information may use:
- platform;
- year;
- last played;
- local/request state when relevant.

Do not add emulator/runtime/BIOS badges to Home cards.

### Requests and acquisition

When Home surfaces a non-local recommended Game, any request action uses the shared Request/Acquisition flow.

No Games-specific Request, Wanted, Downloader or Activity system is created.

### TV

TV Home may include:
- Game Hero;
- Continue Playing;
- Games in normal recommendation rows.

Use the existing TV focus/row model.

Controller pairing/setup is not shown on Home. It belongs at launch/player boundary only when required.

### Mobile and Tablet

Use the existing responsive Home layout:
- Games cards participate in the same horizontal/swipe rows;
- Continue Playing uses touch-sized Game cards;
- no extra Games dashboard or platform-filter controls appear on Home.

Platform filtering remains in Games/Discover, not Home.
