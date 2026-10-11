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

The routes prepare actual layouts but do not invent service data. Unbound
toolbar controls are disabled; rows only render from real service outputs.
Login remains presentation-only. Admin authorization, canonical typed media/
request services, profile locale preference and Player PlaybackPlan integration
remain pending. Do not expose the prepared routes as production-ready.

Development-only `/ShellPreview?view=admin-requests` previews real page
definitions; production returns 404.

```sh
node src/Jularr.Frontend/Tools/verify-ui-resources.mjs
dotnet build src/Jularr.Frontend/Jularr.Frontend.csproj
```

Backend-only CI does not build Razor frontend. Always run targeted component
and browser checks before merge; if the SDK/browser is unavailable, report
the validation as not performed.
