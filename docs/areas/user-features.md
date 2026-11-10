# Statistics, calendar, collections, favorites and notifications

Profile features outside Discover and the library.
Index: [CODEMAP](../CODEMAP.md).

## Backend

- Statistics (module `Statistics`): [ReadProfileStatisticsV1](../../src/Jularr.Backend/Service/User/Statistics/V1/StatisticsServices.cs) (time in the
  player from playback sessions, finished titles by type, most played, retro play time) and the Owner's
  [ReadSystemHealthV1](../../src/Jularr.Backend/Service/Admin/System/V1/SystemHealthServices.cs) (database size, queues, failures, offline storage,
  last backup, configured tools, library size by type); tests `StatisticsTests.cs`.
- Calendar (module `Calendar`): [ReadCalendarV1](../../src/Jularr.Backend/Service/User/Calendar/V1/CalendarServices.cs) lists released units
  (episodes, volumes, tracks by `WorkUnits.ReleasedOn`) of the profile's watchlisted, requested or owned Works and Works without units (movies) by
  `Works.ReleasedOn`, TMDB's first release date, for up to 62 days, each with its subtype, availability, watchlist and request state; both parts read
  only those Works, never catalog units or dates; tests `CalendarTests.cs`.
- Collections (module `Collections`): [CollectionLogic.cs](../../src/Jularr.Backend/Logic/Media/CollectionLogic.cs) keeps a profile's hand-picked
  collections (Works in the order added, only Works the profile sees) and smart collections (a saved filter: type, subtype, release years, only in the
  library, only unfinished); [collection services](../../src/Jularr.Backend/Service/User/Collection/V1/CollectionServices.cs) read one static query
  for both kinds, the covers of the first titles of a hand-picked one, which collections hold given titles (`ReadCollectionMembershipV1`) and a
  collection's Works narrowed by type, year and order; the favorites read (`ReadWatchlistV1`) takes the same narrowing and, like the library, the
  profile's progress per title (`WorkCardProjection.ProgressJoin`: not started, in progress or finished from `MediaProgress`, with a percentage) as
  filter, order and badge; `ReadWatchlistCountV1` counts all favorites.
  A title in a hand-picked collection is a favorite (`CollectionLogic`) and a favorite that goes leaves the profile's collections (`WatchlistLogic`);
  tests `CollectionTests.cs`.
- Notifications (module `Notifications`): [NotificationLogic.cs](../../src/Jularr.Backend/Logic/Notification/NotificationLogic.cs) writes a profile's
  notices where things happen (request decisions in `AcquisitionRequestLogic`, arrivals for requesters and failed downloads for managers in
  `DownloadLogic`, storage that stays offline for Owners in `StorageMountStateLogic`); muted kinds stay silent and an unread notice about the same
  subject counts up.
  [Notification services](../../src/Jularr.Backend/Service/User/Notification/V1/NotificationServices.cs) read, count, mark read and mute; tests
  `NotificationTests.cs`.
  A finished download that waits for a person (`DownloadWaiting`, shared subject `NotificationSubject.DownloadsWaiting`) tells owners and media
  managers with one unread notice that links to the queue, whatever the title; repeats fold into it and the inbox shows no repeat count for it.

## Frontend

- [Statistics](../../src/Jularr.Frontend/Pages/User/Statistics/Statistics.cshtml.cs): the profile's numbers for 7 days, 30 days, a year or all time.
- [Calendar](../../src/Jularr.Frontend/Pages/User/Calendar/Calendar.cshtml.cs),
  [boards](../../src/Jularr.Frontend/Pages/User/Calendar/Calendar.Boards.cs): month, week and day of the profile's releases, the whole view in the
  address (view, date, types, status, search, chosen day, entries per day); type tints, container-width density (text events, posters, phone strips);
  `CalendarTests` (services, week/day/month pages) and `FirstRunTests` (views and filter dialog).
- [Favorites](../../src/Jularr.Frontend/Pages/User/Favorites/Favorites.cshtml.cs) (`/Favorites`, formerly `/Watchlist`, which redirects),
  [parts](../../src/Jularr.Frontend/Pages/User/Favorites/Favorites.Parts.cs),
  [client](../../src/Jularr.Frontend/Pages/User/Favorites/Favorites.entry.ts): the favorites or one collection as a grid or list, narrowed by the
  shared [media-type bar](../../src/Jularr.Frontend/Framework/Components/MediaTypeBar.cs), a filter
  [drawer](../../src/Jularr.Frontend/Framework/Components/MediaFilterDrawer.cs) and the order (only the list that is shown, all favorites or a
  collection, is purple); status tabs and progress badges, collection cards with covers, one read-only card per AniList status that has entries
  (`?anilist=`; the entries as cards that open their Work, one the library holds no unit of marked Not in library), drawers to create, rename and
  delete, and one popover (bottom sheet on phones) that the heart opens when collections exist (else it toggles) and that puts a title or a selection
  into collections.
  `CollectionTests.Favorites_*`, `FilmPathTests`.
- [Collection](../../src/Jularr.Frontend/Pages/User/Collection/Collection.cshtml.cs) (`/Collections/{id}`): a smart collection's filter, adding by
  search and deleting; `/Collections` redirects to Favorites.
- [Notifications](../../src/Jularr.Frontend/Pages/User/Notifications/Notifications.cshtml.cs): the profile's inbox, mark as read and one switch per
  kind; the bell with its unread count sits in the [Shell](../../src/Jularr.Frontend/Pages/Shared/Shell.cs) header (count read once per request by
  `UiText`).
