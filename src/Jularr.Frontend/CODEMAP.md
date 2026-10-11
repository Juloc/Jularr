# Frontend CODEMAP

Navigation only; rules: [Frontend conventions](../../docs/FRONTEND_CONVENTIONS.md),
shared C#/backend rules: [CONVENTIONS](../../docs/CONVENTIONS.md).
See [draft PR #960](https://github.com/Juloc/Jularr/pull/960) for not-yet-merged
prepared page definitions referenced below.

## Host, shell and rendering

- [Program.cs](Program.cs): single ASP.NET Core host, Razor registration,
  middleware and routes. Backend DI/API wiring is still an integration gap.
- [Framework/Page.cs](Framework/Page.cs): `JularrPage`, page definition,
  collections, `Panel`, `MasterDetail` and `Tabs`.
- [Framework/PageComponents.cs](Framework/PageComponents.cs): temporary
  presentation records (headings, scopes, media layouts and table headings).
  Not the approved long-term per-component implementation.
- [Framework/BaseField.cs](Framework/BaseField.cs): typed form-field
  selectors and backend `DataField` metadata; form renderer remains incomplete.
- [Framework/AdminNavigation.cs](Framework/AdminNavigation.cs): shared
  Requests / Rules / Users subnavigation.
- [Pages/Shared/_Layout.cshtml](Pages/Shared/_Layout.cshtml):
  document/shell host; [_AppShell](Pages/Shared/_AppShell.cshtml):
  shared page chrome; [_PageBody](Pages/Shared/_PageBody.cshtml):
  renders a page definition; [_UiComponent](Pages/Shared/_UiComponent.cshtml):
  current temporary type-switch renderer.
- [Framework/ShellNavigation.cs](Framework/ShellNavigation.cs): navigation
  catalog; [frontend.css](wwwroot/css/frontend.css): shared layout/theme
  tokens; [theme.js](wwwroot/js/theme.js): small appearance behavior.

## Feature pages

- [Discover/Home](Pages/Index.cshtml.cs) plus
  [Discover redirect](Pages/DiscoverRedirect.cshtml.cs): one discovery surface.
- [Details](Pages/Details.cshtml.cs): canonical consumer detail structure.
- [Player](Pages/Player.cshtml.cs): separate player-only route,
  without live playback binding or Learning overlay.
- [Watchlist](Pages/Watchlist.cshtml.cs): consumer watchlist definition.
- [Settings](Pages/Settings.cshtml.cs): settings categories;
  future integration in the Profile/Account tab shell.
- [Admin Requests](Pages/Admin/Requests.cshtml.cs),
  [Rules](Pages/Admin/Rules.cshtml.cs),
  [Users](Pages/Admin/Users.cshtml.cs):
  moderation and rules/user assignment surfaces.
- [Admin Wanted](Pages/Admin/Wanted.cshtml.cs):
  acquisition worklist shell; actual dialog/service pending.
- [Admin Works](Pages/Admin/Works.cshtml.cs),
  [Work detail](Pages/Admin/Work.cshtml.cs),
  [Library](Pages/Admin/Library.cshtml.cs):
  canonical work, monitoring and local file surfaces.
- [Login](Pages/Account/Login.cshtml.cs): presentation only; auth
  contract/host integration still pending.
- [ShellPreview](Pages/ShellPreview.cshtml.cs): Development-only
  preview of actual page definitions, not a separate demo-screen registry.

## Localization and backend boundary

- [UiText.cs](Framework/UiText.cs),
  [ConsumerSources.cs](Localization/ConsumerSources.cs),
  [AdminSources.cs](Localization/AdminSources.cs):
  temporary display fallbacks; not a second translation database.
- [Backend localization services](../Jularr.Backend/Service/Public/Localization/V1),
  [Backend service runtime](../Jularr.Backend/Service/Core/ServiceRuntime.cs):
  canonical typed service calls and locale pack resolution.
- [Backend CODEMAP](../../docs/CODEMAP.md): service ownership,
  session/permission enforcement, SQL and focused backend tests.

## Verification gaps

Frontend project build, component renderer tests, real browser checks and
service/authorization integration are not yet complete. Backend CI alone
does not validate these pages.
