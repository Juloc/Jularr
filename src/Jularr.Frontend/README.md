# Jularr.Frontend

The single ASP.NET Core host for Razor Pages, Backend APIs and opted-in Workers.
Razor invokes typed Backend services in process; browser playback/reading uses the
same canonical APIs. No Frontend SQL or separate translation store.

Rules: [Frontend conventions](../../docs/FRONTEND_CONVENTIONS.md).
Owners and tests: [CODEMAP](CODEMAP.md).

## Running and building

Configure `JULARR_DB` explicitly against an installed fresh PostgreSQL baseline.
Startup synchronizes English UI resources, not schema or migrations.
Never point tests at an application database.

```sh
cd src/Jularr.Frontend
npm ci --no-audit --no-fund
npm run check
npm test
npm run verify:ui
dotnet publish -c Release
```

Production publishing requires built, fingerprinted assets. The host reads private
manifests from `Assets/Generated`; it does not compile assets on a request.
Rebuild after asset edits; an asset watch mode is not implemented.
Vite and the HTML renderer use the same `/build/` public root, including images
referenced inside CSS. Do not add a parallel `/assets/` endpoint.

## Current frontend

- User: Discover, Library, Details, Watchlist, Requests, Player, Reader and Settings.
- Discover displays local data first, then loads authorized recommendation HTML;
  the native recommendation link and normal browse routes work without JavaScript.
- Login supports configured password/Plex flows; profile selection, sessions,
  logout and profile name/language editing use Backend services and antiforgery.
- Admin: accounts, instance modules, providers/Plex, storage/WOL/library paths,
  inventory identification, Works, acquisition sources/profiles, moderation,
  Wanted, Activity, language and translation administration.
- Original and Clean share tokens/components, with independent brightness and
  accent controls stored optionally on the device.
  Appearance choices wrap as whole controls on narrow screens; search and color
  inputs use the same 44px minimum control height.
- Original restores Washi paper, blossom framing, ink landscapes and the mascot;
  the shared Hero uses Fuji/mascot artwork only when a real media backdrop is absent.
  Clean remains undecorated. The [original assets](Modules/Appearance/Original/README.md)
  are compressed WebP files; controls stay opaque and flat in both skins.
- UI language uses the verified profile locale, then the instance default.
  PostgreSQL owns translations/descriptions and exact/parent/English fallback.
- Forms use Backend field metadata and invariant wire values. Shared handling
  displays read errors and form errors; passwords and session secrets are not rendered.
- Player/Reader acknowledge successful progress saves, retry temporary failures
  finitely and show unsaved state. Final unload delivery remains best-effort.

Hashed public assets use immutable caching; private page HTML uses no-store.
Optional module assets remain page-scoped; HLS support loads only when needed.
Media image processing/caching remains Backend-owned.

## Not complete

Persistent shell/miniplayer, previous/next episode targets, AniList Discover,
provider filters, full season/episode details, profile media visibility/order,
metadata-locale policy and the full Setup/PIN/quota/rule workflows remain open.
Do not present these as implemented. Manga/Book acquisition is documented by its
Backend owners, not a separate Frontend pipeline.

Development-only `/Design` reuses real components with labeled examples and
disabled playback; `/ShellPreview` exposes prepared definitions. Neither is
available in Production. Real browser/layout/accessibility checks are a separate
verification gate; Node and Razor tests do not replace them.
