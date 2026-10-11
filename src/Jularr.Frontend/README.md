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
- The active UI language is negotiated from enabled DB locales (for now
  Accept-Language, English fallback); trusted profile preference binding
  is an open Backend/frontend integration step. No fixed German dictionary.
- Frontend `Program.cs` hosts Backend API and Razor on the same process.
  It **requires** explicit `JULARR_DB` configuration and an already installed
  fresh PostgreSQL schema. Startup registration does not run migrations.

Consumer routes: `/`, `/Details/{id:long}`, `/Player/{id:long}`,
`/Watchlist`, `/Settings`. `/Discover` redirects to `/`.

Admin routes: `/Admin/Requests`, `/Admin/Rules`,
`/Admin/Requests/Users`, `/Admin/Wanted`, `/Admin/Works`,
`/Admin/Works/{id:long}`, `/Admin/Library`.

The routes prepare actual layouts but do not invent service data. Table rows and filters only render from genuine authorized Backend results.
Login remains presentation-only. Admin authorization, canonical typed media/
request services, profile locale preference and Player PlaybackPlan integration
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
assets. Production publish fails when the manifests are missing. A clean
deployment must run the Node build before publishing; dev can run
`npm run watch` and rebuild icons on demand with `npm run build`.

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

