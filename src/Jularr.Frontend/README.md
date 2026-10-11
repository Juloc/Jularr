# Jularr.Frontend

Minimal ASP.NET Core Razor Pages frontend, separate from the obsolete Jularr.Web.
**One server process** hosts Razor and backend API endpoints once the shared backend
registration and authentication contracts have been integrated.

## Source layout

- `Pages/{Feature}.cshtml.cs`: the actual, independent declarative page definition.
  No centralized AdminScreens, UserScreens, screen registry or shared fake PageModel.
- `Pages/{Feature}.cshtml`: short Razor route/view, rendering `_PageBody`.
- `Framework/Page.cs`: `JularrPage`, `PreparedPage`, `Page`, reusable
  `Panel`, `MasterDetail` and `Tabs` composition.
- `Framework/PageComponents.cs`: shared display-only components that never
  fabricate rows, counts, metadata, files, media sources or request states.
- `Framework/BaseField.cs`: typed form field definitions inferred from backend
  `[DataField]` metadata. Do not reimplement domain validation in the UI.
- `Pages/Shared/_UiComponent.cshtml`: one server-side Razor component renderer.
- `Pages/Shared/_Layout.cshtml`: responsive Clean/Original, light/dark/system shell.

## Prepared screens

Consumer: Discover/Home `/` (with `/Discover` redirect), `/Details/{id:long}`,
`/Player/{id:long}`, `/Watchlist`, `/Settings`.

Admin: `/Admin/Requests`, `/Admin/Rules`, `/Admin/Requests/Users`,
`/Admin/Wanted`, `/Admin/Works`, `/Admin/Works/{id:long}`, `/Admin/Library`.

Page classes define their own sections, navigation, tabs, controls and columns.
The media Player route deliberately renders **only a native video surface**, with
no fabricated playback URL/session/progress and no Learning components. The real
Player/PlaybackPlan integration and its controls require separate design.

No fake data, no provider calls, no direct SQL and no second HTTP loop.
Table bodies are left empty until an actual authorized backend read completes;
unknown create/update/delete services or forms are **not** invented. Unconnected
search/filter/sort controls are visibly disabled, not presented as working.
The eventual backend services determine paging, statuses, record identity, action
authorization and typed editable fields. Admin routes currently have no special
Admin gate; enforce authorized backend caller contracts before production use.

The Settings page structure is prepared for eventual integration into the shared
Profile/Account Settings tab. Do not implement a separate settings persistence domain.

## Localization

Fixed UI labels reference stable semantic keys and temporarily use English-only
feature fallback resources; shell/login retains the original provisional bilingual
`UiText` implementation. These are not the canonical translation store.

**PostgreSQL must own the resource catalog, English source text, translated values,
version/status and human-editable semantic/AI descriptions** for both frontend and
backend. Descriptions must not be embedded in frontend PageModels. Replace
`UiText` with a thin adapter to the backend's per-request memory-cached locale
resolver when available. No per-string SQL lookup, no runtime AI, no dynamic JS
translation requests, and no manually managed translation insert scripts.
Full offline locale packs for every local profile are a future-only requirement.

## Authentication / preview / verification

Login is presentation-only; its submit/credentials remain disabled until the
backend login and session contracts exist. All normal Razor pages require an
authenticated cookie by default, but this **does not** replace future Admin
permission gating.

Development-only `/ShellPreview?view=admin-requests` (and `discover`,
`details`, `player`, `watchlist`, `settings`, `admin-rules`,
`admin-users`, `admin-wanted`, `admin-works`, `admin-work`,
`admin-library`) instantiates the *real page definitions* without executing
their read handlers. Outside Development it returns 404.

```sh
dotnet run --project src/Jularr.Frontend/Jularr.Frontend.csproj
```

A real .NET build and browser checks are required before merging or enabling
production routes; CI currently only validates the backend solution.
