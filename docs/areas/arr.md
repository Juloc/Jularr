# Arr+ acquisition core

Indexer connections, search, release choice, grabs, takeover and language chains.
Index: [CODEMAP](../CODEMAP.md).
Specs: [AUTOMATIC_RELEASE_SELECTION](../AUTOMATIC_RELEASE_SELECTION.md), [ACQUISITION_SEARCH_ALL](../ACQUISITION_SEARCH_ALL.md),
[PARITY_SONARR](../PARITY_SONARR.md).

## Decisions

Owner decisions; change them only with the owner.

- The manual search: a link to `/Admin/Wanted/{id}/Search` that carries `data-search-dialog` opens the search as a dialog over the current page (title
  and the scope fields with "Search again" in the head, Close); the link stays a real address, and that page shows the same panel.
  The bar and its line count the sources that answered ("Searching 3 sources … 2 of 3 done", then "N releases from M sources"); each source is a chip
  (searching, check with its number, or a warning with a short reason) and a click on a chip only hides the releases of the other sources, it never
  asks again.
  Under the chips one row narrows what is loaded by a field for words in the title (all words must occur, any order, accents and case do not matter),
  language (the languages the releases carry), protocol, age and size (a release without a date or size does not fit a limit on it), quality (a list
  of checkboxes, one per quality the releases carry, a release without one is "Unknown"; none checked means no limit) and decision (eligible, review,
  rejected; all are shown on opening), and orders it (by source, best score, newest, largest, smallest; any order but by source puts all releases into
  one list across the sources); "Clear filters" resets them.
  The controls keep their width and place, the status line says "k of N releases" and a notice says when none fits; sources that do not serve the
  media type are not shown.
  Every source is its own request; a running source holds two skeleton lines where its releases will appear, and a failed or limited source is never
  shown as "no releases".
  A release has two buttons, In queue (the end of its client's queue) and Start now (before the others); a rejected release is grey with its reason
  and only a small menu with a warning and "Take anyway".
  On a phone each release is a card: the release on top, then source, quality, size, score and decision, then the buttons.
- Arr+ grabs the best allowed release immediately.
  No release waiting periods, postponement profiles, Usenet holding period, language waiting or time-based substitute qualities.
  Improvements come only through upgrades.
  Technical retry and backoff for unavailable services remain separate.

## Backend

- Library Search All: [approved behavior and focused checks](../ACQUISITION_SEARCH_ALL.md), [queue/read/cancel
  services](../../src/Jularr.Backend/Service/Admin/Media/V1/WorkSearchRunServices.cs), [System
  execution](../../src/Jularr.Backend/Service/System/Acquisition/V1/WorkSearchRunServices.cs), `WorkSearchRunTests` and `SearchReassessmentTests`.
- Language chains (books, films, series, anime, manga):
  [MonitoredLanguageServices.cs](../../src/Jularr.Backend/Service/Admin/Acquisition/V1/MonitoredLanguageServices.cs) (setting "Languages for monitored
  titles", Profiles page), [WantedLanguageServices.cs](../../src/Jularr.Backend/Service/Admin/Wanted/V1/WantedLanguageServices.cs) (a title's own
  chain, Work page), the profile's first and second content language and its switch "Last, the original language" in Settings; views
  `WantedItemChains` and `WantedBookLanguages` (migrations `0044` to `0046`), rules in
  [AUTOMATIC_RELEASE_SELECTION.md](../AUTOMATIC_RELEASE_SELECTION.md) ("Language chain and one edition per language"); tests
  `BookLanguagePerEditionTests`, `DatabaseMigrationTests.LanguageChains_*`.
  Video audio and subtitles ([VideoReleaseJudge.cs](../../src/Jularr.Backend/Logic/Acquisition/VideoReleaseJudge.cs)), the chain step ranking
  (`ReleaseSelection.LanguageTier`) and the language add-ons ([LanguageRuleLogic.cs](../../src/Jularr.Backend/Logic/Acquisition/LanguageRuleLogic.cs),
  [LanguageRuleServices.cs](../../src/Jularr.Backend/Service/Admin/Acquisition/V1/LanguageRuleServices.cs), table `LanguageReleaseRules` from
  migration `0047`, Profiles page "Language add-ons", [Profiles.Languages.cs](../../src/Jularr.Frontend/Pages/Admin/Profiles/Profiles.Languages.cs));
  tests `VideoLanguageSelectionTests`, `LanguageRuleTests`.
  Language upgrades of films and series (view `WantedVideoLanguages`, migration `0050`, `UpgradePolicy.IsBetterLanguage`,
  `StoredFileFormatLogic.AudioLanguages`) go through the feed decision; tests `VideoLanguageUpgradeTests`,
  `AcquisitionTests.Feed_ReleaseInTheFirstLanguage_*`, `AutoApprovalTests`.
- Installed format scores: [StoredFileFormatLogic.cs](../../src/Jularr.Backend/Logic/Acquisition/StoredFileFormatLogic.cs),
  [StoredFileFormatServices.cs](../../src/Jularr.Backend/Service/System/Acquisition/V1/StoredFileFormatServices.cs) and the StoredFileFormatEvidence /
  StoredFileProfileScores / WantedUnitsBelowCutoff views in [Library.sql](../../database/Library.sql).
  Tests: ArrAdminTests.cs.
- One-time takeover (a Sonarr series is TheTVDB's by Sonarr's `tvdbId`; without TheTVDB's key, or while it cannot answer, it comes over as an
  undetailed Work with Sonarr's TheTVDB, TMDB and IMDb ids, title, year and wanted state, and the metadata fill or a first open details it later;
  every title taken over is in the library at once with a target that mirrors the source's monitoring, monitored wanted and unmonitored paused, and a
  target it already has stays as it is; the Setup step asks for a missing TMDB or TheTVDB key in its own card):
  [TakeoverServices.cs](../../src/Jularr.Backend/Service/Admin/Acquisition/V1/TakeoverServices.cs),
  [ServarrProfileLogic.cs](../../src/Jularr.Backend/Logic/Acquisition/ServarrProfileLogic.cs),
  [ServarrFormatMapping.cs](../../src/Jularr.Backend/Logic/Acquisition/ServarrFormatMapping.cs) and
  [ServarrApiClient.cs](../../src/Jularr.Integrations/Logic/Servarr/ServarrApiClient.cs).
  Tests: TakeoverTests.cs, ArrAdminTests.cs.
  Source profile provenance: AcquisitionProfileImports in [Acquisition.sql](../../database/Acquisition.sql).
- Arr+ core: [connection services](../../src/Jularr.Backend/Service/Admin/Acquisition/V1/AcquisitionConnectionServices.cs) (Owner, protected keys,
  list by role and by kinds (`AcquisitionConnectionTypeIds`, the pages of the Downloads group), test (a success ends a backoff), a counted free-text
  test search of an indexer, indexer categories; keyless indexers (a stored key stays only with its connection type), media scope, Sonarr's flags per
  source (`ReadsFeed` for Newznab, `SearchesAutomatically`, `SearchesInteractively`) and `SearchIntervalHours`, the rhythm of a source without a feed
  (migration `0048_feed_first_search.sql`), daily query and grab limits (an offer of a removed indexer is refused as stale), and a backoff after a
  failed search (1, 5, 15, 30, 60 minutes, then 3, 6, 12 and at most 24 hours; no default daily limit, only the one the Owner sets), applied by
  `AcquisitionConnectionLogic.CanSearch` to automatic searches), [profile
  services](../../src/Jularr.Backend/Service/Admin/Acquisition/V1/AcquisitionProfileServices.cs) and
  [AcquisitionProfileLogic.cs](../../src/Jularr.Backend/Logic/Acquisition/AcquisitionProfileLogic.cs) (quality groups, format scores, upgrade
  thresholds and protected profile references), per-target profile and release block/allow in the Wanted services;
  [WantedSearchServices.cs](../../src/Jularr.Backend/Service/Admin/Wanted/V1/WantedSearchServices.cs) searches a target, its season, episode or volume
  ([WantedSearchScope.cs](../../src/Jularr.Backend/Logic/Acquisition/WantedSearchScope.cs)) by hand or, on demand, automatically
  (`SearchWantedAutomaticallyV1` takes the winner at once); a manual search asks all sources or, with `SearchWantedDataV1.AcquisitionConnectionId`,
  one of those `ReadWantedSearchSourcesV1` lists ([SearchSources.cs](../../src/Jularr.Backend/Service/Admin/Wanted/V1/SearchSources.cs) is the one
  choice of sources the search and the list share); `GrabWantedReleaseV1` takes `StartsNow` (forced priority, qBittorrent: first in its queue) and
  `OverridesRejection` (rejected releases are cached apart, only taken with it);
  [WantedAcquisition.cs](../../src/Jularr.Backend/Service/Admin/Wanted/V1/WantedAcquisition.cs) (indexer and client I/O for manual and automatic
  acquisition; decisions in [AcquisitionSelectionLogic.cs](../../src/Jularr.Backend/Logic/Acquisition/AcquisitionSelectionLogic.cs), placement in
  [ImportPlacementLogic.cs](../../src/Jularr.Backend/Logic/Acquisition/ImportPlacementLogic.cs); search/selection data in `Data/Logic/Acquisition`,
  enums in `Data/Enums/Acquisition`), [Wanted search services](../../src/Jularr.Backend/Service/Admin/Wanted/V1/WantedSearchServices.cs), [System
  acquisition services](../../src/Jularr.Backend/Service/System/Acquisition/V1/AcquisitionServices.cs) and
  [AcquisitionWorker.cs](../../src/Jularr.Workers/AcquisitionWorker.cs) (due searches with backoff, SABnzbd monitoring, path mapping, hardlink/copy
  import and assignment).
  Searching as Sonarr does (owner decision): the job `feedSync` (every 15 minutes) runs
  [FeedServices.cs](../../src/Jularr.Backend/Service/System/Acquisition/V1/FeedServices.cs) and
  [FeedSync.cs](../../src/Jularr.Backend/Service/Admin/Wanted/V1/FeedSync.cs) (one `NewznabClient.ReadRecentAsync` per indexer with its feed on, for
  every type of `AcquisitionConnectionLogic.FeedMediaTypes` it serves, counted like a search),
  [FeedMatcher.cs](../../src/Jularr.Backend/Logic/Acquisition/FeedMatcher.cs) (release to wanted title by provider ids, else the parsed title, a
  book's title and author) and [FeedLogic.cs](../../src/Jularr.Backend/Logic/Acquisition/FeedLogic.cs) (`FeedReleases` seen for 14 days);
  `WantedAcquisition.TakeFromFeedAsync` judges a title's new releases by its selection and grabs the best at once (a series per season pack, then
  episodes).
  `SearchDueWantedV1` searches every automatic source only when an event made a title due (`WantedItems.NextSearchAt` NULL or passed: added, resumed,
  a button, a failed download, a retry; `'infinity'` waits for one) and the sources without a feed on their rhythm (`PeriodicSearchAt`,
  `SearchDueWantedV1.Plan`, `AcquisitionConnectionLogic.NextPeriodicSearch`); a grab or an import no longer makes its title due.
  A search no source answered retries after 30 minutes (`search_failed` raises the failure), a running download that holds the winner ends the
  season's search (`in_use`), so no target is searched in every cycle; a grab counts against its indexer's daily limit in the transaction of its
  reservation, so a refused reservation counts nothing (`AcquisitionTests.SearchDue_AGrabARunningDownloadHolds_*`,
  `UsenetTests.Usenet_NzbRateLimited_*`: a grab its indexer limited waits open in the queue with its link protected in
  `AcquisitionReleases.ProtectedDownloadUrl` and `WantedAcquisition.ResumeLimitedGrabAsync` fetches the same release after the pause); migration
  `0040_feed_releases.sql`; tests `AcquisitionTests.Feed_*`, `Search_AfterItsEventSearch_*`, `Plan_*`, `BookTests.Book_FeedRelease_*`.
  Pure ranking ported from dev: [ReleaseParser.cs](../../src/Jularr.Backend/Logic/Acquisition/ReleaseParser.cs),
  [SearchPlanner.cs](../../src/Jularr.Backend/Logic/Acquisition/SearchPlanner.cs),
  [VideoReleaseJudge.cs](../../src/Jularr.Backend/Logic/Acquisition/VideoReleaseJudge.cs),
  [ReleaseSelection.cs](../../src/Jularr.Backend/Logic/Acquisition/ReleaseSelection.cs); Series and Manga share one unit pass: groups (seasons; Manga
  has none) with nothing yet are searched as a pack, otherwise unit by unit, and releases claim `ParentUnitNumber`/`UnitStart`/`UnitEnd`; DML in
  [DownloadLogic.cs](../../src/Jularr.Backend/Logic/Acquisition/DownloadLogic.cs) (shared "missing available unit" rule: regular episodes and Manga
  volumes; a running release that names no season or unit holds the whole Work, for the due search as for the reservation);
  [EpisodeNumbering.cs](../../src/Jularr.Backend/Logic/Acquisition/EpisodeNumbering.cs) maps absolute (Anime) and dated (daily shows) releases onto a
  season's episodes for the judge, the grab's claim and the import and
  [AcquisitionConnectionLogic.cs](../../src/Jularr.Backend/Logic/Acquisition/AcquisitionConnectionLogic.cs).
  Clients: [NewznabClient.cs](../../src/Jularr.Integrations/Logic/Newznab/NewznabClient.cs) (search and NZB fetch, paced per indexer host by
  [NewznabGate.cs](../../src/Jularr.Integrations/Logic/Newznab/NewznabGate.cs): one call per second, a 429 pauses every call to the host for its
  Retry-After, which the indexer's backoff and a refused grab's retry also honour),
  [SabnzbdClient.cs](../../src/Jularr.Integrations/Logic/Sabnzbd/SabnzbdClient.cs),
  [StorageFileTransfer.cs](../../src/Jularr.Integrations/Logic/Storage/StorageFileTransfer.cs).
  Every downloader imports through [DownloadImport.cs](../../src/Jularr.Backend/Service/System/Acquisition/V1/DownloadImport.cs).
- Automatic paths (E2E `AutomaticPathTests`): a Sonarr takeover whose monitored series stays wanted across restart and picks only the releases its
  profile allows, a user's film under a custom profile and an anime, and a user's PDF book with its picture, each found by the automatic search
  without a manual one.

## Frontend

- Library Search All: [Works.Search.cs](../../src/Jularr.Frontend/Pages/Admin/Works/Works.Search.cs), [status/results
  refresh](../../src/Jularr.Frontend/Pages/Admin/Works/Works.entry.ts), [behavior and verification](../ACQUISITION_SEARCH_ALL.md); E2E
  `WorkSearchRunTests` covers all/filtered/selected, cancellation and mobile layout.
- [Takeover](../../src/Jularr.Frontend/Pages/Admin/Takeover/Takeover.cshtml.cs): one-time Prowlarr/Sonarr/Radarr/Lidarr/Readarr preview and import,
  inline missing keys and new libraries from root folders (its form parts also build the setup step).

## Tests

- Connections, manual search/grab, queue progress, completed-download import, release-fault retry, automatic search, TMDB import, season packs and
  episodes, selection gates, Newznab parsing and Razor pages: `AcquisitionTests.cs` (pacing and 429 handling of the client: `NewznabThrottleTests.cs`)
  (fake indexer and SABnzbd over a test HTTP handler).
  AniList import, volume packs and singles, per-volume import, completion and Manga release names: `MangaTests.cs`.
  MusicBrainz album import, artist/album judging, track placement by tags and tagged library files: `MusicTests.cs`.
  Takeover preview, apply and repeat, profiles, monitoring and root folders: `TakeoverTests.cs`.
  Subtypes, media switches, Owner blocks and profile hiding: `MediaTests.cs`.
  AniList matching, season alignment, AniBridge mappings and reviews: `AniListTests.cs`, `AnimeMappingTests.cs`.
  IGDB credentials, search, import, shared platforms, platform requests, game release names and placement, browser play with saves and play time,
  files of the games libraries and their own games: `GameTests.cs` (homebrew in the browser, DS touch on a phone: E2E `GamePathTests`).
  Native Usenet download over a loopback NNTP server, missing articles without repair files, yEnc and NZB safety: `UsenetTests.cs`.
