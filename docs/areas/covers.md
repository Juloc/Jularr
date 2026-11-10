# Covers and missing images

Cover, backdrop and photo storage and delivery, and the NoCover fallback.
Index: [CODEMAP](../CODEMAP.md).

## Decisions

Owner decisions; change them only with the owner.

- A missing poster, backdrop, cover or photo is always `NoCover` (the Jularr mark with a question mark, a silhouette for a person) through
  `MediaImage.Or`; a page never draws its own initials, numbers or gradient tile, and an image that fails to load is swapped for it by the layout
  script.

## Backend

- Covers like Sonarr's MediaCover: [CoverStore.cs](../../src/Jularr.Backend/Service/Core/CoverStore.cs) owns both stores under `<data>/covers`
  (`Media:CoversPath`, no new volume).
  A Work's poster and backdrop, a season's poster and an episode's still are fetched once when its metadata is saved (`ImportAsync` of TMDB, AniList,
  Open Library, MusicBrainz and IGDB; Hardcover gives no image) or by the `StoreCoversV1` pass of `MediaWorker` (backfill, retries after a day,
  units), kept as downloaded (signature, 20 MB, HTTPS to the providers' image hosts only (TMDB, TheTVDB's `artworks.thetvdb.com`, AniList, IGDB, Open
  Library, Cover Art Archive), public addresses only, redirects checked by [CoverClient.cs](../../src/Jularr.Integrations/Logic/Media/CoverClient.cs);
  `Providers:Images:<host>` names a mirror) with each width 250/500 (posters), 500/1280 (backdrops), 250 (units) kept in ONE format, the Owner's
  `CoverCacheSettings.FormatId` (JPEG XL by default or WebP, chosen on `/Admin/Catalog`; no export of covers to a folder or NAS exists, Plex keeps its
  own artwork): ffmpeg scales, the image's `cjxl` ([JxlEncoder.cs](../../src/Jularr.Integrations/Logic/Media/JxlEncoder.cs), distance 1.0, effort 7,
  progressive, from a lossless scaled copy; `Media:CjxlPath`, checked once at start by
  [JxlStartupCheck.cs](../../src/Jularr.Backend/Service/Core/JxlStartupCheck.cs) and shown on the Admin System tools table) makes the JPEG XL, and
  without it WebP is kept and the setting says so; a width no tool can make in the format is made in the next one (WebP, JPEG).
  A library book that no provider names a poster for (no identity, so no lookup) takes the cover of its own file: `StoreBookFileCoversV1` (same
  `covers` job, 5 books per pass; [BookCoverLogic.cs](../../src/Jularr.Backend/Logic/Reading/BookCoverLogic.cs) reads the EPUB's cover-image/cover
  item or a CBZ's first page) stores it with `CoverStore.StoreFileAsync` as the Work's poster fact of its kind's provider (AniList for manga and light
  novels, else Open Library), so that provider's next refresh replaces it; a book without one rests a week (`WorkCovers` failure row, source
  `jularr:book-file`).
  The `coverBackfill` job (`ConvertCoversV1` in [CoverServices.cs](../../src/Jularr.Backend/Service/System/Media/V1/CoverServices.cs) run by
  `MediaWorker`, a few images per pass, paused while Jularr is used, run-now on the Jobs page) converts stored covers whose `WorkCovers.FormatId`
  differs from the effective format (none: stored before) and the cache of other titles; a new file is written and its first bytes checked before the
  other formats of that width leave, and the download is never removed.
  [WorkCoverLogic.cs](../../src/Jularr.Backend/Logic/Media/WorkCoverLogic.cs) / `WorkCovers` ([Media.sql](../../database/Media.sql)) remember the
  provider address: the `Poster`/`Backdrop` facts and `WorkUnits.ImageUrl` hold the local address
  `/api/v1/covers/{workId}/{poster|backdrop|unit-N}[-width]?v=` once stored, so every query, page and app already uses it; a changed address is
  fetched again, a failure waits a day, the facts keep the provider address meanwhile.
  [CoverPaths.cs](../../src/Jularr.Backend/Data/Common/CoverPaths.cs) is the one naming and format rule and, with `MediaImage`, rewrites every
  provider image of a page to `/api/v1/covers/external?u=&w=`, the cache for titles that are not Works
  ([CoverServices.cs](../../src/Jularr.Backend/Service/User/Media/V1/CoverServices.cs): fetched on first look, every serve renews its use;
  `CoverCacheSettings` days and size are edited on `/Admin/Catalog`, `CleanCoversV1` of `MaintenanceWorker` trims least recently used and expired
  images and sweeps files of deleted Works; `DeleteWorkV1` removes a Work's folder at once).
  Served by Jularr only (profile access through `ProfileWorks`, `ETag`, `Last-Modified`, `Vary: Accept`; `CoverPaths.Accepts` reads the formats a
  browser names, `CoverPaths.Serve` gives the stored file when it is one of them, else `CoverStore.FindAsync` makes a WebP (named) or JPEG from the
  download for that answer alone, kept nowhere, two at a time; versioned addresses immutable); the web UI never loads a provider image, the app API
  still returns provider addresses for titles that are not Works (apps use `/api/v1/covers/external`).
  JPEG XL is not built: the image's ffmpeg has no libjxl encoder.
  Tests `CoverTests` (`JULARR_TEST_FFMPEG`, `JULARR_TEST_CJXL` for the real conversions), E2E `WorkPageTests`.

## Frontend

- [NoCover](../../src/Jularr.Frontend/Framework/Components/NoCover.cs) (styles
  [NoCover.scss](../../src/Jularr.Frontend/Framework/Components/NoCover.scss), icons `no-cover`, `no-person`): the one stand-in for a missing poster,
  cover, backdrop or photo, decided in `MediaImage.Or` (no address) and by
  [MediaImage.entry.ts](../../src/Jularr.Frontend/Framework/Components/MediaImage.entry.ts) (an image that fails to load, swapped from the layout's
  template); `ErrorPageTests.TitleWithoutCover_*`.
