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
- [UiText.cs](Framework/UiText.cs): per-request render values via Services: cached
  language pack, Public instance name/default locale, User capabilities. Profile
  locale overrides instance default; Backend owns exact/parent/English fallback.
- [Backend locale services](../Jularr.Backend/Service/Public/Localization/V1),
  [Backend resource sync](../Jularr.Backend/Service/Core/Localization/UiLocalizationRegistration.cs):
  only canonical locale/query/DB owners.

## Rendering and reusable components

- [Page.cs](Framework/Page.cs): `JularrPage`, `PreparedPage`,
  `Page`, page definitions and shared service-error feedback.
  Connected pages reuse `LoadDataAsync` for failed POST binding/service redisplay instead of list reads before successful mutations.
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
- [Discover/Index.cshtml.cs](Pages/User/Discover/Index.cshtml.cs): **owns the page composition**
  (enabled media-type navigation, browse navigation, Hero, Continue and
  type-grouped shelves); awaits the authorized instance/profile discovery read.
- [BaseField.cs](Framework/BaseField.cs) and
  [Form.cs](Framework/Components/Form.cs): `Form<TData>` owns label/input
  markup, field errors and local antiforgery POSTs using canonical backend
  `DataField` metadata, including minimum/maximum string lengths. Metadata and property
  getters are cached by property, not compiled per field/row. Unbound forms remain disabled.
- [Shell.cs](Pages/Shared/Shell.cs) and
  [Shell.scss](Pages/Shared/Shell.scss): Jularr-specific Header, Sidebar,
  Mobile navigation and their private helpers in **one** shell owner.
  Watchlist/manual Works navigation follows the verified User capability result.
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
- [Login Page](Pages/Account/Login/Login.cshtml.cs) and
  [Login SCSS](Pages/Account/Login/Login.entry.scss): short declarative Login
  composition using `Form<LoginDataV1>` and shared Brand. Backend
  exposes email/password and configured Plex flows through Public services.
- [Profiles](Pages/Account/Profiles/Profiles.cshtml.cs), [Sessions](Pages/Account/Sessions/Sessions.cshtml.cs),
  [Logout](Pages/Account/Logout/Logout.cshtml.cs): typed service-driven account session flows.
- [AccessDenied](Pages/Account/AccessDenied/AccessDenied.cshtml.cs): sanitized catalog-backed 403 page for denied Razor routes.
- [Setup start](Pages/Setup/Start/Start.cshtml.cs): first Owner through Public services, field-driven form and shared authentication effects. Remaining wizard steps are not connected.
- [Accounts](Pages/Admin/Accounts/Accounts.cshtml.cs): Owner directory/editor, profile assignments and session revocation; separate from request-limit Users.
- [Providers](Pages/Admin/Providers/Providers.cshtml.cs): real provider registry enablement via Owner services.
- [Storage](Pages/Admin/Storage/Storage.cshtml.cs), [Setup Storage](Pages/Setup/Storage/Storage.cshtml.cs): shared Owner-only SMB/NFS/Local editor with Wake-on-LAN, explicit mount/wake/remove actions and the paths-on-this-connection table with safe folder browser, scan and removal. Colocated Storage assets import shared Form/Split/Table styles.
- [Player](Pages/User/Player/Player.cshtml.cs), [UiPlayer](Framework/Components/Player.cs), [player.entry.ts](Modules/Player/player.entry.ts): canonical asset route, server-rendered chrome (-10/+30 transport, timeline, volume, speed, PiP, fullscreen, delivery info) and the client that probes capabilities, requests the PlaybackPlan, resumes and reports progress. [MediaUnitLabel.cs](Framework/MediaUnitLabel.cs) formats structure units for Details and Player.
- [Library](Pages/Admin/Library/Library.cshtml.cs): Owner/MediaManager inventory tabs, manual identification from Backend suggestions and unit rules, and background scan status.
- [Instance](Pages/Admin/Instance/Instance.cshtml.cs): Owner name/language and implemented module editor using shared field-aware forms and versioned services; disable retains data. Requests is a manual slice, not automatic acquisition.
- [Works](Pages/Admin/Works/Works.cshtml.cs), [Work editor](Pages/Admin/Work/Work.cshtml.cs): real paged catalog, manual creation and version-checked title editing through Admin media services; inventory and acquisition controls remain pending.
- [Details](Pages/User/Details/Details.cshtml.cs), [Watchlist](Pages/User/Watchlist/Watchlist.cshtml.cs): persisted Work identity, stored availability, personal list actions and explicit language requests through Services/shared forms. No provider/NAS/playback call.
- [My requests](Pages/User/Requests/Requests.cshtml.cs), [Admin requests](Pages/Admin/Requests/Requests.cshtml.cs), [Wanted](Pages/Admin/Wanted/Wanted.cshtml.cs): actual profile queues, six status tabs, approval/rejection and shared-target pause/resume. [Table.cs](Framework/Components/Table.cs) supports composed row cells using the same shared Form/Button renderer. Automatic search/download/import and unit targets are not connected.
- [Request users](Pages/Admin/Users/Users.cshtml.cs): read-only canonical account directory; account administration links to its existing owner. Quotas/rule assignments are not implemented.
- [Languages](Pages/Admin/Languages/Languages.cshtml.cs): supported locale creation, names/direction/enablement and versioned edits via Owner services.
- [Button.cs](Framework/Components/Button.cs), [Button.scss](Framework/Components/Button.scss): shared button variants/style imported by the shell; Form owns submission/disabled behavior.
- [Localization](Pages/Admin/Localization/Localization.cshtml.cs): Owner catalog list, source descriptions and manual translation forms; versions and field errors come from Backend services.
- [Settings](Pages/User/Settings/Settings.cshtml.cs): active profile name/language editing,
  read-only non-owner view and grant-based account/admin navigation; no page-local SQL or permissions.
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
  [Discover CSS entry](Pages/User/Discover/Index.entry.scss),
  [Settings CSS entry](Pages/User/Settings/Settings.entry.scss),
  [Player entry](Modules/Player/player.entry.ts): currently declared
  page/module assets; component Sass imported only by their owners.
- [SVG sources](Assets/Icons), [SVG sprite build](Tools/build-icons.mjs):
  deduplicated hashed sprite, with icons also allowed beside modules/pages.
- [MediaImage.cs](Framework/Components/MediaImage.cs): accessible lazy or
  priority image markup and optional responsive variants; Backend owns image
  resizing, ETag/cache, permissions and thumbnail generation.

## Pages

Areas contain one folder per page, with its Razor entry, C# definition and
owned assets together. Public URLs remain explicit and independent of folders.
Shared Razor bridges remain in `Pages/Shared`; optional module assets remain
in `Modules/<Module>/`.

- [Discover](Pages/User/Discover/Index.cshtml.cs) and
  [Discover redirect](Pages/User/DiscoverRedirect/DiscoverRedirect.cshtml.cs).
- [Details](Pages/User/Details/Details.cshtml.cs),
  [Player](Pages/User/Player/Player.cshtml.cs),
  [Watchlist](Pages/User/Watchlist/Watchlist.cshtml.cs),
  [Settings](Pages/User/Settings/Settings.cshtml.cs),
  [Login](Pages/Account/Login/Login.cshtml.cs).
- [Requests](Pages/Admin/Requests/Requests.cshtml.cs),
  [Rules](Pages/Admin/Rules/Rules.cshtml.cs),
  [Users](Pages/Admin/Users/Users.cshtml.cs),
  [Wanted](Pages/Admin/Wanted/Wanted.cshtml.cs),
  [Works](Pages/Admin/Works/Works.cshtml.cs),
  [Work detail](Pages/Admin/Work/Work.cshtml.cs),
  [Library](Pages/Admin/Library/Library.cshtml.cs).
- [ShellPreview](Pages/Development/ShellPreview/ShellPreview.cshtml.cs):
  Development-only, shares real page definitions without service data.

## Verification and integration gaps

- Frontend UI-key coverage tool: [verify-ui-resources.mjs](Tools/verify-ui-resources.mjs).
- [Asset tests](Tools/asset-manifest.test.mjs) assert hashed Vite entries and SVG sprite.
- Module/profile visibility, localized metadata policy, complete Requests/Wanted lifecycle, quota/rules, NAS health/reconnect/Synology and PIN verification remain pending.
- Admin routing consumes Backend policies: MediaManager reaches manual media, Library identification and Requests/Wanted; Rules, user limits and Storage remain Owner-only.
- Backend CI builds Frontend assets and runs real Razor/PostgreSQL integration tests;
  browser verification remains a separate gate.
