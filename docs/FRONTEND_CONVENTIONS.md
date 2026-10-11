# Frontend conventions

Scope: `src/Jularr.Frontend/`. General C# and backend architecture conventions
remain in [CONVENTIONS.md](CONVENTIONS.md). Use the
[frontend CODEMAP](../src/Jularr.Frontend/CODEMAP.md) for entry points and ownership.
Specifications define feature behavior; this document defines how frontend code is built.

## Runtime and responsibility

- One ASP.NET Core host: Frontend renders Razor and hosts the shared Backend APIs.
  The Backend is a class library. No second web server, self-HTTP calls or
  duplicated business logic.
- Default to server-rendered HTML. Razor Pages are **not** a JavaScript SPA;
  a navigation request produces server-rendered HTML.
- Use small, deliberately justified JavaScript for genuinely client-side behavior
  (e.g. player, keyboard input and interactive controls). No client hydration
  framework, implicit JavaScript language-package loading or a parallel state machine.
- Frontend owns presentation, interaction and accessibility. Backend services
  own canonical data, validation, permissions, decisions, transactions and SQL.
  Resolve typed backend services through DI; do not call the local HTTP API from Razor.

## Pages and components

- Each independently routed feature/page owns a small `.cshtml.cs` PageModel
  with its own declarative page definition; keep the `.cshtml` entry minimal.
  Do not rebuild `AdminScreens`, `UserScreens`, a central screen registry or
  presentation-only fake pages.
- Reuse meaningful components rather than duplicating markup, layout rules,
  form logic or UI state in every page.
- **Target component design:** Each substantial component has one clear C# owner
  (normally its own `Components/<Component>.cs` file) defining its inputs,
  supported behavior and render contract. Small strictly related helper types
  may share that file. Prefer a component-owned renderer or tightly paired view
  over extending a single growing `switch` across unrelated components.
- Rendering must be HTML-encoded and safe by default, preserve semantic
  elements, labels, ARIA/focus rules, unobtrusive validation and antiforgery.
  Do not construct HTML by concatenating unchecked strings.
- Shared presentation primitives (`Panel`, `Tabs`, `MasterDetail`, headings)
  and typed controls (`Table<T>`, `Form<T>`, filters, selects, dialogs,
  media cards) have one canonical implementation and a consistent API.
- A `Table` requires real typed rows/columns and an authorized read/list
  contract before claiming filtering, sorting, selection, counts or paging.
  Form fields reuse the backend's `BaseData`/`DataField` required/type/length
  metadata. Do not duplicate domain validation or synthesize backend types.
- A `MasterDetail` layout is not a second, independent domain/state store.
  Keep selections, forms and actions bound to their actual page/service context.
- Do not add placeholder rows, invented titles, procedure names, fake sessions,
  mock permission results or controls that appear to work but cannot execute.
  Keep missing service integration explicit and inactive until available.

## C# HTML-element DSL: proposed, not decided

An optional server-side API such as `new Div(new H1(...), new Button(...))`
is technically possible and fits declarative C# pages. It is **not yet the
approved rendering contract**. Before implementing it, compare:

- minimal HTML primitives versus a second general-purpose DOM/HTML framework;
- generated/encoded attributes, text and form elements, Tag Helpers and antiforgery;
- allocation/rendering costs, readability and component-local maintenance.

Whether markup is authored in C# or a component-local Razor view, the output
must remain ordinary SSR HTML. Do not start a wholesale renderer replacement
or create a class for every HTML tag without an explicit decision.

## Localization

- Both Frontend and Backend use one canonical PostgreSQL resource catalog.
  Stable semantic keys identify strings; English source text, translations,
  version/status and human-editable **AI semantic descriptions** belong to
  the backend catalog/database, not to frontend PageModels.
- Resolve translations server-side through the backend's locale service
  and bounded cache, then send ready HTML. No AI at render time, SQL per label,
  manual translation insert scripts or routine browser JS translation fetch.
- The current `UiText` bilingual/English fallback definitions are temporary.
  Do not expand them into a second permanent catalog.
- Offline clients may later download full needed locale packages for all
  locally available profiles, deduplicated by locale. This is not an
  implementation task now.

## UX, performance and verification

- Support Clean/Original plus light/dark/system, keyboard/screen reader,
  reduced motion, touch/mobile, tablet and desktop. Do not hide essential
  actions on narrow screens.
- Prefer bounded server queries and efficient HTML. Avoid N+1 service calls,
  unbounded result sets, rebuilding reflection metadata per row/request,
  heavy client bundles and redundant caches. Optimize on measured hotspots,
  not invented complexity.
- The normal Player is its own full-screen media surface. Do not add Learning
  to the initial Player or invent `PlaybackPlan`/`ActiveSession` sources.
- Validate changed frontend code with a targeted frontend project build and
  focused tests; browser-check affected responsive and keyboard flows.
  A successful backend-only solution build does **not** verify Razor frontend.
- Keep the [CODEMAP](../src/Jularr.Frontend/CODEMAP.md) current for changed
  ownership/entry paths, and report pending backend contracts and unverified tests.

## Current implementation debt (not approved target)

The still-draft [frontend PR #960](https://github.com/Juloc/Jularr/pull/960)
currently groups thin records in `Framework/PageComponents.cs` and renders
them through a large `Pages/Shared/_UiComponent.cshtml` type switch.
`UiTable` exposes only headings and the toolbar has disabled controls;
`Form<T>` metadata exists without a complete renderer. This is **not**
proof that component-local rendering or the typed Table/Form contracts
are implemented. Refactor those parts deliberately once the rendering
choice and backend contracts are settled.
