# Discover / Search — Clean Design

Status: **approved clean UX direction**.

## Purpose

Discover and Search are one coherent surface.

- **Home** = personal continuation and recommendations from the user's existing context.
- **Discover** = actively finding new media, including titles not yet in the local library.
- Empty search = Discover.
- Typing = live search.
- Submit = full result view.

## Global search bar

At the top of Discover on every platform:

- very wide search bar
- Discover/Compass icon inside or directly attached to it
- searches Anime, Series, Movies, Books & Light Novels, Manga, Audiobooks and Games
- optional compact Filter icon on the right
- the control must clearly communicate both **search** and **discover**

## Media-type switch

Directly below the search bar:

- All
- Anime
- Series
- Movies
- Books & Light Novels
- Manga
- Audiobooks
- Games

This is the primary quick scope control.

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
- Result groups are independently collapsible/expandable: Anime, Series, Movies, Books & Light Novels, Manga and Audiobooks. “Show all” keeps the active type/filter context.
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
