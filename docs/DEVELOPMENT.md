# Development

## Stack

- .NET 10 (C#): ASP.NET Core Razor pages rendered on the server, htmx 4 for in-place navigation, TypeScript and SCSS built by Vite.
- PostgreSQL 18 only.
- One process per database: `Jularr.Frontend` is the only web host; Backend, Workers and Integrations are libraries inside it.

## Folders

- `src/Jularr.Frontend`: host, pages, components, assets. `src/Jularr.Backend`: services, logic, SQL.
- `src/Jularr.Workers`: background timers and queues. `src/Jularr.Integrations`: external providers.
- `database`: frozen baseline and `migrations/`. `tests`: Backend tests and Playwright browser tests.
- `clients/android`: phone and TV apps. `scripts`, `.github/workflows`: tooling and CI.

## Commands

```bash
dotnet build Jularr.sln
cd src/Jularr.Frontend && npm ci && npm run check && npm test && npm run verify:ui
```

Tests need the isolated test server (never the dev database). Where `/tmp` is tmpfs or overlay, set `TMPDIR` to a folder on the real disk.

```bash
docker run -d --name jularr-test-pg -p 127.0.0.1:15433:5432 -e POSTGRES_USER=jularr_test \
  -e POSTGRES_PASSWORD=isolated-test-password -e POSTGRES_DB=jularr_test_admin postgres:18
export JULARR_TEST_DB='Host=127.0.0.1;Port=15433;Username=jularr_test;Password=isolated-test-password;Database=jularr_test_admin'
dotnet test tests/Jularr.Backend.Tests --filter "FullyQualifiedName~StorageTests"
```

Browser tests need `npm run build` in the frontend first:

```bash
dotnet build tests/Jularr.E2E.Tests -c Release
pwsh tests/Jularr.E2E.Tests/bin/Release/net10.0/playwright.ps1 install chromium
dotnet test tests/Jularr.E2E.Tests -c Release --no-build --filter "FullyQualifiedName~LayoutShiftTests"
```

Android (in `clients/android`, JDK 17, Android SDK 36): `gradle :app-tv:testDebugUnitTest :app-mobile:testDebugUnitTest`.

CI runs on PRs to `main`; the label `ci/full` or `[full-ci]` in the PR title adds the full regression.

## Migrations

- New file `database/migrations/NNNN_short_name.sql`: the next number without gaps, plain SQL without `BEGIN`/`COMMIT`, guards such as
  `IF NOT EXISTS` where they help.
- Never change a released migration; fix it with the next one. Test it in `DatabaseMigrationTests` (baseline, data, migration, data kept).
- On start Jularr backs up the database and keys, then runs all pending migrations in one transaction. On failure nothing changes and a
  maintenance page is shown; start the previous version.
- `node scripts/freeze-baseline.mjs` is for releases only.

## Release

The release commit's Frontend and Backend Full runs on `main` are green (`gh run list --commit <sha>`; start Frontend by hand with
`gh workflow run Frontend --ref main` if the commit changed no frontend file). Then:

```bash
git fetch origin && git tag vX.Y.Z origin/main && git push origin vX.Y.Z
```

A suffix (`v0.3.0-rc.1`) makes a prerelease; the TV app has its own tag `tv-vX.Y.Z`. A server updates with `JULARR_VERSION` in `.env` and
`docker compose pull && docker compose up -d`.

The owner's local preview (`scripts/run-main-preview.ps1`) runs only `origin/main` from its own checkout; do not develop in it.

## Code rules

- Pages call Backend services through DI, never over HTTP or with their own SQL. Browser scripts use the same service API or server-rendered fragments.
- The Backend owns permissions, validation, transactions and storage. Services read; writes and business rules live in `Logic`; one service call is
  one transaction. Workers only run System services.
- SQL is written in the executing method with bound parameters and explicit columns. Lists read one bounded page, without counts.
- No new third-party library or native dependency without the owner's yes.
- Never log tokens, codes or passwords, and never put them in URLs.
- Unreachable storage never counts as deleted: an offline NAS or empty mount point removes no files or inventory.
- Store timestamps as UTC and render them with `LocalTime`.
- Server-rendered HTML from the C# components; every route works without JavaScript.
- Every visible text is a UI resource key declared in English in `src/Jularr.Frontend/Localization/UiResources.cs`; no hard-coded language.
- Use the design tokens (`src/Jularr.Frontend/Modules/Appearance/Theme.scss`, `Palette.ts`), never literal colours, radii, shadows or font sizes.
  Change a look in its token or the shared component, and reuse shared components instead of copying them.
- No layout shifts: images have dimensions, script-only controls show through `@media (scripting: enabled)` and keep their room.

## UX rules (owner)

- The breadcrumb in the top bar is the only page title; never repeat it as a heading.
- At most one row of tabs per page.
- Flat and quiet: no grey surfaces, glass, gradients or extra shadows. One stroke-only icon set (`src/Jularr.Frontend/Assets/Icons`).
- Settings are inputs in their card, saved by the page's one save pill. Dialogs only add something new or confirm a destructive step.
- Actions post through htmx and swap only their region; their result is the short hint at the bottom, not page content.
- Admin lists use the shared list kit, 25 rows per page (Next only when the page is full) and the floating action dock. User lists scroll endlessly.
- Every row of a list looks alike; nothing moves while content loads.
- On phones every tap target is at least 44x44px. Truncated text shows its full value on hover, focus and tap.
