# Frontend design and page behavior

Status: **active** shared design and UX decisions, subject to newer explicit Owner decisions for individual features. Technical structure and localization belong in [Frontend conventions](FRONTEND_CONVENTIONS.md); navigation is in the [Frontend CODEMAP](../src/Jularr.Frontend/CODEMAP.md).

## Design tokens (owner's mockups, jobs redesign 21 Oct)

One visual language for every page, owned by [Theme.scss](../src/Jularr.Frontend/Modules/Appearance/Theme.scss) (defaults, shape, type,
status, shadows) and [Palette.ts](../src/Jularr.Frontend/Modules/Appearance/Palette.ts) (colours from the accent, applied at runtime). The
default accent `#8040f7` reproduces the mockup palette **verbatim**; any other accent turns the same palette to its hue and chroma
(tinted neutrals, lavender, lines), so the Owner's accent setting still drives everything. Components use the tokens, never literals; a page
adds none of its own colours, radii, shadows or font sizes. Design follows the owner mockups and the global tokens; no accessibility/contrast rule set. Invisible aria attributes stay (tests use them).

| Group | Tokens (light / dark) |
| --- | --- |
| Surfaces | `--background` #faf9ff / #12121d, `--surface` #fff / #1d1b2b, `--field` (= `--surface-soft`, `--surface-hover`) #f8f7ff / #262339 |
| Line | `--border` (= `--line`) #eae6f4 / #38334e: 1px, the only outline; `--border-strong` #e1dcee / #4b4260 for inputs and switches |
| Text | `--text` #26223d / #f4f1ff, `--muted` #938fa9 / #aba5bf (secondary text, labels, captions) |
| Accent | `--accent` #8040f7 / #9a70ff, `--accent-text` #7136d9 / #c3a8ff, `--accent-soft` (lavender) #f2edff / #35294e, `--accent-outline` (hover edge) and `--focus` #b59aff / #8060bf, `--accent-edge` (active chip outline) #dcd0ff, `--accent-contrast` white, `--accent-hover` |
| Status | `--success` #159775 / #75dcb6, `--warning` #ad7618 / #ffcd82, `--danger` #c6536b / #ff9cb0, `--info` #487bd6 / #9dbdf7 and their `-soft` tints |
| Shape | `--radius-control` 999px (buttons, chips, search, pills), `--radius-panel` 19px, `--radius-dialog` 22px, `--radius-inner` 13px (detail tiles, textareas), `--radius-tile` 12px (icon tiles), `--radius-field` 11px, `--radius-small` 10px |
| Depth | `--shadow` 0 9px 30px (panels, tables, sections), `--shadow-drawer` -10px 0 40px #0002, `--shadow-overlay` 0 11px 30px #0004 (popovers, save pill, toast), `--scrim` #16122570 with a 4px blur; no other shadows, bevels or coloured stripes |
| Type | Inter, then Segoe UI/system; body `--text-body` 12px, controls `--text-control` 11px/740, captions, labels, pills and table heads `--text-small` 10px (heads 810, uppercase, .65px); h1 27px/850 -1.05px, h2 `--text-heading` 14px/805 -.3px, drawer title 22px |
| Controls | `--control-height` 37px (button, 11px), compact 32px (row button, 10px), `--icon-button` 36px circle, `--field-height` 40px, search 39px (both 44px on a phone, up to 640px); 1px borders, focus ring 3px `--focus` offset 2px, `--transition` .15s, press scales to .98, disabled .4 opacity |

- Owner's decision 21 Oct: admin lists are built from the list kit, never from their own grids or tables. A page composes one `ListToolbar` (a
  `NavigationTabs` row of filter chips, modifier `filters`: icon of the state, name and count; at most one filter field (`SearchBox` with `listFilter`; the header
  search is the global one), the list's own tools) and a `ListPanel` of `ListRow`s (name cell `ListIdentity` with an `IconTile`, `StateChip` with an icon per state,
  the row's actions, a caption for phones); details open in a `Drawer` (`sheet`). The panel drops its `HideLevel` columns and becomes cards by its own width; the
  page's stylesheet sets only the column grid. No page heading above it: the breadcrumb is the title. Figures above the list (`KpiTiles(...).Strip()`) only where
  they are live and not already a chip's count (the Queue's tiles); the state chips carry the other pages' counts.
- Owner's decision 21 Oct: every row of a list looks alike. Value columns (`ListRole.Data`) and the actions stand at the right edge of their column, head included;
  what a row does not always have (a pill such as "while in use" or "High", an error line, a monitoring switch) is shown only when it is there, never as a different
  shape or a placeholder; a state that replaces a value (a paused download says "Paused" where the speed stands) uses the value's own style, so nothing shifts.
  Rows have one minimum height that holds the longest usual content (title, line, note).
- Owner's decision 21 Oct: a list that has several views (the library: posters, overview, table) switches them with the one `ViewSwitch` in its `ListToolbar`
  (links with `view=` in the address; the page keeps the choice in a cookie of this browser, so the next visit starts in it). Posters are `MediaCard`s in
  `media-grid` (box top left, monitoring top right, state over the foot), the overview is a `ListPanel` whose name cell carries a large `ListPoster`, the type and year,
  the start of the synopsis and the facts, the table is the `ListPanel` with the column picker. Boxes of all views belong to the page's one bulk form.
- Owner's decision 21 Oct: one icon set. Icons come from [Assets/Icons](../src/Jularr.Frontend/Assets/Icons) and are stroke-only (24 box, round caps and joins; no filled
  glyphs, no fills that depend on the surface behind them); a missing icon is drawn in that style and added there, never taken from another set.
- Owner's decision 21 Oct: things that are few and carry a state (storage connections, providers, media servers) are a `CardGrid` of `FeatureCard`s: icon tile, name and one line, `StateChip` with
  an icon, rows (a `SpaceBar` for a volume's free space), the actions at the foot (first group at the start, second at the end). A card with an id is a Region: its switch or test swaps just that card.
  Settings of a card are inputs inside it (22 Oct decision above); a thing with many parts (a storage connection with its library paths) has its own page. One chip row joins the pages of an area (Storage: connections, download storage;
  Integrations: providers, media servers, catalogs); keys are entered in a masked field and never shown again, only whether one is stored.
- Owner's decision 21 Oct: admin list pages keep their page actions and bulk actions in the one floating
  [ActionDock](../src/Jularr.Frontend/Framework/Components/ActionDock.cs), never in a page head, toolbar or at the end of the list. It is the save pill's
  floating pill (centred, well above the bottom edge, above the floating bar on a phone, always in view) with two optional zones as slots: actions (icon and short
  label; a phone keeps the icons, so two actions never share one) and bulk (shown instead of the actions while rows marked `data-dock-select` are chosen, with their count); the pager of the page's list joins it by itself
  (see Paging). **No infinite scroll on admin pages**: lists are paged, 25 rows by default. With a visible save pill the dock stacks above it.
- **Paging: set `.Paged()` on the list.** `Table`, `ListPanel` (`.Paged(pageSize: 25, key: "pageNumber", primary: false)`) and any other component (`content.Paged(rows, pageSize, key)`, e.g. a grid of cards)
  take paging as an option; the page never builds a dock, a pager or an address for it. The rule is the same everywhere (see [Paging.cs](../src/Jularr.Frontend/Framework/Components/Paging.cs)):
  a page is up to `pageSize` rows read without a count and without looking ahead, the pager says "Page 2", **Next is on iff the page is full**, Previous from page 2 on, and a page behind the last one shows
  the list's empty state and leads back. The page number is read from the address (`?pageNumber=2`); the links keep every other query value. A page with several paged lists gives each its own key
  (`.Paged(key: "files")` pages `?files=2`, the others keep their page); the pager of the primary list (the first, or the one marked `primary: true`) sits in the floating dock, every other list draws the same small
  pager under itself. The service call takes the same page number and size and returns exactly its rows; never a total, never an extra row.
- Owner's decision 23 Oct: User lists use infinite scroll. The Library uses `content.Infinite(rows, 48, "p", ".library-grid")` from the same paging model and `InfiniteList.entry.ts`: append bounded server-rendered pages, keep a native Load more link, expose failures with a retry, and restore filters, loaded pages and scroll position on Back. History keeps only pagination state, never HTML. Admin lists retain their dock pager.
  Owner's decision 3 Nov, as on Discover: while pages load by themselves the Load more link stays unseen in its place (it is the next page without script);
  a grid of cards shows grey skeleton cards where the page arrives (the rest of its last row and one more), a failed page turns the link into *Try again*,
  and a grid says *No more titles.* where it ends. Nothing above the row moves while pages arrive.
  The Library defaults to actual present files (no metadata-only, Plex-only or Wanted-only titles). Optional availability choices union Available files, My requests and Other requests; active requests of all profiles are shared for now, under the existing profile/media visibility rules. Genre, audio/subtitle language, progress and minimum-rating filters combine with media type/year; choices within a multi-select combine with OR, different filters with AND. A separate request-sharing model is deferred.
- Owner's decision 2 Nov: the catalog of titles is called "Works" (German "Werke") everywhere in the interface: the admin menu group and breadcrumb, the user menu, the Dashboard figure, the jobs area and the Works pages. "Media type(s)" (Film, Series, Book, ...), "media server" and "media files" are other concepts and keep their wording. The type filters (Library, Requests, Discover) name the types in the plural ("Books"); a title's or request's own type reads in the singular ("Book"), as on the cards.
- Owner's decision 3 Nov (#1880): the media-type bar is also the type filter of the administration's Requests list (it narrows the list and its tab counts), the library overview, the Naming panels and the AniList matches, the Wanted list (its view counts follow it, and its Search all searches the chosen types' targets) (`?type=a,b`) and the Media import preview. There the bar lists the types the list holds and narrows without reading the list again: rows of a type left out are hidden and not chosen (they are not imported, and Select all and the count follow the rows shown); after Update preview they stay unchosen.
- Owner's decision 2 Nov: a list's search keeps its words. The search fields of the admin Works list and the Library show their results shortly after typing (`SearchBox(..., live: true)`,
  [LiveSearch](../src/Jularr.Frontend/Framework/Components/LiveSearch.entry.ts): the page with the query replaces the history entry, the field keeps its focus); on these pages and Discover
  every chip, type, order, view and reset of the same page takes the words typed last, even before they were sent, and a reset clears filters, never the search. The admin Works list
  sorts in every view with a choice beside the search and an arrow for the direction; it starts with the titles added last.
- Owner's decision 2 Nov (Sonarr's library): the admin Media list starts in the posters view on a first visit (the cookie keeps any other choice), shows the figures of the
  list under it (titles, monitored, unmonitored, files, total size, for the types, library, profile and words chosen) and starts the library search (filtered, all) from its dock; a
  search run shows as one quiet line under the toolbar only while it runs or right after it was started.
- Related admin list pages are joined by **one** row of section chips whose group is defined once (an entry of `AdminMenu`; the quality rules are panes of one address), above the list in a `ListToolbar` that may carry the list's one scope menu on its right; a page without filters of its own (the acquisition lists) has no other chips. Owner's decision 22 Oct: indexers, download clients, subtitle providers, release profiles and tags are wide `FeatureCard`s whose settings are inputs (`form.Save()`, saved by the page's pill; a key is masked and never echoed); the switch, test and search are direct actions, and on the connection pages the figures and cards are one `Region`. A `Modal` that the address opens (`?new=`, `?Type=`, `?delete=true`) with the list's address as `returnTo` is only for adding (with `form.Cancel()` and `form.Submit(...)`), for confirming a removal and for tools (the test search); the custom format keeps its dialog because its conditions need the room. There is no delay, waiting or minimum-age option on any acquisition page.

Everything that changes colour with the theme follows the switch with `transition: color var(--transition)` (or the colour it changes), the wordmark and the mark included; no element has a
colour of its own that skips it.

Changing a look means changing a token or the one base component (Button, Form, Table, Tag, StateChip, Section, NavigationTabs, Toggle, SearchBox,
SavePill, FolderPicker, IconInfo, TimePicker, FactList, Drawer, FilterDialog, Shell); all pages inherit it. Button variants: default, `Primary()`, `Soft()`, `Warn()`, `Compact()`, `IconOnly()`.
## Page identity and approved UX (MANDATORY)

- **The shared Topbar breadcrumb/current-page label is the sole page title.**
  Every shelled page sets `page.Title(resourceKey)`, which the layout renders
  in the Topbar; a page showing one item may pass its name as the browser tab title
  (`page.Title(resourceKey, name)`). **Never** repeat the same title as an `H1`, `UiHeading`,
  hero title, banner, card heading, or description at the top of the content.
  No automatic "page description" below the breadcrumb. Standalone unauthenticated
  pages without the shared Topbar (e.g. Login) may use a semantic `H1`.
- Owner's decision 20 Oct: the breadcrumb sits top-left in the header on every page as Area > Group > Page > Title, every
  part a link except the last. [Shell.cs](../src/Jularr.Frontend/Pages/Shared/Shell.cs) adds the area (Admin) and the sidebar
  group itself; a page names only what lies between with `page.Parent(key, href)` / `page.ParentName(name, href)` and its
  item as `page.Title(key, name)`. Narrow headers show only the last two parts; a longer trail scrolls, never wraps.
  Where the last part equals a heading under it, that heading is dropped (kept visually hidden for assistive technology).
- Owner's decision (issue #1881, Activity like Sonarr): the entry **Activity** has three views, **Queue** (`/Admin/Activity`, the default), **History** (`/Admin/History`)
  and **Blocked** (`/Admin/Activity/Blocked`), each under the normal media-type capsule (`type=a,b` stays in every view's address). The Queue lists what is open in four groups,
  what needs the owner first, each with its count: *Needs your help* (a check at the source such as Anna's Archive, a failure), *To import* (also what is imported by hand),
  *Downloading* (with the live pace) and *Waiting in the downloader*; a group without entries is left out. Blocked lists the releases that are not taken again (blocked
  by hand or by a fault) with **Allow again**. **Wanted** (missing titles) is a menu entry of its own and no part of Activity. The Queue's old filters (All, Active, Waiting,
  Import, Errors), its four live tiles and its list of finished operations are gone (assumptions of the implementing agent, open to correction by the Owner).
- Owner's decision 20 Oct, sidebar modes: on `/Admin/*` the sidebar shows only the administration menu, all groups open; the user
  entries are not shown, and the Jularr logo (linking to the user home) is the way back. No extra back button.
  The user menu has one entry, Administration, that opens the first allowed entry. On a narrow header a breadcrumb part with sub-pages opens them as a menu, the first part stays whole and a long last part (the title) ends in an ellipsis with its full text in a tip.
  The header is one row at every width: the search shrinks to its icon on a phone, the trail scrolls.
- Owner's decision 2 Nov, tap targets: on a phone (up to 640px) every tap target has a hit area of at least 44x44px while its look may stay smaller, made by the shared mixins of [HitArea.scss](../src/Jularr.Frontend/Framework/Components/HitArea.scss) (overlay or padding, never overlapping a neighbour: where 44px does not fit the largest free size is taken, and a tight group sets `--hit-gap-x`/`--hit-gap-y`).
- Owner's decision 3 Nov, the manual search: a link to `/Admin/Wanted/{id}/Search` that carries `data-search-dialog` opens the search as a dialog over the current page (title and the scope fields with "Search again" in the head, Close); the link stays a real address, and that page shows the same panel. The bar and its line count the sources that answered ("Searching 3 sources … 2 of 3 done", then "N releases from M sources"); each source is a chip (searching, check with its number, or a warning with a short reason) and a click on a chip only hides the releases of the other sources, it never asks again. Under the chips one row of dropdowns narrows what is loaded by language (the languages the releases carry), protocol, age and size (a release without a date or size does not fit a limit on it), and orders it (by source, best score, newest, largest, smallest; any order but by source puts all releases into one list across the sources); "Clear filters" resets them. The controls keep their width and place, the status line says "k of N releases" and a notice says when none fits; sources that do not serve the media type are not shown. Every source is its own request; a running source holds two skeleton lines where its releases will appear, and a failed or limited source is never shown as "no releases". A release has two buttons, In queue (the end of its client's queue) and Start now (before the others); a rejected release is grey with its reason and only a small menu with a warning and "Take anyway". On a phone each release is a card: the release on top, then source, quality, size, score and decision, then the buttons.
- Local section headings are allowed only when they genuinely separate distinct
  content, not when they echo the current breadcrumb. Contextual explanations
  are shown only for meaningful tasks or failure/help states, never as filler.
- Before preparing or changing a page, inspect the relevant current
  Owner-approved feature specification or issue, current `main` code, and the
  canonical component owner. Historical [`dev` mockups](https://github.com/Juloc/Jularr/tree/dev/docs/mockups)
  are recovery references only, not automatically approved current designs.
  Reconcile missing historical behavior with tracked issues and explicit Owner
  decisions; never copy `Jularr.Web` storage or service classes into the new Backend.
- Never render a headings-only `UiTable`, disabled pseudo-toolbar, fake cards
  or an unbound static filter list as a "prepared page". A real table is
  `Table<TData>` with typed fields and actual authorized Backend results;
  server paging, sorting, filters and allowed actions follow the real contract.
  If the contract is unavailable, do not render a false table. Record the
  missing integration instead.
- **Discover/Home at `/` is capability-driven.** Media-type tabs are the
  intersection of enabled instance modules, active-profile visibility/policy
  and available discovery capabilities. A disabled/hidden type must disappear
  from the media-type bar, its browse views, Hero candidates and shelves.
  `All` contains only eligible types and cannot silently include disabled
  provider results. Books, Light Novels and Manga remain distinct. Owner's decision 2 Nov: every type stands strictly apart, in the type
  filter, the Continue shelves, Recently added and the highlights: series without anime, books without manga and light novels, and a Work
  belongs to the type of its subtype (an anime film is anime).
- Discover separates media-type scope, browse view and filters. Its All
  landing has a shared Hero/Continue followed by per-enabled-type shelves
  in the approved order; only provider-supported rankings may appear. A ranking
  opens from its shelf's See all and names itself with a way back. Owner's decision 2 Nov: every ranking loads on while scrolling, its See all
  grid and its shelf alike, 20 titles (the provider's next page) at a time; a page goes on while it has titles, three pages in a row
  that add nothing stop the automatic loading (the grid then shows *Load more*), and both say *No more titles.* where the ranking ends. While a page
  loads, grey skeleton cards stand where it arrives (a row under the grid, three at the end of a shelf); no Next button shows while pages load by themselves,
  and a failed page offers *Try again*.
  Owner's decision 2 Nov: anime, manga and light novels have AniList's lists (anime: Trending, Popular this season, Upcoming next season,
  All time popular, Top 100; manga and light novels: Trending, All time popular, Popular manhwa (manga only, Korea), Top 100); films,
  series and books keep theirs. Only a ranking's grid has a sort: AniList's orders (trending, popularity, average score, favorites, date
  added, release date, title) for those types, for other types only what Jularr's whole catalog holds (popularity, score, release date,
  title), none otherwise; a ranking opens in its own order (trending, score for Top 100, else popularity) and shelves keep it. Owner's mockup of 19 Oct
  ("capsules"): one capsule row (All, then Watch, Read, Hear and Game with their types; several
  types may be chosen, `type=a,b`) with the Customize and Filter drawers at its end, the highlights
  as one carousel that always keeps its place (with nothing to show, the shared landscape and *No highlights yet*): for each chosen type its
  continued titles, then its best by rank (series from Discover's series list), then what the library added last, each title once; the types
  take turns place by place, and a type with fewer titles shows fewer. The device keeps how many per type (1–5, standard 3; no overall
  maximum), whether continued titles count and whether it turns by itself), feed chips and a rows/grid switch. The header owns the **one**
  global Search; do not add another search field inside Discover. Owner's decision 2 Nov, the header search: large in the middle of the header (where the header is tight it narrows before the trail, which stays whole), no key hint
  or shortcut; from two letters, after a short pause, a popup under the field lists the titles Jularr keeps (no provider is asked) grouped by type, the type
  with the best match first, in each type the typed title, then titles beginning with it, then other matches, local before trending before popular, two to
  six per type as the room allows, then All results; each group's heading (its type's icon, name and a See all pill) opens that type's results, titles show a 40×60 cover with the typed words marked and year · first genre · rating under them (what is unknown is left out), and two or more groups stand in two columns where there is room. Enter opens Discover's results: one row per type in the same order, each with See all for that type
  alone (which pages); the providers are asked once, together, only on Enter, and the kept titles join their rows, so a provider that does not answer never hides what the suggestions showed. On a phone the icon opens the field over the header, the suggestions fill
  the screen and the back button closes them. Owner's decision 2 Nov: under the media-type bar a filter row as AniList's
  search has it: Genres, Year, Season (anime) and Format at hand, More opens the Filter drawer with the others the chosen type's catalog can
  answer (status, country with Manhwa/Manhua, source, AniList's tags by category, streaming or reading sites, ranges of years, episodes,
  chapters, volumes and minutes, language for books, adult titles as a switch for every profile; `DiscoverFilterOptionsV1` names them per
  type). Several types share only genre and year. A popup applies its choices when it closes, a select at once; on a phone the row swipes
  sideways and a popup is a sheet. Any filter (not the type alone) replaces the feed chips and shelves with one grid of the chosen types'
  titles under the highlights, popularity first, with the orders the type offers, loading on while scrolling; with Jularr holding the whole
  catalog (`LocalDiscovery`: AniList, TMDB's films) the grid is read locally, otherwise from the provider (AniList, TMDB's discover lists,
  Open Library). The Filter drawer lists the media types first, then the local availability, the filters above, then only the rankings the
  chosen types have; the rankings a chosen type lacks leave the panel as the types change. Library and Favorites share the same media-type bar,
  filter panel (types, release year), order and layout. Owner's decision 2 Nov: the admin Media list has the same media-type bar above its toolbar (every type, several at once); its status chips stay in the toolbar and its filter drawer has no type. Owner's decision 3 Nov: the same media-type bar is on every page where a media type is chosen to narrow a list: also My requests (below the status tabs) and the Calendar (replacing its own chip bar); the Anime tab appears when Anime is active.
  Cards use the shared cover presentation and Backend-supplied state below. User/profile ordering is respected.
  Backend supplies effective capability/order/provider data; do not copy
  old `Jularr.Web` persistence or invent a new module toggle in Frontend.
- Apply the same module/profile visibility gate to Favorites, Library and
  every other media-type navigation, plus playback routes/actions when the
  instance Playback module is disabled.
