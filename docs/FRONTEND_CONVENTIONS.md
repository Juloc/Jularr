# Frontend conventions

Scope: `src/Jularr.Frontend/`. Shared C# and backend rules:
[CONVENTIONS](CONVENTIONS.md). Code ownership:
[Frontend CODEMAP](../src/Jularr.Frontend/CODEMAP.md).
**These are mandatory rules for every Frontend implementation, review and agent.**

## Architecture

- One ASP.NET Core host: Razor frontend plus shared Backend APIs; Backend is
  the same-process class library. Razor invokes typed services through DI,
  never by HTTP requests to its own server.
- Server-side rendering (SSR), **not** a JavaScript SPA. The response is complete,
  encoded HTML. Small TypeScript enhancements may handle playback, reading,
  live search and server-rendered fragments; no client hydration, client-side
  business templates or translation-bundle fetching. Preserve native navigation
  and forms wherever the feature does not inherently require JavaScript.
- Owner's decision 19 Oct: in-place navigation uses **htmx 4** (boost), not a SPA.
  Links and forms of app pages replace `#app-content` with the same element of the
  server's full page; the head, theme and an active Player stay. Player, Reader, Game,
  Account, Setup, API addresses, downloads, new windows and `data-navigation="full"`
  (also links a page script handles itself) load normally ([rules](../src/Jularr.Frontend/Modules/Navigation/Navigation.ts)).
  Back reloads the page from the server; no page HTML is kept in browser storage.
  Every route stays usable without JavaScript; authorization, form validation and
  rendering stay server-owned. A POST that shows its page again adds no history entry.
- App-page styles and scripts render inside `#app-content`. A page script registers
  its DOM setup with `onPage(import.meta.url, setup)`: it runs once per document load
  and again for each new page the script belongs to; document-level listeners are
  installed once at module level. HTML a script inserts goes through `inserted(element)`.
- Frontend owns UI appearance, accessible interaction, presentation-only state.
  Backend owns permissions, identities, validation, business decisions,
  transaction boundaries, persistence, paging data and API contracts.
- Browser interactions use the canonical Backend API or an authorized Razor HTML
  fragment. This does not permit server-side Razor to call its own host over HTTP.
- No second database, duplicate permission logic, fake row/provider data,
  invented service procedures, dummy playback state or inactive buttons
  presented as functional. If a backend contract is missing, prepare the
  UI structure honestly and list the integration gap.

## C# HTML DSL and component ownership (DECIDED)

- **Author HTML hierarchies declaratively in C#**, for example:
  `new Div(new H1(new Text("profile.title")), new Button(new Text("common.save")))`.
  Razor remains the ASP.NET entry and shell, but the shared components render
  server-side HTML without requiring page-specific HTML templates.
- Use small encoded HTML primitives (`Div`, `H1`, `P`, `Span`, `Button`,
  etc.) backed by one safe HTML-writing foundation; **do not** reinvent a
  complete browser DOM, virtual DOM or JavaScript framework.
- **Every substantial UI component has its own C# file** under
  `Framework/Components/<Component>.cs`. It owns the public API, supported
  behavior, layout markup and rendering. Small private helper types used
  **only by that component** may remain in the same C# file. Small,
  closely related HTML primitives may share `Framework/Elements.cs`.
- No central growing component-type `switch` or duplicate Razor partial
  per component. Render output directly from the component through the
  shared HTML writer, encoding dynamic values and attribute values.
- Never concatenate unchecked raw HTML. Validate HTML attribute names and
  URL schemes, use semantic tags, accessible names, keyboard/focus states,
  reduced motion, responsive layout and antiforgery for real POST forms.
- A page owns its independent `.cshtml.cs` definition and thin `.cshtml`
  entry. Reuse `Table<T>`, `Form<T>`, `Split`, tabs, menus, media cards
  and other domain-neutral components rather than copying UI code.
- **Pages compose components; components never contain the complete screen.**
  Page-specific information hierarchy, media-type ordering, browse-mode choice,
  conditional Hero/Continue placement and screen-specific state belong in the
  owning PageModel. Reusable, small pieces such as `NavigationTabs`,
  `MediaShelf`, `MediaCard`, `Table<T>` and `Form<T>` own only their
  presentation and interaction contract. Do not create feature-sized
  `Framework/Components/Discover.cs`, `Requests.cs`, etc. to hide a page.
  Small page-only helper records can remain beside the PageModel.
- Real form fields derive required/type/length/range metadata from Backend
  `BaseData`/`DataField`; missing select options or save procedures
  are not guessed. Tables need real typed data and authorized reads for
  sorting/paging/filtering. Unbound structures never imply successful loads.
- **Generic layout versus optional behavior:** `Split` is the one
  canonical two-area layout. Master/Detail is an **optional mode of Split**,
  not a second Framework component. The owning page supplies the selected
  item and a real back route. Mobile detail/list switching must remain
  keyboard-accessible, server-authoritative and must not invent selections.
- **Tabs are navigation, not stacked H2 sections.** Route-backed
  `Tabs` renders a single selected pane; `NavigationTabs` handles
  navigation-only scopes. Neither duplicates the global Breadcrumb heading.
- Put Jularr-specific shell presentation in `Pages/Shared/Shell.cs`,
  including helpers only used there. `_AppShell.cshtml` remains a thin
  one-call Razor bridge. Cross-page Brand may have its own shared C# file;
  generic components stay under `Framework/Components`.
- Pages describe forms via `Form<TData>` field selectors and optional
  labels/autocomplete; **the Form owns HTML labels, controls, ids, disabled
  states and backend DataField metadata**. Do not copy input/label helper
  classes into each page. Do not activate submission, provider buttons or
  session persistence without a real, authorized backend operation and
  antiforgery handling. A password field may carry the eye
  ([PasswordReveal](../src/Jularr.Frontend/Framework/Components/PasswordReveal.cs), client-only) and a link on its label's row through
  `FieldHelp.Trailing` and `FieldHelp.Aside`.
- Field examples and explanations belong to the Backend field: `hint.<Fields>.<Field>.placeholder`
  is the placeholder of typed fields (not names; a password shows only a hint its page names; an optional field shows what
  happens when it stays empty, such as its default), `hint.<Fields>.<Field>.info` the text behind
  the label's (i), shown on hover, focus and tap ([FieldInfo](../src/Jularr.Frontend/Framework/Components/FieldInfo.cs)).
  A page passes `FieldHelp` only where the field means something special there; an empty key shows
  none. Labels stay short: units belong in the label, explanations in the (i).
- Actions (a switch, a search, a delete, a dialog's save) are forms that post through htmx and do not reload the page: the server answers with the same page and
  the client swaps only the [Region](../src/Jularr.Frontend/Framework/Components/Region.cs) (a container with an id) around the form, else the page content, keeping scroll position,
  opened `<details>` and sections, and focus ([ActionSwap](../src/Jularr.Frontend/Modules/Navigation/ActionSwap.ts)); an answer from another path (a redirect to the list, a new item) navigates like a link.
  A page script that must run again over swapped elements opts in with `onPage(..., { partial: true })` and guards every element. Errors stay in place in the swapped region. Without script the form posts and redirects.
  The result of an action is never content of the layout (the hero, a status line) but the quiet hint at the bottom centre
  ([ActionToast](../src/Jularr.Frontend/Framework/Components/ActionToast.cs), about two seconds): a handler calls `Hint(key, values)`, every other successful POST that redirects says "Saved", `NoHint()` says nothing.
  A script's request gets the hint in the `HX-Trigger` header of the answering page, a page without script shows it itself.
- A dialog is the shared [Modal](../src/Jularr.Frontend/Framework/Components/Modal.cs): title row with the X at the right, the body, and the footer, which is the actions of its form
  (`form.Cancel()` and the primary `form.Submit(...)`, never `form.Save()`); a dialog's form saves on its own and the page's save pill does not take part.
- Owner's decision 19 Oct: settings forms end with `form.Save()` instead of their own save button. The page's one
  [save pill](../src/Jularr.Frontend/Framework/Components/SavePill.cs) appears centred in the content while a value
  differs from the loaded page, marks the changed fields and saves every changed form in turn; a failing form keeps its
  error while the others save. Discard shows the saved page again in place without asking; leaving with changes asks.
  Actions (test, search, delete) stay their own buttons. Without script each form keeps its visible save button.
  Pages with many small forms of one kind label each form with its item instead of repeating a field label.
  A form shown again as a draft (a row added, a choice applied) counts as unsaved in the pill until saved or discarded.
- Times use `Form.Time` ([TimePicker](../src/Jularr.Frontend/Framework/Components/TimePicker.ts), from the UiDesigner study): hour and
  minute fields on a desktop, a wheel sheet on Android, the system's own picker on iOS (owner's decision 19 Oct); the native
  time field stays the submitted value and the picker without script.
- `Form.File` adds a file field from a Backend `FileField` (accepted extensions) and makes the form a multipart upload; the page handler wraps `IFormFile` into `UploadedFile` and declares its request size.
- `Form.Select` may declare a local draft refresh action and a fallback button label. `Form.entry.ts` replaces only the server-rendered form on a field change; the action preserves the draft and performs no mutation. The native button remains usable without JavaScript. Pages opt into the component asset.
- Form transport values are invariant, independently of the display language.
  Explicit device-only settings may opt into `Form<T>(..., local: true)`: the same
  typed fields render as an editable group, not a form. Local mode rejects server
  actions, antiforgery tokens, refresh and Submit; it never implies profile saving.
  `Form<TData>` emits ASP.NET's `__Invariant` field markers; use normal framework
  binding, not per-page parsing or a parallel binder. Field validation remains
  Backend-owned. Hidden-field errors must be visible at form level; failed reads
  receive the shared Page error presentation, including empty-result pages; unknown errors render in the page too
  (`ErrorText`), not as a bare JSON answer.
- Buttons and search use the pill radius, fields the 11px field radius. Form labels sit
  above the field (11px/770, with the (i) after them), remain associated through `for`/id (a group of choices through its legend) and wrap without
  truncation. Reuse `Tag` and native-checkbox `Toggle`; bind editable switches
  through `Form.Toggle` so Backend field metadata and POST values stay central.
- `Form<T>` owns the layout: short fields share two columns where there is room and one
  in narrow panels; multiline, file, list and long text fields, errors and the buttons take
  the whole row, buttons at its end. A form with at most one short field sits in one row
  beside its buttons. A select of up to three short fixed choices (Boolean or enum) renders
  as one segmented control under the same name; data lists stay a select. `Form.Section`
  groups fields in a titled panel under one save; `Form.ChoiceTable` shows options as rows
  and several fields as checkbox or radio columns (a quality ladder). Pages do not
  restyle form grids.
- Owner's decision 15 Oct (look overridden 20/21 Oct by the mockups): no grey surfaces. Panels and tables are white
  (dark surface in dark mode), fields the soft lavender `--field`, outlined by the one quiet 1px `--border`; section titles are bold in the
  text colour, table heads are muted uppercase on `--field`; accent tints (`--accent-soft`, `--accent-outline`, `--accent-edge`) mark hover and selection only.
  The shell (sidebar, header, search, floating bar) separates with the same 1px line instead of shadows or filled panels; the current place
  uses `--accent-soft` with `--accent-text`.
- Controls and navigation are flat and opaque: no glass blur, transparency
  settings, bevels or 3D button gradients. Keep keyboard focus visible but
  restrained; outlined fields indicate focus with their thin border.
- Owner's decision 15 Oct: a page has at most one row of tabs. Administration areas
  (Media, Library, Requests, Arr+) are collapsible groups in the sidebar whose pages are their own
  addresses; the area of the current page is open. Spread content over pages rather than
  stacking tab rows or crowding one page.
- Tabs and subsection navigation reuse `NavigationTabs`: the mockups' filter row of
  plain pills, the selection the lavender tint with its `--accent-edge` outline. `Tabs` adds only the server-selected content pane.
  Keep the row single-line and horizontally scrollable when necessary, with complete labels. The shell follows the mockups: a 252px sidebar (230px, then
  67px with icons only up to 1120px, the selected area tinted), on a phone (up to 640px) an off-canvas drawer over a dimmed page opened from the header's menu
  button. The admin sidebar has an ADMINISTRATION eyebrow; the header has the trail, the one search with its `/` key hint and shortcut, a light/dark switch,
  the inbox and the avatar (profile initials, linking to the settings). The bottom pill is gone.
- Owner's decision 21 Oct: the instance settings are five pages below `/Admin/Instance` (Instance, Modules, Media types, Languages, Naming) with one chip row ([SettingsPages](../src/Jularr.Frontend/Framework/SettingsPages.cs)), sections inside a page instead of more tabs, one save pill for all of it. A switch that is a setting is a form field (`Form.Switch`, shown as a [SettingCard](../src/Jularr.Frontend/Framework/Components/SettingCard.cs)), so toggling reloads nothing and saves with the pill; settings of a module appear only while it is on; a module that needs another shows it and cannot be switched on while that is off, and one that others need names them and stays on.
- Settings is a page, not a special `SettingsGroup` renderer.
  Reuse semantic `UiSection` and generic `ItemList` while actual
  settings operations are not connected.


## Design tokens (owner's mockups, jobs redesign 21 Oct)

One visual language for every page, owned by [Theme.scss](../src/Jularr.Frontend/Modules/Appearance/Theme.scss) (defaults, shape, type,
status, shadows) and [Palette.ts](../src/Jularr.Frontend/Modules/Appearance/Palette.ts) (colours from the accent, applied at runtime). The
default accent `#8040f7` reproduces the mockup palette **verbatim**; any other accent turns the same palette to its hue and chroma
(tinted neutrals, lavender, lines), so the Owner's accent setting still drives everything. Components use the tokens, never literals; a page
adds none of its own colours, radii, shadows or font sizes. The mockup values are the rule: contrast ratios are not a design constraint.

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
| Controls | `--control-height` 37px (button, 11px), compact 32px (row button, 10px), `--icon-button` 36px circle, `--field-height` 40px, search 39px; all 44px for a coarse pointer; 1px borders, focus ring 3px `--focus` offset 2px, `--transition` .15s, press scales to .98, disabled .4 opacity |

- Owner's decision 21 Oct: admin lists are built from the list kit, never from their own grids or tables. A page composes `KpiTiles(...).Strip()`, one
  `NavigationTabs` row of filter chips with counts (modifier `filters`), at most one filter field (`SearchBox` with `listFilter`; the header search is the
  global one) and a `ListPanel` of `ListRow`s (name cell `ListIdentity` with an `IconTile`, `StateChip` with an icon per state, the row's actions, a caption for phones);
  details open in a `Drawer` (`sheet`). The panel drops its `HideLevel` columns and becomes cards by its own width; the page's stylesheet sets only the column grid.
  No page heading above it: the breadcrumb is the title.
- Owner's decision 21 Oct: admin list pages keep their page actions, bulk actions and paging in the one floating
  [ActionDock](../src/Jularr.Frontend/Framework/Components/ActionDock.cs), never in a page head, toolbar or at the end of the list. It is the save pill's
  floating pill (centred, well above the bottom edge, above the floating bar on a phone, always in view) with three optional zones as slots: actions (icon and short
  label; a phone keeps the icons), bulk (shown instead of the actions while rows marked `data-dock-select` are chosen, with their count) and paging (previous, "Page 2 / 9"
  or a range, next, optional page size select; plain GET links with the filters in the address, `pageNumber`). **No infinite scroll on admin pages**: lists are paged,
  25 rows by default. With a visible save pill the dock stacks above it.

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
- Owner's decision 20 Oct, sidebar modes: on `/Admin/*` the sidebar shows only the administration areas, all open; the user
  entries are not shown, and the Jularr logo (linking to the user home) is the way back. No extra back button.
  The Instance area exists in admin mode only. On a narrow header a breadcrumb part with sub-pages opens them as a menu.
  The header is one row at every width: the search shrinks to its icon on a phone, the trail scrolls.
- Local section headings are allowed only when they genuinely separate distinct
  content, not when they echo the current breadcrumb. Contextual explanations
  are shown only for meaningful tasks or failure/help states, never as filler.
- Before preparing or changing a page, read its relevant approved
  specification from [the `dev` mockups](https://github.com/Juloc/Jularr/tree/dev/docs/mockups)
  (until those specs are transferred to `clean-start`), plus the applicable
  current architecture and canonical implementation. The written spec owns
  information hierarchy and interactions; do not copy the old `Jularr.Web`
  storage or service classes into the new Backend. Where a newer owner
  decision overrides a legacy spec, explicitly record it.
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
  provider results. Books, Light Novels and Manga remain distinct.
- Discover separates media-type scope, browse view and filters. Its All
  landing has a shared Hero/Continue followed by per-enabled-type shelves
  in the approved order; only provider-supported rankings may appear. A ranking
  opens from its shelf's See all and names itself with a way back. Owner's mockup of 19 Oct
  ("capsules"): one capsule row (All, then Watch, Read, Hear and Game with their types; several
  types may be chosen, `type=a,b`) with the Customize and Filter drawers at its end, the highlights
  as one carousel (continued titles first, then each type's best by rank; the device keeps how many
  and whether it turns by itself), feed chips, sort and a rows/grid switch. The header owns the **one**
  global Search; do not add another search field inside Discover. The Filter drawer lists the media types first, then the local availability
  (in Jularr, can be requested), then only what the chosen types have: the release year and their rankings; the rankings a chosen
  type lacks leave the panel as the types change. No genre, platform, language, runtime or rating facets exist yet (Discover's
  catalogs keep no such data). Library and Favorites share the same media-type bar, filter panel (types, release year), order and layout.
  Cards use the shared cover presentation and Backend-supplied state below. User/profile ordering is respected.
  Backend supplies effective capability/order/provider data; do not copy
  old `Jularr.Web` persistence or invent a new module toggle in Frontend.
- Apply the same module/profile visibility gate to Favorites, Library and
  every other media-type navigation, plus playback routes/actions when the
  instance Playback module is disabled.

## UI localization (MANDATORY, no exceptions)

- **English is the canonical source/fallback language**. Do **not** choose
  German (`de`) or any other specific locale by hard-coded checks, default
  translations, compiled dictionaries or fixed locale lists.
- **Every single visible UI string** (headings, buttons, labels, filters,
  errors, status labels, hints, tooltips, ARIA text and new component copy)
  **MUST have a stable semantic UI resource key** and be rendered through
  the shared `UiText` resolver or declarative `Text(key)`. Never embed
  English, German or any other language as directly displayed page text.
  Non-display identifiers, CSS classes, HTML attributes, route names,
  canonical names/logo symbols and provider data are not UI translations.
- **In the same change that adds or renames a UI key**, register its English
  source declaration in `Localization/UiResources.cs`, verify there are
  no duplicate/conflicting declarations, and use the same key everywhere.
  Do not defer this work to another agent or release. The resource
  coverage check and/or component runtime must fail on missing keys,
  never silently print the raw key.
- English source declarations in C# are **registration inputs**, not a second
  runtime dictionary or a UI fallback. During host startup, invoke the
  canonical Backend `AddJularrLocalization(UiResources.English)` registration.
  Its hosted synchronization idempotently INSERTs/UPDATEs source keys and
  English text in **PostgreSQL** using the existing Logic transaction.
  It is **not** an EF migration, not a manual per-feature SQL file and
  not a second locale store. Schema creation/migration remains the
  separately controlled Backend/database baseline.
- Human-editable AI context/semantic descriptions, translations, locale
  enablement, versions and statuses are stored **only in PostgreSQL**.
  Do not hard-code descriptions or non-English translations in Frontend.
  Description/translation editing and AI generation belong to authorized
  Backend/Admin workflows; new DB descriptions remain awaiting admin review
  until that contract exists. Never fabricate translations.
- Every normal Razor request resolves an enabled locale from Backend
  locale metadata and a complete translated pack through the existing
  `ReadUiLocalesV1`/`ReadUiTextsV1` services and bounded Backend cache.
  Exact locale -> parent -> English fallback is handled centrally.
  There is **no SQL per label**, no per-page browser translation download,
  and no AI generation during rendering.
- Verified active-profile `UiLocaleId` takes precedence over the stored instance
  default. The shared Backend language-pack service resolves enabled exact/parent
  locale and English fallback; browser hints do not override configured defaults.
  Offline language bundles for all local profiles are future-only.
- Apply the resolved request culture around downstream rendering in the host
  middleware. Setting culture inside an awaited initialization method alone does
  not establish the caller's rendering culture. Never localize IDs or wire values.

## Asset ownership, build and caching (MANDATORY)

- Pages are grouped by area and page: `Pages/<Area>/<Page>/`. Keep the
  `.cshtml` entry, `.cshtml.cs` PageModel and any owned assets together.
  Current areas are `Account`, `Admin`, `User`, `Setup` and `Development`; shared
  Razor bridges stay in `Pages/Shared`. Namespaces follow the folder path.
  Declare public URLs explicitly in `@page`; Razor page identities used by
  `Url.Page`, redirects and authorization conventions follow the physical path.
  Optional module code/assets stay in `Modules/<Module>/`, reusable components
  in `Framework/Components/`. Do not add empty files or custom Razor discovery.
- **Colocation is mandatory:** assets owned by a Page live next to that
  `Pages/**/<Page>.cshtml.cs`; assets belonging to an optional feature module
  live under `Modules/<Module>/`; assets for a reusable component live beside
  its `Framework/Components/<Component>.cs`. Shared shell tokens/icons and
  global styles live in `Assets/Shared/` and `Assets/Icons/`.
  Add `.ts`, `.scss` or `.svg` files **only when needed**; do not create
  empty entry points or broad feature-sized Framework components.
- Styles use **SCSS modules** (`@use`, no legacy `@import`) and **BEM**
  selectors: `.media-shelf`, `.media-shelf__items`,
  `.media-shelf--compact`. Component-specific selectors stay in their
  owning stylesheet, no competing/global copies. Shared design tokens
  are centralized; avoid deep Sass nesting and CSS selector specificity.
  Existing shell CSS retains its historical class names until migrated.
- Browser JS is TypeScript. Add `*.entry.ts` only when a Page/Module has
  real client interactions. CSS-only entry points use `*.entry.scss`.
  The [Vite configuration](../src/Jularr.Frontend/vite.config.ts) discovers
  entries automatically, keyed by their complete source path including extension;
  same-name CSS and TypeScript entries remain independent. No central list of all Pages/Modules and no
  per-component runtime injection by default. A Page opts in to owned
  entries with `page.Asset("Pages/User/Discover/Index.entry.scss")` (or the module path).
  Fundamental Form, Table, layout, Tag/Toggle and capsule-navigation styles
  load once through the shared shell entry, retaining their colocated SCSS owners.
  Pages need no entry solely to import these base components. Optional media,
  player and reader styles/TS remain bundled with the entry that needs them.
  Don't import all optional Pages/Modules into the shared shell.
- `npm ci` (locked package versions), `npm run check` (TypeScript, Vite,
  SVG sprite), `npm test`, then `dotnet build` are required in Frontend
  CI. Production publish **must not proceed** without built assets.
  Nothing compiles Sass/JS or computes hashes on a live request.
- Vite writes only minified/compiled output into `wwwroot/build/assets`
  and emits the manifest at `Assets/Generated/manifest.json` (private, copied to published host).
  `FrontendAssets` parses it once per process to render just the hashed
  CSS/JS for the shell and current page. Unused module bundles are not
  referenced by other pages.
- SVG source icons may live with the owning Page/Module/Component or in the
  shared icon library. The build creates one fingerprinted symbol sprite
  and a private symbols manifest, referenced through `FrontendAssets.IconHref`.
  No unversioned sprite URLs and no loading each small icon separately.
- **Hash by content, not release number.** Fingerprinted CSS/JS/SVG
  assets receive `Cache-Control: public,max-age=31536000,immutable`;
  unchanged assets keep their URLs/cache across Jularr updates.
  Nonfingerprinted public files revalidate. Page HTML and
  account/profile-specific responses use private/no-store and must not
  share caches across users. Always deploy new assets before the
  HTML/manifest that references them, and retain old hashes during
  rolling deployments until old pages have expired.
- Media image components require dimensions (prevent layout shift),
  `decoding="async"`, `loading="lazy"` below the fold and appropriately
  sized `srcset/sizes` when the Backend returns variants. The critical
  first visible Hero/LCP image must use eager/high priority and should not
  be lazy-loaded. Do not lazy-load other above-the-fold critical assets.
  Images must be authorized where necessary; the **Backend image service**
  owns thumbnail conversion, cache/storage budgets, ETags and secure
  response headers. Do not proxy arbitrary providers in Frontend or
  invent a second image cache. The image component does **not** implement
  server-side thumbnail caching by itself.
- Avoid gratuitous bundles, network requests, CSS duplication, sync
  image transforms and unrestricted external image URLs. Only
  on-demand optional TS chunks should use dynamic import. Report measured
  bytes/requests on real pages before introducing further infrastructure.

## Performance and checks

- Avoid N+1 calls, unbounded lists, repeated field metadata reflection,
  unnecessary allocations/extra requests, speculative caching and heavy JS.
  Keep the view HTML server-rendered and the language cache bounded.
- A page that asks several providers asks them together (a few at a time, each with its own limit) and a provider that just failed
  is remembered for a minute; slow shelves are logged and timed in `Server-Timing`.
- Local content must not wait for optional external recommendations. Load their
  server-rendered fragments independently, retaining usable native links and
  visible failures. Do not parallelize operations over one shared SQL transaction.
- Paging uses returned item count only. Empty later pages keep Previous and the
  current filter/sort scope; every bounded editable collection remains navigable.
- Playback and Reading are independent capabilities. Continue reads and actions
  include only assets whose own module is enabled; no Frontend-only bypass.
- Progress writes are serialized, retain only the latest pending state and become
  acknowledged only after a successful response. Retry transient failures finitely
  and display unsaved state. Unload delivery is best-effort, not guaranteed durable
  storage. Reader completion requires reaching the actual content end.
- Device preferences are optional: validate stored shapes/ranges once on load and
  keep controls functional when browser storage is unavailable. Focused playback
  controls stay visible; honor reduced motion and prefer wrapping over truncation.
- Use the shared Button and its variants rather than rebuilding buttons in pages.
  Disabled buttons use the mockups' .4 opacity.
  Custom interaction requires an owner decision. Keep SCSS BEM selectors together
  with `&__element` and `&--modifier`; reuse the same style for equivalent controls.
- Keep CSS minimal: reuse inherited rules, avoid equivalent duplicated declarations
  and fix the owning style instead of stacking compensating overrides.
- All catalog/Continue cards use the same canonical `MediaCard` (the UiDesigner MediaCard, owner decision 20 Oct): a small
  portrait cover of 133-150 px (a card to continue 176 px) with softly rounded corners, local availability top left as a tag,
  the heart top right, then the linked title and one line of facts: `year · type · rating` (what is unknown is left out; the rating
  is the catalog's, on a scale of ten). A card to continue reads `Type Year | detail` and `ContinueDetail` writes the detail (`S01 E05 ·
  22 min left`, `Chapter 183`, `Track 4 · 3 min left`, `42%`). Every text is one line with an ellipsis; a clipped text shows in full in a
  tip over the cover on hover, focus and tap. Hover or focus shows only the one contextual action over the cover, icon and short label
  (Request, Watch, Continue, Read), never Details; touch keeps it visible. A missing real primary action shows none; do not
  fabricate playback or requests. Series lifecycle, genres, descriptions and language capsules belong in Details, not the cover.
  Grids hold as many covers per row as fit (`media-grid`), no two-row wrapping of card text.
- Progress is a thin capsule inside the foot of the cover (translucent white track, accent fill); no time text and it never moves
  other content. A page may add one short label over the cover, bottom left (Favorites shows where the profile stands).
- Availability is green only when all monitored units have local profile-language
  audio; yellow means complete in other audio languages; orange means partial
  with profile audio; yellow–orange means partial in other audio languages.
  Subtitles do not change this classification. Purple downloading, blue searching/
  queued, gray requested and no dot for missing stay distinct. Hover expands the
  dot into a compact tag (icon and a short word or the unit counts) with the full sentence below it.
  Library, Favorites, Collections, People, Franchise and Home read the whole projection; Discover
  cards carry the coarse state of the Work and show a tag only for a held or requested title.
  Backend must supply these facts. Existing list services do not yet expose audio
  completeness: production does not infer it from `MediaStateType` or episode counts.
- The delayed hover preview (owner's hover-card mockup, 21 Oct): after 550 ms of the mouse resting on a cover, never on one of
  its buttons (or of the keyboard focus on it), the card opens a panel over the row, as high as the card and 1.84 times as wide,
  fetched then from `/MediaPreview` (nothing loads before, nothing by hover on touch or devices without hover). The cards of the row
  never change width or position: the card under the panel is only hidden, and the panel shifts sideways to stay inside the row and
  the viewport. Trailer mode: the title's trailer in a 16:9 frame (the one allowed external embed, the privacy host, one muted, very
  wide, cropped frame; Jularr's own pause top left, sound top right, a progress line and `0:12 / 2:24` from the player's own
  messages, a running line when it reports none) above the linked title, the year, type and runtime, and two lines of description;
  description mode, for a title without a trailer: a larger title, the facts and the longer description. The foot holds the
  card's action (a pill), its heart and Details. A touch device has no hover: a card that stays in view near the middle for 2 s
  opens the same panel by itself, one at a time, unless the profile switched `AutoplayPreviews` off (rendered as
  `data-autoplay-previews` on `#app-content`; data saver and offline also keep it off). It closes on leave, Escape, scrolling,
  resizing, a click elsewhere, a hidden document or page navigation, and drops its frame. The cached component
  module owns the delegated handlers once across app-shell visits. List contracts carry no trailer IDs: the page asks one title at a
  time (a Work's trailer is its `Trailer` fact, a TMDB title's and an anime season's come from their provider within 3 s, otherwise the
  local catalog's description alone).
- Bookmark mutations use the existing Details POST, typed Form, antiforgery and
  Backend watchlist service (the page is called Favorites). Return only to validated local URLs. If a list does not
  supply current membership, the icon links to management in Details, not a false toggle.
  Provider-only titles without a canonical Work ID have no bookmark action yet.
  Shelves retain native touch/keyboard scrolling and local chevrons, no visible
  scrollbar or navigation-only server reads.
- Truncated visible text (`text-overflow: ellipsis`, line clamping or text clipped by
  `overflow: hidden`) must expose its complete value through the owning shared
  component, at minimum a localized tooltip usable by pointer, keyboard and touch.
  Do not rely on the native `title` attribute alone. Prefer wrapping when practical;
  essential information must remain accessible without hover. Clipped decoration
  and layout containers without hidden text do not need tooltips.
- Preserve Clean/Original, light/dark/system, and desktop/mobile/tablet
  accessibility. Full Player and minimized Now Playing are presentations of the
  same playback session; keep the media element outside exchanged page content.
  Learning remains out of the current implementation scope.
- Display the skin names as **Original** and **Clean**. Clean is neutral with a purple
  accent and no ornamental artwork; Original uses the restored ink/paper/blossom
  treatment. Both consume the same central tokens and components, never separate pages.
- Original's default seed is Jularr red (`#c8102e`); Clean's is Jularr purple
  (`#813eff`, the mockups' accent; its palette is the mockups' reference palette, other seeds derive theirs). Derive light/dark surfaces, readable accent labels and artwork hue
  from one selected seed through the shared Appearance palette. Theme artwork
  and the mascot follow the accent; media artwork, provider logos
  and semantic status colors do not. The Jularr mark of the Clean skin is the owner's cosmic parrot, one
  single-colour path (Clean shows the parrot in the accent colour, so it follows the chosen accent in light and dark; [brand-clean.svg](../src/Jularr.Frontend/Pages/Shared/brand-clean.svg), shown by [Brand.cs](../src/Jularr.Frontend/Pages/Shared/Brand.cs) in the shell, login and profile pages); Original keeps its
  ring-and-ink mark ([brand-original.svg](../src/Jularr.Frontend/Pages/Shared/brand-original.svg)), shown only in that skin. The tab and home-screen icons are the Clean mark in the default accent (`#8040f7`, `#9a70ff` in a dark tab; the PNGs sit on a white tile)
  ([favicon.svg](../src/Jularr.Frontend/Assets/Shared/favicon.svg) and the PNGs beside it). Current choices are device-local; do not
  imply implemented instance/profile policy or persistence.
- Development-only design workshops may show explicitly labeled sample data and
  disabled component states. They must not start library playback or pretend to
  perform business actions. An explicitly requested promotional trailer may load
  its official provider embed after Play or sustained hover, without a playback session or progress.
  Reuse real components and approved references, not copies.
- Before completing a change, check the
  [CODEMAP](../src/Jularr.Frontend/CODEMAP.md),
  verify every displayed key is in the canonical English declaration,
  run the targeted frontend project build plus changed component tests,
  and check relevant browser layouts/interactions.
  A Backend-only CI build does **not** validate Razor frontend.
  Report limitations explicitly; do not claim unrun checks passed.
