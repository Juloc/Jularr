# Discover, home and recommendations

Discover feeds and rankings, filters, the home layout and recommendations.
Index: [CODEMAP](../CODEMAP.md).
Specs: [CATALOG_PERFORMANCE](../CATALOG_PERFORMANCE.md), [PARITY_SEERR](../PARITY_SEERR.md).

## Decisions

Owner decisions; change them only with the owner.

- **Discover/Home at `/` is capability-driven.** Media-type tabs are the intersection of enabled instance modules, active-profile visibility/policy
  and available discovery capabilities.
  A disabled/hidden type must disappear from the media-type bar, its browse views, Hero candidates and shelves.
  `All` contains only eligible types and cannot silently include disabled provider results.
  Books, Light Novels and Manga remain distinct.
  Every type stands strictly apart, in the type filter, the Continue shelves, Recently added and the highlights: series without anime, books without
  manga and light novels, and a Work belongs to the type of its subtype (an anime film is anime).
- Discover separates media-type scope, browse view and filters.
  Its All landing has a shared Hero/Continue followed by per-enabled-type shelves in the approved order; only provider-supported rankings may appear.
  A ranking opens from its shelf's See all and names itself with a way back.
  Every ranking loads on while scrolling, its See all grid and its shelf alike, 20 titles (the provider's next page) at a time; a page goes on while
  it has titles, three pages in a row that add nothing stop the automatic loading (the grid then shows *Load more*), and both say *No more titles.*
  where the ranking ends.
  While a page loads, grey skeleton cards stand where it arrives (a row under the grid, three at the end of a shelf); no Next button shows while pages
  load by themselves, and a failed page offers *Try again*.
  The shelves themselves arrive the same way, four at a time (about two screens): the first ones right after the page, the next ones as the page nears
  them, with a row of grey cards where they arrive; each type is named once, above its first shelf, and a failed part offers *Try again*.
  On a phone (up to 760 px) the shelves have no arrows (rows are swiped); a shelf's title keeps the rest of its row beside *See all*, wraps between
  words, hyphenates a long word and otherwise ends in "…", never under the link.
  Anime, manga and light novels have AniList's lists (anime: Trending, Popular this season, Upcoming next season, All time popular, Top 100; manga and
  light novels: Trending, All time popular, Popular manhwa (manga only, Korea), Top 100); films, series and books keep theirs.
  Only a ranking's grid has a sort: AniList's orders (trending, popularity, average score, favorites, date added, release date, title) for those
  types, for other types only what Jularr's whole catalog holds (popularity, score, release date, title), none otherwise; a ranking opens in its own
  order (trending, score for Top 100, else popularity) and shelves keep it.
  One capsule row (All, then Watch, Read, Hear and Game with their types; several types may be chosen, `type=a,b`) with the Customize and Filter
  drawers at its end, the highlights as one carousel that always keeps its place (with nothing to show, the shared landscape and *No highlights yet*):
  for each chosen type its continued titles, then its best by rank (series from Discover's series list), then what the library added last, each title
  once; the types take turns place by place, and a type with fewer titles shows fewer.
  The device keeps how many per type (1–5, standard 3; no overall maximum), whether continued titles count and whether it turns by itself), feed chips
  and a rows/grid switch.
  The header owns the **one** global Search; do not add another search field inside Discover.
- Under the media-type bar a filter row as AniList's search has it: Genres, Year, Season (anime) and Format at hand, More opens the Filter drawer with
  the others the chosen type's catalog can answer (status, country with Manhwa/Manhua, source, AniList's tags by category, streaming or reading sites,
  ranges of years, episodes, chapters, volumes and minutes, language for books, adult titles as a switch for every profile; `DiscoverFilterOptionsV1`
  names them per type).
  Several types share only genre and year.
  A popup applies its choices when it closes, a select at once; on a phone the row swipes sideways and a popup is a sheet.
  Any filter (not the type alone) replaces the feed chips and shelves with one grid of the chosen types' titles under the highlights, popularity
  first, with the orders the type offers, loading on while scrolling; with Jularr holding the whole catalog (`LocalDiscovery`: AniList, TMDB's films)
  the grid is read locally, otherwise from the provider (AniList, TMDB's discover lists, Open Library).
  The Filter drawer lists the media types first, then the local availability, the filters above, then only the rankings the chosen types have; the
  rankings a chosen type lacks leave the panel as the types change.
- Cards use the shared cover presentation and Backend-supplied state below.
  User/profile ordering is respected.
  Backend supplies effective capability/order/provider data; do not invent a new module toggle in Frontend.
- With the Caching module (`DiscoverCache`) on, public Discover feeds and TMDB recommendations persist their order as references to catalog `Works`
  (an anime season as its series with the season, a TMDB series as its TheTVDB series).
  Reads serve existing lists without checking age; `RefreshDiscoverFeedsV1` owns renewal and prepares standard feeds and known Works' recommendations,
  and with `InstanceSettings.DetailsDiscoverAhead` it then details the titles of every Discover list (not recommendations) through
  `MetadataCatalog.DetailAsync`, within each catalog's requests per minute and its pause while Jularr is used (TMDB's series lists within TheTVDB's).
  Only a never-filled list waits for a bounded initial fill; concurrent fills share the provider gate.
  Failed or empty refreshes retain a usable previous list and ask that list again after 15 minutes; only an unavailable provider fails the pass, an
  empty answer does not.
  Viewer state is resolved on each read.
  With the module off the existing entry point selects live provider reads and never enters the cache path; the titles shown still become catalog
  Works, since every card is a Work.

## Backend

- Persistent public Discover lists: [ExternalFeedCache.cs](../../src/Jularr.Backend/Service/Core/ExternalFeedCache.cs),
  [ExternalFeedLogic.cs](../../src/Jularr.Backend/Logic/Media/ExternalFeedLogic.cs), [refresh
  service](../../src/Jularr.Backend/Service/System/Media/V1/ExternalFeedRefreshServices.cs) and `discoverFeeds` in
  [CatalogWorker.cs](../../src/Jularr.Workers/CatalogWorker.cs); tests `ExternalFeedTests` and `DiscoverTests.CacheWorker_*`.
- Home layout per profile (HomeLayout module): [HomeLayoutServices.cs](../../src/Jularr.Backend/Service/User/Home/V1/HomeLayoutServices.cs) and
  [HomeLayoutLogic.cs](../../src/Jularr.Backend/Logic/Profile/HomeLayoutLogic.cs) keep the order and visibility of the home shelves
  (`ProfileHomeShelves`); Discover builds its local shelves in that order, Settings edits it.
- User discovery: [Discover services](../../src/Jularr.Backend/Service/User/Discover/V1/DiscoverServices.cs) (TMDB feeds with a short shared cache,
  search over the profile's Works with details or a library tie and TMDB; books, light novels, manga, music and games are searched and ranked (Open
  Library's global trending for books; AniList trending, top and newest for manga and light novels) through
  [MetadataCatalog.cs](../../src/Jularr.Backend/Service/Core/MetadataCatalog.cs), which the Works page shares; every card, shelf, search result and
  suggestion is a Work (`WorkId`, an AniList anime season a series maps carries the series and its `SeasonNumber` and opens `/Details/{id}?season=N`)
  with the profile's state from `ProfileWorkStates` (`DiscoverQueries.ReadStatesInBatchAsync`), and is requested with the normal `CreateRequestV1` on
  its page; the admin search marks only Works with details or a library tie as known, so a catalog title is imported into its own Work; the scopes of
  the media-type bar and their feeds come from [DiscoverScopes.cs](../../src/Jularr.Backend/Logic/Media/DiscoverScopes.cs) through
  `ReadDiscoverScopesV1`, limited to the profile's listed types) and [Library
  services](../../src/Jularr.Backend/Service/User/Library/V1/LibraryServices.cs) (local catalog, Continue for video, audio, books and comics with the
  read share).
  Overview, poster and backdrop are `WorkMetadataFacts`.
  A profile marks its own progress with `UpdateWorkProgressV1`
  ([ProgressServices.cs](../../src/Jularr.Backend/Service/User/Media/V1/ProgressServices.cs),
  [ProgressLogic.cs](../../src/Jularr.Backend/Logic/Media/ProgressLogic.cs), `ProgressTests`): a film, a series, a season, an episode, a volume or a
  track with everything below it is marked watched (aired episodes and tracks, and the film itself, even without a file; a book's volume only where
  progress exists) or unwatched (the progress rows go; starting over is that followed by playing); game play time is no watching and stays.
  The card of the library and the watchlist is one projection
  ([WorkCardProjection.cs](../../src/Jularr.Backend/Service/User/Library/V1/WorkCardProjection.cs)): the canonical `MediaAvailabilityState` with the
  present and the monitored units, the audio and subtitle languages of the tracks that are really there, the subtype and the catalog rating
  (`Works.Rating` of the catalog the title came from, `CatalogNamespace`; Discover items and catalog items carry the same rating, none when unknown),
  read for the library, the favorites, collections, people and franchises as `LibraryItemV1`; the library and the favorites decide their order over
  the cheap candidate ids and run the projection for the shown page alone (`WorkCardProjection.Page`; the library takes several types, a release year
  range and every order, tests `MediaTests.Library_*`); a card's delayed hover preview reads the Work's description and trailer through `ReadWorkV1`,
  which shows a catalog title without details from its catalog columns (`Works.Overview`, `ImageUrl`, `BackdropUrl`; no provider); a provider that
  just failed is not asked again for a minute (`WorkMetadata.ReadFeedAsync`, `MetadataCatalog.ReadFeedAsync`, which also reads the lists of one
  provider one after the other, so the lists queued behind a failure give up at once; test `DiscoverTests.Feed_AProviderThatJustFailed_*`); a Continue
  item names its type, year and unit for the card line (`ContinueDetail`, tests `ContinueDetailTests`); a profile takes a title out of its Continue
  rows with `HideContinueV1` (`DELETE /api/v1/continue/{workId}`, `MediaProgress.ContinueHiddenAt` of
  [0055](../../database/migrations/0055_continue_hidden.sql): the progress stays and the next progress write brings the title back; the item also
  carries the unit it stands at and whether the title is a favorite for the card's menu, `ProgressTests.Continue_*`); the state names the library
  (complete, partial, missing), the acquisition (downloading, searching, requested) and whether the audio is in the profile's language.

## Frontend

- [Discover](../../src/Jularr.Frontend/Pages/User/Discover/Index.cshtml.cs), [client](../../src/Jularr.Frontend/Pages/User/Discover/Index.entry.ts),
  [device choices](../../src/Jularr.Frontend/Pages/User/Discover/Controls.entry.ts): capsule bar (several types per address, `type=a,b`), highlights
  from the whole catalogs, feed chips, filter row and grid ([Index.Filters.cs](../../src/Jularr.Frontend/Pages/User/Discover/Index.Filters.cs),
  [client](../../src/Jularr.Frontend/Pages/User/Discover/FilterRow.entry.ts), [tests](../../src/Jularr.Frontend/Tools/discover-filter-row.test.mjs);
  `ReadDiscoverFilterOptionsV1` names what each type offers, `ReadDiscoverFilteredV1` reads a page locally or from the provider;
  `DiscoverTests.Filter_*`, `DiscoverTests.Razor_Filters_*`, E2E `DiscoverFilterTests`), filter drawer (the media types first, then the local
  availability, then More's filters and only the rankings the chosen types have, which leave the list as the types change in the panel,
  [Feeds.ts](../../src/Jularr.Frontend/Pages/User/Discover/Feeds.ts), [tests](../../src/Jularr.Frontend/Tools/discover-filter.test.mjs)), device-local
  highlight and layout choices, rankings of the providers asked together (a few at a time, none longer than 8 s, slow ones logged and timed in
  `Server-Timing`), Continue shelves, recommendation HTML fragments (four shelves per part, `p`, the next part while scrolling; a whole catalog
  answers a part's shelves of a type in one read; E2E `DiscoverRecommendationsTests`), the header search's suggestions (`Suggestions` handler) and its
  results (one row per type, the best match first; one type alone pages); a ranking's grid and shelf (`MediaShelf` `moreHref`) load its next pages
  while scrolling; a ranking's grid alone has the sort (`sort=`, the orders `DiscoverScopeV1.Sorts` offers, its own order `FeedSorts` as the empty
  choice); `DiscoverLayoutTests`, `DiscoverCatalogTests`.
- Every Discover card, highlight and suggestion is a Work and opens `/Details/{id}` (an AniList anime season of a mapped series `?season=N`); its
  Request action leads there, its hover preview is the Work's.
  There is no provider title page.
  A Narō web novel (`syosetu`) is a *Web novel* on its card.
  The scopes of the media-type bar come from `ReadDiscoverScopesV1`.

## Tests

- [Discover read benchmark](../CATALOG_PERFORMANCE.md): the shared local shelf lookup, authenticated feed/batch/detail controls, identity and
  visibility preservation, SQL plans and before/after snapshots.
