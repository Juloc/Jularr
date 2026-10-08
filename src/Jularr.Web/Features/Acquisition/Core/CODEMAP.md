# Acquisition core (search, selection, grab)

Purpose: one search -> rank -> grab path for every media type; adapters only supply facts.
Sources: Usenet (Prowlarr/Newznab indexers, SABnzbd) and direct sources (`IDirectSource`, registered per media type; today Book: free catalog edition and OPDS; Light Novel: public full-text web copies). Both return `AcquisitionCandidate`s that one selection ranks together before the winner is routed by `AcquisitionType` (UsenetDownload: download client and the completed-download dispatcher; DirectImport: the source imports it). Anna's Archive does not exist in Jularr; Manga, Music and Video have no direct source; a Syosetu ncode request is an explicit web import and does not search.

Canonical owners
- `AcquisitionCore`: `SearchAsync` (indexer search, identity judgement, `ReleaseSelectionEngine` ranking) and `GrabAsync` (`ReleaseRequestTracker` lifecycle, `DownloadClientSubmissionService`).
- `ReleaseRanker`: judges and orders releases with `ReleaseSelectionEngine` (also usable without an indexer search).
- Shared records: `MediaSearchPlan<T>` (query intent, judge, wanted-since), `ReleaseJudgement<T>`, `ReleaseEvaluation<T>`, `SearchEvaluation<T>`, `GrabTarget`.
- Underneath: `IndexerSearchCoordinator`/`SearchPlanner` (Usenet search), `ReleaseSelectionEngine` + `QualityProfile`, `ReleaseRequestTracker` (tried releases, back-off), SABnzbd via the download-client layer, `CompletedDownloadDispatcher` for import.

Flow
WantedItems / request -> media adapter builds `MediaSearchPlan` -> `AcquisitionCore.SearchAsync` -> ranked `ReleaseEvaluation` list
-> `AcquisitionCore.GrabAsync(GrabTarget)` -> operation -> completed-download dispatcher -> media import adapter.

Media adapters on the core
- Video (Movie, Tv): `VideoAcquisitionEngine` (`VideoReleaseJudge`, unit and season-pack coverage, playback priority).
- Music: `MusicAcquisitionEngine` (`MusicReleaseJudge`).
- Manga and Light Novel: `ReadingAcquisitionEngine` (`ReadingReleaseJudge.Plan/Judge`, `DisplayScore` for the pages); Light Novel adds `LightNovelWebDirectSource`. Without a configured indexer or SABnzbd only a direct candidate can serve the request.

- Book: `BookAcquisitionExecutor` (`BookReleaseSelector.Plan` and `Judge`, `BookCatalogDirectSource`, `BookOpdsDirectSource`); the free edition wins a tie with an equal Usenet EPUB through the lowest source priority, a better quality wins before source preference.

- Audiobook: `AudiobookAcquisitionRequestExecutor` (`AudiobookReleaseJudge`, `AudiobookReleaseParser` quality M4B / MP3-320 / MP3); the import adapter binds the audiobook to the request's Book Work as its audio edition.

Anime searches and grabs through the core too (`AnimeAcquisitionEngine`); its old pipeline run paths are being removed.

Manual Search reads the same `SearchEvaluation` through `VideoManualSearchService`, `MusicManualSearchService`, `ReadingManualSearchService`, `BookManualSearchService`.

Tests: `BookPdfAcquisitionTests` (incl. direct vs Usenet routing), `VideoAcquisitionRequestExecutorTests`, `MusicAcquisitionTests`, `VideoManualSearchTests`, `ReadingAcquisitionTests`, `ReleaseSelectionEngineTests`.
