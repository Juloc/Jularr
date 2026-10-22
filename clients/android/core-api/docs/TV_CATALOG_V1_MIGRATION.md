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
| Set watchlisted | PUT `/api/v1/watchlist/{workId}` | body `{"isWatchlisted":true/false}`, result `{"success":true}` |
| Discover scopes | GET `/api/v1/discover/scopes` | JSON array of DiscoverScopeV1 |
| Discover shelves | POST `/api/v1/discover/shelves` | body optional `scopeSlug`; JSON array of DiscoverShelfV1 |
| Discover feed | POST `/api/v1/discover/feed` | body `mediaTypeId`, `discoverFeedTypeId`, `page`; JSON array of DiscoverItemV1 |
| Discover search | POST `/api/v1/discover/search` | body `query`, optional `mediaTypeId`, `page`; JSON array of DiscoverItemV1 |
| Work details | GET `/api/v1/works/{workId}` | WorkDetailsV1 |
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
- Watchlist: `watchlist(page)` and `setWatchlisted(workId, true/false)`.
  `TvWorkCardV1.id` **is** `WorkId`.
- Search/Discover: `library(query = text)` searches the local library;
  `discoverSearch(text)` searches local + provider catalogs. Call
  `discoverScopes()` before choosing a valid feed or subtype; some feed/type
  combinations are not valid (e.g. upcoming series).
- Details: `work(workId)` returns WorkDetailsV1, followed by
  `seasons(workId)` and `episodes(workId, seasonNumber)`.
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
the token on HTTP 401, and surfaces server problem details through existing
`AppApiException`. It does not own or persist session state.

## Integration and API decision points

1. **Home composition:** HomeLayout exposes shelf order/hidden flags only, not a
   materialized TV hero or row contents. Real rows must be sourced from the
   existing library, progress, watchlist and optional discovery calls. If the
   old TV layout needs different row contents, discuss an actual API gap before
   proposing any backend change. Do not invent recommendations.
2. **Provider-only Discover results:** An external entry without `workId`
   cannot open `/api/v1/works/{workId}`; do not use TMDB id as a WorkId. A
   future flow must use the existing discover/catalog read endpoints or
   owner-approved behavior before navigation.
3. **Asset selection:** Episode endpoint exposes one playable MediaAssetId
   when its video is present, whereas availability may list several editions.
   The UI must not synthesize a fallback asset id for missing episodes.
4. **Capabilities/permissions:** Library, Discover, and Works require the
   Library module and `profile.media.read`; watchlist writes require
   `profile.watchlist.update`. `/api/v1/home/layout` additionally requires
   the HomeLayout module. Capability-based UI gating is owned by the
   session/controller agent.
5. **Not covered in this PR:** Profile switching, device pairing UI,
   playback sessions, stream delivery, native player controls, multi-account
   persistence, rich provider-only catalog details, and Phone app.

## Validation

`core-api` / `core-model` tests and `app-tv` build are the relevant
Android verification targets. This PR adds strict parser tests, HTTP
contract tests using local HTTP responses shaped like the real backend
DTOs, and route/query tests. A green CI run or a local Gradle run is
required before declaring runtime/build success; no TV device runtime
verification is claimed.
