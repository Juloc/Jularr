# Frontend CODEMAP

Navigation only. Rules:
[Frontend conventions](../../docs/FRONTEND_CONVENTIONS.md).
Shared conventions and Backend ownership:
[CONVENTIONS](../../docs/CONVENTIONS.md),
[Backend CODEMAP](../../docs/CODEMAP.md).

## Host / localization

- [Program.cs](Program.cs): one ASP.NET Core host; DI Backend service
  registration; `AddJularrLocalization` startup resource sync; Razor request
  middleware; API and Razor routing.
- [UiResources.cs](Localization/UiResources.cs): every English UI source
  declaration. Not a runtime translation store.
- [UiText.cs](Framework/UiText.cs): per-request, backend-cached language
  snapshot via Public services; verified profile preference before browser hints.
- [Backend locale services](../Jularr.Backend/Service/Public/Localization/V1),
  [Backend resource sync](../Jularr.Backend/Service/Core/Localization/UiLocalizationRegistration.cs):
  only canonical locale/query/DB owners.

## Rendering and reusable components

- [Page.cs](Framework/Page.cs): `JularrPage`, `PreparedPage`,
  `Page`, page definitions and shared service-error feedback.
- [UiComponent.cs](Framework/UiComponent.cs): component-to-Razor HTML boundary,
  interface and encoder wiring, **no type switch**.
- [Elements.cs](Framework/Elements.cs): minimal encoded C# HTML DSL;
  `Div`, `H1`, `Text`, `Button` and related small primitives.
- [Components](Framework/Components): reusable typed tables, field-aware
  forms, navigational controls and layout primitives.
- [Split.cs](Framework/Components/Split.cs): generic two-area layout with
  optional `MasterDetail(detailOpen, backHref)` mode; Mobile list/editor
  switching is driven by server-selected state, not a second component.
- [Tabs.cs](Framework/Components/Tabs.cs): actual SSR tab navigation
  with exactly one selected pane. [NavigationTabs.cs](Framework/Components/NavigationTabs.cs)
  is for navigation-only lists with no local tab content.
- [Section.cs](Framework/Components/Section.cs) and
  [ItemList.cs](Framework/Components/ItemList.cs): reusable localized
  grouping and read-only lists, used by Settings instead of `SettingsGroup`.
- [NavigationTabs.cs](Framework/Components/NavigationTabs.cs):
  reusable, localized accessible navigation tabs driven by supplied links.
- [MediaShelf.cs](Framework/Components/MediaShelf.cs):
  reusable media shelf with truthful title, optional browse link and real cards.
- [Discover/Index.cshtml.cs](Pages/Index.cshtml.cs): **owns the page composition**
  (enabled media-type navigation, browse navigation, Hero, Continue and
  type-grouped shelves); awaits the authorized instance/profile discovery read.
- [BaseField.cs](Framework/BaseField.cs) and
  [Form.cs](Framework/Components/Form.cs): `Form<TData>` owns label/input
  markup, field errors and local antiforgery POSTs using canonical backend
  `DataField` metadata, including minimum/maximum string lengths. Unbound forms remain disabled.
- [Shell.cs](Pages/Shared/Shell.cs) and
  [Shell.scss](Pages/Shared/Shell.scss): Jularr-specific Header, Sidebar,
  Mobile navigation and their private helpers in **one** shell owner.
  Navigation exposes only unconditional routes pending backend capabilities.
- [Brand.cs](Pages/Shared/Brand.cs): shared Jularr brand for Shell and Login;
  [Icon.cs](Framework/Components/Icon.cs): generic fingerprinted SVG primitive.
- [AdminNavigation.cs](Framework/AdminNavigation.cs):
  Requests/Rules/Users section links; Requests hides configuration links using verified service grants.
- [Shared layout](Pages/Shared/_Layout.cshtml):
  document shell, asset/locale setup and RenderBody only.
  [App shell](Pages/Shared/_AppShell.cshtml): delegates actual markup
  to component renderers.
  [Page body](Pages/Shared/_PageBody.cshtml),
  [Ui component boundary](Pages/Shared/_UiComponent.cshtml):
  thin Razor-to-C# bridges.
- [Login Page](Pages/Account/Login.cshtml.cs) and
  [Login SCSS](Pages/Account/Login.entry.scss): short declarative Login
  composition using `Form<LoginDataV1>` and shared Brand. Backend
  exposes email/password and configured Plex flows through Public services.
- [Profiles](Pages/Account/Profiles.cshtml.cs), [Sessions](Pages/Account/Sessions.cshtml.cs),
  [Logout](Pages/Account/Logout.cshtml.cs): typed service-driven account session flows.
- [AccessDenied](Pages/Account/AccessDenied.cshtml.cs): sanitized catalog-backed 403 page for denied Razor routes.
- [Settings](Pages/Settings.cshtml.cs): active profile name/language editing,
  read-only non-owner view and account navigation; no page-local SQL or permissions.
- [Vite build](vite.config.ts), [package.json](package.json),
  [locked dependencies](package-lock.json), [tsconfig](tsconfig.json):
  compile/fingerprint Sass and TypeScript from colocated `*.entry.scss`
  and `*.entry.ts` files without loading unused modules.
- [Generated private manifests](Assets/Generated): build-only, published alongside
  server content but never in `wwwroot`; regenerated from Vite and icon build.
- [FrontendAssets.cs](Framework/FrontendAssets.cs): cached hashed manifest
  resolver for Razor styles, scripts and icon URLs, without request-time builds.
- [Shared shell SCSS](Assets/Shared/shell.entry.scss),
  [appearance module](Modules/Appearance/theme.entry.ts),
  [Discover CSS entry](Pages/Index.entry.scss),
  [Settings CSS entry](Pages/Settings.entry.scss),
  [Player CSS entry](Modules/Player/player.entry.scss): currently declared
  page/module assets; component Sass imported only by their owners.
- [SVG sources](Assets/Icons), [SVG sprite build](Tools/build-icons.mjs):
  deduplicated hashed sprite, with icons also allowed beside modules/pages.
- [MediaImage.cs](Framework/Components/MediaImage.cs): accessible lazy or
  priority image markup and optional responsive variants; Backend owns image
  resizing, ETag/cache, permissions and thumbnail generation.

## Pages

- [Discover](Pages/Index.cshtml.cs) and
  [Discover redirect](Pages/DiscoverRedirect.cshtml.cs).
- [Details](Pages/Details.cshtml.cs),
  [Player](Pages/Player.cshtml.cs),
  [Watchlist](Pages/Watchlist.cshtml.cs),
  [Settings](Pages/Settings.cshtml.cs),
  [Login](Pages/Account/Login.cshtml.cs).
- [Requests](Pages/Admin/Requests.cshtml.cs),
  [Rules](Pages/Admin/Rules.cshtml.cs),
  [Users](Pages/Admin/Users.cshtml.cs),
  [Wanted](Pages/Admin/Wanted.cshtml.cs),
  [Works](Pages/Admin/Works.cshtml.cs),
  [Work detail](Pages/Admin/Work.cshtml.cs),
  [Library](Pages/Admin/Library.cshtml.cs).
- [ShellPreview](Pages/ShellPreview.cshtml.cs):
  Development-only, shares real page definitions without service data.

## Verification and integration gaps

- Frontend UI-key coverage tool: [verify-ui-resources.mjs](Tools/verify-ui-resources.mjs).
- [Asset tests](Tools/asset-manifest.test.mjs) assert hashed Vite entries and SVG sprite.
- Media/request/moderation contracts, Storage/NAS and PIN verification remain pending.
- Admin routing consumes Backend policies: MediaManager reaches manual media/Requests/Wanted; Rules, user limits and Storage remain Owner-only.
- Backend CI builds Frontend assets and runs real Razor/PostgreSQL integration tests;
  browser verification remains a separate gate.
