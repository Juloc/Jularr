# Jularr.Frontend

A new Razor Pages frontend, isolated from the existing Jularr.Web application.

- `Framework/Page.cs`: `JularrPage`, Page, reusable component tree, MasterDetail and Tabs.
- `Framework/BaseField.cs`: input field metadata from the backend's canonical `[DataField]` attributes, including required, length and numeric bounds.
- `Pages/Shared/_Layout.cshtml`: shared guest/authenticated shell, same theme tokens for Clean and Original.
- `Pages/Account/Login.cshtml`: **presentation only**. Credentials are disabled and no POST handler or authentication implementation is provided.
- `Pages/ShellPreview.cshtml`: development-only shell preview; unavailable outside Development.

The frontend project references `Jularr.Backend` for the existing DataField contracts, but does not modify any backend code or database. The backend's login operation, authenticated shell context, localized backend error catalog and service-bound list/paging contracts are not yet present. No fake sign-in or bypass is installed.

Until the backend supplies these contracts, do not enable login submission or represent development shell fixtures as authenticated application data. Razor Pages are used directly; no SPA router or self-HTTP loop is introduced.

To run the presentation preview once .NET 10 SDK is available:

```sh
dotnet run --project src/Jularr.Frontend/Jularr.Frontend.csproj
```

Visit `/Account/Login` or, only in Development, `/ShellPreview`.

## Page skeletons (frontend-only)

- `/` is the one Discover/Home render; `/Discover` redirects permanently, preserving query strings.
- `/Details/{id:long}` is the canonical consumer details shell, with media-specific body deferred until Work contracts exist.
- `/Player/{id:long}` is a **player-only full-screen canvas**, no Learning UI, fabricated playback URL, session, progress or transport.
- `/Watchlist`, `/Settings` (future Profile Settings tab), `/Admin/Requests`,
  `/Admin/Rules`, `/Admin/Requests/Users`, `/Admin/Wanted`,
  `/Admin/Works`, `/Admin/Works/{id:long}`, `/Admin/Library`.
- Admin data is *not* loaded and actions are intentionally absent; future Admin access must come from an authoritative backend permission/caller contract, not hard-coded UI roles.
- In Development only, `/ShellPreview?view=discover` (or watchlist, details, player, settings, admin-requests, admin-rules, admin-users, admin-wanted, admin-works, admin-work, admin-library) previews the exact same shared component definitions without a login.

### Localization boundary

The current `UiText` bilingual shell/login entries and English feature fallback labels are **temporary presentation scaffolding**, not a final translation store. Do not expand the legacy translations table. All canonical source texts, language translations, version statuses **and AI semantic descriptions** must be persisted by the backend in PostgreSQL. Semantic descriptions are authored/edited in the database Admin workflow, **not embedded in frontend .cs files**. Only stable semantic keys are referenced by Razor/pages. The frontend must resolve compact per-request texts from the backend's bounded memory-cached catalog via DI when that contract exists. No runtime AI, JS translation fetch, per-text SQL, or manual SQL insert scripts. Offline bundle preloading for every locally available profile is a separate future task.
