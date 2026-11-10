# Frontend conventions

Technical rules for `src/Jularr.Frontend/`.
Backend and C# rules: [CONVENTIONS](CONVENTIONS.md); design and shared page behaviour: [Frontend UX](FRONTEND_UX.md); where code lives:
[Frontend CODEMAP](../src/Jularr.Frontend/CODEMAP.md).

## Architecture

- One ASP.NET Core host: Razor frontend plus shared Backend APIs; Backend is the same-process class library.
  Razor invokes typed services through DI, never by HTTP requests to its own server.
- Server-side rendering (SSR), **not** a JavaScript SPA.
  The response is complete, encoded HTML.
  Small TypeScript enhancements may handle playback, reading, live search and server-rendered fragments; no client hydration, client-side business
  templates or translation-bundle fetching.
  Preserve native navigation and forms wherever the feature does not inherently require JavaScript.
- In-place navigation uses **htmx 4** (boost), not a SPA.
  Links and forms of app pages replace `#app-content` with the same element of the server's full page; the head, theme and an active Player stay.
  Player, Reader, Game, Account, Setup, API addresses, downloads, new windows and `data-navigation="full"` (also links a page script handles itself)
  load normally ([rules](../src/Jularr.Frontend/Modules/Navigation/Navigation.ts)).
  Back reloads the page from the server; no page HTML is kept in browser storage.
  Every route stays usable without JavaScript; authorization, form validation and rendering stay server-owned.
  A POST that shows its page again adds no history entry.
- App-page styles and scripts render inside `#app-content`.
  A page script registers its DOM setup with `onPage(import.meta.url, setup)`: it runs once per document load and again for each new page the script
  belongs to; document-level listeners are installed once at module level.
  HTML a script inserts goes through `inserted(element)`.
- Pages hold still while they load (layout shift below 0.01, [LayoutShiftTests](../tests/Jularr.E2E.Tests/LayoutShiftTests.cs)).
  What only works with script, or only without it, shows or hides through `@media (scripting: enabled)` / `(scripting: none)`, which holds before the
  first paint, never through a class a script sets later.
  A control a script reveals keeps its room (a shelf's heading is as high as its arrows), and a native control a script replaces is as high as its
  replacement.
  A device choice that changes a page's look or layout (skin, brightness, the list and Discover layouts) is also a cookie of the same name
  ([DevicePreference.ts](../src/Jularr.Frontend/Modules/Appearance/DevicePreference.ts)); the server writes it into `<html>`
  ([DevicePreferences.cs](../src/Jularr.Frontend/Modules/Appearance/DevicePreferences.cs), known values only), the browser storage stays its source.
  A live value renders on the page as the stream's first snapshot will say it (the same text owner,
  [LiveEntities](../src/Jularr.Frontend/Modules/Live/LiveEntities.cs)), so a row keeps its size when the stream arrives.
- Frontend owns UI appearance, interaction, presentation-only state.
  Backend owns permissions, identities, validation, business decisions, transaction boundaries, persistence, paging data and API contracts.
- Browser interactions use the canonical Backend API or an authorized Razor HTML fragment.
  This does not permit server-side Razor to call its own host over HTTP.
- No second database, duplicate permission logic, fake row/provider data, invented service procedures, dummy playback state or inactive buttons
  presented as functional.
  If a backend contract is missing, prepare the UI structure honestly and list the integration gap.

## C# HTML DSL and components

- **Author HTML hierarchies declaratively in C#**, for example: `new Div(new H1(new Text("profile.title")), new Button(new Text("common.save")))`.
  Razor remains the ASP.NET entry and shell, but the shared components render server-side HTML without requiring page-specific HTML templates.
- Use small encoded HTML primitives (`Div`, `H1`, `P`, `Span`, `Button`, etc.) backed by one safe HTML-writing foundation; **do not** reinvent a
  complete browser DOM, virtual DOM or JavaScript framework.
- **Every substantial UI component has its own C# file** under `Framework/Components/<Component>.cs`.
  It owns the public API, supported behavior, layout markup and rendering.
  Small private helper types used **only by that component** may remain in the same C# file.
  Small, closely related HTML primitives may share `Framework/Elements.cs`.
- No central growing component-type `switch` or duplicate Razor partial per component.
  Render output directly from the component through the shared HTML writer, encoding dynamic values and attribute values.
- Never concatenate unchecked raw HTML.
  Validate HTML attribute names and URL schemes, use semantic tags, accessible names, keyboard/focus states, responsive layout and antiforgery for
  real POST forms.
- A page owns its independent `.cshtml.cs` definition and thin `.cshtml` entry.
  Reuse `Table<T>`, `Form<T>`, `Split`, tabs, menus, media cards and other domain-neutral components rather than copying UI code.
- **Pages compose components; components never contain the complete screen.** Page-specific information hierarchy, media-type ordering, browse-mode
  choice, conditional Hero/Continue placement and screen-specific state belong in the owning PageModel.
  Reusable, small pieces such as `NavigationTabs`, `MediaShelf`, `MediaCard`, `Table<T>` and `Form<T>` own only their presentation and interaction
  contract.
  Do not create feature-sized `Framework/Components/Discover.cs`, `Requests.cs`, etc. to hide a page.
  Small page-only helper records can remain beside the PageModel.
- Real form fields derive required/type/length/range metadata from Backend `BaseData`/`DataField`; missing select options or save procedures are not
  guessed.
  Tables need real typed data and authorized reads for sorting/paging/filtering.
  Unbound structures never imply successful loads.
- **Generic layout versus optional behavior:** `Split` is the one canonical two-area layout.
  Master/Detail is an **optional mode of Split**, not a second Framework component.
  The owning page supplies the selected item and a real back route.
  Mobile detail/list switching must remain keyboard-accessible, server-authoritative and must not invent selections.
- **Tabs are navigation, not stacked H2 sections.** Route-backed `Tabs` renders a single selected pane; `NavigationTabs` handles navigation-only
  scopes.
  Neither duplicates the global Breadcrumb heading.
- Put Jularr-specific shell presentation in `Pages/Shared/Shell.cs`, including helpers only used there.
  `_AppShell.cshtml` remains a thin one-call Razor bridge.
  Cross-page Brand may have its own shared C# file; generic components stay under `Framework/Components`.
- Pages describe forms via `Form<TData>` field selectors and optional labels/autocomplete; **the Form owns HTML labels, controls, ids, disabled states
  and backend DataField metadata**.
  Do not copy input/label helper classes into each page.
  Do not activate submission, provider buttons or session persistence without a real, authorized backend operation and antiforgery handling.
  A password field may carry the eye ([PasswordReveal](../src/Jularr.Frontend/Framework/Components/PasswordReveal.cs), client-only) and a link on its
  label's row through `FieldHelp.Trailing` and `FieldHelp.Aside`.
- Field examples and explanations belong to the Backend field: `hint.<Fields>.<Field>.placeholder` is the placeholder of typed fields (not names; a
  password shows only a hint its page names; an optional field shows what happens when it stays empty, such as its default),
  `hint.<Fields>.<Field>.info` the text behind the label's (i), shown on hover, focus and tap
  ([FieldInfo](../src/Jularr.Frontend/Framework/Components/FieldInfo.cs)).
  A page passes `FieldHelp` only where the field means something special there; an empty key shows none.
  Labels stay short: units belong in the label, explanations in the (i).
- Actions (a switch, a search, a delete, a dialog's save) are forms that post through htmx and do not reload the page: the server answers with the
  same page and the client swaps only the [Region](../src/Jularr.Frontend/Framework/Components/Region.cs) (a container with an id) around the form,
  else the page content, keeping scroll position, opened `<details>` and sections, and focus
  ([ActionSwap](../src/Jularr.Frontend/Modules/Navigation/ActionSwap.ts)); an answer from another path (a redirect to the list, a new item) navigates
  like a link.
  A page script that must run again over swapped elements opts in with `onPage(..., { partial: true })` and guards every element.
  Errors stay in place in the swapped region.
  Without script the form posts and redirects.
  The result of an action is never content of the layout (the hero, a status line) but the quiet hint at the bottom centre
  ([ActionToast](../src/Jularr.Frontend/Framework/Components/ActionToast.cs), about two seconds): a handler calls `Hint(key, values)`, every other
  successful POST that redirects says "Saved", `NoHint()` says nothing.
  A script's request gets the hint in the `HX-Trigger` header of the answering page, a page without script shows it itself.
- A dialog is the shared [Modal](../src/Jularr.Frontend/Framework/Components/Modal.cs): title row with the X at the right, the body, and the footer,
  which is the actions of its form (`form.Cancel()` and the primary `form.Submit(...)`, never `form.Save()`); a dialog's form saves on its own and the
  page's save pill does not take part.
- The settings of a provider, service or catalog are inputs inside its card (a `Form.Save` form saved by the page's pill), never behind a Settings or
  Configure button that opens a dialog.
  Exception: the admin lists of indexers and subtitle providers are compact rows whose settings are edited in a dialog
  ([Frontend UX](FRONTEND_UX.md)).
  Dialogs stay for adding something new (with Cancel) and for destructive confirmations (`Modal.Confirm`: a Disconnect that deletes a stored key or
  token asks first, Cancel keeps it).
  A row of a catalog list (a speech model) carries its switches inline in the same way.
- Settings forms end with `form.Save()` instead of their own save button.
  The page's one [save dock](../src/Jularr.Frontend/Framework/Components/SavePill.cs) (the
  [ActionDock](../src/Jularr.Frontend/Framework/Components/ActionDock.cs) in its form-page mode: "N changes", Discard, Save) appears centred in the
  content while a value differs from the loaded page, marks the changed fields and saves every changed form in turn; a failing form keeps its error
  while the others save.
  Discard shows the saved page again in place without asking; leaving with changes asks.
  Actions (test, search, delete) stay their own buttons.
  Without script each form keeps its visible save button.
  Pages with many small forms of one kind label each form with its item instead of repeating a field label.
  A form shown again as a draft (a row added, a choice applied) counts as unsaved in the pill until saved or discarded.
- No control looks like the system's.
  A `select` is the styled [Dropdown](../src/Jularr.Frontend/Framework/Components/Dropdown.ts): the field's button and its own popup list (keyboard,
  type-ahead, placed below or above), enhanced from every native select of the app on every page; the native select stays in the form, hidden, as the
  submitted value and the field without script (a context that styles its select by a class gives the button the same class).
  Checkboxes and radios are the shared box and dot ([Checkbox](../src/Jularr.Frontend/Framework/Components/Checkbox.cs)); a number steps with its own
  plus and minus ([NumberStepper](../src/Jularr.Frontend/Framework/Components/NumberStepper.ts)); a slider shows its number while it moves
  (`Form.entry.ts` is part of every page); a file field has the pill button of the page (the system's file dialog itself cannot be restyled).
  `Form.entry.ts` and `Dropdown.entry.ts` load in the shell; pages do not opt in.
- A field may wear a `FieldAffix` (`Text`, `Number`, `Select`, `Range`): an icon circle at its start (the page builds the icon) and a quiet unit at
  its end, from a `unit.*` text key, inside the frame and before a number's plus and minus.
  A unit belongs there, not in the label ("Library scan interval" + hours); validation and stepping stay the field's own.
  A select whose `SelectOption`s carry an `Icon` (a language's flag) shows the chosen option's icon as the prefix.
- Form transport values are invariant, independently of the display language.
  Explicit device-only settings may opt into `Form<T>(..., local: true)`: the same typed fields render as an editable group, not a form.
  Local mode rejects server actions, antiforgery tokens, refresh and Submit; it never implies profile saving.
  `Form<TData>` emits ASP.NET's `__Invariant` field markers; use normal framework binding, not per-page parsing or a parallel binder.
  Field validation remains Backend-owned.
  Hidden-field errors must be visible at form level; failed reads receive the shared Page error presentation, including empty-result pages; unknown
  errors render in the page too (`ErrorText`), not as a bare JSON answer.
- Buttons, search and single-line fields use the pill radius (`--radius-field`; multiline fields keep `--radius-inner`).
  Form labels sit above the field, inset by `--field-label-inset` (12px; 11px/650, with the (i) after them), remain associated through `for`/id (a
  group of choices through its legend) and wrap without truncation.
  Reuse `Tag` and native-checkbox `Toggle`; bind editable switches through `Form.Toggle` so Backend field metadata and POST values stay central.
- `Form<T>` owns the layout: short fields share two columns where there is room and one in narrow panels; multiline, file, list and long text fields,
  errors and the buttons take the whole row, buttons at its end.
  A form with at most one short field sits in one row beside its buttons.
  A select of up to three short fixed choices (Boolean or enum) renders as one segmented control under the same name; data lists stay a select.
  `Form.Section` groups fields in a titled panel under one save; `Form.ChoiceTable` shows options as rows and several fields as checkbox or radio
  columns (a quality ladder).
  Pages do not restyle form grids.
- Tabs and subsection navigation reuse `NavigationTabs`: the mockups' filter row of plain pills, the selection the lavender tint with its
  `--accent-edge` outline.
  `Tabs` adds only the server-selected content pane.
  Keep the row single-line and horizontally scrollable when necessary, with complete labels.
- Settings is a page, not a special `SettingsGroup` renderer.

## UI localization

- **English is the canonical source/fallback language**.
  Do **not** choose German (`de`) or any other specific locale by hard-coded checks, default translations, compiled dictionaries or fixed locale
  lists.
- **Every single visible UI string** (headings, buttons, labels, filters, errors, status labels, hints, tooltips, ARIA text and new component copy)
  **MUST have a stable semantic UI resource key** and be rendered through the shared `UiText` resolver or declarative `Text(key)`.
  Never embed English, German or any other language as directly displayed page text.
  Non-display identifiers, CSS classes, HTML attributes, route names, canonical names/logo symbols and provider data are not UI translations.
- **In the same change that adds or renames a UI key**, register its English source declaration in `Localization/UiResources.cs`, verify there are no
  duplicate/conflicting declarations, and use the same key everywhere.
  The resource coverage check and/or component runtime must fail on missing keys, never silently print the raw key.
- How texts are stored, synchronized and resolved per request: [Localization](areas/localization.md).

## Time and calendar dates

- Persist absolute timestamps as UTC.
  Render them through `LocalTime` so the browser applies its own IANA zone, locale and daylight-saving rules; no `DateTime.ToLocalTime()` in Razor.
- `DateOnly` release dates are civil dates and **never** UTC-converted.
  For Calendar and Work, compute the request's `Today` once using `BrowserTimeZone` and the browser's advisory timezone cookie; the browser corrects
  visible day markers/countdowns in place before requesting additional pages.
- Timezone changes and local midnight must not trigger `location.reload()`, full-page replacement, layout jumps or periodic global scans.
  Synchronize the cookie before htmx navigation and only update the relevant text/classes.
  Keep UTC and timezone tests in the Frontend, Backend and Chromium suites.

## Asset ownership, build and caching

- Pages are grouped by area and page: `Pages/<Area>/<Page>/`.
  Keep the `.cshtml` entry, `.cshtml.cs` PageModel and any owned assets together.
  Current areas are `Account`, `Admin`, `User`, `Setup` and `Development`; shared Razor bridges stay in `Pages/Shared`.
  Namespaces follow the folder path.
  Declare public URLs explicitly in `@page`; Razor page identities used by `Url.Page`, redirects and authorization conventions follow the physical
  path.
  Optional module code/assets stay in `Modules/<Module>/`, reusable components in `Framework/Components/`.
  Do not add empty files or custom Razor discovery.
- **Colocation is mandatory:** assets owned by a Page live next to that `Pages/**/<Page>.cshtml.cs`; assets belonging to an optional feature module
  live under `Modules/<Module>/`; assets for a reusable component live beside its `Framework/Components/<Component>.cs`.
  Shared shell tokens/icons and global styles live in `Assets/Shared/` and `Assets/Icons/`.
  Add `.ts`, `.scss` or `.svg` files **only when needed**; do not create empty entry points or broad feature-sized Framework components.
- Styles use **SCSS modules** (`@use`, no legacy `@import`) and **BEM** selectors: `.media-shelf`, `.media-shelf__items`, `.media-shelf--compact`.
  Component-specific selectors stay in their owning stylesheet, no competing/global copies.
  Shared design tokens are centralized; avoid deep Sass nesting and CSS selector specificity.
- Browser JS is TypeScript.
  Add `*.entry.ts` only when a Page/Module has real client interactions.
  CSS-only entry points use `*.entry.scss`.
  The [Vite configuration](../src/Jularr.Frontend/vite.config.ts) discovers entries automatically, keyed by their complete source path including
  extension; same-name CSS and TypeScript entries remain independent.
  No central list of all Pages/Modules and no per-component runtime injection by default.
  A Page opts in to owned entries with `page.Asset("Pages/User/Discover/Index.entry.scss")` (or the module path).
  Fundamental Form, Table, layout, Tag/Toggle and capsule-navigation styles load once through the shared shell entry, retaining their colocated SCSS
  owners.
  Pages need no entry solely to import these base components.
  Optional media, player and reader styles/TS remain bundled with the entry that needs them.
  Don't import all optional Pages/Modules into the shared shell.
- Media image components require dimensions (prevent layout shift), `decoding="async"`, `loading="lazy"` below the fold and appropriately sized
  `srcset/sizes` when the Backend returns variants.
  The critical first visible Hero/LCP image must use eager/high priority and should not be lazy-loaded.
  Do not lazy-load other above-the-fold critical assets.
  Images must be authorized where necessary; the **Backend image service** owns thumbnail conversion, cache/storage budgets, ETags and secure response
  headers.
  Do not proxy arbitrary providers in Frontend or invent a second image cache.
  The image component does **not** implement server-side thumbnail caching by itself.
- Avoid gratuitous bundles, network requests, CSS duplication, sync image transforms and unrestricted external image URLs.
  Only on-demand optional TS chunks should use dynamic import.
  Report measured bytes/requests on real pages before introducing further infrastructure.

## Performance and checks

- Avoid N+1 calls, unbounded lists, repeated field metadata reflection, unnecessary allocations/extra requests, speculative caching and heavy JS.
  Keep the view HTML server-rendered and the language cache bounded.
- A page that asks several providers asks them together (a few at a time, each with its own limit) and a provider that just failed is remembered for a
  minute; slow shelves are logged and timed in `Server-Timing`.
- Local content must not wait for optional external recommendations.
  Load their server-rendered fragments independently, retaining usable native links and visible failures.
  Do not parallelize operations over one shared SQL transaction.
- Paging uses returned item count only (`.Paged()`, above).
  Empty later pages keep Previous and the current filter/sort scope; every bounded editable collection remains navigable.
- Playback and Reading are independent capabilities.
  Continue reads and actions include only assets whose own module is enabled; no Frontend-only bypass.
- Device preferences are optional: validate stored shapes/ranges once on load and keep controls functional when browser storage is unavailable.
  Focused playback controls stay visible; prefer wrapping over truncation.
- Use the shared Button and its variants rather than rebuilding buttons in pages.
  Custom interaction requires an owner decision.
  Keep SCSS BEM selectors together with `&__element` and `&--modifier`; reuse the same style for equivalent controls.
- Keep CSS minimal: reuse inherited rules, avoid equivalent duplicated declarations and fix the owning style instead of stacking compensating
  overrides.
- Layers use the z tokens of [Theme.scss](../src/Jularr.Frontend/Modules/Appearance/Theme.scss), lowest first: `--z-sticky` (floating pill, continue
  bar, sticky toolbars), `--z-dropdown` (menus, lists and tips opened from the content), `--z-shell` (header, sidebar, minimized player), `--z-menu`
  (phone menu), `--z-drawer` (drawers and panels that are no dialog), `--z-toast`, `--z-tooltip`, `--z-top`.
  Inside a component only small local numbers (1-7); dialogs and popovers use the top layer.
  A container that is a layer of its own and holds a menu lifts itself to `--z-dropdown` while the menu is open (`:has(details[open])`), so what
  follows it never covers the menu.
- Truncated visible text (`text-overflow: ellipsis`, line clamping or text clipped by `overflow: hidden`) must expose its complete value through the
  owning shared component, at minimum a localized tooltip usable by pointer, keyboard and touch.
  Do not rely on the native `title` attribute alone.
  Prefer wrapping when practical; essential information must remain accessible without hover.
  Clipped decoration and layout containers without hidden text do not need tooltips.
- Preserve Clean/Original, light/dark/system, and desktop/mobile/tablet behaviour.
- Before completing a change, update the area file of the feature, verify every displayed key is in the canonical English declaration, run the
  targeted frontend project build plus changed component tests, and check relevant browser layouts/interactions.
  A Backend-only CI build does **not** validate Razor frontend.
  Report limitations explicitly; do not claim unrun checks passed.
