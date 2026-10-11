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
  snapshot via `ReadUiLocalesV1` and `ReadUiTextsV1`.
- [Backend locale services](../Jularr.Backend/Service/Public/Localization/V1),
  [Backend resource sync](../Jularr.Backend/Service/Core/Localization/UiLocalizationRegistration.cs):
  only canonical locale/query/DB owners.

## Rendering and reusable components

- [Page.cs](Framework/Page.cs): `JularrPage`, `PreparedPage`,
  `Page` and page definition collection.
- [UiComponent.cs](Framework/UiComponent.cs): component-to-Razor HTML boundary,
  interface and encoder wiring, **no type switch**.
- [Elements.cs](Framework/Elements.cs): minimal encoded C# HTML DSL;
  `Div`, `H1`, `Text`, `Button` and related small primitives.
- [Components](Framework/Components): component-owned HTML for typed tables,
  navigation, panels, master/detail, tabs, settings, forms and Player.
- [NavigationTabs.cs](Framework/Components/NavigationTabs.cs):
  reusable, localized accessible navigation tabs driven by supplied links.
- [MediaShelf.cs](Framework/Components/MediaShelf.cs):
  reusable media shelf with truthful title, optional browse link and real cards.
- [Discover/Index.cshtml.cs](Pages/Index.cshtml.cs): **owns the page composition**
  (enabled media-type navigation, browse navigation, Hero, Continue and
  type-grouped shelves); awaits the authorized instance/profile discovery read.
- [BaseField.cs](Framework/BaseField.cs),
  [Form.cs](Framework/Components/Form.cs): typed backend DataField metadata
  and its display-only form contract. No invented save operation.
- [ShellNavigation.cs](Framework/ShellNavigation.cs),
  [AdminNavigation.cs](Framework/AdminNavigation.cs):
  declared navigation destinations.
- [Shared layout](Pages/Shared/_Layout.cshtml),
  [App shell](Pages/Shared/_AppShell.cshtml),
  [Page body](Pages/Shared/_PageBody.cshtml),
  [Ui component boundary](Pages/Shared/_UiComponent.cshtml):
  Razor shell and C# component rendering.
- [frontend.css](wwwroot/css/frontend.css) and
  [theme.js](wwwroot/js/theme.js): presentation and minimal appearance logic.

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
- Backend service contracts for list data, forms, request/moderation,
  media, playback and genuine admin authorization remain pending.
- Frontend compile/browser validation must be performed separately from
  the current Backend-only test workflow.
