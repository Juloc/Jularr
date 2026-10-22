# Original Android TV — catalog API-v1 adapter

Branch: `feat/android-tv-catalog-api-v1` based on `feat/android-tv-legacy-api-v1-foundation` (PR #1459).

This slice deliberately adds **only new files** under `core-api`. It does **not** change
the original Compose TV screens, `TvClientFlow`, `TvAppController`, authentication,
shared existing Kotlin files, the Phone app, or any backend code.

## Verified backend contracts

Checked on `clean-start` against service endpoint attributes and public response
classes in `src/Jularr.Backend/Service/User/{Library,Media,Discover,Home}/V1`
and `src/Jularr.Backend/Data/User/{Library,Media,Discover,Home}/V1`,
plus `src/Jularr.Backend/Data/Pagination.cs` and
`src/Jularr.Backend/Data/Common/BaseData.cs`.

| Operation | Route | Server response |
| --- | --- | --- |
| Home shelves | GET `/api/v1/home/layout` | JSON array of HomeShelfV1; requires HomeLayout module |
| Library / local search | GET `/api/v1/library?page=1&pageSize=48&query=...` | PageResult<LibraryItemV1> |
| Continue Watching | GET `/api/v1/continue` | JSON array of ContinueItemV1, at most 20 |
| Watchlist | GET `/api/v1/watchlist?page=1&pageSize=25` | PageResult<LibraryItemV1> |
| Watchlist count | GET `/api/v1/watchlist/count` | `{"total":61}` for this profile |
| Set watchlisted | PUT `/api/v1/watchlist/{workId}` | body `{"isWatchlisted":true/false}`, result `{"success":true}` |
| Discover scopes | GET `/api/v1/discover/scopes` | JSON array of DiscoverScopeV1 |
| Discover shelves | POST `/api/v1/discover/shelves` | body optional `scopeSlug`; JSON array of DiscoverShelfV1 |
| Discover feed | POST `/api/v1/discover/feed` | body `mediaTypeId`, `discoverFeedTypeId`, `page`; JSON array of DiscoverItemV1 |
| Discover search | POST `/api/v1/discover/search` | body `query`, optional `mediaTypeId`, `page`; JSON array of DiscoverItemV1 |
| External TMDB details | POST `/api/v1/discover/{mediaTypeId}/{tmdbId}` | `DiscoverItemV1` for movie/series; `workId` may be null |
| External catalog details | POST `/api/v1/discover/catalog/entry` | `DiscoverCatalogEntryV1`; body `mediaTypeId`, `namespace`, `externalId`, optional `mediaSubtypeId` |
| Catalog search (books/manga/light novels/music/games/anime) | POST `/api/v1/discover/catalog/search` | body `mediaTypeId`, `query`, optional `mediaSubtypeId`; JSON array of DiscoverCatalogItemV1 |
| Catalog feed | POST `/api/v1/discover/catalog/feed` | body `mediaTypeId`, `discoverFeedTypeId`, optional `mediaSubtypeId`; JSON array of DiscoverCatalogItemV1 |
| Suggestions | POST `/api/v1/discover/suggestions` | body `query`, optional `scopeSlug`; JSON array of DiscoverSuggestionV1 |
| Work details | GET `/api/v1/works/{workId}` | WorkDetailsV1 |
| Editions | GET `/api/v1/works/{workId}/editions` | JSON array of WorkEditionV1 |
| Credits | GET `/api/v1/works/{workId}/credits` | JSON array of WorkCreditV1 |
| Person | GET `/api/v1/people/{personId}` | PersonV1 |
| Person works | GET `/api/v1/people/{personId}/works?page=1&pageSize=25` | PageResult<LibraryItemV1> |
| Seasons | GET `/api/v1/works/{workId}/seasons` | JSON array of WorkSeasonV1 |
| Episodes | GET `/api/v1/works/{workId}/seasons/{seasonNumber}/episodes` | JSON array of WorkEpisodeV1 |
| Media inventory | GET `/api/v1/works/{workId}/availability?page=1&pageSize=25` | PageResult<WorkAvailabilityV1> |

`BaseDataList<T>` is **serialized as a JSON array**, unlike `PageResult<T>`
which is an object with `items`, `page`, and `pageSize`.
All enum ids are serialized as **numbers**, not the C# enum names.
The server's public JSON uses **camelCase** property names.
Page numbers are 1-based; `pageSize` must be at most 100 and offset at most 100,000.
No guessed total count or hasMore value is added to `TvPageV1`.

The `/api/v1/discover/search` response can contain both local and external entries;
external entries can have `workId = null`. External TMDB IDs and catalog namespace/externalId
cannot be used as WorkIds. Catalog shelves return distinct `items` and `catalogItems`.

## How to integrate (other agent's files are untouched)

Once the foundation pairing adapter is wired to secure token persistence, construct the
catalog client using the **same** `DeviceTokenStore` instance:

```kotlin
val tokens = DeviceTokenStore(initialToken = decryptedStoredToken) { newToken ->
    // Existing Keystore-backed persistence from the pairing/session owner.
    persistEncryptedToken(newToken)
}
val pairingClient = HttpAppApiClient(serverOrigin, tokens)
val catalogClient: TvCatalogV1Client = HttpTvCatalogV1Client(serverOrigin, tokens)
```

- Home: `catalogClient.homeLayout()` for configured visible shelf order if the
  HomeLayout module is enabled; `library()` for cards and `continueWatching()`
  for the resume row. Do not substitute a fake shelf/layout when the module is absent.
- Watchlist: `watchlist(page)`, `watchlistCount()`, and `setWatchlisted(workId, true/false)`.
  Use `watchlistFiltered(page, pageSize, TvCatalogV1Filters(...))` for subtype, year, and progress filters.
- Library: `libraryFiltered(page, pageSize, TvCatalogV1Filters(...), query)` for server-side
  media type, subtype and release year filters. Type and subtype IDs are separate; a type
  does not silently include the explicitly separated subtype. The backend handles paging
  and sorting; do not fetch every page for client-side filtering.
  `TvWorkCardV1.id` **is** `WorkId`.
- Search/Discover: `library(query = text)` searches the local library;
  `discoverSearch(text)` searches local + provider catalogs. Call
  `discoverScopes()` before choosing a valid feed or subtype; some feed/type
  combinations are not valid (e.g. upcoming series).
- Details: `work(workId)` returns WorkDetailsV1. Use `editions(workId)` for
  edition descriptors, `credits(workId)` for real Work people, and
  `person(personId)` / `personWorks(personId, page)` for existing person
  navigation. Use `seasons(workId)` and `episodes(workId, seasonNumber)`
  for episodic details. Edition ID is **not** a playable MediaAssetId.
- Playback selection: `TvWorkEpisodeV1.id` is a **WorkUnitId**, **not**
  a playable asset. `TvWorkEpisodeV1.mediaAssetId` may be null if unavailable.
  Use `playableMediaAssetId()` and pass that actual MediaAssetId to the
  separate playback session adapter. Movies/other editions can be inspected
  through `availability(workId)`; `TvWorkAvailabilityV1.id` is the
  MediaAssetId and `workUnitId` may be null. Avoid using `WorkId` as asset id.
- Continue: `continueWatching()` already returns a genuine MediaAssetId and
  progress in milliseconds. It can also return reading assets; the TV player
  should filter by supported asset types without fabricating playable streams.

The adapter sends `Authorization: Bearer` from the supplied device token, accepts
`Jularr-Device-Token` rotation into the same store, never sends cookies, clears
the token on HTTP 401 only if that request still uses the active token,
and surfaces server problem details through existing `AppApiException`.
Late 401s or rotation headers cannot overwrite a newer token. It does not own
or persist session state.

## Integration and API decision points

1. **Home composition:** HomeLayout exposes shelf order/hidden flags only, not a
   materialized TV hero or row contents. Real rows must be sourced from the
   existing library, progress, watchlist and optional discovery calls. If the
   old TV layout needs different row contents, discuss an actual API gap before
   proposing any backend change. Do not invent recommendations.
2. **Provider-only Discover results:** An external entry without `workId`
   cannot open `/api/v1/works/{workId}`; do not use TMDB id as a WorkId. Instead,
   use the existing `discoverTmdbItem(mediaTypeId, tmdbId)` or
   `discoverCatalogEntry(mediaTypeId, namespace, externalId, mediaSubtypeId)`.
   These return provider details without creating a local Work; navigation into
   `/api/v1/works/{workId}` still requires a genuine non-null WorkId.
3. **Asset selection:** Episode endpoint exposes one playable MediaAssetId
   when its video is present, whereas availability may list several editions.
   `editions(workId)` gives WorkEditionIds and real file counts; the separate
   `availability(workId)` response gives playable MediaAssetIds and their
   WorkEditionId, so an edition selector can compare IDs without invention.
   The UI must not synthesize a fallback asset id for missing episodes.
4. **Capabilities/permissions:** Library, Discover, and Works require the
   Library module and `profile.media.read`; watchlist writes require
   `profile.watchlist.update`. `/api/v1/home/layout` additionally requires
   the HomeLayout module. Capability-based UI gating is owned by the
   session/controller agent.
5. **Not covered in this PR:** Profile switching, device pairing UI,
   playback sessions, stream delivery, native player controls, multi-account
   persistence, provider request submission, and Phone app.

## Validation

`core-api` / `core-model` tests and `app-tv` build are the relevant
Android verification targets. This PR adds strict parser tests, HTTP
contract tests using local HTTP responses shaped like the real backend
DTOs, and route/query tests. A green CI run or a local Gradle run is
required before declaring runtime/build success; no TV device runtime
verification is claimed.

## Incremental follow-up (22 October 2026)

The separate catalog client additionally exposes the existing provider detail
routes, server-side library/watchlist filters (type, subtype, year, progress),
and real watchlist count. These are all **client-only** changes. The catalog
backend is still read-only to this PR, and screen/controller integration remains
owned by the parallel TV agent.

The foundation branch has additional session/auth changes after this branch's
creation. **Do not rebase or merge** as long as that parallel work is ongoing;
the integration owner must verify the latest foundation tip before final merge.

### More Work detail operations

`credits`, `editions`, `person` and `personWorks` were checked against the
existing `clean-start` service definitions. The responses preserve nullable
publisher/credit/artwork and the server-provided role/edition enum IDs. Unit
and local HTTP contract tests use exact field names and bare array versus page
response shapes. These are optional catalog API calls for the original TV
Details screen; no UI was modified.
