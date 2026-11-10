# Anime and manga (AniList)

AniList accounts and mapping, manga acquisition and manga upload.
Index: [CODEMAP](../CODEMAP.md).

## Backend

- AniList account per profile (module `AniList`, its own switch off by default; it needs Library, recorded in `InstanceModuleDependencies`; the
  profile card, sync and push also need ProgressSync at call time; off, no card, no sync and no AniList call for it): the Owner enters the
  application's client id and secret on `/Admin/Providers` (the `anilist` provider row, secret protected); a profile connects its own account on
  `/Settings` with the code of AniList's PIN page ([AniListServices.cs](../../src/Jularr.Backend/Service/User/AniList/V1/AniListServices.cs), token
  exchange and list/progress calls in [AniListAccountClient.cs](../../src/Jularr.Integrations/Logic/AniList/AniListAccountClient.cs) on the shared
  pace and rate limit of [AniListClient.cs](../../src/Jularr.Integrations/Logic/AniList/AniListClient.cs)).
  One pass (job `aniListSync`, [SyncAniListV1](../../src/Jularr.Backend/Service/System/Provider/V1/AniListSyncServices.cs) on
  [AniListAccounts.cs](../../src/Jularr.Backend/Service/Core/AniListAccounts.cs), run on the Media worker) reads each list into `AniListEntries`
  ([Playback.sql](../../database/Playback.sql); the view `AniListEntryUnits` says which film, episodes (season identity with offset) or volumes an
  entry covers; entries of unknown titles wait as pending matches, nothing creates a Work), takes over what AniList counts as watched or read, and,
  only where the profile switched sending on, sends progress made after that moment.
  The rules are in [AniListSyncRules.cs](../../src/Jularr.Backend/Logic/AniList/AniListSyncRules.cs) and the SQL in
  [AniListLogic.cs](../../src/Jularr.Backend/Logic/AniList/AniListLogic.cs): both sides only move forward (the further progress wins), progress is the
  episodes or volumes finished in a row, only progress is written (a planned entry also starts as CURRENT), the last episode or volume is left to
  AniList, failed writes wait one minute and double up to six hours, a rate limit ends the pass.
  The profile sees the result read-only: the Work page asks `ReadWorkAniListV1` (the followed entry and what the profile finished in a row; `StateOf`
  compares them and `ProblemOf` names only what the next pass does not settle: sending off, a failed write, an account that needs attention) and
  Favorites lists the entries per status (`ReadAniListListsV1`, `ReadAniListEntriesV1`).
  Only when instance caching (`DiscoverCache`) is enabled does the pass keep entries as catalog Works (`MetadataCatalog.KeepWorksAsync`, namespace
  `anilist`); otherwise unmatched entries stay in `AniListEntries` without creating Works and their cards lead to title search.
  Catalog-only Works are not added to the managed library.
  The cover for the list is kept in `AniListEntries.CoverUrl`.
  Tests `AniListSyncTests`, `InstanceModuleDependencyTests`, E2E `AniListTests`.
- AniList mapping (targets are Works with details or a library tie, never pure catalog titles):
  [AniListClient.cs](../../src/Jularr.Integrations/Logic/AniList/AniListClient.cs),
  [AniListMatcher.cs](../../src/Jularr.Backend/Service/Admin/Media/V1/AniListMatcher.cs),
  [AniListMatching.cs](../../src/Jularr.Backend/Logic/Media/AniListMatching.cs) (an AniList anime entry's series is the TheTVDB series the AniBridge
  mappings name for it, else the one a title search names for its first season, `MetadataCatalog.FindAnimeSeriesAsync`; where the mappings name
  several series, the search chooses only among them), [IdentityMatchLogic.cs](../../src/Jularr.Backend/Logic/Media/IdentityMatchLogic.cs),
  [MediaMatchServices.cs](../../src/Jularr.Backend/Service/System/Media/V1/MediaMatchServices.cs) with
  [MediaWorker.cs](../../src/Jularr.Workers/MediaWorker.cs), reviews in
  [IdentityReviewServices.cs](../../src/Jularr.Backend/Service/Admin/Media/V1/IdentityReviewServices.cs).
  AniBridge mappings (anibridge/anibridge-mappings, MIT; [0024](../../database/migrations/0024_anime_mappings_tvdb_budget.sql) `AnimeMappings` with
  one row per AniList entry and TheTVDB season, `AnimeMappingSources` with the release's ETag): job `animeMappings`
  ([SyncAnimeMappingsV1](../../src/Jularr.Backend/Service/System/Media/V1/MediaMatchServices.cs), daily, only with the `AniList` module,
  `Providers:AnimeMappings`) downloads `mappings.min.json` conditionally
  ([AniBridgeClient.cs](../../src/Jularr.Integrations/Logic/AniBridge/AniBridgeClient.cs)) and replaces all rows in one transaction
  ([AnimeMappingLogic.cs](../../src/Jularr.Backend/Logic/Media/AnimeMappingLogic.cs)); a failed, unreadable or much smaller release keeps the loaded
  rows.
  A series with its TheTVDB id in the mappings takes its seasons from them (`AniListMatching.FromMappings`: an entry sits in the season of its
  earliest episode, its offset is the episodes before it, specials in S00); overlapping ranges in a regular season or an entry naming another series
  too go to the review, overlapping specials are left out; the mapped seasons replace earlier automatic ones (`ReplacesAutomatic`), a person's stay,
  and a newly loaded release makes mapped automatic series due again unless a person mapped a season.
  Tests `AnimeMappingTests`, `AniListTests.FromMappings_*`/`Matching_TvdbSeriesTheMappingsName_*`,
  `DiscoverTests.Request_OnAnAnimeSeasonTheMappingsName_*`.
  An AniList anime season no series held yet is a Work of its own (series, anime, only its AniList identity, no units); once `SaveIdentityMatchAsync`
  maps that entry to a season, `MergeAnimePlaceholdersAsync` moves its favorites, collections, relations (deduplicated, none naming itself), list
  places (at that season), requests, wanted entries and progress to the series and deletes it, in the same transaction.
  A request on such a Work imports the series, matches it when it was never matched and merges first (`MetadataCatalog.PrepareRequestAsync`).
  Tests `AniListTests.Matching_SeasonOfAPlaceholder_*`, `DiscoverTests.Request_OnAnAnimeSeasonOfItsOwn_*`.
  Relations of matched Works (module `AniList` with Library and Franchises): job `aniListRelations`
  ([SyncAniListRelationsV1](../../src/Jularr.Backend/Service/System/Media/V1/MediaMatchServices.cs), a few Works a pass on the shared AniList client
  and its limits) reads every AniList entry a matched Work holds (a film or book, or each season of a series; a new match makes it due at once, then
  daily, `WorkIdentityMatches.RelationsCheckedAt`) and saves the entries' relation edges as provider relations (`SourceKey` anilist,
  [WorkRelationLogic.cs](../../src/Jularr.Backend/Logic/Media/WorkRelationLogic.cs); seasons of the same Work are no relations, hidden ones stay
  hidden, ones AniList stops naming go); a limit ends the pass and the Work stays due.
  Tests `AniListRelationTests`.
- Manga in Arr+ (Book with subtype Manga): [AniListMetadata.cs](../../src/Jularr.Backend/Service/Admin/Media/V1/AniListMetadata.cs) (English and
  romanized titles, author fact, cover, known volumes as units, `anilist` identity, daily refresh while wanted);
  [MangaReleaseJudge.cs](../../src/Jularr.Backend/Logic/Acquisition/MangaReleaseJudge.cs) (series name, volume ranges, complete packs, chapter-only
  releases rejected; unnamed formats are `COMIC`); placement `Title/Title v01.ext` per volume with a CBZ/CBR/CB7 content check.
  Without known volumes a Manga is searched as a whole Work.
- Manga upload (module `Uploads`): [UploadVolumeV1](../../src/Jularr.Backend/Service/Admin/Media/V1/VolumeUploadServices.cs) takes a
  CBZ/CBR/CB7/EPUB/PDF of a manga Work as multipart form ([FormDataBinder.cs](../../src/Jularr.Backend/Service/Core/FormDataBinder.cs)), checks the
  caller before the body is read, the file's content against its extension, writes it under the import layout of the Work's library through
  [LibraryFileWriter.cs](../../src/Jularr.Backend/Service/Core/LibraryFileWriter.cs) (never over an existing file; subtitles use it too) and assigns
  its volume; page `/Admin/Works/{id}/Upload`; test `VolumeUploadTests`.
