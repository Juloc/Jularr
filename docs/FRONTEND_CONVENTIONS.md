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
  encoded HTML. JavaScript is reserved for unavoidable client interactions
  such as playback; no client hydration or translation-bundle fetching.
- Frontend owns UI appearance, accessible interaction, presentation-only state.
  Backend owns permissions, identities, validation, business decisions,
  transaction boundaries, persistence, paging data and API contracts.
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
  entry. Reuse `Table<T>`, `Form<T>`, `MasterDetail`, menus, media cards
  and other domain-neutral components rather than copying UI code.
- Real form fields derive required/type/length/range metadata from Backend
  `BaseData`/`DataField`; missing select options or save procedures
  are not guessed. Tables need real typed data and authorized reads for
  sorting/paging/filtering. Unbound structures never imply successful loads.

## Page identity and approved UX (MANDATORY)

- **The shared Topbar breadcrumb/current-page label is the sole page title.**
  Every shelled page sets `page.Title(resourceKey)`, which the layout renders
  in the Topbar. **Never** repeat the same title as an `H1`, `UiHeading`,
  hero title, banner, card heading, or description at the top of the content.
  No automatic "page description" below the breadcrumb. Standalone unauthenticated
  pages without the shared Topbar (e.g. Login) may use a semantic `H1`.
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
  in the approved order; only provider-supported rankings may appear. The
  header owns the **one** global Search and Filter entry; do not add another
  search/filter field inside Discover. User/profile ordering is respected.
  Backend supplies effective capability/order/provider data; do not copy
  old `Jularr.Web` persistence or invent a new module toggle in Frontend.
- Apply the same module/profile visibility gate to Watchlist, Library and
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
- Later, trusted active-profile `UiLocaleId` must take precedence over
  Accept-Language once the session/profile preference contract exists.
  Offline language bundles for all local profiles are future-only.

## Performance and checks

- Avoid N+1 calls, unbounded lists, repeated field metadata reflection,
  unnecessary allocations/extra requests, speculative caching and heavy JS.
  Keep the view HTML server-rendered and the language cache bounded.
- Preserve Clean/Original, light/dark/system, and desktop/mobile/tablet
  accessibility. Player is a separate fullscreen page without Learning
  until its own design/PlaybackPlan contract exists.
- Before completing a change, check the
  [CODEMAP](../src/Jularr.Frontend/CODEMAP.md),
  verify every displayed key is in the canonical English declaration,
  run the targeted frontend project build plus changed component tests,
  and check relevant browser layouts/interactions.
  A Backend-only CI build does **not** validate Razor frontend.
  Report limitations explicitly; do not claim unrun checks passed.
