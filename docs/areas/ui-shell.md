# UI shell: layout, menus, appearance, PWA and errors

Shell, admin menu, header search, appearance and skins, PWA and error pages.
Index: [CODEMAP](../CODEMAP.md).

## Decisions

Owner decisions; change them only with the owner.

- Sidebar modes: on `/Admin/*` the sidebar shows only the administration menu, all groups open; the user entries are not shown, and the Jularr logo
  (linking to the user home) is the way back.
  No extra back button.
  The user menu has one entry, Administration, that opens the first allowed entry.
  On a narrow header a breadcrumb part with sub-pages opens them as a menu, the first part stays whole and a long last part (the title) ends in an
  ellipsis with its full text in a tip.
  The header is one row at every width: the search shrinks to its icon on a phone, the trail scrolls.
- The header search: large in the middle of the header (where the header is tight it narrows before the trail, which stays whole), no key hint or
  shortcut; from two letters, after a short pause, a popup under the field lists the titles Jularr keeps (no provider is asked) grouped by type, the
  type with the best match first, in each type the typed title, then titles beginning with it, then other matches, local before trending before
  popular, two to six per type as the room allows, then All results; each group's heading (its type's icon, name and a See all pill) opens that type's
  results, titles show a 40×60 cover with the typed words marked and year · first genre · rating under them (what is unknown is left out), and two or
  more groups stand in two columns where there is room.
  Enter opens Discover's results: one row per type in the same order, each with See all for that type alone (which pages); the providers are asked
  once, together, only on Enter, and the kept titles join their rows, so a provider that does not answer never hides what the suggestions showed.
  On a phone the icon opens the field over the header, the suggestions fill the screen and the back button closes them.
- The administration menu: one definition ([AdminMenu](../../src/Jularr.Frontend/Framework/AdminMenu.cs): groups, entries with their own icon, the
  pages of an entry, each page's right and modules, the entry's counter) drives the sidebar, the chip row of every page and the breadcrumb.
  The sidebar (the owner's proposal, variant B) has a find field that narrows the entries as one types (by the entry's, its pages' and its group's
  name), then seven flat groups, open, each under a thin line with its small name; an entry links to the first of its allowed pages and is hidden when
  its module is off or the account lacks the right (no tree, nothing collapses).
  Three entries wear a live figure that hides at zero (Queue: open downloads, Jobs: tasks that need help, Users: open requests; the `menu` entity of
  the live stream).
  The pages of an entry are reached by **one** row of chips above the page, the same on each of them (a detail page has none; `Page.NoPageRow()`); a
  page with filter chips of its own shows them in that row after a divider (`AdminMenu.Row`).
- The shell follows the mockups: a 252px sidebar (230px, then 67px with icons only up to 1120px, the selected area tinted), on a phone (up to 640px)
  an off-canvas drawer over a dimmed page opened from the header's menu button.
  The admin sidebar has an ADMINISTRATION eyebrow; the header has the trail, the one search large in the middle (no key hint or shortcut), a
  light/dark switch, the inbox and the avatar (profile initials, linking to the settings).
- **Installable, not offline.** `Modules/Pwa` makes the app installable on a phone or desktop: `/manifest.webmanifest` (name Jularr, standalone, scope
  `/`, the fingerprinted brand icons including the maskable one, theme colour the default accent seed, background the surface) and
  `/service-worker.js` (site root, `Service-Worker-Allowed: /`, `no-cache`), both public; `_Layout` links the manifest and the Apple/theme tags for
  every page (app, sign-in, setup) and loads `pwa.entry.ts`, which registers the worker after load.
  The worker has an empty fetch handler: it never answers a request and caches nothing, so htmx swaps, `/live/events`, sign-in redirects and range
  requests behave as without it.
  Always on (no module switch fits; it only offers installation).
  No path base: the host serves from the root.
  Offline use is a separate decision.
- Error pages (owner 21 Oct): one `ErrorState` for every status of a browser request, server-rendered at `/Error/{code}`.
  Clean shows the Jularr mark large in the accent, a bold `404 Not Found`, one line and Home/Back/Retry/Sign in; Original shows its artwork over the
  whole page with exactly two buttons (Try again, Go to home page) and no other visible text.
  Failures of the service itself (5xx) and a visitor who is not signed in use the standalone layout; a 5xx page names a reference and the time, never
  the exception.
  A page read whose entity does not exist (the service's not-found) answers with this same page and status, centrally in `JularrPage`, not per page; a
  fragment (named GET handler) gets the bare 404.
  `/api`, streams, assets and non-HTML clients keep their plain answers.
- Display the skin names as **Original** and **Clean**.
  Clean is neutral with a purple accent and no ornamental artwork; Original uses the restored ink/paper/blossom treatment.
  Both consume the same central tokens and components, never separate pages.
- Original's default seed is Jularr red (`#c8102e`); Clean's is Jularr purple (`#813eff`, the mockups' accent; its palette is the mockups' reference
  palette, other seeds derive theirs).
  Derive light/dark surfaces, readable accent labels and artwork hue from one selected seed through the shared Appearance palette.
  Theme artwork and the mascot follow the accent; media artwork, provider logos and semantic status colors do not.
  The Jularr mark of the Clean skin is the owner's cosmic parrot, one single-colour path (Clean shows the parrot in the accent colour, so it follows
  the chosen accent in light and dark; [brand-clean.svg](../../src/Jularr.Frontend/Pages/Shared/brand-clean.svg), shown by
  [Brand.cs](../../src/Jularr.Frontend/Pages/Shared/Brand.cs) in the shell, login and profile pages); Original keeps its ring-and-ink mark
  ([brand-original.svg](../../src/Jularr.Frontend/Pages/Shared/brand-original.svg)), shown only in that skin.
  The tab and home-screen icons are the Clean mark in the default accent (`#8040f7`, `#9a70ff` in a dark tab; the PNGs sit on a white tile)
  ([favicon.svg](../../src/Jularr.Frontend/Assets/Shared/favicon.svg) and the PNGs beside it).
  Current choices are device-local; do not imply implemented instance/profile policy or persistence.

## Frontend

- [Shell.cs](../../src/Jularr.Frontend/Pages/Shared/Shell.cs), [Brand.cs](../../src/Jularr.Frontend/Pages/Shared/Brand.cs): capability-driven
  application chrome and branding; the header's breadcrumb (Admin > entry > page, the entry left out where it is named like the page, `Page.Parent`
  between) and the sidebar's user and admin modes (`/Admin/*` shows [the administration menu](../../src/Jularr.Frontend/Framework/AdminMenu.cs): find
  field, seven groups, twenty-two entries (Wanted next to Activity; the Downloads group: Downloads, Servers, Speed and schedule, External clients),
  three live figures from [AdminMenuCounters](../../src/Jularr.Frontend/Framework/AdminMenuCounters.cs) and the `menu` entity of the live stream;
  `AdminMenuTests`, `admin-menu.test.mjs`, `list-filter.test.mjs`, E2E `AdminMenuTests`, `PhoneShellTests` (phone trail and its menu, Activity queue
  row); the user menu has one link per area, taken from the entries flagged `UserMenu`).
  [Breadcrumb.entry.ts](../../src/Jularr.Frontend/Pages/Shared/Breadcrumb.entry.ts) opens a long trail at its end;
  [Shell.entry.ts](../../src/Jularr.Frontend/Pages/Shared/Shell.entry.ts) owns the phone's menu drawer and the tablet's icon-only areas,
  [HeaderSearch.ts](../../src/Jularr.Frontend/Pages/Shared/HeaderSearch.ts) the header search's suggestions grouped by type on every page
  ([tests](../../src/Jularr.Frontend/Tools/header-search.test.mjs), E2E `DiscoverCatalogTests`);
  [LinkMenu](../../src/Jularr.Frontend/Framework/Components/LinkMenu.cs) turns a part with sub-pages into a menu on narrow headers; the Clean mark
  [brand-clean.svg](../../src/Jularr.Frontend/Pages/Shared/brand-clean.svg) (drawn in the accent colour by
  [Brand.scss](../../src/Jularr.Frontend/Pages/Shared/Brand.scss)) and Original's
  [brand-original.svg](../../src/Jularr.Frontend/Pages/Shared/brand-original.svg) (one per skin), tab and home-screen icons in
  [Assets/Shared](../../src/Jularr.Frontend/Assets/Shared).
- Account-scoped PWA main-profile designation and validated profile accent: [profile appearance
  API](../../src/Jularr.Backend/Service/User/Profile/V1/PwaAppearanceServices.cs), [profiles
  picker](../../src/Jularr.Frontend/Pages/Account/Profiles/Profiles.cshtml.cs),
  [settings](../../src/Jularr.Frontend/Pages/User/Settings/Settings.cshtml.cs), database migration `0009_pwa_main_profile_accent.sql`.
  The main profile persists independently of active profile selection.
  [Canonical vector renderer](../../src/Jularr.Frontend/Modules/Pwa/PwaIconRenderer.cs) supplies opaque color-only PNGs with the 80%-diameter Android
  safe zone and private personalized manifest/Apple tags, without user IDs in icon URLs.
- Accent-colored tab favicons are rendered from the canonical parrot SVG/32px PNG via [PWA icon
  renderer](../../src/Jularr.Frontend/Modules/Pwa/PwaIconRenderer.cs); authenticated [theme
  controls](../../src/Jularr.Frontend/Modules/Appearance/theme.entry.ts) start from the main profile color and persist changes via the scoped [main
  accent service](../../src/Jularr.Backend/Service/User/Profile/V1/PwaAppearanceServices.cs).
  Anonymous design previews remain device-local.
  The installed Android/Apple icons and authenticated manifest use the same main-profile color.
  The standalone Android expired-cookie/login/deep-link journey is covered by `Jularr.E2E.Tests/PwaTests.cs`.
- [Installable app](../../src/Jularr.Frontend/Modules/Pwa/PwaEndpoint.cs) (`/manifest.webmanifest`, no-store `/pwa/version` and the pass-through
  [worker](../../src/Jularr.Frontend/Modules/Pwa/ServiceWorker.js) at `/service-worker.js`, no caching, no offline),
  [registration](../../src/Jularr.Frontend/Modules/Pwa/Pwa.ts) after load ([entry](../../src/Jularr.Frontend/Modules/Pwa/pwa.entry.ts)); [foreground
  update prompt](../../src/Jularr.Frontend/Modules/Pwa/PwaUpdate.ts) only for standalone apps, user-initiated reload,
  [styles](../../src/Jularr.Frontend/Modules/Pwa/pwa.entry.scss) and SSR notice in [_Layout](../../src/Jularr.Frontend/Pages/Shared/_Layout.cshtml);
  [tests](../../src/Jularr.Frontend/Tools/pwa.test.mjs), `PwaTests` (Backend and E2E).
- [Appearance](../../src/Jularr.Frontend/Modules/Appearance/Appearance.cs), [palette](../../src/Jularr.Frontend/Modules/Appearance/Palette.ts), [theme
  client](../../src/Jularr.Frontend/Modules/Appearance/theme.entry.ts): Original/Clean, brightness and optional device preferences.
- [Design tokens](../../src/Jularr.Frontend/Modules/Appearance/Theme.scss) (shape, type, status, defaults) and
  [Palette.ts](../../src/Jularr.Frontend/Modules/Appearance/Palette.ts) (colours from the accent, the owner's mockup palette verbatim for #8040f7,
  other accents turn it to their hue): the one visual language of every page; [token table](../FRONTEND_CONVENTIONS.md).
- [Original artwork](../../src/Jularr.Frontend/Modules/Appearance/Original/README.md), [skin
  styles](../../src/Jularr.Frontend/Modules/Appearance/Theme.scss): Washi, blossoms, landscapes and mascot; shared Hero fallback, no provider-art
  tinting.
- [Error](../../src/Jularr.Frontend/Pages/Account/Error/Error.cshtml.cs) (`/Error/{code}`, where
  [ErrorPages.cs](../../src/Jularr.Frontend/Framework/ErrorPages.cs) re-executes a failed browser request: status code pages and unhandled exceptions,
  never for `/api`, `/live`, assets or non-HTML clients) and
  [AccessDenied](../../src/Jularr.Frontend/Pages/Account/AccessDenied/AccessDenied.cshtml.cs) (403) render the one
  [ErrorState](../../src/Jularr.Frontend/Pages/Shared/ErrorState.cs) ([styles](../../src/Jularr.Frontend/Pages/Shared/ErrorState.scss)): Clean the
  Jularr mark large in the accent over a bold `404 Not Found`, one line and Home/Back/Retry/Sign in; Original its full-page artwork
  ([images](../../src/Jularr.Frontend/Modules/Appearance/Original/README.md)) with exactly Try again and Go to home page.
  401 and 5xx use the standalone layout, 5xx show a reference and the time and never the exception; `ErrorPageTests`.
