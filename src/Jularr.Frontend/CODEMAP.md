# Frontend CODEMAP

Navigation only. Rules: [Frontend conventions](../../docs/FRONTEND_CONVENTIONS.md),
[shared conventions](../../docs/CONVENTIONS.md). Backend owners: [CODEMAP](../../docs/CODEMAP.md).

## Host and framework

- [Program.cs](Program.cs): single host, service registration, authorization, request culture and Razor/API routing.
- [UiText.cs](Framework/UiText.cs), [UiResources.cs](Localization/UiResources.cs): service-backed request language pack and English registration inputs.
- [Page.cs](Framework/Page.cs): page definitions, shared read errors and mutation redisplay.
- [Elements.cs](Framework/Elements.cs), [UiComponent.cs](Framework/UiComponent.cs): encoded HTML primitives and Razor boundary.
- [Form.cs](Framework/Components/Form.cs), [BaseField.cs](Framework/BaseField.cs): canonical Backend metadata, invariant form binding, antiforgery and field/form errors.
- [Table.cs](Framework/Components/Table.cs): typed rows and composed cells.
- [Button.cs](Framework/Components/Button.cs), [Tag.cs](Framework/Components/Tag.cs), [Toggle.cs](Framework/Components/Toggle.cs): shared controls.
- [Split.cs](Framework/Components/Split.cs), [Tabs.cs](Framework/Components/Tabs.cs), [NavigationTabs.cs](Framework/Components/NavigationTabs.cs): layout and route-backed navigation.
- [Shell.cs](Pages/Shared/Shell.cs), [Brand.cs](Pages/Shared/Brand.cs): capability-driven application chrome and branding.
- [Shared layout](Pages/Shared/_Layout.cshtml), [page body](Pages/Shared/_PageBody.cshtml): thin Razor bridges.
- [FrontendAssets.cs](Framework/FrontendAssets.cs), [Vite](vite.config.ts), [build tools](Tools): fingerprinted, entry-scoped assets and SVG sprite.
- [Shared SCSS](Assets/Shared/shell.entry.scss): colocated base component styles loaded once.
- [Appearance](Modules/Appearance/Appearance.cs), [palette](Modules/Appearance/Palette.ts), [theme client](Modules/Appearance/theme.entry.ts): Original/Clean, brightness and optional device preferences.
- [MediaCard](Framework/Components/MediaCard.cs), [MediaHero](Framework/Components/MediaHero.cs), [MediaShelf](Framework/Components/MediaShelf.cs), [MediaImage](Framework/Components/MediaImage.cs): shared media presentation; Backend owns image processing.

## User and account pages

- [Discover](Pages/User/Discover/Index.cshtml.cs), [client](Pages/User/Discover/Index.entry.ts): local-first landing, recommendation HTML fragments, debounced search and progressive result paging.
- [Provider title](Pages/User/DiscoverItem/DiscoverItem.cshtml.cs): TMDB title and authorized request before canonical Work creation.
- [Library](Pages/User/Library/Library.cshtml.cs), [Watchlist](Pages/User/Watchlist/Watchlist.cshtml.cs): real bounded catalogs.
- [Details](Pages/User/Details/Details.cshtml.cs): Work, paged versions, personal actions and capability-based Play/Read links.
- [Player](Pages/User/Player/Player.cshtml.cs), [chrome](Framework/Components/Player.cs), [client](Modules/Player/player.entry.ts): PlaybackPlan, transport, HLS and progress.
- [Reader](Pages/User/Reader/Reader.cshtml.cs), [client](Modules/Reader/reader.entry.ts): sanitized sections, device settings, semantic locators and end-of-content completion.
- [Progress transport](Modules/Tracking/Progress.ts): serialized acknowledged writes, finite transient retries and visible unsaved state for Reader/Player.
- [Requests](Pages/User/Requests/Requests.cshtml.cs): profile queue and authorized source confirmation.
- [Settings](Pages/User/Settings/Settings.cshtml.cs): profile settings, appearance and granted administration links.
- [Login](Pages/Account/Login/Login.cshtml.cs), [Profiles](Pages/Account/Profiles/Profiles.cshtml.cs), [Sessions](Pages/Account/Sessions/Sessions.cshtml.cs), [Logout](Pages/Account/Logout/Logout.cshtml.cs): canonical service-driven authentication flows.
- [AccessDenied](Pages/Account/AccessDenied/AccessDenied.cshtml.cs): catalog-backed 403.
- [Setup](Pages/Setup/Start/Start.cshtml.cs), [Setup Storage](Pages/Setup/Storage/Storage.cshtml.cs): first Owner and shared storage editor.

## Admin pages

- [Accounts](Pages/Admin/Accounts/Accounts.cshtml.cs): roles, profile assignments and sessions.
- [Instance](Pages/Admin/Instance/Instance.cshtml.cs): supported settings and module controls.
- [Storage](Pages/Admin/Storage/Storage.cshtml.cs): mounts, WOL, safe browsing and independently paged library paths.
- [Providers](Pages/Admin/Providers/Providers.cshtml.cs): registry, protected TMDB/IGDB credentials and Plex server.
- [Works](Pages/Admin/Works/Works.cshtml.cs), [Work](Pages/Admin/Work/Work.cshtml.cs), [Library inventory](Pages/Admin/Library/Library.cshtml.cs): metadata, editing and file identification.
- [Requests](Pages/Admin/Requests/Requests.cshtml.cs), [Wanted](Pages/Admin/Wanted/Wanted.cshtml.cs), [Activity](Pages/Admin/Activity/Activity.cshtml.cs): moderation, acquisition and recovery.
- [Acquisition](Pages/Admin/Acquisition/Acquisition.cshtml.cs), [Sources](Pages/Admin/Sources/Sources.cshtml.cs), [Quality profiles](Pages/Admin/Profiles/Profiles.cshtml.cs): connection/catalog/profile configuration.
- [Takeover](Pages/Admin/Takeover/Takeover.cshtml.cs): one-time Prowlarr/Sonarr/Radarr preview and import.
- [Localization](Pages/Admin/Localization/Localization.cshtml.cs), [Languages](Pages/Admin/Languages/Languages.cshtml.cs): DB descriptions, translations and locale administration.
- [Request users](Pages/Admin/Users/Users.cshtml.cs), [Rules](Pages/Admin/Rules/Rules.cshtml.cs): directory and currently unconnected rule editing.

## Verification and open work

- [Asset tests](Tools/asset-manifest.test.mjs), [appearance tests](Tools/appearance.test.mjs), [structure tests](Tools/shell-login.test.mjs).
- [Discover tests](Tools/discover.test.mjs), [progress tests](Tools/progress.test.mjs), [Reader/Player tests](Tools/reader.test.mjs), [UI-key verification](Tools/verify-ui-resources.mjs).
- Backend tests: FrontendAuthenticationTests, DiscoverTests, MediaTests, LibraryTests, ReadingTests and PlaybackTests; isolated PostgreSQL only.
- [Design lab](Pages/Development/Design/Design.cshtml.cs), [ShellPreview](Pages/Development/ShellPreview/ShellPreview.cshtml.cs): Development-only; browser verification is separate.
- Remaining: persistent shell/miniplayer, previous/next playback targets, AniList Discover, provider facets, complete episode details, profile media visibility/order, metadata-locale policy, full Setup/PIN/quota/rules.
- [Local preview](../../docs/LOCAL_PREVIEW.md); feature scope and Backend integration gaps: [BACKEND_NEXT](../../docs/BACKEND_NEXT.md).
