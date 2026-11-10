# Catalog: Works, people, editions and details

Works, people, editions, relations, series and the Details page.
Index: [CODEMAP](../CODEMAP.md).

## Backend

- Named sections over a Work's unit ranges (UnitGroups module):
  [WorkUnitGroupServices.cs](../../src/Jularr.Backend/Service/Admin/Media/V1/WorkUnitGroupServices.cs) and
  [WorkUnitGroupLogic.cs](../../src/Jularr.Backend/Logic/Media/WorkUnitGroupLogic.cs); the availability read names each unit's section (chapters
  follow their volume).
- Details on first open: the Work page opens a title through `OpenWorkV1`
  ([MediaServices.cs](../../src/Jularr.Backend/Service/User/Media/V1/MediaServices.cs)); the visit sets `ViewedAt` at most once a day, and a Work
  without details (`DetailedAt` NULL) takes them once from its catalog entry into the same Work (`MetadataCatalog.DetailAsync` over the canonical
  import, one import per Work at a time), then the page reads everything locally.
  While the catalog cannot answer (no key, timeout, 5xx, rate limit) the page shows the catalog row with the notice `details.unavailable`; nothing is
  retried, the next open asks again.
  Details older than a day are shown as they are and read again by the `metadataFill` job.
  `CreateRequestV1` brings the details first as well (`MetadataCatalog.PrepareRequestAsync`, so a series has its episodes); a failed import still
  saves the request and the job fills the Work.
  An anime season that is a Work of its own is requested as its series (below, AniList mapping).
  Tests `DiscoverTests.Details_FirstOpen_*`, `DiscoverTests.Request_On*`.
- Typed Work relations (module `Franchises`): `WorkRelations` and the view `WorkRelationLinks` ([Media.sql](../../database/Media.sql)) store one row
  per relation (what the related title is to the Work, its `SourceKey` anilist/tmdb/tvdb/hardcover/openlibrary/musicbrainz/igdb/manual,
  `IsSuppressed`); the related title is always a Work (`RelatedWorkId`; a title the provider names that is no Work yet is saved as a catalog Work
  first, AniList's OVAs, specials and music videos are left out, an anime season as its series), and the view gives the other Work the inverse type
  (sequel/prequel, side story or spin-off/main story, adaptation/source material).
  [WorkRelationLogic.cs](../../src/Jularr.Backend/Logic/Media/WorkRelationLogic.cs) owns the rules (inverse, forward storage, AniList mapping) and the
  writes: a provider's sync (`SaveWorkRelationsAsync`, from the AniList import in
  [AniListMetadata.cs](../../src/Jularr.Backend/Service/Admin/Media/V1/AniListMetadata.cs)) replaces its own rows and keeps hidden ones hidden, a
  person adds one (`CreateWorkRelationV1`, refuses duplicates from either side, itself and unknown titles) or removes one (`DeleteWorkRelationV1`: a
  person's row is deleted, a provider's stays hidden).
  [ReadWorkRelationsV1](../../src/Jularr.Backend/Service/User/Media/V1/WorkRelationServices.cs) reads them for a profile (one row per title and type,
  the titles of the same series as one universe, availability from the card projection).
  A relation row of a catalog title without details that the profile has no file, request or wanted entry of says Not in library and is requested from
  the Work page (`RequestRelated` handler of [Details](../../src/Jularr.Frontend/Pages/User/Details/Details.cshtml.cs) calls the normal
  `CreateRequestV1` with the related Work; `DiscoverTests.RelatedWork_*`).
  Tests `WorkRelationTests`, E2E `WorkRelationsTests`.
- Series (Franchises module): a series of any type is kept once in `Franchises` and found again through `FranchiseExternalIdentities` by
  [FranchiseLogic.cs](../../src/Jularr.Backend/Logic/Media/FranchiseLogic.cs) with the metadata save: TMDB's collection of a movie; for a book
  Hardcover's series through its editions' ISBNs ([HardcoverClient.cs](../../src/Jularr.Integrations/Logic/Hardcover/HardcoverClient.cs), provider
  `hardcover` with the account's API token), else a numbered series an Open Library edition names (publishers' lines without a number are left out).
  A Work keeps its place (`FranchisePosition`, also 2.5); an administrator's series and place (`UpdateWorkFranchiseV1`, Work page) stay through
  imports until the name is cleared.
  Named with the place on the details page and listed in the series' order by
  [FranchiseServices.cs](../../src/Jularr.Backend/Service/User/Media/V1/FranchiseServices.cs) on `/Franchise/{id}`; tests
  `DiscoverTests.Franchise_TmdbCollection_ListsTheVisibleTitles`, `BookTests.Book_Series_*`.
- [WorkServices.cs](../../src/Jularr.Backend/Service/Admin/Media/V1/WorkServices.cs),
  [Works.cs](../../src/Jularr.Backend/Data/Admin/Media/V1/Works.cs): manual Work creation, paged catalog, detail and version-checked title edits for
  Owner/MediaManager.
  `ReadWorksV1` is the library list (filters type, monitored, status, library, profile and title words, sort, paging; one row with cover, monitoring,
  status, profile, library, files and size).
  The mass editor is [WorkBulkServices.cs](../../src/Jularr.Backend/Service/Admin/Media/V1/WorkBulkServices.cs) with
  [WorkBulkLogic.cs](../../src/Jularr.Backend/Logic/Acquisition/WorkBulkLogic.cs): monitoring, profile, library for new files (no file moves), search,
  refresh metadata and delete, each bounded and answering with one outcome per title; tests `WorkListTests`.
- [WorkLogic.cs](../../src/Jularr.Backend/Logic/Media/WorkLogic.cs): shared canonical Work/title writer; preserves independent localized/provider
  title sources.
- People ([Media.sql](../../database/Media.sql) `People`, `PersonExternalIdentities`, `PersonRoles`, `WorkPeople`): every import replaces a Work's
  people through [WorkPeopleLogic.cs](../../src/Jularr.Backend/Logic/Media/WorkPeopleLogic.cs) from the provider's `ProviderPerson` list (TMDB cast,
  crew and creators, also each episode's guest stars, director and writer; Open Library authors; AniList story and art staff; MusicBrainz artists,
  also a track's own; IGDB developing studios).
  A person is one row per provider id and merged only through it, never by name; people without an id stay with their Work.
  The view `WorkCreators` names a Work's authors and illustrators (an album's first artist) for search, release judging and file names.
  Tests: `DiscoverTests`, `AcquisitionTests` (episode people), `MusicTests`, `BookTests`, `GameTests`.
- Editions ([Media.sql](../../database/Media.sql) `WorkEditions` with `EditionFormats`, `WorkEditionExternalIdentities`): a cut or release name, or a
  book's language, format, own title, publisher and year with its ISBNs; files without such facts share the Work's plain edition.
  The Open Library import keeps one edition per language and format (one with an ISBN and the latest year first) and their translators as edition
  people ([WorkEditionLogic.cs](../../src/Jularr.Backend/Logic/Media/WorkEditionLogic.cs)); a request's languages bring their editions' titles and
  ISBNs to the search (`WantedTargetData.EditionTitles`, `Isbns`: release judge aliases, direct sources, Anna's Archive by ISBN).
  Test `BookTests.Book_Editions_*`.
- [MediaServices.cs](../../src/Jularr.Backend/Service/User/Media/V1/MediaServices.cs),
  [Media.cs](../../src/Jularr.Backend/Data/User/Media/V1/Media.cs): persisted Work detail (with a movie's length, genres, studio and trailer, and what
  its hero shows: the catalog's rating with its source, original title and language, the card's availability state and the profile's progress), the
  Work's editions with the files each has (`ReadWorkEditionsV1`; a file also names its edition, a game's platform, a volume's cover and a track's
  disc), the Work's people (`ReadWorkCreditsV1`: authors, artists and studios, cast with their character, key crew with their job), the other titles
  of its film series as related works (`ReadFranchiseWorksV1`), more like it (`ReadWorkRecommendationsV1` in the Discover services: TMDB
  recommendations of a visible film or series, persisted as a public list and refreshed by the System service every half day, with each title's
  current viewer state), seasons and their episodes for viewers (`ReadWorkSeasonsV1`, `ReadWorkEpisodesV1`: still, summary, length, progress, state),
  stored availability and session-profile-scoped watchlist reads/updates (`ReadWatchlistV1`: library items with state, type filter and sort).
  [WatchlistLogic.cs](../../src/Jularr.Backend/Logic/Media/WatchlistLogic.cs) changes personal state only.
- [Media.sql](../../database/Media.sql): fresh catalog/title/watchlist baseline after Account.sql; no active installation or migration.
  Effective metadata locale and inventory/playback remain unintegrated.

## Frontend

- [Details](../../src/Jularr.Frontend/Pages/User/Details/Details.cshtml.cs) (`/Details/{id}`, parts:
  [hero](../../src/Jularr.Frontend/Pages/User/Details/Details.Hero.cs), [tabs and
  blocks](../../src/Jularr.Frontend/Pages/User/Details/Details.Blocks.cs), [units](../../src/Jularr.Frontend/Pages/User/Details/Details.Units.cs),
  [versions and editions](../../src/Jularr.Frontend/Pages/User/Details/Details.Versions.cs)): the title's own page of the owner's board proposal.
  A GET first calls `OpenWorkV1` (a title without details takes them from its catalog once; while the catalog cannot answer, the catalog row shows
  with the `details.unavailable` notice above the hero), then reads locally.
  [MediaHero](../../src/Jularr.Frontend/Framework/Components/MediaHero.cs) with a poster carries the title, type chip, facts with the catalog's rating
  and its source, one summary line that opens on a click ([client](../../src/Jularr.Frontend/Framework/Components/MediaHero.entry.ts)), the primary
  action by state (play, continue, read, Open in Plex, the live request phase, the request panel), the choice of version, edition or platform
  ([SelectLinkMenu](../../src/Jularr.Frontend/Framework/Components/SelectLinkMenu.cs)), trailer, heart and the
  [ActionMenu](../../src/Jularr.Frontend/Framework/Components/ActionMenu.cs) with mark watched/unwatched, start over and the link to
  `/Admin/Works/{id}`; the availability tag rests on the poster and the profile's progress bar sits bottom right; under it a line says where the
  profile stands on its AniList list (the figures with a small check while the two agree, a reason only where they stay apart, or not on the list) for
  anime, manga and light novels while the AniList module is on.
  One row of [NavigationTabs](../../src/Jularr.Frontend/Framework/Components/NavigationTabs.cs) (`?tab=`, also `season`, `sort`, `view`, `volume`,
  `version`; works without script): Overview shows each block with a few entries (next five units, eight people, six related, eight similar) and a
  link to its own tab; blocks and tabs without content are absent.
  Related works ([Details.Relations.cs](../../src/Jularr.Frontend/Pages/User/Details/Details.Relations.cs)) is a
  [ListPanel](../../src/Jularr.Frontend/Framework/Components/ListPanel.cs) of typed relations inside a
  [Region](../../src/Jularr.Frontend/Framework/Components/Region.cs): cover on the tile, title, relation chip (the source in its tip), media type and
  what the library has; every row opens its Work (the whole row is the link), and a catalog title without details that the profile has no file,
  request or wanted entry of says Not in library and is requested from its row (`RequestRelated`: the normal request of the related Work in the
  profile's language, the row swaps in place with a hint; no button without the Requests module); the Owner adds one in a
  [Modal](../../src/Jularr.Frontend/Framework/Components/Modal.cs) (type, search of Discover's titles, Add) and removes one, both without a reload
  (`SearchRelation`, `AddRelation`, `RemoveRelation` handlers).
  Units: episodes as [episode cards](../../src/Jularr.Frontend/Framework/Components/EpisodeCard.cs) in lines or cards
  ([LayoutSwitch](../../src/Jularr.Frontend/Framework/Components/LayoutSwitch.cs), season and order menus, per-episode and per-season mark and
  start-over buttons), volumes as covers with their chapters, tracks by disc in a compact
  [Table](../../src/Jularr.Frontend/Framework/Components/Table.cs), editions and platforms as cards, the files of a film or the rest as a table, facts
  in a [FactGrid](../../src/Jularr.Frontend/Framework/Components/FactGrid.cs).
  Status pills are [StateChip](../../src/Jularr.Frontend/Framework/Components/StateChip.cs)s with an icon.
  The progress forms post to the `Progress` handler (`ProgressTests.Details_*`, `DetailsPageTests`, E2E `DetailsTabsTests`, `FilmPathTests`).

## Tests

- Manual catalog/title edits, role restriction, stale updates, personal list isolation and real Razor forms: `MediaTests.cs`.
