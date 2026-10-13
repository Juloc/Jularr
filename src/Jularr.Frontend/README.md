# Jularr.Frontend

Minimal server-side Razor web host for Jularr Backend services.
Architecture and hard rules:
[Frontend conventions](../../docs/FRONTEND_CONVENTIONS.md);
[CODEMAP](CODEMAP.md).

- Pages own their small C# definitions; routes still use ordinary Razor Pages.
- HTML layout is composed with `new Div(new H1(new Text("...")))` and
  component-owned C# rendering under `Framework/Components/`.
- Shared Razor body delegates to the component HTML encoder. No centralized
  type `switch`, no SPA framework and no inline display translations.
- All English resources are declared once in `Localization/UiResources.cs`,
  synchronized into the existing PostgreSQL resource catalog at startup.
  Backend owns descriptions, non-English translations, status and cache.
- The active UI language uses the verified profile's locale ID first; anonymous
  requests negotiate enabled locales via Accept-Language with English fallback.
  No fixed German dictionary or client-side translation loading.
- Frontend `Program.cs` hosts Backend API and Razor on the same process.
  It **requires** explicit `JULARR_DB` configuration and an already installed
  fresh PostgreSQL schema. Startup registration does not run migrations.

Consumer routes: `/`, `/Details/{id:long}`, `/Player/{id:long}`,
`/Watchlist`, `/Settings`. `/Discover` redirects to `/`.

Admin routes: `/Admin/Requests`, `/Admin/Rules`,
`/Admin/Requests/Users`, `/Admin/Wanted`, `/Admin/Works`,
`/Admin/Works/{id:long}`, `/Admin/Library`.

The routes prepare actual layouts but do not invent service data. Table rows and filters only render from genuine authorized Backend results.
Login is assembled using one typed `Form<LoginDataV1>` in its C# PageModel.
Labels and inputs come from the reusable form renderer and canonical
DataField metadata, not page-local HTML builders. Existing Login backend
data supports email/password; username login has no confirmed backend contract.
Login, configured Plex authentication, profile selection, session revocation,
logout and profile name/language settings invoke typed Backend services directly.
POST forms use antiforgery, canonical field metadata and shared sanitized errors.
Required values and minimum/maximum lengths come from Backend fields. The Backend
service validates inputs; shared page handling and Form place translated field or
form-level errors without page-local validation rules. Password creation currently
requires 12-128 Unicode characters; sign-in checks existing credentials without
applying the new-password minimum. A compromised-password blocklist is not implemented.
Password values and session secrets are never rendered. PIN-protected profiles
remain inaccessible until the PIN contract is implemented; passkeys are not guessed.
The Jularr-specific application Shell belongs to `Pages/Shared/Shell.cs`,
including its private header/sidebar/mobile helpers; the Razor bridge calls it
once. Brand is shared across Shell and Login; only generic components remain
in Framework. The Topbar Breadcrumb is the sole visible page title.
The shell currently offers only unconditional Home/Settings navigation;
authorized module links are pending. Admin routes require the Backend `jularr.admin`
policy plus feature-specific Backend policies. MediaManager is limited to manual
media/Requests/Wanted; configuration is Owner-only. Canonical typed media/
request services and Player PlaybackPlan integration
remain pending. Do not expose the prepared routes as production-ready.

Development-only `/ShellPreview?view=admin-requests` previews real page
definitions; production returns 404.

```sh
node src/Jularr.Frontend/Tools/verify-ui-resources.mjs
dotnet build src/Jularr.Frontend/Jularr.Frontend.csproj
```

Frontend CI builds Vite assets, checks the asset manifest, and builds Razor.
Browser checks remain a separate required verification before production.

## Asset build (required)

```sh
cd src/Jularr.Frontend
npm ci --no-audit --no-fund
npm run check
npm test
dotnet build -c Release
dotnet publish -c Release
```

The .NET host does not compile TypeScript, Sass or sprites at request time.
`npm run build` produces hashed CSS/JS and a hashed shared SVG symbol
sprite in `wwwroot/build/assets`. `FrontendAssets` reads the private published manifests from
`Assets/Generated/` once per process and includes only shell plus owning Page/Module
assets. Production publish fails when the manifests are missing. A clean deployment must run the Node build before publishing; during
Development, rerun `npm run build` after changing assets. Live asset watch
is not implemented yet and must not be advertised as available.

Static hashed assets have 1-year immutable public cache headers, and
authorized server-rendered HTML uses private/no-store. Each modified asset
gets a new content hash; unchanged assets survive application releases.
For rolling deployments, retain the previous hashed assets until old HTML
is no longer served. Browser image markup lazily decodes/loads noncritical
media, reserves dimensions and can emit Backend-supplied responsive
variants. The canonical Backend media image endpoint must still provide
actual thumbnail resizing, ETag and authorization where required.

Asset ownership: styles/scripts/SVG live next to the relevant Page,
component or optional module; shared shell styles are the only global
SCSS entry. New CSS uses BEM selectors and `@use` for Sass modules.


The two-area `Split` can optionally enable Master/Detail mobile behavior
without introducing a parallel component. `Tabs` uses route-based selection
and renders just the active pane. Settings uses generic `UiSection` and
`ItemList`, not a special Settings-only framework component.
