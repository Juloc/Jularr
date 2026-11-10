# Frontend design and page behavior

Shared design and page behaviour for every page (owner decisions; change them only with the owner).
Decisions for one feature live in its area file ([CODEMAP](CODEMAP.md)); technical rules in [Frontend conventions](FRONTEND_CONVENTIONS.md).

## Design tokens

One visual language for every page, owned by [Theme.scss](../src/Jularr.Frontend/Modules/Appearance/Theme.scss) (defaults, shape, type, status,
shadows) and [Palette.ts](../src/Jularr.Frontend/Modules/Appearance/Palette.ts) (colours from the accent, applied at runtime).
The default accent `#8040f7` reproduces the mockup palette **verbatim**; any other accent turns the same palette to its hue and chroma (tinted
neutrals, lavender, lines), so the Owner's accent setting still drives everything.
Components use the tokens, never literals; a page adds none of its own colours, radii, shadows or font sizes.
Design follows the owner mockups and the global tokens; no accessibility/contrast rule set.
Invisible aria attributes stay (tests use them).

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

- No grey surfaces.
  Panels and tables are white (dark surface in dark mode), fields the soft lavender `--field`, outlined by the one quiet 1px `--border`; section
  titles are bold in the text colour, table heads are muted uppercase on `--field`; accent tints (`--accent-soft`, `--accent-outline`,
  `--accent-edge`) mark hover and selection only.
  The shell (sidebar, header, search, floating bar) separates with the same 1px line instead of shadows or filled panels; the current place uses
  `--accent-soft` with `--accent-text`.
- Controls and navigation are flat and opaque: no glass blur, transparency settings, bevels or 3D button gradients.
  Keep keyboard focus visible but restrained; outlined fields indicate focus with their thin border.
- A page has at most one row of tabs.
  Spread content over pages rather than stacking tab rows or crowding one page.
- Everything that changes colour with the theme follows the switch with `transition: color var(--transition)` (or the colour it changes), the
  wordmark and the mark included; no element has a colour of its own that skips it.
- Changing a look means changing a token or the one base component (Button, Form, Table, Tag, StateChip, Section, NavigationTabs, Toggle,
  SearchBox, SavePill, FolderPicker, IconInfo, TimePicker, FactList, Drawer, FilterDialog, Shell); all pages inherit it.
  Button variants: default, `Primary()`, `Soft()`, `Warn()`, `Compact()`, `IconOnly()`.

## Lists

- Admin lists are built from the list kit, never from their own grids or tables.
  A page composes one `ListToolbar` (a `NavigationTabs` row of filter chips, modifier `filters`: icon of the state, name and count; at most one filter
  field (`SearchBox` with `listFilter`; the header search is the global one), the list's own tools) and a `ListPanel` of `ListRow`s (name cell
  `ListIdentity` with an `IconTile`, `StateChip` with an icon per state, the row's actions, a caption for phones); details open in a `Drawer`
  (`sheet`).
  The panel drops its `HideLevel` columns and becomes cards by its own width; the page's stylesheet sets only the column grid.
  No page heading above it: the breadcrumb is the title.
  Figures above the list (`KpiTiles(...).Strip()`) only where they are live and not already a chip's count (the Queue's tiles); the state chips carry
  the other pages' counts.
- Every row of a list looks alike.
  Value columns (`ListRole.Data`) and the actions stand at the right edge of their column, head included; what a row does not always have (a pill such
  as "while in use" or "High", an error line, a monitoring switch) is shown only when it is there, never as a different shape or a placeholder; a
  state that replaces a value (a paused download says "Paused" where the speed stands) uses the value's own style, so nothing shifts.
  Rows have one minimum height that holds the longest usual content (title, line, note).
- A list that has several views (the library: posters, overview, table) switches them with the one `ViewSwitch` in its `ListToolbar` (links with
  `view=` in the address; the page keeps the choice in a cookie of this browser, so the next visit starts in it).
  Posters are `MediaCard`s in `media-grid` (box top left, monitoring top right, state over the foot), the overview is a `ListPanel` whose name cell
  carries a large `ListPoster`, the type and year, the start of the synopsis and the facts, the table is the `ListPanel` with the column picker.
  Boxes of all views belong to the page's one bulk form.
- One icon set.
  Icons come from [Assets/Icons](../src/Jularr.Frontend/Assets/Icons) and are stroke-only (24 box, round caps and joins; no filled glyphs, no fills
  that depend on the surface behind them); a missing icon is drawn in that style and added there, never taken from another set.
- Things that are few and carry a state (storage connections, providers, media servers) are a `CardGrid` of `FeatureCard`s: icon tile, name and one
  line, `StateChip` with an icon, rows (a `SpaceBar` for a volume's free space), the actions at the foot (first group at the start, second at the
  end).
  A card with an id is a Region: its switch or test swaps just that card.
  Settings of a card are inputs inside it ([Frontend conventions](FRONTEND_CONVENTIONS.md)); a thing with many parts (a storage connection with
  its library paths) has its own page.
  One chip row joins the pages of an area (Storage: connections, download storage; Integrations: providers, media servers, catalogs); keys are entered
  in a masked field and never shown again, only whether one is stored.
- Admin list pages keep their page actions and bulk actions in the one floating
  [ActionDock](../src/Jularr.Frontend/Framework/Components/ActionDock.cs), never in a page head, toolbar or at the end of the list.
  It is the save pill's floating pill (centred, well above the bottom edge, above the floating bar on a phone, always in view) with two optional zones
  as slots: actions (icon and short label; a phone keeps the icons, so two actions never share one) and bulk (shown instead of the actions while rows
  marked `data-dock-select` are chosen, with their count); the pager of the page's list joins it by itself (see Paging).
  **No infinite scroll on admin pages**: lists are paged, 25 rows by default.
  With a visible save pill the dock stacks above it.
  The pill keeps floating, and every list below it ends with room for it: scrolled to the end, no row stays under the pill, which itself floats above
  the bar of the book being read and above the minimized player (one rule, `--floating-bottom` in
  [FloatingPill](../src/Jularr.Frontend/Framework/Components/FloatingPill.scss)).
- **Paging: set `.Paged()` on the list.** `Table`, `ListPanel` (`.Paged(pageSize: 25, key: "pageNumber", primary: false)`) and any other component
  (`content.Paged(rows, pageSize, key)`, e.g. a grid of cards) take paging as an option; the page never builds a dock, a pager or an address for it.
  The rule is the same everywhere (see [Paging.cs](../src/Jularr.Frontend/Framework/Components/Paging.cs)): a page is up to `pageSize` rows read
  without a count and without looking ahead, the pager says "Page 2", **Next is on iff the page is full**, Previous from page 2 on, and a page behind
  the last one shows the list's empty state and leads back.
  The page number is read from the address (`?pageNumber=2`); the links keep every other query value.
  A page with several paged lists gives each its own key (`.Paged(key: "files")` pages `?files=2`, the others keep their page); the pager of the
  primary list (the first, or the one marked `primary: true`) sits in the floating dock, every other list draws the same small pager under itself.
  The service call takes the same page number and size and returns exactly its rows; never a total, never an extra row.
- User lists use infinite scroll.
  The Library uses `content.Infinite(rows, 48, "p", ".library-grid")` from the same paging model and `InfiniteList.entry.ts`: append bounded
  server-rendered pages, keep a native Load more link, expose failures with a retry, and restore filters, loaded pages and scroll position on Back.
  History keeps only pagination state, never HTML.
  Admin lists retain their dock pager.
  As on Discover: while pages load by themselves the Load more link stays unseen in its place (it is the next page without script); a grid of cards
  shows grey skeleton cards where the page arrives (the rest of its last row and one more), a failed page turns the link into *Try again*, and a grid
  says *No more titles.* where it ends.
  Nothing above the row moves while pages arrive.
- The catalog of titles is called "Works" (German "Werke") everywhere in the interface: the admin menu group and breadcrumb, the user menu, the
  Dashboard figure, the jobs area and the Works pages.
  "Media type(s)" (Film, Series, Book, ...), "media server" and "media files" are other concepts and keep their wording.
  The type filters (Library, Requests, Discover) name the types in the plural ("Books"); a title's or request's own type reads in the singular
  ("Book"), as on the cards.
- A list's search keeps its words.
  The search fields of the admin Works list and the Library show their results shortly after typing (`SearchBox(..., live: true)`,
  [LiveSearch](../src/Jularr.Frontend/Framework/Components/LiveSearch.entry.ts): the page with the query replaces the history entry, the field keeps
  its focus); on these pages and Discover every chip, type, order, view and reset of the same page takes the words typed last, even before they were
  sent, and a reset clears filters, never the search.
  The admin Works list sorts in every view with a choice beside the search and an arrow for the direction; it starts with the titles added last.
- Related admin list pages are joined by **one** row of section chips whose group is defined once (an entry of `AdminMenu`; the quality rules are tabs
  of one address), above the list in a `ListToolbar` that may carry the list's one scope menu on its right; a page without filters of its own (the
  acquisition lists) has no other chips.

## Page identity

- **The shared Topbar breadcrumb/current-page label is the sole page title.** Every shelled page sets `page.Title(resourceKey)`, which the layout
  renders in the Topbar; a page showing one item may pass its name as the browser tab title (`page.Title(resourceKey, name)`).
  **Never** repeat the same title as an `H1`, `UiHeading`, hero title, banner, card heading, or description at the top of the content.
  No automatic "page description" below the breadcrumb.
  Standalone unauthenticated pages without the shared Topbar (e.g. Login) may use a semantic `H1`.
- The breadcrumb sits top-left in the header on every page as Area > Group > Page > Title, every part a link except the last.
  [Shell.cs](../src/Jularr.Frontend/Pages/Shared/Shell.cs) adds the area (Admin) and the sidebar group itself; a page names only what lies between
  with `page.Parent(key, href)` / `page.ParentName(name, href)` and its item as `page.Title(key, name)`.
  Narrow headers show only the last two parts; a longer trail scrolls, never wraps.
  Where the last part equals a heading under it, that heading is dropped (kept visually hidden for assistive technology).
- Tap targets: on a phone (up to 640px) every tap target has a hit area of at least 44x44px while its look may stay smaller, made by the shared mixins
  of [HitArea.scss](../src/Jularr.Frontend/Framework/Components/HitArea.scss) (overlay or padding, never overlapping a neighbour: where 44px does not
  fit the largest free size is taken, and a tight group sets `--hit-gap-x`/`--hit-gap-y`).
- Local section headings are allowed only when they genuinely separate distinct content, not when they echo the current breadcrumb.
  Contextual explanations are shown only for meaningful tasks or failure/help states, never as filler.
- Apply the same module/profile visibility gate to Favorites, Library and every other media-type navigation, plus playback routes/actions when the
  instance Playback module is disabled.
