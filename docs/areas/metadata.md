# Metadata providers and provider catalog

The provider catalog, caching tiers, metadata fill, TMDB and TVDB.
Index: [CODEMAP](../CODEMAP.md).
Specs: [CATALOG_PERFORMANCE](../CATALOG_PERFORMANCE.md).

## Backend

- Provider catalog (the Caching module decides for the whole instance, `ExternalWorkLogic.ReadCachingTierAsync`/`CatalogTier`,
  [0022](../../database/migrations/0022_catalog_cache_tiers.sql): module `DiscoverCache` off is the lean tier, which asks the provider each time and
  keeps no lists, but every title it shows still becomes a catalog Work so its card opens `/Details/{id}`, `MetadataCatalog.KeepWorksAsync`; on keeps
  Discover's lists; with its sub-option `LocalDiscovery` a catalog with a full list (`ExternalWorkLogic.FullSyncCatalogs`) is kept whole, the others
  keep their lists): [ExternalWorkLogic.cs](../../src/Jularr.Backend/Logic/Media/ExternalWorkLogic.cs) keeps every title Discover loaded as a catalog
  Work (`Works` with `CatalogNamespace`, its identity in `WorkExternalIdentities`, no details until `DetailedAt`;
  [0020](../../database/migrations/0020_merge_external_works.sql)) without losing known facts; an AniList anime entry that is a season of a series is
  that series with the season (feed items keep `WorkId` and `SeasonNumber`); `WorkLibraryRelations` names what ties a Work to the library (a file
  record also while its storage is offline, a request, wanted entry, favorite, collection, progress, AniList list entry, Plex, and a person's edits:
  manual titles, facts, identities, subtypes, series, relations, hidden relations, episode groups and season mappings), a list or search never renames
  a Work with such a tie or with details, and only an undetailed Work without a tie or file record leaves when its provider drops it; TheTVDB's search
  and TMDB's series lists carry the series' IMDb and TMDB ids, so a library series that lacks its TheTVDB id takes it instead of a twin; the first
  trending page of each catalog and type comes from it for six hours and while the provider cannot answer (`DiscoverTests.Catalog_*`).
  With the `LocalDiscovery` module ([ExternalWorkSyncServices.cs](../../src/Jularr.Backend/Service/System/Media/V1/ExternalWorkSyncServices.cs),
  [CatalogWorker](../../src/Jularr.Workers/CatalogWorker.cs), every minute) TMDB's films are kept whole (series come from TheTVDB, which has no full
  list; [0023](../../database/migrations/0023_series_from_tvdb.sql) dropped the TMDB series catalog and its catalog-only titles; TMDB's series lists
  and recommendations give only the order: each entry becomes the TheTVDB series TMDB links, `WorkMetadata.ExternalWorksAsync`, keeping TMDB's id as
  the Work's `tmdb_tv` identity so the link is asked once, `ExternalFeedLogic.CatalogOf`; an entry without a TheTVDB id is left out,
  `DiscoverTests.Series_*`): the daily public id list (after 09:00 UTC, no adult titles or video extras) adds titles, `/changes` marks changed ones
  every six hours, and each pass reads the next 240 entries by popularity (one request each, with the streaming services appended), four at a time,
  outside the write batches (`ExternalWorkSyncTests`).
  TheTVDB's catalog card (`tvdb` row of [0024](../../database/migrations/0024_anime_mappings_tvdb_budget.sql)) has no full list and is never kept
  whole (`MaySync`); its requests per minute and pause budget the details ahead of TMDB's series lists.
  The Owner's choices per catalog (`ExternalWorkSyncs`, windows in `ExternalWorkSyncWindows`,
  [ExternalWorkSyncServices.cs](../../src/Jularr.Backend/Service/Admin/Media/V1/ExternalWorkSyncServices.cs), which also reports each catalog's tier):
  requests per minute as the pass budget, pause while Jularr is used ([UserActivity.cs](../../src/Jularr.Backend/Service/Core/UserActivity.cs): a
  signed-in request within five minutes) and windows in the server's local time, also past midnight.
  AniList has no id list and pages no deeper than 5000 entries: a pass asks for the next 50 ids per request (adult titles included and marked
  `IsAdult`) up to its budget, continues at `Cursor`, ends at AniList's newest id and starts again a week after a complete walk; anime
  TV/ONA/OVA/specials/music videos become series, films films, manga and novels their Book subtypes (`AniListMatching.CatalogType`).
  Both passes also keep what Discover filters by on the Work ([0037](../../database/migrations/0037_catalog_filter_facts.sql), values of
  [CatalogFacts.cs](../../src/Jularr.Backend/Data/Enums/Media/CatalogFacts.cs), mapped by
  [CatalogFactValues.cs](../../src/Jularr.Backend/Logic/Media/CatalogFactValues.cs)): format, status, source, country of origin,
  episodes/chapters/volumes, duration, genres, AniList's tag ids and the streaming/reading site ids (AniList's links, TMDB's flat-rate services in the
  language's country) from the same requests.
  Once a catalog was read whole (`CompletedAt`), it answers all of Discover's shelves in one statement (`ReadDiscoverShelvesV1`: trending in order,
  top and top 100 by rating among titles with 300 votes or more, new as the most popular of the last 60 days, upcoming of the next 180, AniList's
  season lists, all-time popular and manhwa by popularity from the season, favourites and country its pass keeps,
  [0036](../../database/migrations/0036_anilist_discover_lists.sql)) and every browse page, Discover's filtered grid too (`ReadDiscoverFilteredV1` in
  [DiscoverFilterServices.cs](../../src/Jularr.Backend/Service/User/Discover/V1/DiscoverFilterServices.cs): the same statement narrowed by the facts
  of 0037, anime's films and series as one scope; without the whole catalog AniList, TMDB's discover lists or Open Library answer the same choice,
  [DiscoverFilterScopes.cs](../../src/Jularr.Backend/Logic/Media/DiscoverFilterScopes.cs) names each type's filters and translates them), also in
  another order (`DiscoverSort`, `ExternalWorkLogic.ReadExternalWorkShelvesAsync`; without the whole catalog AniList sorts live,
  `MetadataCatalog.ReadFeedAsync`, `DiscoverTests.Catalog_AniListLists_*`, `Catalog_ReadWholeAniList_*`); suggestions while typing
  (`SuggestDiscoverV1`) search what `DiscoverCache` keeps by title prefix: the profile's Works first, then trending, title beginnings and popularity
  within each catalog, a title with one IMDb id once (`DiscoverTests.Catalog_ReadWhole_*`, `DiscoverCatalogTests`).
  The daily `catalogCleanup` job ([CatalogCleanupServices.cs](../../src/Jularr.Backend/Service/System/Media/V1/CatalogCleanupServices.cs),
  [CatalogCleanupLogic.cs](../../src/Jularr.Backend/Logic/Media/CatalogCleanupLogic.cs), batches of 1000, `CatalogCleanupTests`) drops the details of
  catalog Works without a library tie, a file record, a trending place, a place in any Discover list while `DetailsDiscoverAhead` is on (both only
  while Discover keeps its lists), or a recent opening (`CatalogDetailKeepDays`), then deletes their rows in catalogs not kept whole (those with sync
  settings, TheTVDB's series and those left of TMDB's series catalog) unless a feed or a relation of a detailed or tied Work names them.
  The Admin Works list and its summary show only Works with a library tie; `WorkLibraryRelations` reads its Plex/manual identities and manual
  classifications through partial indexes ([0021](../../database/migrations/0021_library_tie_indexes.sql)); System health counts catalog-only Works
  apart.
  The Caching module's settings (`InstanceSettings.CatalogDetailKeepDays`, `DetailsDiscoverAhead`: the feed refresh details every Discover list's
  titles through `MetadataCatalog.DetailAsync` within each catalog's budget and pause, `DiscoverTests.FeedRefresh_*`) are read by
  `ReadCachingSettingsV1` and saved with the module switches (`UpdateInstanceModulesV1`, `SaveSetupModulesV1` through
  `InstanceModuleLogic.UpdateInstanceModulesAsync`, `InstanceModuleDependencyTests.Caching_*`).
- Titles without facts: a media server's sync creates Works with title, year and ids only.
  The `metadataFill` job (`FillWorkMetadataV1` in [MetadataFillServices.cs](../../src/Jularr.Backend/Service/System/Media/V1/MetadataFillServices.cs),
  run by `MediaWorker`, three at a time with a pause, paused while Jularr is used, run-now on the Jobs page) refreshes every Work with details or a
  library tie (`WorkLibraryRelations`; a catalog title nobody opened is left alone, a requested one is filled) that has a catalog identity but no
  details (a library title, such as every library series after 0023 until TheTVDB described it), neither an Overview nor a Poster fact, or was opened
  while its details were more than a day old (`DetailedAt < ViewedAt - 1 day`), like the Work page's Refresh (a series by its TheTVDB, TMDB or IMDb
  id), finds a film known only by its IMDb id through TMDB first, and tries a title once a day (`Works.MetadataTriedAt`, whatever the answer, so
  titles the catalog cannot give never hold back the others).
  Only Works with an identity their type's catalog reads count, a few of each type in turns; a catalog that is not there holds back only its own kind
  of title, and series start only while TheTVDB's requests a minute (its catalog card) leave room.
  Migration 0019 marks existing Works as detailed and opened at the same time, so the upgrade reads no library again; 0023 makes the library's series
  due on purpose.
  Tests `MetadataFillTests`.
- Film and series metadata: [TmdbClient.cs](../../src/Jularr.Integrations/Logic/Tmdb/TmdbClient.cs),
  [WorkMetadata.cs](../../src/Jularr.Backend/Service/Admin/Media/V1/WorkMetadata.cs) (key from the Providers registry, instance language; films from
  TMDB; series are searched at TheTVDB, `SearchSeriesAsync` for Discover, the user search, Admin add and the scan, takeover and anime fallbacks, and
  without its key the search says `tvdb_key_missing` while films still work; a series from TheTVDB with provider key `tvdb` (`ImportSeriesAsync`:
  texts in the instance language, else English, else the original, every language's title and summary, best artwork by language and score, people,
  genres, networks, trailer, seasons and episodes with summary and image, anime by TheTVDB's genre or Japanese animation for a new title only, a
  refresh never changes a subtype; people TheTVDB credits are looked up once by `people/{id}/extended` within TheTVDB's budget and join the person
  TMDB or IMDb already knows by the same id, `WorkPeopleLogic.LinkProviderPeopleAsync`), keyed on its `tvdb` identity and reached from `tmdb_tv`
  through TMDB's external ids or from `imdb` through `search/remoteid`; `tmdb_tv` stays as an extra identity for TMDB's rating, the only thing still
  read from TMDB; without TheTVDB nothing changes and a first open marks the details unavailable), [metadata
  services](../../src/Jularr.Backend/Service/Admin/Media/V1/WorkMetadataServices.cs) (search, import, refresh, re-match to another entry of the type's
  provider keeping files and manual edits; results name their provider, Manga and Light Novels search Open Library and AniList) and
  [WorkMetadataLogic.cs](../../src/Jularr.Backend/Logic/Media/WorkMetadataLogic.cs) (identities, provider titles and facts, seasons and episodes; a
  Book of the same subtype, title and year from another provider is the same Work; a catalog title without a tie that holds one of a film's or series'
  ids goes into the library Work, `MergeCatalogTwinsAsync` over `IdentityMatchLogic.MergeWorksIntoAsync`, while two library Works stay apart); wanted
  series refresh daily.
  `SeriesCatalogTests`.
- TVDB: [TvdbClient.cs](../../src/Jularr.Integrations/Logic/Tvdb/TvdbClient.cs) reads TVDB v4 (`series/{id}/extended?meta=translations&short=false`,
  episodes in the default order and per language, `search?type=series`) and validates IMDb lookup;
  [TvdbMetadata.cs](../../src/Jularr.Backend/Service/Admin/Media/V1/TvdbMetadata.cs) reads the encrypted provider key, keeps one session per key for
  most of TheTVDB's month and replaces a refused one once, and counts every request against the minute budget background work keeps to
  (`TvdbConnectionState`); [TvdbSeriesPreviewServices.cs](../../src/Jularr.Backend/Service/Admin/Media/V1/TvdbSeriesPreviewServices.cs) exposes an
  Owner/MediaManager-only read-only episode comparison (`new`, `review`, `local_only`) with English episode titles like Sonarr, else the original
  name.
  It never changes WorkUnits, files, IDs or progress.
  The provider registry seed in [Account.sql](../../database/Account.sql) applies only to fresh installs, not existing-database migrations.
  Focused tests: `TvdbTests.cs` and `TvdbSeriesPreviewTests.cs`.

## Frontend

- [Discover catalog](../../src/Jularr.Frontend/Pages/Admin/Catalog/Catalog.cshtml.cs) (`/Admin/Catalog`, a page of the Integrations entry): figures,
  then each kept catalog as cards (how the Caching module keeps it: live, kept, whole; titles, pending, last complete pass, state) with requests per
  minute, pause while Jularr is used and the sync windows as inputs in the card, saved by the page's pill; Sync now in the dock starts the shared
  `catalog` job; the days and size of the cache of images of titles that are not Works stay a pill-saved form (`CatalogSettingsTests`,
  `ExternalWorkSyncTests.CatalogPage_*`).
