# Documentation index

One line per document. Start with [AGENTS](../AGENTS.md); feature areas are listed in the [CODEMAP](CODEMAP.md).

## Rules and navigation

- [CODEMAP](CODEMAP.md): one line per owner area with code paths, tests and its area file.
- [Frontend CODEMAP](../src/Jularr.Frontend/CODEMAP.md): frontend folders by area.
- [Conventions](CONVENTIONS.md): C#, structure and ownership, SQL and transactions, tests.
- [Frontend conventions](FRONTEND_CONVENTIONS.md): SSR and htmx, C# components, forms, localization, assets, performance.
- [Frontend UX](FRONTEND_UX.md): design tokens, lists and paging, page identity.
- [Documentation policy](DOCUMENTATION_POLICY.md): where information belongs, size and line limits.
- [Database migrations](DATABASE_MIGRATIONS.md): frozen baseline, numbered migrations, upgrade on start.

## Operations

- [CI](CI.md): CI gates for `main` and `dev`.
- [Release](RELEASE.md): tagging and publishing a release.
- [Local preview](LOCAL_PREVIEW.md): the isolated `main` preview on the owner's machine.
- [Instance setup](INSTANCE_SETUP.md): the first-run setup flow.
- [Storage runtime](STORAGE_RUNTIME.md): storage connections, direct SMB and NAS volumes.
- [VPN network](VPN_NETWORK.md): the optional VPN network path.

## Feature specs

- [Automatic release selection](AUTOMATIC_RELEASE_SELECTION.md): one engine for manual, automatic and Search All release choice.
- [Library Search All](ACQUISITION_SEARCH_ALL.md): searching all or filtered Works from the admin list.
- [Download workspaces](DOWNLOAD_WORKSPACES.md): native download folders and release validation.
- [Torrents](TORRENT_ACQUISITION.md): Nyaa, qBittorrent and Jularr's own engine.
- [Media list import](MEDIA_LIST_IMPORT.md): importing many titles at once.
- [Player design](PLAYER_DESIGN.md): the approved Player design.
- [Reader design](READER_DESIGN.md): the approved Reader design.
- [Speech](SPEECH.md): device speech synthesis and speech packages.
- [App API](APP_API.md): `/api/v1` for native apps.
- [Android clients](ANDROID_CLIENTS.md): Android, Android TV and companion architecture.
- [Phone companion](COMPANION_V1.md): companion protocol and phone remote.
- [Schema snapshot alignment](SCHEMA_SNAPSHOT_ALIGNMENT.md): mapping of the original table snapshot.
- Parity with other apps: [Sonarr](PARITY_SONARR.md), [Radarr](PARITY_RADARR.md), [Lidarr](PARITY_LIDARR.md), [Readarr](PARITY_READARR.md),
  [Prowlarr](PARITY_PROWLARR.md), [Bazarr](PARITY_BAZARR.md), [Seerr](PARITY_SEERR.md).
- Mockups: [downloader tabs](mockups/admin-downloader-tabs/SPEC.md), [profile scoring](mockups/profile-scoring/SPEC.md),
  [work management](mockups/work-management/index.html).

## Measurements

- [Catalog performance](CATALOG_PERFORMANCE.md), [Library performance](LIBRARY_PERFORMANCE.md), [Wanted performance](WANTED_PERFORMANCE.md):
  benchmarks and how to reproduce them.

## History (not current)

- [Archive](archive/CODEMAP_MIGRATION.md): the 2026-11 restructure trace; old plans in `docs/archive/` grant no permission.
